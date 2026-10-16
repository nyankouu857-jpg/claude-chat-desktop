using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;

namespace ClaudeChat.Core;

public sealed class WorkflowRun
{
    public string Id { get; } = "wf_" + Guid.NewGuid().ToString("N")[..10];
    public required string ParentId { get; init; }
    public required string Title { get; init; }
    // 同梱の台本の名前。research のときは画面が送信を待たせる
    public string? Name { get; init; }
    // 始めた時点の往復の回数。結果を親に返すときに引き継ぐ
    public int Hops { get; init; }
    // run_workflow で選んだモデルと effort。台本の agent() の指定がさらに優先する
    public JsonObject? Overrides { get; init; }
    public string Status { get; set; } = "running";
    public string? Phase { get; set; }
    public List<string> Logs { get; } = [];
    public ConcurrentDictionary<string, (string Label, string Phase, string Status)> Agents { get; } = new();
    public int AgentCount => Agents.Count;
    public string? Result { get; set; }
    public string? Error { get; set; }
    public CancellationTokenSource Cancellation { get; } = new();
    public long StartedAt { get; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    // 子は終わると消えるので、使った料金はその都度ここに足しておく
    decimal costUsd;
    readonly Lock costLock = new();

    public decimal CostUsd
    {
        get { lock (costLock) return costUsd; }
    }

    public void Charge(decimal cost)
    {
        lock (costLock) costUsd += cost;
    }

    JsonArray LogsView()
    {
        lock (Logs) return new JsonArray(Logs.TakeLast(20).Select(l => (JsonNode)l).ToArray());
    }

    public JsonObject ToWire() => new()
    {
        ["id"] = Id,
        ["parentId"] = ParentId,
        ["title"] = Title,
        ["name"] = Name,
        ["status"] = Status,
        ["phase"] = Phase,
        ["logs"] = LogsView(),
        ["agents"] = new JsonArray(Agents.Select(a => (JsonNode)new JsonObject
        {
            ["chatId"] = a.Key,
            ["label"] = a.Value.Label,
            ["phase"] = a.Value.Phase,
            ["status"] = a.Value.Status,
        }).ToArray()),
        ["error"] = Error,
        ["startedAt"] = StartedAt,
    };
}

// Claude Code の Workflow にならい、Claude が書いた JavaScript で多数のサブエージェントを並べて動かす。
// 台本は Jint で動かし、PC の資源には触らせない (agent / log / phase / args / parallel / pipeline だけを渡す)
public sealed partial class WorkflowRunner(ChatHub hub, SettingsStore settings, ConversationStore conversations, IChatEvents events)
{
    readonly ConcurrentDictionary<string, WorkflowRun> runs = new();

    public const string ToolDescription = """
        Run a workflow: a JavaScript script that orchestrates many sub-agents in the background, then returns one consolidated result to this chat as a <task-notification>. Use it only when the user asked for a workflow or deep research, or a task truly needs many agents in parallel (broad reviews, research across many sources). It costs far more than doing the work yourself.
        Script API (plain JavaScript, top-level await allowed, `return` the final value):
        - agent(prompt, {label, phase, schema, webSearch, model, effort}) -> Promise of the sub-agent's final text, or of a parsed object when `schema` (a JSON Schema with type "object") is given. Resolves to null if the agent fails. Each agent sees only its prompt. model and effort default to the workflow's, which default to this chat's; set them only when an agent's job clearly calls for it (a cheaper model for simple bulk work, a stronger one for hard reasoning).
        - parallel([() => agent(...), ...]) -> runs thunks concurrently, failed ones become null.
        - pipeline(items, stage1, stage2, ...) -> runs each item through the stages independently; each stage gets (previousResult, item, index).
        - log(message), phase(title), args (the value passed in).
        Agents run a limited number at a time and the total per workflow is capped. Use name "research" with args {question, context?} for the bundled deep-research workflow.
        """;

    const string Prelude = """
        async function parallel(thunks) {
          return Promise.all(thunks.map(async (thunk) => {
            try { return await thunk() } catch (e) { log('失敗: ' + String(e)); return null }
          }))
        }
        async function pipeline(items, ...stages) {
          return Promise.all(items.map(async (item, index) => {
            let value = item
            for (const stage of stages) {
              try { value = await stage(value, item, index) } catch (e) { log('失敗: ' + String(e)); return null }
              if (value === null || value === undefined) return null
            }
            return value
          }))
        }
        """;

