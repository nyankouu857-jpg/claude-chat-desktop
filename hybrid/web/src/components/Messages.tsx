import { Bell, Bot, CalendarClock, ChevronRight, Code, Copy, Download, ExternalLink, FileOutput, FolderOpen, Globe, History, Inbox, Link, ListTree, LoaderCircle, MessageSquare, PanelsTopLeft, Pencil, Plug, RotateCcw, Square, TerminalSquare, TriangleAlert, Webhook, Workflow, X } from "lucide-react";
import { memo, useEffect, useMemo, useState, type ReactElement, type ReactNode } from "react";
import { approve, edit, navigate, openArtifact, openFile, retry, stopWorkflow, textOf, toast } from "../actions";
import { findBuiltin } from "../builtins";
import { renderMarkdown } from "../markdown";
import { setState, useStore } from "../store";
import type { Approval, LiveReply, WireBlock, WireMessage } from "../types";
import { FileChip } from "./Composer";
import { STATUS_LABELS, StatusDot } from "./Sidebar";
import { Spark } from "./ui";

export type Turn =
  | { kind: "human"; index: number; content: WireMessage["content"] }
  | { kind: "assistant"; blocks: WireBlock[]; results: Record<string, WireBlock>; live: LiveReply | null };

const isToolResults = (m: WireMessage) =>
  m.role === "user" && Array.isArray(m.content) && m.content.length > 0 && m.content.every((b) => b.type === "tool_result");

export function buildTurns(messages: WireMessage[], live: LiveReply | undefined): Turn[] {
  const turns: Turn[] = [];
  let group: Extract<Turn, { kind: "assistant" }> | null = null;
  messages.forEach((message, index) => {
    if (message.role === "user" && !isToolResults(message)) {
      group = null;
      turns.push({ kind: "human", index, content: message.content });
      return;
    }
    if (!group) {
      group = { kind: "assistant", blocks: [], results: {}, live: null };
      turns.push(group);
    }
    if (message.role === "assistant") {
      group.blocks.push(...(typeof message.content === "string" ? [{ type: "text", text: message.content }] : message.content));
    } else if (Array.isArray(message.content)) {
      for (const block of message.content) group.results[block.tool_use_id] = block;
    }
  });
  if (live) {
    if (!group) {
      group = { kind: "assistant", blocks: [], results: {}, live: null };
      turns.push(group);
    }
    const current: Extract<Turn, { kind: "assistant" }> = group;
    current.blocks = [...current.blocks, ...live.blocks.filter(Boolean)];
    current.results = { ...current.results, ...live.toolResults };
    current.live = live;
  }
  return turns;
}

async function copyText(text: string) {
  await navigator.clipboard.writeText(text);
  toast("コピーしました");
}

export const Markdown = memo(function Markdown({ text }: { text: string }) {
  const html = useMemo(() => renderMarkdown(text), [text]);
  return (
    <div
      className="markdown"
      dangerouslySetInnerHTML={{ __html: html }}
      onClick={(e) => {
        const target = e.target as HTMLElement;
        if (target.closest("[data-copy]")) {
          const code = target.closest(".code-block")?.querySelector("code");
          if (code) void copyText(code.textContent ?? "");
          return;
        }
        const link = target.closest("a");
        if (link && /^https?:/.test(link.href)) {
          e.preventDefault();
          // WebView2 側で既定のブラウザに回す
          window.open(link.href, "_blank");
        }
      }}
    />
  );
});

// ほかのチャットやアプリから届いた発言の印。中身はタグで包まれている
const PEER_TAGS = ["message", "task", "task-notification", "idle-notification", "scheduled-task", "hook-feedback", "compact-summary", "command"];
const PEER_PATTERN = new RegExp(`^<(${PEER_TAGS.join("|")})((?:\\s+[\\w-]+="[^"]*")*)\\s*>\\n?([\\s\\S]*?)\\n?</\\1>\\s*$`);

export interface PeerMessage {
  tag: string;
  attrs: Record<string, string>;
  body: string;
}

export function parsePeer(text: string): PeerMessage | null {
  const match = PEER_PATTERN.exec(text.trim());
  if (!match) return null;
  const attrs: Record<string, string> = {};
  for (const [, key, value] of match[2].matchAll(/([\w-]+)="([^"]*)"/g)) attrs[key] = value;
  return { tag: match[1], attrs, body: match[3] };
}

