import { Bell, Bot, CalendarClock, Check, Quote, ChevronLeft, ChevronRight, Code, Copy, Download, ExternalLink, FileOutput, FolderOpen, Globe, History, Inbox, Link, ListTree, LoaderCircle, MessageSquare, PanelsTopLeft, Pencil, Plug, RotateCcw, Square, TerminalSquare, TriangleAlert, Webhook, Workflow, X } from "lucide-react";
import { memo, useEffect, useMemo, useState, type ReactElement, type ReactNode } from "react";
import { answer, approve, edit, navigate, openArtifact, openFile, retry, setQuote, stopWorkflow, switchBranch, textOf } from "../actions";
import { MODELS } from "../models";
import { findBuiltin } from "../builtins";
import { renderMarkdown } from "../markdown";
import { setState, useStore } from "../store";
import type { Approval, LiveReply, WireBlock, WireMessage } from "../types";
import { FileChip } from "./Composer";
import { STATUS_LABELS, StatusDot } from "./Sidebar";
import { Dropdown, MenuItem, Spark } from "./ui";

export type Turn =
  | { kind: "human"; index: number; content: WireMessage["content"] }
  // start は返事の最初のメッセージの位置。やり直しで分かれた版はここに付く
  | { kind: "assistant"; start: number; human: number; blocks: WireBlock[]; results: Record<string, WireBlock>; live: LiveReply | null };

const isToolResults = (m: WireMessage) =>
  m.role === "user" && Array.isArray(m.content) && m.content.length > 0 && m.content.every((b) => b.type === "tool_result");

