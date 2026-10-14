import { ChevronRight, Copy, Globe, LoaderCircle, Pencil, Plug, RotateCcw, TriangleAlert } from "lucide-react";
import { memo, useEffect, useMemo, useState, type ReactElement } from "react";
import { approve, edit, retry, textOf, toast } from "../actions";
import { renderMarkdown } from "../markdown";
import { useStore } from "../store";
import type { Approval, LiveReply, WireBlock, WireMessage } from "../types";
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

function ApprovalCard({ conversationId, approval }: { conversationId: string; approval: Approval }) {
  return (
    <div className="approval">
      <div className="approval-title">
        <Plug size={16} />
        <span><b>{approval.source}</b> の <b>{approval.title || approval.tool}</b> を使おうとしています</span>
      </div>
      {approval.description && <div className="approval-desc">{approval.description}</div>}
      <pre className="approval-input">{JSON.stringify(approval.input, null, 2)}</pre>
      <div className="approval-actions">
        <button type="button" className="button primary" onClick={() => approve(conversationId, approval.requestId, "always")}>常に許可</button>
        <button type="button" className="button" onClick={() => approve(conversationId, approval.requestId, "once")}>今回だけ許可</button>
        <button type="button" className="button ghost" onClick={() => approve(conversationId, approval.requestId, "deny")}>拒否</button>
      </div>
    </div>
  );
}

function ToolUseBlock({ block, result, conversationId, live }: { block: WireBlock; result?: WireBlock; conversationId: string; live: LiveReply | null }) {
  const [open, setOpen] = useState(false);
  const approval = live?.approvals.find((a) => a.toolUseId === block.id);
  const running = live?.runningTools.includes(block.id) ?? false;
  const [source, ...rest] = String(block.name).split("__");
  const toolName = rest.join("__") || source;
  const status = approval ? "承認待ち" : running ? "実行中" : result ? (result.is_error ? "エラー" : "完了") : live ? "準備中" : "";
  return (
    <div className="tool-card">
      <button type="button" className="tool-card-header" onClick={() => setOpen((o) => !o)}>
        {running || (live && !result && !approval) ? <LoaderCircle size={15} className="spin" /> : result?.is_error ? <TriangleAlert size={15} /> : <Plug size={15} />}
        <span className="tool-card-title">{toolName}</span>
        <span className="tool-card-sub">{rest.length ? source : ""}</span>
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
              <pre className={result.is_error ? "error" : ""}>{resultText(result)}</pre>
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
  for (const block of turn.blocks) if (block.type === "web_search_tool_result") searchResults.set(block.tool_use_id, block);

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
        }
        break;
      case "tool_use":
        elements.push(<ToolUseBlock key={i} block={block} result={turn.results[block.id]} conversationId={conversationId} live={live} />);
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
  const text = textOf(turn.content);
  const [draft, setDraft] = useState(text);
  const images = Array.isArray(turn.content) ? turn.content.filter((b) => b.type === "image") : [];

  if (editing) {
    const save = () => {
      setEditing(false);
      const content = images.length ? [...images, { type: "text", text: draft }] : draft;
      void edit(conversationId, turn.index, content);
    };
    return (
      <div className="user-turn editing">
        <textarea className="input" value={draft} autoFocus rows={Math.min(10, draft.split("\n").length + 1)} onChange={(e) => setDraft(e.target.value)} />
        <div className="edit-actions">
          <button type="button" className="button ghost" onClick={() => setEditing(false)}>キャンセル</button>
          <button type="button" className="button primary" disabled={!draft.trim()} onClick={save}>保存して送信</button>
        </div>
      </div>
    );
  }

  return (
    <div className="user-turn">
      <div className="user-bubble">
        {images.map((img, i) => <img key={i} src={`data:${img.source.media_type};base64,${img.source.data}`} alt="" />)}
        {text && <div className="user-text">{text}</div>}
      </div>
      <div className="user-actions">
        <button type="button" className="icon-button small" title="コピー" onClick={() => void copyText(text)}><Copy size={14} /></button>
        {!running && <button type="button" className="icon-button small" title="編集" onClick={() => { setDraft(text); setEditing(true); }}><Pencil size={14} /></button>}
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
