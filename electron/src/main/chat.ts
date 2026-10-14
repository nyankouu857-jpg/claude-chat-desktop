import Anthropic from "@anthropic-ai/sdk";
import { DEFAULT_TITLE, type Conversation, type Settings, type StreamEvent } from "../shared/types";
import { toViewBlock } from "../shared/view";

const TITLE_MODEL = "claude-haiku-5-5";
// pause_turn が延々続いたときの保険
const MAX_CONTINUATIONS = 5;

type BetaStreamParams = Anthropic.Beta.MessageCreateParamsStreaming;

function buildRequest(settings: Settings, conversation: Conversation): BetaStreamParams {
  const isHaiku = settings.model.startsWith("claude-haiku");
  const params: BetaStreamParams = {
    model: settings.model,
    max_tokens: 64000,
    stream: true,
    messages: conversation.messages,
    thinking: { type: "adaptive", display: "summarized" },
    output_config: { effort: settings.effort },
    cache_control: { type: "ephemeral" },
  };
  if (settings.systemPrompt.trim()) params.system = settings.systemPrompt;
  if (settings.webSearch) {
    // Haiku は動的フィルタ版の web_search に非対応
    params.tools = [
      isHaiku
        ? { type: "web_search_20250305", name: "web_search", max_uses: 5 }
        : { type: "web_search_20260209", name: "web_search", max_uses: 5 },
    ];
  }
  // Haiku 5.5 にはサーバー側フォールバックが無い
  if (!isHaiku) {
    params.betas = ["server-side-fallback-2026-07-01"];
    params.fallbacks = "default";
  }
  return params;
}

function describeError(error: unknown): string {
  if (error instanceof Anthropic.AuthenticationError) return "APIキーが無効です。設定を確認してください";
  if (error instanceof Anthropic.PermissionDeniedError) return "このモデルを使う権限がありません";
  if (error instanceof Anthropic.RateLimitError) return "レート制限に達しました。少し待ってから再送してください";
  if (error instanceof Anthropic.BadRequestError) return `リクエストが拒否されました: ${error.message}`;
  if (error instanceof Anthropic.APIConnectionError) return "API に接続できません。ネットワークを確認してください";
  if (error instanceof Anthropic.APIError) return `API エラー ${error.status ?? ""}: ${error.message}`;
  return error instanceof Error ? error.message : String(error);
}

export async function streamReply(
  client: Anthropic,
  settings: Settings,
  conversation: Conversation,
  signal: AbortSignal,
  emit: (event: StreamEvent) => void,
): Promise<"done" | "aborted" | "error"> {
  // pause_turn で続きを取りに行くと index が 0 から振り直されるのでずらす
  let indexBase = 0;
  const partialText = new Map<number, string>();

  try {
    for (let turn = 0; turn <= MAX_CONTINUATIONS; turn++) {
      const stream = client.beta.messages.stream(buildRequest(settings, conversation), { signal });
      const toolInputJson = new Map<number, string>();
      partialText.clear();

      for await (const event of stream) {
        if (event.type === "content_block_start") {
          const index = indexBase + event.index;
          const block = toViewBlock(event.content_block);
          if (block) emit({ type: "block", index, block });
          if (event.content_block.type === "text") partialText.set(index, "");
        } else if (event.type === "content_block_delta") {
          const index = indexBase + event.index;
          const delta = event.delta;
          if (delta.type === "text_delta") {
            partialText.set(index, (partialText.get(index) ?? "") + delta.text);
            emit({ type: "delta", index, text: delta.text });
          } else if (delta.type === "thinking_delta") {
            emit({ type: "delta", index, text: delta.thinking });
          } else if (delta.type === "input_json_delta") {
            toolInputJson.set(index, (toolInputJson.get(index) ?? "") + delta.partial_json);
          }
        } else if (event.type === "content_block_stop") {
          const json = toolInputJson.get(indexBase + event.index);
          if (json) {
            try {
              const query = (JSON.parse(json) as { query?: string }).query;
              if (query) emit({ type: "search-query", index: indexBase + event.index, query });
            } catch {
              // 表示用なので壊れていても無視
            }
          }
        }
      }

      const message = await stream.finalMessage();
      indexBase += message.content.length;
      // 思考ブロックは同じモデルに無加工で返す必要があるので content を丸ごと積む
      conversation.messages.push({ role: "assistant", content: message.content });
      conversation.usage.input += message.usage.input_tokens + (message.usage.cache_creation_input_tokens ?? 0);
      conversation.usage.output += message.usage.output_tokens;
      conversation.usage.cacheRead += message.usage.cache_read_input_tokens ?? 0;

      if (message.stop_reason === "pause_turn") continue;
      if (message.stop_reason === "refusal") {
        emit({ type: "block", index: indexBase, block: { kind: "notice", text: "Claude がこのリクエストへの応答を断りました" } });
      } else if (message.stop_reason === "max_tokens") {
        emit({ type: "block", index: indexBase, block: { kind: "notice", text: "出力上限に達したため途中で終わっています" } });
      }
      return "done";
    }
    return "done";
  } catch (error) {
    if (signal.aborted) {
      // 中断時は思考ブロックの署名が揃わないので、出せた本文だけ残す
      const text = [...partialText.values()].join("");
      if (text) conversation.messages.push({ role: "assistant", content: [{ type: "text", text }] });
      return "aborted";
    }
    emit({ type: "error", message: describeError(error), conversation });
    return "error";
  }
}

function firstText(content: Conversation["messages"][number]["content"]): string {
  if (typeof content === "string") return content;
  return content.flatMap((b) => (b.type === "text" ? [b.text] : [])).join("\n");
}

export async function generateTitle(client: Anthropic, conversation: Conversation): Promise<string | null> {
  const [user, assistant] = conversation.messages;
  if (!user || !assistant) return null;
  // タイトル用途なので長文は頭だけで十分
  const excerpt = (s: string) => s.slice(0, 2000);
  const response = await client.messages.create({
    model: TITLE_MODEL,
    max_tokens: 1024,
    output_config: { effort: "low" },
    system: "会話の内容を表す短い日本語タイトルを20文字以内で1つだけ出力してください。記号や引用符、前置きは付けないでください。",
    messages: [
      {
        role: "user",
        content: `ユーザー:\n${excerpt(firstText(user.content))}\n\nアシスタント:\n${excerpt(firstText(assistant.content))}`,
      },
    ],
  });
  if (response.stop_reason === "refusal") return null;
  const title = response.content.flatMap((b) => (b.type === "text" ? [b.text] : [])).join("").trim();
  return title && title !== DEFAULT_TITLE ? title.slice(0, 40) : null;
}