function peerHeader(peer: PeerMessage): { icon: ReactNode; title: string } {
  const a = peer.attrs;
  switch (peer.tag) {
    case "message": return { icon: <MessageSquare size={15} />, title: `「${a.from_chat ?? "ほかのチャット"}」からのメッセージ` };
    case "task": return { icon: <Bot size={15} />, title: `「${a.from_chat ?? "親チャット"}」から任された作業` };
    case "task-notification": {
      const status = a.status === "completed" ? "終わりました" : a.status === "stopped" ? "止まりました" : "失敗しました";
      return { icon: a.workflow ? <Workflow size={15} /> : <Bot size={15} />, title: `${a.workflow ? "ワークフロー" : "サブエージェント"}「${a.workflow ?? a.agent ?? ""}」が${status}` };
    }
    case "idle-notification": return { icon: <Bell size={15} />, title: `「${a.chat ?? ""}」が待機中になりました` };
    case "scheduled-task": return { icon: <CalendarClock size={15} />, title: `予定「${a.title ?? ""}」` };
    case "hook-feedback": return { icon: <Webhook size={15} />, title: `${a.event ?? ""} フックからの補足` };
    case "compact-summary": return { icon: <ListTree size={15} />, title: "ここより前の会話は要約して続けています" };
    case "command": return { icon: <TerminalSquare size={15} />, title: a.name ?? "コマンド" };
    default: return { icon: <Inbox size={15} />, title: peer.tag };
  }
}

function PeerCard({ peer }: { peer: PeerMessage }) {
  // 作業の依頼と予定は読めないと困るので開いておく
  const [open, setOpen] = useState(peer.tag === "task" || peer.tag === "message" || peer.tag === "scheduled-task");
  const { icon, title } = peerHeader(peer);
  const chatId = peer.attrs.chat_id;
  return (
    <div className={`peer-card peer-${peer.tag}`}>
      <button type="button" className="peer-header" onClick={() => setOpen((o) => !o)}>
        {icon}
        <span className="peer-title">{title}</span>
        {chatId && (
          <span className="link-button small" role="link" onClick={(e) => { e.stopPropagation(); navigate({ name: "chat", id: chatId }); }}>
            開く <ExternalLink size={12} />
          </span>
        )}
        <ChevronRight size={14} className={`chevron${open ? " open" : ""}`} />
      </button>
      {open && peer.body.trim() && <div className="peer-body"><Markdown text={peer.body} /></div>}
    </div>
  );
}