    // Claude Code の /deep-research にあたる同梱の台本
    const string ResearchScript = """
        const question = args && args.question
        if (!question) throw new Error('args.question がありません')
        // それまでの会話を踏まえて調べる。「さっきの件を詳しく」のような頼み方でも的を外さないように
        const context = args.context ? `\n\nThe conversation so far, for context:\n${args.context}` : ''
        phase('計画')
        const plan = await agent(`You are planning a research report. Break this question into 3 to 5 focused sub-questions that together cover it. Question: ${question}${context}`, {
          label: '計画', schema: { type: 'object', properties: { subquestions: { type: 'array', items: { type: 'string' } } }, required: ['subquestions'] },
        })
        const subquestions = (plan && plan.subquestions || [question]).slice(0, 5)
        phase('調査')
        const findings = await parallel(subquestions.map((q, i) => () => agent(
          `Research this question thoroughly with web search and web fetch. Read primary sources where possible. Report the facts you found with the URL of each source, and note disagreements between sources.\nQuestion: ${q}\nIt is part of the larger question: ${question}`,
          { label: `調査 ${i + 1}`, webSearch: true })))
        phase('まとめ')
        const notes = findings.map((f, i) => f ? `## ${subquestions[i]}\n${f}` : '').filter(Boolean).join('\n\n')
        return await agent(`Write a well-structured research report that answers the question below, based only on the findings provided. Use headings, cite sources inline as [title](url), and end with a short summary of the answer and open questions.\nQuestion: ${question}\n\nFindings:\n${notes}`, { label: 'まとめ' })
        """;

    [GeneratedRegex(@"^\s*export\s+const\s+meta\b", RegexOptions.Multiline)]
    private static partial Regex ExportMeta();

    public WorkflowRun? Get(string id) => runs.GetValueOrDefault(id);

    public void Charge(string id, decimal cost) => runs.GetValueOrDefault(id)?.Charge(cost);

    public IEnumerable<WorkflowRun> ForParent(string parentId) => runs.Values.Where(r => r.ParentId == parentId).OrderBy(r => r.StartedAt);

    public JsonArray ListView() => new(runs.Values.OrderByDescending(r => r.StartedAt).Select(r => (JsonNode)r.ToWire()).ToArray());

    void Changed(WorkflowRun run) => events.Emit("workflow.changed", run.ToWire());

    public WorkflowRun Start(Conversation parent, string? script, string? name, JsonNode? args, string title, JsonObject? overrides = null)
    {
        var source = name switch
        {
            "research" => ResearchScript,
            null or "" => script is { Length: > 0 } s ? s : throw new ToolException("script か name を指定してください"),
            _ => throw new ToolException($"{name} という同梱のワークフローはありません。research が使えます"),
        };
        // Claude Code の書き方の先頭の export const meta をそのまま受け付ける
        source = ExportMeta().Replace(source, "const meta");
        var run = new WorkflowRun { ParentId = parent.Id, Title = title, Name = name, Hops = hub.RunHopsOf(parent.Id), Overrides = overrides };
        runs[run.Id] = run;
        Changed(run);
        var thread = new Thread(() => Execute(run, parent, source, args)) { IsBackground = true, Name = $"workflow {run.Id}" };
        thread.Start();
        return run;
    }

    public void Stop(string id)
    {
        if (!runs.TryGetValue(id, out var run)) return;
        run.Cancellation.Cancel();
        foreach (var chatId in run.Agents.Keys) hub.Stop(chatId);
    }

    static JsonObject StrictSchema(JsonObject schema)
    {
        // 構造化出力は object に additionalProperties: false が要る
        var copy = (JsonObject)schema.DeepClone();
        void Walk(JsonNode? node)
        {
            if (node is JsonObject obj)
            {
                if ((string?)obj["type"] == "object" && obj["additionalProperties"] is null) obj["additionalProperties"] = false;
                foreach (var (_, child) in obj.ToList()) Walk(child);
            }
            else if (node is JsonArray array)
            {
                foreach (var child in array) Walk(child);
            }
        }
        Walk(copy);
        return copy;
    }

