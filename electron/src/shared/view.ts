import type Anthropic from "@anthropic-ai/sdk";
import type { ChatMessage, ViewBlock } from "./types";

type AnyBlock = Anthropic.Beta.BetaContentBlock | Anthropic.Beta.BetaContentBlockParam;

export function toViewBlock(block: AnyBlock): ViewBlock | null {
  switch (block.type) {
    case "text":
      return { kind: "text", text: block.text };
    case "thinking":
      return { kind: "thinking", text: block.thinking };
    case "server_tool_use": {
      if (block.name !== "web_search") return null;
      const input = block.input as { query?: string } | undefined;
      return { kind: "search", query: input?.query ?? "" };
    }
    case "web_search_tool_result": {
      if (!Array.isArray(block.content)) {
        return { kind: "search-result", hits: [], error: block.content.error_code };
      }
      return { kind: "search-result", hits: block.content.map((r) => ({ title: r.title, url: r.url })) };
    }
    case "fallback":
      return { kind: "notice", text: `${block.from.model} が応答を断ったため ${block.to.model} が続きを書きました` };
    case "image":
      if (block.source.type !== "base64") return null;
      return { kind: "image", src: `data:${block.source.media_type};base64,${block.source.data}` };
    default:
      return null;
  }
}

export function contentToViewBlocks(content: ChatMessage["content"]): ViewBlock[] {
  if (typeof content === "string") return [{ kind: "text", text: content }];
  const blocks: ViewBlock[] = [];
  for (const block of content) {
    const view = toViewBlock(block);
    if (view) pushViewBlock(blocks, view);
  }
  return blocks;
}

// Web検索ありの回答は引用ごとに text ブロックが細切れになる。そのまま描くと段落の途中で改行されるので繋げる
export function pushViewBlock(blocks: ViewBlock[], view: ViewBlock): number {
  const last = blocks.at(-1);
  if (view.kind === "text" && last?.kind === "text") {
    last.text += view.text;
    return blocks.length - 1;
  }
  blocks.push(view);
  return blocks.length - 1;
}