function useElapsed(since: number | undefined, running: boolean) {
  const [now, setNow] = useState(Date.now());
  useEffect(() => {
    if (!running) return;
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, [running]);
  return since ? Math.max(0, Math.round((now - since) / 1000)) : 0;
}

function ThinkingBlock({ block, active }: { block: WireBlock; active: boolean }) {
  const [open, setOpen] = useState(false);
  const elapsed = useElapsed(block._startedAt, active);
  const finishedSeconds = block._startedAt && block._stoppedAt ? Math.round((block._stoppedAt - block._startedAt) / 1000) : null;
  const label = active
    ? `考えています... ${elapsed}秒`
    : finishedSeconds !== null ? `${finishedSeconds}秒間 考えました` : "思考プロセス";
  return (
    <div className={`thinking${open ? " open" : ""}`}>
      <button type="button" className="thinking-toggle" onClick={() => setOpen((o) => !o)}>
        {active && <LoaderCircle size={14} className="spin" />}
        <span>{label}</span>
        <ChevronRight size={14} className="chevron" />
      </button>
      {open && <div className="thinking-body">{block.thinking || (active ? "..." : "思考の要約はありません")}</div>}
    </div>
  );
}

function CompactionBlock({ block, active }: { block: WireBlock; active: boolean }) {
  const [open, setOpen] = useState(false);
  // 要約の指示で <summary> で囲ませているので、表示では外す
  const summary = typeof block.content === "string" ? block.content.replace(/<\/?summary>/g, "").trim() : "";
  // 要約は最後に一度に届くので、届くまでは空
  const pending = active && !summary;
  const label = pending ? "会話が長くなったので要約しています..." : summary ? "ここまでの会話を要約しました" : "会話の要約に失敗しました";
  return (
    <div className={`thinking compaction${open ? " open" : ""}`}>
      <button type="button" className="thinking-toggle" onClick={() => setOpen((o) => !o)} disabled={!summary}>
        {pending && <LoaderCircle size={14} className="spin" />}
        <span>{label}</span>
        {summary && <ChevronRight size={14} className="chevron" />}
      </button>
      {open && summary && <div className="thinking-body">{summary}</div>}
    </div>
  );
}

function WebSearchBlock({ query, result, active }: { query: string; result?: WireBlock; active: boolean }) {
  const [open, setOpen] = useState(false);
  const hits: { title: string; url: string }[] = Array.isArray(result?.content) ? result!.content : [];
  const failed = result && !Array.isArray(result.content);
  return (
    <div className="tool-card">
      <button type="button" className="tool-card-header" onClick={() => setOpen((o) => !o)} disabled={!result}>
        {active && !result ? <LoaderCircle size={15} className="spin" /> : <Globe size={15} />}
        <span className="tool-card-title">{result ? "Web を検索しました" : "Web を検索しています"}</span>
        {query && <span className="tool-card-sub">{query}</span>}
        {failed && <span className="tool-badge error">失敗</span>}
        {hits.length > 0 && <span className="tool-card-count">{hits.length} 件</span>}
        {result && <ChevronRight size={14} className={`chevron${open ? " open" : ""}`} />}
      </button>
      {open && hits.length > 0 && (
        <ul className="search-hits">
          {hits.map((hit, i) => {
            let host = "";
            try { host = new URL(hit.url).hostname.replace(/^www\./, ""); } catch { /* 表示用なので壊れた URL は無視 */ }
            return (
              <li key={i}>
                <a href={hit.url} onClick={(e) => { e.preventDefault(); window.open(hit.url, "_blank"); }}>
                  <span className="hit-title">{hit.title || hit.url}</span>
                  <span className="hit-host">{host}</span>
                </a>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}

function resultText(result: WireBlock | undefined) {
  if (!result) return "";
  if (typeof result.content === "string") return result.content;
  return (result.content ?? []).map((c: WireBlock) => (c.type === "text" ? c.text : `[${c.type}]`)).join("\n");
}

function ApprovalInput({ approval }: { approval: Approval }) {
  const input = (approval.input ?? {}) as Record<string, any>;
  if (approval.apiName === "plan__exit_plan_mode" && typeof input.plan === "string") {
    return <div className="plan-body"><Markdown text={input.plan} /></div>;
  }
  // コマンドは JSON のままだと読みにくいので、そのまま見せる
  if (approval.source === "terminal" && typeof input.command === "string") {
    return (
      <>
        <pre className="approval-input command">{input.command}</pre>
        {input.working_directory && <div className="approval-desc">場所: {input.working_directory}</div>}
      </>
    );
  }
  return <pre className="approval-input">{JSON.stringify(approval.input, null, 2)}</pre>;
}

function ApprovalCard({ conversationId, approval }: { conversationId: string; approval: Approval }) {
  const builtin = findBuiltin(approval.source);
  const name = builtin?.titles[approval.tool] ?? approval.title ?? approval.tool;
  if (approval.apiName === "plan__exit_plan_mode") {
    return (
      <div className="approval plan-approval">
        <div className="approval-title"><ListTree size={16} /><span>Claude が計画を立てました。この計画で進めますか?</span></div>
        <ApprovalInput approval={approval} />
        <div className="approval-actions">
          <button type="button" className="button primary" onClick={() => approve(conversationId, approval.requestId, "once")}>この計画で進める</button>
          <button type="button" className="button ghost" onClick={() => approve(conversationId, approval.requestId, "deny")}>計画を直してもらう</button>
        </div>
        <div className="approval-note">進めると計画モードが終わり、Claude が変更を始めます</div>
      </div>
    );
  }
  return (
    <div className="approval">
      <div className="approval-title">
        {builtin ? <builtin.icon size={16} /> : <Plug size={16} />}
        <span>
          {builtin
            ? <>Claude が <b>{name}</b> を実行しようとしています</>
            : <><b>{approval.source}</b> の <b>{name}</b> を使おうとしています</>}
        </span>
      </div>
      {!builtin && approval.description && <div className="approval-desc">{approval.description}</div>}
      <ApprovalInput approval={approval} />
      <div className="approval-actions">
        {approval.allowAlways !== false && <button type="button" className="button primary" onClick={() => approve(conversationId, approval.requestId, "always")}>常に許可</button>}
        <button type="button" className={`button${approval.allowAlways === false ? " primary" : ""}`} onClick={() => approve(conversationId, approval.requestId, "once")}>{approval.allowAlways === false ? "許可" : "今回だけ許可"}</button>
        <button type="button" className="button ghost" onClick={() => approve(conversationId, approval.requestId, "deny")}>拒否</button>
      </div>
      {approval.allowAlways === false && <div className="approval-note">内容によって何でもできる操作なので、毎回確認します</div>}
    </div>
  );
}

function resultImages(result: WireBlock | undefined): WireBlock[] {
  return Array.isArray(result?.content) ? result!.content.filter((c: WireBlock) => c.type === "image") : [];
}

const CHAT_ID = /chat_id ([0-9a-f-]{36})/;
const WORKFLOW_ID = /id (wf_[0-9a-f]+)/;
const ARTIFACT_ID = /id (art_[0-9a-f]+)/;

function AgentCard({ block, result, conversationId }: { block: WireBlock; result?: WireBlock; conversationId: string }) {
  const title = block.input?.title ?? "サブエージェント";
  // 結果が届く前は、同じ名前の一番新しい子を探す
  const childId = useStore((s) => CHAT_ID.exec(resultText(result))?.[1]
    ?? s.summaries.find((c) => c.parentId === conversationId && c.title === title)?.id);
  const status = useStore((s) => (childId ? s.statuses[childId] : undefined));
  return (
    <div className="tool-card agent-card">
      <div className="tool-card-header static">
        {status === "running" ? <LoaderCircle size={15} className="spin" /> : <Bot size={15} />}
        <span className="tool-card-title">サブエージェント「{title}」</span>
        {block.input?.background && <span className="tool-badge">バックグラウンド</span>}
        <StatusDot status={status} />
        {status && <span className="tool-card-sub">{STATUS_LABELS[status]}</span>}
        {childId && <button type="button" className="link-button small" onClick={() => navigate({ name: "chat", id: childId })}>チャットを開く <ExternalLink size={12} /></button>}
      </div>
    </div>
  );
}

function WorkflowCard({ block, result }: { block: WireBlock; result?: WireBlock }) {
  const id = WORKFLOW_ID.exec(resultText(result))?.[1];
  const run = useStore((s) => (id ? s.workflows[id] : undefined));
  const [open, setOpen] = useState(true);
  const title = run?.title ?? block.input?.title ?? "ワークフロー";
  const done = run?.agents.filter((a) => a.status === "done").length ?? 0;
  return (
    <div className="tool-card workflow-card">
      <button type="button" className="tool-card-header" onClick={() => setOpen((o) => !o)}>
        {run?.status === "running" ? <LoaderCircle size={15} className="spin" /> : <Workflow size={15} />}
        <span className="tool-card-title">{title}</span>
        {run && <span className="tool-card-sub">{run.phase ?? ""} {run.agents.length > 0 ? `エージェント ${done}/${run.agents.length}` : ""}</span>}
        {run && <span className={`tool-badge${run.status === "failed" ? " error" : ""}`}>{{ running: "実行中", completed: "完了", failed: "失敗", stopped: "停止" }[run.status]}</span>}
        <ChevronRight size={14} className={`chevron${open ? " open" : ""}`} />
      </button>
      {open && run && (
        <div className="tool-card-body">
          {run.agents.length > 0 && (
            <ul className="workflow-agents">
              {run.agents.map((a) => (
                <li key={a.chatId}>
                  <span className={`agent-state ${a.status}`} />
                  <span className="agent-label">{a.label}</span>
                  <span className="agent-phase">{a.phase}</span>
                  {!a.chatId.startsWith("pending_") && <button type="button" className="link-button small" onClick={() => navigate({ name: "chat", id: a.chatId })}>開く</button>}
                </li>
              ))}
            </ul>
          )}
          {run.logs.length > 0 && <pre className="workflow-logs">{run.logs.slice(-20).join("\n")}</pre>}
          {run.error && <pre className="error">{run.error}</pre>}
          {run.status === "running" && <button type="button" className="button small" onClick={() => void stopWorkflow(run.id)}><Square size={12} /> 止める</button>}
        </div>
      )}
    </div>
  );
}

function ArtifactCard({ block, result, conversationId }: { block: WireBlock; result?: WireBlock; conversationId: string }) {
  const id = ARTIFACT_ID.exec(resultText(result))?.[1] ?? block.input?.id;
  const artifact = useStore((s) => s.conversations[conversationId]?.artifacts?.find((a) => a.id === id));
  const title = artifact?.title ?? block.input?.title ?? "アーティファクト";
  return (
    <button type="button" className="artifact-card" disabled={!artifact} onClick={() => artifact && openArtifact(conversationId, artifact.id)}>
      <PanelsTopLeft size={20} />
      <span className="artifact-card-text">
        <span className="artifact-card-title">{title}</span>
        <span className="artifact-card-sub">{artifact ? `${artifact.type} ・ 版 ${artifact.version}` : result?.is_error ? "失敗しました" : "作っています..."}</span>
      </span>
      {artifact && <span className="artifact-open">開く</span>}
    </button>
  );
}

// コード実行で作られたファイル。手元に保存できたものは開ける
function FileOutputs({ result, conversationId }: { result?: WireBlock; conversationId: string }) {
  const files = useStore((s) => s.conversations[conversationId]?.files);
  const ids = useMemo(() => {
    const found: string[] = [];
    const walk = (node: unknown) => {
      if (Array.isArray(node)) node.forEach(walk);
      else if (node && typeof node === "object") {
        const obj = node as Record<string, unknown>;
        if (typeof obj.file_id === "string") found.push(obj.file_id);
        Object.values(obj).forEach(walk);
      }
    };
    walk(result);
    return [...new Set(found)];
  }, [result]);
  if (ids.length === 0) return null;
  return (
    <div className="file-outputs">
      {ids.map((id) => {
        const path = files?.[id];
        const name = path ? path.split(/[\\/]/).pop() : id;
        return (
          <span key={id} className="file-output">
            <FileOutput size={15} />
            <span className="file-output-name">{name}</span>
            {path ? (
              <>
                <button type="button" className="icon-button tiny" title="開く" onClick={() => void openFile(conversationId, id)}><Download size={13} /></button>
                <button type="button" className="icon-button tiny" title="フォルダを開く" onClick={() => void openFile(conversationId, id, true)}><FolderOpen size={13} /></button>
              </>
            ) : <span className="field-hint">保存中...</span>}
          </span>
        );
      })}
    </div>
  );
}

const SERVER_TOOL_TITLES: Record<string, string> = {
  web_fetch: "Web ページを読みました",
  code_execution: "コードを実行しました",
  bash_code_execution: "コマンドを実行しました",
  text_editor_code_execution: "ファイルを編集しました",
};

function serverToolText(result: WireBlock | undefined) {
  const content = result?.content;
  if (!content) return "";
  if (typeof content === "string") return content;
  if (Array.isArray(content)) return content.map((c: WireBlock) => c.text ?? "").join("\n");
  const parts = [content.stdout, content.stderr, content.content?.source?.data?.slice?.(0, 4000), content.error_code && `エラー: ${content.error_code}`];
  return parts.filter(Boolean).join("\n");
}

function ServerToolBlock({ block, result, conversationId, active }: { block: WireBlock; result?: WireBlock; conversationId: string; active: boolean }) {
  const [open, setOpen] = useState(false);
  const input = block.input ?? {};
  const isFetch = block.name === "web_fetch";
  const subject = input.url ?? input.command ?? input.path ?? (typeof input.code === "string" ? input.code.split("\n")[0] : "");
  const failed = result?.content?.type?.endsWith?.("_error") || result?.content?.error_code;
  const title = result ? SERVER_TOOL_TITLES[block.name] ?? block.name : isFetch ? "Web ページを読んでいます" : "コードを実行しています";
  return (
    <div className="tool-card">
      <button type="button" className="tool-card-header" onClick={() => setOpen((o) => !o)} disabled={!result}>
        {active && !result ? <LoaderCircle size={15} className="spin" /> : isFetch ? <Link size={15} /> : <Code size={15} />}
        <span className="tool-card-title">{title}</span>
        {subject && <span className="tool-card-sub">{String(subject).slice(0, 100)}</span>}
        {failed && <span className="tool-badge error">失敗</span>}
        {result && <ChevronRight size={14} className={`chevron${open ? " open" : ""}`} />}
      </button>
      {open && (
        <div className="tool-card-body">
          {!isFetch && <><div className="tool-section-label">入力</div><pre>{input.code ?? input.command ?? JSON.stringify(input, null, 2)}</pre></>}
          {result && <><div className="tool-section-label">結果</div><pre>{serverToolText(result) || "(出力なし)"}</pre></>}
        </div>
      )}
      <FileOutputs result={result} conversationId={conversationId} />
    </div>
  );
}

function ToolUseBlock({ block, result, conversationId, live }: { block: WireBlock; result?: WireBlock; conversationId: string; live: LiveReply | null }) {
  const approval = live?.approvals.find((a) => a.toolUseId === block.id);
  if (block.name === "agents__spawn_agent" && !approval) return <AgentCard block={block} result={result} conversationId={conversationId} />;
  if (block.name === "agents__run_workflow" && result && !result.is_error) return <WorkflowCard block={block} result={result} />;
  if ((block.name === "artifacts__create" || block.name === "artifacts__update") && !result?.is_error) return <ArtifactCard block={block} result={result} conversationId={conversationId} />;
  return <GenericToolBlock block={block} result={result} conversationId={conversationId} live={live} />;
}

function GenericToolBlock({ block, result, conversationId, live }: { block: WireBlock; result?: WireBlock; conversationId: string; live: LiveReply | null }) {
  const [open, setOpen] = useState(false);
  const approval = live?.approvals.find((a) => a.toolUseId === block.id);
  const running = live?.runningTools.includes(block.id) ?? false;
  const [source, ...rest] = String(block.name).split("__");
  const tool = rest.join("__");
  const builtin = findBuiltin(source);
  const toolName = (builtin ? builtin.titles[tool] : undefined) ?? (tool || source);
  const subject = builtin ? builtin.subject(tool, block.input ?? {}) : rest.length ? source : "";
  const status = approval ? "承認待ち" : running ? "実行中" : result ? (result.is_error ? "エラー" : "完了") : live ? "準備中" : "";
  const Icon = builtin?.icon ?? Plug;
  const images = resultImages(result);
  return (
    <div className="tool-card">
      <button type="button" className="tool-card-header" onClick={() => setOpen((o) => !o)}>
        {running || (live && !result && !approval) ? <LoaderCircle size={15} className="spin" /> : result?.is_error ? <TriangleAlert size={15} /> : <Icon size={15} />}
        <span className="tool-card-title">{toolName}</span>
        <span className="tool-card-sub">{subject}</span>
        {status && <span className={`tool-badge${result?.is_error ? " error" : approval ? " waiting" : ""}`}>{status}</span>}
        <ChevronRight size={14} className={`chevron${open ? " open" : ""}`} />
      </button>
      {open && (
        <div className="tool-card-body">
          <div className="tool-section-label">入力</div>
          <pre>{JSON.stringify(block.input ?? {}, null, 2)}</pre>
          {result && (
            <>
              <div className="tool-section-label">結果</div>
              {images.map((img, i) => <img key={i} className="tool-image" src={`data:${img.source.media_type};base64,${img.source.data}`} alt="" />)}
              {(images.length === 0 || resultText(result).replace(/\[image\]/g, "").trim()) && <pre className={result.is_error ? "error" : ""}>{resultText(result)}</pre>}
            </>
          )}
        </div>
      )}
      {approval && <ApprovalCard conversationId={conversationId} approval={approval} />}
    </div>
  );
}

// useStore の戻り値が毎回別の配列だと再描画が止まらなくなるので、空は使い回す
const NO_NOTICES: string[] = [];

function AssistantTurn({ turn, conversationId, isLast }: { turn: Extract<Turn, { kind: "assistant" }>; conversationId: string; isLast: boolean }) {
  const showThinking = useStore((s) => s.settings?.chat.showThinking ?? true);
  const notices: string[] = useStore((s) => (isLast ? s.live[conversationId]?.notices ?? (s.conversations[conversationId] as any)?._notices ?? NO_NOTICES : NO_NOTICES));
  const live = turn.live;
  const searchResults = new Map<string, WireBlock>();
  const serverResults = new Map<string, WireBlock>();
  for (const block of turn.blocks) {
    if (block.type === "web_search_tool_result") searchResults.set(block.tool_use_id, block);
    else if (typeof block.type === "string" && block.type.endsWith("_tool_result") && block.tool_use_id) serverResults.set(block.tool_use_id, block);
  }

  const elements: ReactElement[] = [];
  let textBuffer = "";
  const flushText = (key: string) => {
    if (textBuffer) elements.push(<Markdown key={key} text={textBuffer} />);
    textBuffer = "";
  };
  const lastIndex = turn.blocks.length - 1;
  turn.blocks.forEach((block, i) => {
    if (block.type === "text") {
      textBuffer += block.text ?? "";
      return;
    }
    flushText(`text-${i}`);
    const active = !!live && i === lastIndex;
    switch (block.type) {
      case "thinking":
        if (showThinking || active) elements.push(<ThinkingBlock key={i} block={block} active={active} />);
        break;
      case "server_tool_use":
        if (block.name === "web_search") {
          elements.push(<WebSearchBlock key={i} query={block.input?.query ?? ""} result={searchResults.get(block.id)} active={!!live} />);
        } else {
          elements.push(<ServerToolBlock key={i} block={block} result={serverResults.get(block.id)} conversationId={conversationId} active={!!live} />);
        }
        break;
      case "tool_use":
        elements.push(<ToolUseBlock key={i} block={block} result={turn.results[block.id]} conversationId={conversationId} live={live} />);
        break;
      case "compaction":
        elements.push(<CompactionBlock key={i} block={block} active={active} />);
        break;
      case "fallback":
        elements.push(<div key={i} className="notice">{block.from?.model} が応答を断ったため {block.to?.model} が続きを書きました</div>);
        break;
    }
  });
  flushText("text-end");

  const fullText = turn.blocks.filter((b) => b.type === "text").map((b) => b.text).join("");
  return (
    <div className="assistant-turn">
      {elements}
      {notices.map((n: string, i: number) => <div key={`n${i}`} className="notice">{n}</div>)}
      <div className="assistant-footer">
        {/* claude.ai と同じく、マークは最新の回答の下にだけ出す */}
        {(isLast || live) && <span className={`footer-spark${live ? " live" : ""}`}><Spark size={live ? 22 : 18} spinning={!!live} /></span>}
        {!live && (
          <div className={`message-actions${isLast ? " visible" : ""}`}>
            <button type="button" className="icon-button small" title="コピー" onClick={() => void copyText(fullText)}><Copy size={15} /></button>
            {isLast && <button type="button" className="icon-button small" title="やり直す" onClick={() => void retry(conversationId)}><RotateCcw size={15} /></button>}
          </div>
        )}
        {isLast && !live && <span className="disclaimer">Claude は間違えることがあります。回答内容は必ずご確認ください。</span>}
      </div>
    </div>
  );
}

function UserTurn({ turn, conversationId, running }: { turn: Extract<Turn, { kind: "human" }>; conversationId: string; running: boolean }) {
  const [editing, setEditing] = useState(false);
  const textBlocks: string[] = typeof turn.content === "string" ? [turn.content] : turn.content.filter((b) => b.type === "text").map((b) => b.text ?? "");
  const peers = textBlocks.map(parsePeer);
  // フックの補足のように本文に添えられたものは、本文と分けて小さく出す
  const text = textBlocks.filter((_, i) => !peers[i]).join("\n");
  const [draft, setDraft] = useState(text);
  const images = Array.isArray(turn.content) ? turn.content.filter((b) => b.type === "image") : [];
  const documents = Array.isArray(turn.content) ? turn.content.filter((b) => b.type === "document") : [];
  // 大きすぎる添付で送れなくなった会話から抜け出せるよう、編集で外せる
  const [kept, setKept] = useState<WireBlock[]>([]);

  if (peers.length > 0 && peers.every(Boolean) && images.length === 0 && documents.length === 0) {
    return (
      <div className="peer-turn">
        {peers.map((peer, i) => <PeerCard key={i} peer={peer!} />)}
      </div>
    );
  }

  if (editing) {
    const save = () => {
      setEditing(false);
      // 空の text ブロックは API に拒否される
      const content = kept.length ? [...kept, ...(draft.trim() ? [{ type: "text", text: draft }] : [])] : draft;
      void edit(conversationId, turn.index, content);
    };
    return (
      <div className="user-turn editing">
        {kept.length > 0 && (
          <div className="attachments">
            {kept.map((b, i) => (
              <div className={`attachment${b.type === "image" ? "" : " file"}`} key={i}>
                {b.type === "image"
                  ? <img src={`data:${b.source.media_type};base64,${b.source.data}`} alt="" />
                  : <FileChip name={b.title || "添付ファイル"} kind={b.source?.media_type === "application/pdf" ? "pdf" : "text"} />}
                <button type="button" aria-label="添付を外す" onClick={() => setKept((list) => list.filter((_, j) => j !== i))}><X size={12} /></button>
              </div>
            ))}
          </div>
        )}
        <textarea className="input" value={draft} autoFocus rows={Math.min(10, draft.split("\n").length + 1)} onChange={(e) => setDraft(e.target.value)} />
        <div className="edit-actions">
          <button type="button" className="button ghost" onClick={() => setEditing(false)}>キャンセル</button>
          <button type="button" className="button primary" disabled={!draft.trim() && kept.length === 0} onClick={save}>保存して送信</button>
        </div>
      </div>
    );
  }

  return (
    <div className="user-turn">
      <div className="user-bubble">
        {documents.map((doc, i) => <FileChip key={`d${i}`} name={doc.title || "添付ファイル"} kind={doc.source?.media_type === "application/pdf" ? "pdf" : "text"} />)}
        {images.map((img, i) => <img key={i} src={`data:${img.source.media_type};base64,${img.source.data}`} alt="" />)}
        {text && <div className="user-text">{text}</div>}
      </div>
      {peers.some(Boolean) && <div className="peer-turn attached">{peers.filter(Boolean).map((peer, i) => <PeerCard key={i} peer={peer!} />)}</div>}
      <div className="user-actions">
        <button type="button" className="icon-button small" title="コピー" onClick={() => void copyText(text)}><Copy size={14} /></button>
        {!running && <button type="button" className="icon-button small" title="編集" onClick={() => { setDraft(text); setKept([...documents, ...images]); setEditing(true); }}><Pencil size={14} /></button>}
        {!running && <button type="button" className="icon-button small" title="ここまで巻き戻す" onClick={() => setState((s) => ({ ...s, dialog: { kind: "rewind", conversationId, messageIndex: turn.index } }))}><History size={14} /></button>}
      </div>
    </div>
  );
}

export function MessageList({ conversationId }: { conversationId: string }) {
  const conversation = useStore((s) => s.conversations[conversationId]);
  const live = useStore((s) => s.live[conversationId]);
  const error = useStore((s) => s.errors[conversationId]);
  const turns = useMemo(() => buildTurns(conversation?.messages ?? [], live), [conversation?.messages, live]);
  if (!conversation) return <div className="loading"><LoaderCircle className="spin" /></div>;
  const lastAssistant = turns.map((t) => t.kind).lastIndexOf("assistant");
  return (
    <div className="message-list">
      {turns.map((turn, i) =>
        turn.kind === "human"
          ? <UserTurn key={`h${turn.index}`} turn={turn} conversationId={conversationId} running={!!live} />
          : <AssistantTurn key={`a${i}`} turn={turn} conversationId={conversationId} isLast={i === lastAssistant} />,
      )}
      {error && (
        <div className="error-box">
          <TriangleAlert size={16} />
          <span>{error}</span>
          <button type="button" className="button small" onClick={() => void retry(conversationId)}><RotateCcw size={14} /> 再試行</button>
        </div>
      )}
    </div>
  );
}