    void Execute(WorkflowRun run, Conversation parent, string script, JsonNode? args)
    {
        var token = run.Cancellation.Token;
        var pending = new ConcurrentQueue<Action>();
        using var signal = new SemaphoreSlim(0);
        void Post(Action action)
        {
            pending.Enqueue(action);
            signal.Release();
        }
        var maxAgents = Math.Max(1, settings.Current?.Agents.MaxWorkflowAgents ?? 10);
        string? outcome = null;
        string? failure = null;
        var finished = false;

        var engine = new Engine(options => options.CancellationToken(token).LimitRecursion(512).Strict());
        engine.SetValue("log", new Action<JsValue>(message =>
        {
            lock (run.Logs) run.Logs.Add(message.ToString());
            Changed(run);
        }));
        engine.SetValue("phase", new Action<string>(title =>
        {
            run.Phase = title;
            Changed(run);
        }));
        engine.SetValue("args", args is null ? JsValue.Undefined : engine.Evaluate($"({args.ToJsonString()})"));
        engine.SetValue("__done", new Action<JsValue>(value =>
        {
            outcome = value.IsUndefined() ? "" : value.IsString() ? value.AsString() : engine.Evaluate("JSON.stringify").AsFunctionInstance().Call(JsValue.Undefined, [value, JsValue.Null, 2]).ToString();
            finished = true;
        }));
        engine.SetValue("__fail", new Action<string>(message =>
        {
            failure = message;
            finished = true;
        }));
        engine.SetValue("agent", new Func<string, JsValue, JsValue>((prompt, options) =>
        {
            var (promise, resolve, reject) = engine.Advanced.RegisterPromise();
            if (run.AgentCount >= maxAgents)
            {
                reject(JsValue.FromObject(engine, $"このワークフローで使えるエージェントは {maxAgents} 個までです"));
                return promise;
            }
            var opts = options.IsObject() ? JsonNode.Parse(engine.Evaluate("JSON.stringify").AsFunctionInstance().Call(JsValue.Undefined, [options]).ToString()) as JsonObject : null;
            var label = (string?)opts?["label"] ?? $"エージェント {run.AgentCount + 1}";
            var phase = (string?)opts?["phase"] ?? run.Phase ?? "";
            var schema = opts?["schema"] as JsonObject;
            var webSearch = (bool?)opts?["webSearch"];
            JsonObject? overrides;
            try
            {
                overrides = ModelCatalog.Overrides((string?)opts?["model"] ?? (string?)run.Overrides?["model"], (string?)opts?["effort"] ?? (string?)run.Overrides?["effort"]);
            }
            catch (ToolException e)
            {
                reject(JsValue.FromObject(engine, e.Message));
                return promise;
            }
            // 上限を数えるため、始まる前に枠を確保する
            var placeholder = "pending_" + Guid.NewGuid().ToString("N");
            run.Agents[placeholder] = (label, phase, "starting");
            _ = Task.Run(async () =>
            {
                Conversation? child = null;
                try
                {
                    child = await hub.SpawnAsync(parent, prompt, label, "agent", run.Id, options => options with
                    {
                        OutputSchema = schema is null ? null : StrictSchema(schema),
                        WebSearch = webSearch ?? options.WebSearch,
                        WebFetch = webSearch ?? options.WebFetch,
                    }, background: false, token, overrides);
                    run.Agents.TryRemove(placeholder, out _);
                    run.Agents[child.Id] = (label, phase, "running");
                    // 止めたのと入れ違いに始まった子も止める
                    if (token.IsCancellationRequested) hub.Stop(child.Id);
                    Changed(run);
                    var result = await hub.WaitAsync(child.Id, token);
                    var text = ChatHub.FinalText(child);
                    run.Agents[child.Id] = (label, phase, result.Status == ChatStatus.Done ? "done" : "failed");
                    Changed(run);
                    Post(() =>
                    {
                        if (result.Status != ChatStatus.Done)
                        {
                            lock (run.Logs) run.Logs.Add($"{label} が失敗しました: {result.Error}");
                            resolve(JsValue.Null);
                        }
                        else if (schema is not null)
                        {
                            try
                            {
                                resolve(engine.Evaluate("JSON.parse").AsFunctionInstance().Call(JsValue.Undefined, [text]));
                            }
                            catch (Exception)
                            {
                                lock (run.Logs) run.Logs.Add($"{label} の答えが JSON になっていませんでした");
                                resolve(JsValue.Null);
                            }
                        }
                        else
                        {
                            resolve(text);
                        }
                    });
                }
                catch (Exception e)
                {
                    run.Agents.TryRemove(placeholder, out _);
                    if (child is not null)
                    {
                        hub.Stop(child.Id);
                        run.Agents[child.Id] = (label, phase, "failed");
                    }
                    Changed(run);
                    Post(() => reject(JsValue.FromObject(engine, e.Message)));
                }
            });
            return promise;
        }));

        try
        {
            engine.Execute(Prelude);
            engine.Execute($"(async () => {{ try {{ __done(await (async () => {{\n{script}\n}})()) }} catch (e) {{ __fail(String(e && e.stack || e)) }} }})()");
            while (!finished)
            {
                token.ThrowIfCancellationRequested();
                while (pending.TryDequeue(out var action)) action();
                engine.Advanced.ProcessTasks();
                if (!finished) signal.Wait(TimeSpan.FromMilliseconds(200), token);
            }
            run.Status = failure is null ? "completed" : "failed";
            run.Result = outcome;
            run.Error = failure;
        }
        catch (OperationCanceledException)
        {
            run.Status = "stopped";
        }
        catch (Exception e)
        {
            run.Status = "failed";
            run.Error = e.Message;
        }
        Changed(run);
        _ = NotifyParentAsync(run);
    }

    async Task NotifyParentAsync(WorkflowRun run)
    {
        if (await conversations.GetAsync(run.ParentId) is not { } parent) return;
        var body = run.Status switch
        {
            "completed" => run.Result ?? "",
            "stopped" => "ユーザーが止めました",
            _ => run.Error ?? "失敗しました",
        };
        // 長すぎると親の会話を圧迫する
        if (body.Length > 60_000) body = body[..60_000] + "\n... (長いので省略)";
        // リサーチはすぐ親がまとめて答えるので、その知らせだけで足りる
        if (run.Status != "stopped" && run.Name != "research")
        {
            var verb = run.Status == "completed" ? "終わりました" : "失敗しました";
            hub.Alert?.Invoke(parent, $"ワークフロー「{run.Title}」が{verb}: {ChatHub.Digest(body, run.CostUsd)}", false);
        }
        await hub.DeliverAsync(parent, $"<task-notification workflow=\"{run.Title.Replace("\"", "'")}\" id=\"{run.Id}\" status=\"{run.Status}\" agents=\"{run.AgentCount}\">\n{body}\n</task-notification>", run.Hops);
    }
}