export function buildTurns(messages: WireMessage[], live: LiveReply | undefined): Turn[] {
  const turns: Turn[] = [];
  let group: Extract<Turn, { kind: "assistant" }> | null = null;
  let lastHuman = -1;
  messages.forEach((message, index) => {
    if (message.role === "user" && !isToolResults(message)) {
      group = null;
      lastHuman = index;
      turns.push({ kind: "human", index, content: message.content });
      return;
    }
    if (!group) {
      group = { kind: "assistant", start: index, human: lastHuman, blocks: [], results: {}, live: null };
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
      group = { kind: "assistant", start: messages.length, human: lastHuman, blocks: [], results: {}, live: null };
      turns.push(group);
    }
    const current: Extract<Turn, { kind: "assistant" }> = group;
    current.blocks = [...current.blocks, ...live.blocks.filter(Boolean)];
    current.results = { ...current.results, ...live.toolResults };
    current.live = live;
  }
  return turns;
}

export interface Cite {
  url?: string;
  title?: string;
  citedText?: string;
}

// 本文中の引用の位置の印。Markdown として解釈されない私用領域の文字で挟む
const citeMark = (n: number) => `\uE000${n}\uE001`;


export const ARTIFACT_TYPE_LABELS: Record<string, string> = { html: "HTML", react: "React", markdown: "文書", svg: "画像", mermaid: "図", code: "コード" };

function citeChip(cites: Cite[]) {
  const first = cites[0];
  const url = first.url && /^https?:\/\//i.test(first.url) ? first.url : null;
  let label = first.title ?? "出典";
  try { if (url) label = new URL(url).hostname.replace(/^www\./, ""); } catch { /* 表示用なので壊れた URL は題名で出す */ }
  const chip = document.createElement(url ? "a" : "span");
  chip.className = "cite-chip";
  if (url) chip.setAttribute("href", url);
  chip.title = cites.map((c) => [c.title, c.citedText && `「${c.citedText}」`].filter(Boolean).join(" ")).join("\n");
  chip.textContent = label + (cites.length > 1 ? ` +${cites.length - 1}` : "");
  return chip;
}

// 出典のチップは消毒の後に DOM として差し込む。文字列で差し込むと、属性の中に紛れた印からタグを抜け出せてしまう
function withCites(html: string, cites: Cite[][]) {
  const template = document.createElement("template");
  template.innerHTML = html;
  const walker = document.createTreeWalker(template.content, NodeFilter.SHOW_TEXT);
  const marked: Text[] = [];
  while (walker.nextNode()) if ((walker.currentNode as Text).data.includes("\uE000")) marked.push(walker.currentNode as Text);
  for (const node of marked) {
    const fragment = document.createDocumentFragment();
    node.data.split(/\uE000(\d+)\uE001/).forEach((part, i) => {
      if (i % 2 === 0) fragment.append(part);
      else if (cites[Number(part)]) fragment.append(citeChip(cites[Number(part)]));
    });
    node.replaceWith(fragment);
  }
  for (const element of template.content.querySelectorAll("*")) {
    for (const attribute of [...element.attributes]) {
      if (/[\uE000\uE001]/.test(attribute.value)) element.setAttribute(attribute.name, attribute.value.replace(/[\uE000\uE001]\d*/g, ""));
    }
  }
  return template.innerHTML;
}

// 同じ出典を同じ文の後ろに何度も並べない
function uniqueCites(citations: WireBlock[]): Cite[] {
  const seen = new Set<string>();
  const cites: Cite[] = [];
  for (const c of citations) {
    const key = c.url ?? c.document_title ?? c.title ?? "";
    if (seen.has(key)) continue;
    seen.add(key);
    cites.push({ url: c.url, title: c.title ?? c.document_title, citedText: c.cited_text });
  }
  return cites;
}

export const Markdown = memo(function Markdown({ text, cites }: { text: string; cites?: Cite[][] }) {
  const rendered = useMemo(() => {
    const result = renderMarkdown(text);
    return cites?.length ? { ...result, html: withCites(result.html, cites) } : result;
  }, [text, cites]);
  return (
    <div
      className="markdown"
      dangerouslySetInnerHTML={{ __html: rendered.html }}
      onClick={(e) => {
        const target = e.target as HTMLElement;
        const copyButton = target.closest<HTMLElement>("[data-copy]");
        if (copyButton) {
          const [owner, index] = (target.closest(".code-block")?.getAttribute("data-code") ?? "").split(/-(?=\d+$)/);
          if (owner !== rendered.nonce) return;
          void navigator.clipboard.writeText(rendered.codes[Number(index)] ?? "");
          copyButton.textContent = "コピーしました";
          // 押せたと分かる程度の時間
          setTimeout(() => { copyButton.textContent = "コピー"; }, 2000);
          return;
        }
        // SVG の a は href が文字列ではないので、属性で見る。どのリンクでも画面そのものは遷移させない
        const link = target.closest("a, area");
        if (link) {
          e.preventDefault();
          const href = link.getAttribute("href") ?? link.getAttribute("xlink:href") ?? "";
          // WebView2 側で既定のブラウザに回す
          if (/^https?:\/\//i.test(href)) window.open(href, "_blank", "noopener,noreferrer");
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

// Claude Code の作業中の表示。動詞はその都度ひとつ選ぶ
const SPINNER_VERBS = [
  "Accomplishing", "Actioning", "Actualizing", "Baking", "Brewing", "Calculating", "Cerebrating", "Churning", "Clauding", "Coalescing",
  "Cogitating", "Computing", "Conjuring", "Considering", "Cooking", "Crafting", "Creating", "Crunching", "Deliberating", "Determining",
  "Doing", "Effecting", "Finagling", "Forging", "Forming", "Generating", "Germinating", "Hatching", "Herding", "Honking", "Hustling",
  "Ideating", "Inferring", "Manifesting", "Marinating", "Moseying", "Mulling", "Mustering", "Musing", "Noodling", "Percolating",
  "Pondering", "Processing", "Puttering", "Reticulating", "Ruminating", "Schlepping", "Shucking", "Simmering", "Smooshing", "Spinning",
  "Stewing", "Synthesizing", "Thinking", "Transmuting", "Vibing", "Working",
];
const DONE_VERBS = ["Baked", "Brewed", "Churned", "Cogitated", "Cooked", "Crunched", "Sautéed", "Worked"];
// 記号が膨らんでしぼむ動き
const SPINNER_FRAMES = ["·", "✢", "✳", "✶", "✻", "✽", "✻", "✶", "✳", "✢"];

const pick = (list: string[], seed: number) => list[Math.abs(Math.floor(seed)) % list.length];

function formatDuration(seconds: number) {
  if (seconds < 60) return `${seconds}s`;
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ${seconds % 60}s`;
  return `${Math.floor(seconds / 3600)}h ${Math.floor((seconds % 3600) / 60)}m`;
}

const formatTokens = (tokens: number) => (tokens < 1000 ? String(Math.round(tokens)) : `${(tokens / 1000).toFixed(1)}k`);

function LiveTurnStatus({ live }: { live: LiveReply }) {
  const [frame, setFrame] = useState(0);
  useEffect(() => {
    // 端末の Claude Code と同じくらいの速さで回す
    const timer = setInterval(() => setFrame((f) => (f + 1) % SPINNER_FRAMES.length), 120);
    return () => clearInterval(timer);
  }, []);
  const elapsed = useElapsed(live.startedAt, true);
  const verb = useMemo(() => pick(SPINNER_VERBS, live.startedAt), [live.startedAt]);
  return (
    <span className="turn-status live">
      <span className="turn-status-glyph">{SPINNER_FRAMES[frame]}</span> <span className="turn-status-verb">{verb}…</span>{" "}
      ({formatDuration(elapsed)} · ↓ {formatTokens(live.outputTokens + live.streamingTokens)} tokens · esc to interrupt)
    </span>
  );
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
      {approval.autoReason && <div className="approval-desc">自動の判定で確認が必要になりました: {approval.autoReason}</div>}
      {!builtin && approval.description && <div className="approval-desc">{approval.description}</div>}
      <ApprovalInput approval={approval} />
      <div className="approval-actions">
        {approval.allowAlways !== false && <button type="button" className="button primary" onClick={() => approve(conversationId, approval.requestId, "always")}>常に許可</button>}
        <button type="button" className={`button${approval.allowAlways === false ? " primary" : ""}`} onClick={() => approve(conversationId, approval.requestId, "once")}>{approval.allowAlways === false ? "許可" : "今回だけ許可"}</button>
        <button type="button" className="button ghost" onClick={() => approve(conversationId, approval.requestId, "deny")}>拒否</button>
      </div>
      {approval.allowAlways === false && !approval.autoReason && <div className="approval-note">内容によって何でもできる操作なので、毎回確認します</div>}
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
        <span className="artifact-card-sub">{artifact ? `${ARTIFACT_TYPE_LABELS[artifact.type] ?? artifact.type} ・ 版 ${artifact.version}` : result?.is_error ? "失敗しました" : "作っています..."}</span>
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
  const asking = approval?.kind === "question";
  const toolName = asking ? "Claude からの質問" : (builtin ? builtin.titles[tool] : undefined) ?? (tool || source);
  const subject = builtin ? builtin.subject(tool, block.input ?? {}) : rest.length ? source : "";
  const status = asking ? "回答待ち" : approval ? "承認待ち" : running ? "実行中" : result ? (result.is_error ? "エラー" : "完了") : live ? "準備中" : "";
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
          {/* 質問は結果に問いと答えが並ぶので、入力の JSON は見せない */}
          {source !== "ask" && <><div className="tool-section-label">入力</div><pre>{JSON.stringify(block.input ?? {}, null, 2)}</pre></>}
          {result && (
            <>
              {source !== "ask" && <div className="tool-section-label">結果</div>}
              {images.map((img, i) => <img key={i} className="tool-image" src={`data:${img.source.media_type};base64,${img.source.data}`} alt="" />)}
              {(images.length === 0 || resultText(result).replace(/\[image\]/g, "").trim()) && <pre className={result.is_error ? "error" : ""}>{resultText(result)}</pre>}
            </>
          )}
        </div>
      )}
      {approval && (asking ? <QuestionForm conversationId={conversationId} approval={approval} /> : <ApprovalCard conversationId={conversationId} approval={approval} />)}
    </div>
  );
}

interface Question {
  question: string;
  header?: string;
  options: { label: string; description?: string }[];
  multi_select?: boolean;
}

function QuestionForm({ conversationId, approval }: { conversationId: string; approval: Approval }) {
  const questions = ((approval.input as { questions?: Question[] })?.questions ?? []);
  const [picked, setPicked] = useState<string[][]>(() => questions.map(() => []));
  const [others, setOthers] = useState<string[]>(() => questions.map(() => ""));
  const complete = questions.every((_, i) => picked[i].length > 0 || others[i].trim());
  // 1 つだけ選ぶ質問は、選択肢と自由記述のどちらか一方を答えにする
  const pick = (i: number, label: string, multi: boolean) => {
    setPicked((p) => p.map((list, j) => (j !== i ? list : multi ? (list.includes(label) ? list.filter((l) => l !== label) : [...list, label]) : [label])));
    if (!multi) setOthers((o) => o.map((text, j) => (j === i ? "" : text)));
  };
  const write = (i: number, text: string, multi: boolean) => {
    setOthers((o) => o.map((t, j) => (j === i ? text : t)));
    if (!multi && text) setPicked((p) => p.map((list, j) => (j === i ? [] : list)));
  };
  const submit = () => {
    if (complete) void answer(conversationId, approval.requestId, questions.map((_, i) => ({ selected: picked[i], other: others[i] })));
  };
  return (
    <div className="approval question-form">
      {questions.map((q, i) => (
        <div key={i} className="question">
          <div className="question-head">
            {q.header && <span className="question-chip">{q.header}</span>}
            <span>{q.question}</span>
          </div>
          {q.multi_select && <div className="approval-note">いくつでも選べます</div>}
          <div className="question-options">
            {q.options.map((o) => (
              <button key={o.label} type="button" aria-pressed={picked[i].includes(o.label)} className={`question-option${picked[i].includes(o.label) ? " on" : ""}`} onClick={() => pick(i, o.label, !!q.multi_select)}>
                <span className="question-option-label">{o.label}</span>
                {o.description && <span className="question-option-desc">{o.description}</span>}
              </button>
            ))}
          </div>
          <input
            className="question-other"
            placeholder="その他 (自由に入力)"
            value={others[i]}
            onChange={(e) => write(i, e.target.value, !!q.multi_select)}
            onKeyDown={(e) => { if (e.key === "Enter" && !e.nativeEvent.isComposing) submit(); }}
          />
        </div>
      ))}
      <div className="approval-actions">
        <button type="button" className="button primary" disabled={!complete} onClick={submit}>回答する</button>
        <button type="button" className="button ghost" onClick={() => void approve(conversationId, approval.requestId, "deny")}>答えずに進める</button>
      </div>
    </div>
  );
}

// 編集ややり直しで分かれた版を行き来する「‹ 1/2 ›」
function BranchNav({ conversationId, index, fork }: { conversationId: string; index: number; fork: { count: number; active: number } }) {
  const running = useStore((s) => !!s.live[conversationId]);
  return (
    <span className="branch-nav">
      <button type="button" className="icon-button tiny" aria-label="前の版" disabled={running || fork.active === 0} onClick={() => void switchBranch(conversationId, index, fork.active - 1)}><ChevronLeft size={14} /></button>
      <span className="branch-count">{fork.active + 1} / {fork.count}</span>
      <button type="button" className="icon-button tiny" aria-label="次の版" disabled={running || fork.active === fork.count - 1} onClick={() => void switchBranch(conversationId, index, fork.active + 1)}><ChevronRight size={14} /></button>
    </span>
  );
}

// claude.ai と同じく、コピーしたらしばらくチェックの印にする
function CopyButton({ text, size = 15 }: { text: string; size?: number }) {
  const [copied, setCopied] = useState(false);
  return (
    <button type="button" className="icon-button small" title="コピー" onClick={async () => {
      await navigator.clipboard.writeText(text);
      setCopied(true);
      // 押せたと分かる程度の時間
      setTimeout(() => setCopied(false), 2000);
    }}>{copied ? <Check size={size} /> : <Copy size={size} />}</button>
  );
}

function RetryMenu({ conversationId, human }: { conversationId: string; human: number }) {
  const current = useStore((s) => s.choice.model);
  return (
    <Dropdown
      direction="auto"
      trigger={(open, toggle) => (
        <button type="button" className={`icon-button small${open ? " open" : ""}`} title="やり直す" onClick={toggle}><RotateCcw size={15} /></button>
      )}
    >
      {(close) => (
        <>
          <MenuItem onClick={() => { close(); void retry(conversationId, { messageIndex: human }); }}>変更せずにやり直す</MenuItem>
          <div className="menu-separator" />
          <div className="menu-heading">モデルを変えてやり直す</div>
          {MODELS.filter((m) => m.id !== current).map((m) => (
            <MenuItem key={m.id} hint={m.description} onClick={() => { close(); void retry(conversationId, { messageIndex: human, model: m.id }); }}>{m.label}</MenuItem>
          ))}
        </>
      )}
    </Dropdown>
  );
}

// useStore の戻り値が毎回別の配列だと再描画が止まらなくなるので、空は使い回す
const NO_NOTICES: string[] = [];

function AssistantTurn({ turn, conversationId, isLast }: { turn: Extract<Turn, { kind: "assistant" }>; conversationId: string; isLast: boolean }) {
  const showThinking = useStore((s) => s.settings?.chat.showThinking ?? true);
  const lastTurn: { seconds: number; tokens: number } | undefined = useStore((s) => (isLast ? (s.conversations[conversationId] as any)?._lastTurn : undefined));
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
  let cites: Cite[][] = [];
  const sources = new Map<string, string>();
  const flushText = (key: string) => {
    if (textBuffer) elements.push(<Markdown key={key} text={textBuffer} cites={cites} />);
    textBuffer = "";
    cites = [];
  };
  const lastIndex = turn.blocks.length - 1;
  turn.blocks.forEach((block, i) => {
    if (block.type === "text") {
      // 出典の印と同じ文字が本文にあると、印として扱われてしまう
      textBuffer += (block.text ?? "").replace(/[\uE000\uE001]/g, "");
      // claude.ai と同じく、出典を使った文のすぐ後ろに出典を付ける
      if (Array.isArray(block.citations) && block.citations.length > 0) {
        const unique = uniqueCites(block.citations);
        for (const c of unique) if (c.url) sources.set(c.url, c.title ?? c.url);
        textBuffer += citeMark(cites.length);
        cites.push(unique);
      }
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
      case "redacted_thinking":
        if (showThinking) elements.push(<div key={i} className="notice">一部の思考は安全上の理由で表示できません</div>);
        break;
      case "fallback":
        elements.push(<div key={i} className="notice">{block.from?.model} が応答を断ったため {block.to?.model} が続きを書きました</div>);
        break;
    }
  });
  flushText("text-end");

  const body = turn.blocks.filter((b) => b.type === "text").map((b) => b.text).join("");
  // コピーしたときにも出典が残るように
  const fullText = sources.size > 0 ? `${body}\n\n${[...sources].map(([url, title]) => `- [${title.replace(/[[\]()]/g, "\\$&")}](${url})`).join("\n")}` : body;
  const forkView = useStore((s) => s.conversations[conversationId]?.forkView?.[String(turn.start)]);
  return (
    <div className="assistant-turn">
      {elements}
      {notices.map((n: string, i: number) => <div key={`n${i}`} className="notice">{n}</div>)}
      <div className="assistant-footer">
        {/* claude.ai と同じく、マークは最新の回答の下にだけ出す */}
        {(isLast || live) && <span className={`footer-spark${live ? " live" : ""}`}><Spark size={live ? 22 : 18} spinning={!!live} /></span>}
        {!live && (
          <div className={`message-actions${isLast || forkView ? " visible" : ""}`}>
            {forkView && <BranchNav conversationId={conversationId} index={turn.start} fork={forkView} />}
            <CopyButton text={fullText} />
            {turn.human >= 0 && <RetryMenu conversationId={conversationId} human={turn.human} />}
          </div>
        )}
        {live && <LiveTurnStatus live={live} />}
        {isLast && !live && lastTurn && <span className="turn-status">✻ {pick(DONE_VERBS, lastTurn.seconds + lastTurn.tokens)} for {formatDuration(lastTurn.seconds)}</span>}
        {isLast && !live && <span className="disclaimer">Claude は間違えることがあります。回答内容は必ずご確認ください。</span>}
      </div>
    </div>
  );
}

function UserTurn({ turn, conversationId, running }: { turn: Extract<Turn, { kind: "human" }>; conversationId: string; running: boolean }) {
  const [editing, setEditing] = useState(false);
  const fork = useStore((s) => s.conversations[conversationId]?.forkView?.[String(turn.index)]);
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
      <div className={`user-actions${fork ? " visible" : ""}`}>
        {fork && <BranchNav conversationId={conversationId} index={turn.index} fork={fork} />}
        <CopyButton text={text} size={14} />
        {!running && <button type="button" className="icon-button small" title="編集" onClick={() => { setDraft(text); setKept([...documents, ...images]); setEditing(true); }}><Pencil size={14} /></button>}
        {!running && <button type="button" className="icon-button small" title="ここまで巻き戻す" onClick={() => setState((s) => ({ ...s, dialog: { kind: "rewind", conversationId, messageIndex: turn.index } }))}><History size={14} /></button>}
      </div>
    </div>
  );
}

// claude.ai と同じく、回答の一部を選ぶと「返信」ボタンが出て、その部分を引用して返信できる
function QuoteReply({ conversationId }: { conversationId: string }) {
  const [selection, setSelection] = useState<{ text: string; x: number; y: number } | null>(null);
  useEffect(() => {
    const onUp = () => {
      // クリックで選択が消えるのを待ってから読む
      setTimeout(() => {
        const current = window.getSelection();
        const text = current?.toString().trim() ?? "";
        const anchor = current?.anchorNode?.parentElement;
        if (!text || !current || current.rangeCount === 0 || !anchor?.closest(".assistant-turn")) {
          setSelection(null);
          return;
        }
        const rect = current.getRangeAt(0).getBoundingClientRect();
        setSelection({ text, x: rect.left + rect.width / 2, y: rect.top });
      }, 0);
    };
    document.addEventListener("mouseup", onUp);
    return () => document.removeEventListener("mouseup", onUp);
  }, []);
  if (!selection) return null;
  return (
    <button
      type="button"
      className="quote-reply"
      style={{ left: selection.x, top: selection.y }}
      onMouseDown={(e) => e.preventDefault()}
      onClick={() => {
        setQuote(conversationId, selection.text);
        setSelection(null);
        window.getSelection()?.removeAllRanges();
        document.querySelector<HTMLTextAreaElement>(".composer textarea")?.focus();
      }}
    >
      <Quote size={13} /> 返信
    </button>
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
      <QuoteReply conversationId={conversationId} />
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
