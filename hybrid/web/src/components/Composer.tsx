import { ArrowUp, Check, ChevronDown, Circle, CircleDot, Code, FileText, Globe, Link, MonitorCog, Paperclip, Plug, Plus, Settings2, SlidersHorizontal, Square, Telescope, X } from "lucide-react";
import { useEffect, useMemo, useRef, useState, type ReactNode } from "react";
import { clearPrefill, openSettings, send, setChoice, setMode, stop, toast } from "../actions";
import { BUILTIN_SOURCES } from "../builtins";
import { allCommands, runCommand } from "../commands";
import { EFFORTS, MODELS, modelLabel } from "../models";
import { getState, useStore } from "../store";
import type { PermissionMode, Todo, WireBlock } from "../types";
import { Dropdown, MenuItem, Toggle } from "./ui";

const IMAGE_TYPES = ["image/png", "image/jpeg", "image/gif", "image/webp"];
// API 側の1枚あたり上限。base64 にした後の長さで判定される
const MAX_IMAGE_BYTES = 5 * 1024 * 1024;
// リクエスト全体の上限 32MB。履歴の添付も毎回送るので、それも含めて余裕を見る
const MAX_REQUEST_ATTACHMENT_CHARS = 30 * 1024 * 1024;
const base64Length = (bytes: number) => Math.ceil(bytes / 3) * 4;
// リクエスト全体の上限 32MB を base64 で膨らんでも超えないように
const MAX_PDF_BYTES = 20 * 1024 * 1024;
// これ以上はトークンが膨らみすぎる
const MAX_TEXT_BYTES = 2 * 1024 * 1024;
// MIME が空で届くことが多い拡張子
const TEXT_EXTENSIONS = [
  "txt", "md", "markdown", "csv", "tsv", "json", "jsonl", "xml", "yaml", "yml", "toml", "ini", "log", "html", "htm", "css",
  "js", "jsx", "ts", "tsx", "mjs", "cjs", "py", "rb", "go", "rs", "java", "kt", "cs", "csproj", "sln", "xaml", "c", "h", "cpp", "hpp",
  "php", "swift", "sql", "sh", "bash", "ps1", "psm1", "bat", "cmd", "vue", "svelte", "env", "gitignore", "dockerfile",
];
const ACCEPT = [...IMAGE_TYPES, "application/pdf", ".pdf", "text/*", ...TEXT_EXTENSIONS.map((e) => `.${e}`)].join(",");

interface PendingAttachment {
  kind: "image" | "pdf" | "text";
  mediaType: string;
  // image と pdf は base64、text は本文
  data: string;
  name: string;
}

function isTextFile(file: File) {
  if (file.type.startsWith("text/") || file.type === "application/json" || file.type === "application/xml") return true;
  const ext = file.name.includes(".") ? file.name.split(".").pop()!.toLowerCase() : file.name.toLowerCase();
  return TEXT_EXTENSIONS.includes(ext);
}

async function readAsText(file: File) {
  const bytes = new Uint8Array(await file.arrayBuffer());
  // PowerShell 5.1 のリダイレクトなどは BOM 付き UTF-16
  if (bytes[0] === 0xff && bytes[1] === 0xfe) return new TextDecoder("utf-16le").decode(bytes.subarray(2));
  if (bytes[0] === 0xfe && bytes[1] === 0xff) return new TextDecoder("utf-16be").decode(bytes.subarray(2));
  if (bytes[0] === 0xef && bytes[1] === 0xbb && bytes[2] === 0xbf) return new TextDecoder("utf-8").decode(bytes.subarray(3));
  try {
    return new TextDecoder("utf-8", { fatal: true }).decode(bytes);
  } catch {
    // 古い Windows のテキストファイル
    return new TextDecoder("shift_jis").decode(bytes);
  }
}

// 履歴にある画像と PDF。送るたびに全部載る
function attachedChars(conversationId: string | null) {
  if (!conversationId) return 0;
  const messages = getState().conversations[conversationId]?.messages ?? [];
  let total = 0;
  for (const m of messages) {
    if (!Array.isArray(m.content)) continue;
    for (const b of m.content) if ((b.type === "image" || b.type === "document") && b.source?.type === "base64") total += String(b.source.data ?? "").length;
  }
  return total;
}

function toBlock(a: PendingAttachment): WireBlock {
  if (a.kind === "image") return { type: "image", source: { type: "base64", media_type: a.mediaType, data: a.data } };
  if (a.kind === "pdf") return { type: "document", title: a.name, source: { type: "base64", media_type: "application/pdf", data: a.data } };
  return { type: "document", title: a.name, source: { type: "text", media_type: "text/plain", data: a.data } };
}

function readAsBase64(file: File) {
  return new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => {
      const url = reader.result as string;
      resolve(url.slice(url.indexOf(",") + 1));
    };
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
}

export function FileChip({ name, kind }: { name: string; kind: "pdf" | "text" }) {
  return (
    <span className="file-chip">
      <FileText size={18} />
      <span className="file-chip-text">
        <span className="file-chip-name">{name}</span>
        <span className="file-chip-kind">{kind === "pdf" ? "PDF" : "テキスト"}</span>
      </span>
    </span>
  );
}

function ModelMenu() {
  const choice = useStore((s) => s.choice);
  return (
    <Dropdown
      align="right"
      direction="up"
      className="model-menu"
      trigger={(open, toggle) => (
        <button type="button" className={`model-button${open ? " open" : ""}`} onClick={toggle}>
          {modelLabel(choice.model)}
          <ChevronDown size={14} />
        </button>
      )}
    >
      {(close) => (
        <>
          {MODELS.map((m) => (
            <MenuItem key={m.id} checked={choice.model === m.id} hint={m.description} onClick={() => { setChoice({ model: m.id }); close(); }}>
              {m.label}
            </MenuItem>
          ))}
          <div className="menu-separator" />
          <div className="menu-heading">思考の深さ</div>
          <div className="effort-row">
            {EFFORTS.map((e) => (
              <button
                key={e.id}
                type="button"
                title={e.description}
                className={`effort-pill${choice.effort === e.id ? " on" : ""}`}
                onClick={() => setChoice({ effort: e.id })}
              >
                {e.label}
              </button>
            ))}
          </div>
          <div className="menu-footnote">{EFFORTS.find((e) => e.id === choice.effort)?.description}</div>
        </>
      )}
    </Dropdown>
  );
}

// 新しいチャットでは入力欄が画面の中ほどにあり、上に開くと項目が画面からはみ出す
function ToolsMenu({ direction }: { direction: "up" | "down" }) {
  const choice = useStore((s) => s.choice);
  const mcp = useStore((s) => s.mcp);
  const settings = useStore((s) => s.settings);
  const builtins = settings ? BUILTIN_SOURCES.filter((b) => b.isAvailable(settings)) : [];
  const pcTools = builtins.filter((b) => b.group === "pc");
  const claudeTools = builtins.filter((b) => b.group === "claude");
  const builtinsOn = pcTools.filter((b) => !choice.disabledSources.includes(b.id)).length;
  const serverTools = [choice.webSearch, choice.webFetch, choice.codeExecution].filter(Boolean).length;
  const activeCount = mcp.filter((m) => m.status === "running" && !choice.disabledSources.includes(m.name)).length + builtinsOn + serverTools;
  const toggleSource = (name: string, enabled: boolean) =>
    setChoice({
      disabledSources: enabled ? choice.disabledSources.filter((n) => n !== name) : [...choice.disabledSources, name],
    });
  const row = (key: string, icon: ReactNode, label: string, hint: string | null, checked: boolean, onChange: (v: boolean) => void) => (
    <div className="menu-row" key={key}>
      {icon}
      <span className="menu-label">
        {label}
        {hint && <span className="menu-hint">{hint}</span>}
      </span>
      <Toggle checked={checked} onChange={onChange} label={label} />
    </div>
  );
  return (
    <Dropdown
      direction={direction}
      className="tools-menu"
      trigger={(open, toggle) => (
        <button type="button" className={`icon-button composer-tool${open ? " open" : ""}${activeCount ? " active" : ""}`} aria-label="検索とツール" title="検索とツール" onClick={toggle}>
          <SlidersHorizontal size={17} />
          {activeCount > 0 && <span className="tool-count">{activeCount}</span>}
        </button>
      )}
    >
      {(close) => (
        <>
          {row("research", <Telescope size={16} />, "リサーチ", "複数のエージェントで深く調べる", choice.research, (v) => setChoice({ research: v }))}
          {row("web", <Globe size={16} />, "Web 検索", `1 回の返答で ${settings?.chat.webSearchMaxUses ?? 5} 回まで`, choice.webSearch, (v) => setChoice({ webSearch: v }))}
          {row("fetch", <Link size={16} />, "Web ページの取得", "URL の中身を読む", choice.webFetch, (v) => setChoice({ webFetch: v }))}
          {row("code", <Code size={16} />, "コード実行", "計算、グラフ、ファイル作成", choice.codeExecution, (v) => setChoice({ codeExecution: v }))}
          {pcTools.length > 0 && <div className="menu-separator" />}
          {pcTools.map((b) => row(b.id, <b.icon size={16} />, b.label, b.hint(settings!), !choice.disabledSources.includes(b.id), (v) => toggleSource(b.id, v)))}
          {claudeTools.length > 0 && <div className="menu-separator" />}
          {claudeTools.map((b) => row(b.id, <b.icon size={16} />, b.label, b.hint(settings!), !choice.disabledSources.includes(b.id), (v) => toggleSource(b.id, v)))}
          {mcp.length > 0 && <div className="menu-separator" />}
          {mcp.map((server) => row(
            server.name,
            <Plug size={16} />,
            server.name,
            server.status === "running" ? `ツール ${server.tools.length} 個` : server.status === "starting" ? "接続中..." : server.status === "disabled" ? "無効" : "接続できません",
            server.status === "running" && !choice.disabledSources.includes(server.name),
            (v) => toggleSource(server.name, v),
          ))}
          <div className="menu-separator" />
          {pcTools.length < BUILTIN_SOURCES.filter((b) => b.group === "pc").length && <MenuItem icon={<MonitorCog size={16} />} onClick={() => { close(); openSettings(pcTools.some((b) => b.id === "files") ? "computer" : "files"); }}>PC 操作を設定</MenuItem>}
          <MenuItem icon={<Settings2 size={16} />} onClick={() => { close(); openSettings("features"); }}>機能を設定</MenuItem>
          <MenuItem icon={<Plug size={16} />} onClick={() => { close(); openSettings("connectors"); }}>コネクタを管理</MenuItem>
        </>
      )}
    </Dropdown>
  );
}

export const MODE_LABELS: Record<PermissionMode, { label: string; hint: string }> = {
  default: { label: "毎回確認", hint: "変更する操作は毎回確認する" },
  acceptEdits: { label: "編集は自動で許可", hint: "フォルダ内のファイル操作は確認しない" },
  plan: { label: "計画モード", hint: "調べて計画を出すだけ。変更はしない" },
  bypass: { label: "確認なし", hint: "すべての操作を確認せずに実行する" },
};

function ModeMenu({ conversationId }: { conversationId: string | null }) {
  const mode = useStore((s) => (conversationId ? s.conversations[conversationId]?.permissionMode : s.choice.permissionMode) ?? "default");
  const allowBypass = useStore((s) => s.settings?.features.allowBypassMode ?? false);
  const modes = (Object.keys(MODE_LABELS) as PermissionMode[]).filter((m) => m !== "bypass" || allowBypass || mode === "bypass");
  return (
    <Dropdown
      direction="up"
      trigger={(open, toggle) => (
        <button type="button" className={`mode-button mode-${mode}${open ? " open" : ""}`} title="権限モード" onClick={toggle}>
          {MODE_LABELS[mode].label}
          <ChevronDown size={13} />
        </button>
      )}
    >
      {(close) => modes.map((m) => (
        <MenuItem key={m} checked={mode === m} hint={MODE_LABELS[m].hint} onClick={() => { close(); void setMode(conversationId, m); }}>{MODE_LABELS[m].label}</MenuItem>
      ))}
    </Dropdown>
  );
}

const NO_TODOS: Todo[] = [];

function TodoList({ conversationId }: { conversationId: string | null }) {
  const todos = useStore((s) => (conversationId ? s.conversations[conversationId]?.todos ?? NO_TODOS : NO_TODOS));
  const [open, setOpen] = useState(true);
  if (todos.length === 0) return null;
  const done = todos.filter((t) => t.status === "completed").length;
  // 全部終わったら畳んで邪魔しない
  if (done === todos.length && !open) return null;
  return (
    <div className="todo-panel">
      <button type="button" className="todo-header" onClick={() => setOpen((o) => !o)}>
        <span>タスク {done}/{todos.length}</span>
        <ChevronDown size={14} className={open ? "" : "rotated"} />
      </button>
      {open && (
        <ul className="todo-list">
          {todos.map((t, i) => (
            <li key={i} className={`todo ${t.status}`}>
              {t.status === "completed" ? <Check size={14} /> : t.status === "in_progress" ? <CircleDot size={14} /> : <Circle size={14} />}
              <span>{t.content}</span>
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

function SlashMenu({ query, selected, onPick }: { query: string; selected: number; onPick: (name: string) => void }) {
  const items = useSlashItems(query);
  if (items.length === 0) return null;
  return (
    <div className="slash-menu" role="listbox">
      {items.map((c, i) => (
        <button key={c.name} type="button" role="option" aria-selected={i === selected} className={`slash-item${i === selected ? " selected" : ""}`}
          onMouseDown={(e) => { e.preventDefault(); onPick(c.name); }}>
          <span className="slash-name">/{c.name}{c.args && <span className="slash-args"> {c.args}</span>}</span>
          <span className="slash-desc">{c.kind !== "builtin" && <span className="slash-kind">{c.kind === "skill" ? "Skill" : "自作"}</span>}{c.description}</span>
        </button>
      ))}
    </div>
  );
}

function useSlashItems(query: string | null) {
  const extensions = useStore((s) => s.extensions);
  return useMemo(() => {
    if (query === null) return [];
    const q = query.toLowerCase();
    return allCommands().filter((c) => c.name.toLowerCase().startsWith(q)).slice(0, 12);
    // extensions が変わったら作り直す
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [query, extensions]);
}

export function Composer({ conversationId, autoFocus, prefill }: { conversationId: string | null; autoFocus?: boolean; prefill?: { text: string; nonce: number } }) {
  const running = useStore((s) => (conversationId ? !!s.live[conversationId] : false));
  const research = useStore((s) => s.choice.research);
  // リサーチの結果が届く前に別の質問を始めると、結果がその応答の後ろに回ってしまう
  const researching = useStore((s) => !!conversationId && Object.values(s.workflows).some((w) => w.parentId === conversationId && w.name === "research" && w.status === "running"));
  const storedPrefill = useStore((s) => (conversationId ? s.prefill[conversationId] : undefined));
  const [text, setText] = useState("");
  const [slashIndex, setSlashIndex] = useState(0);
  // 「/」で始まり、まだ空白を打っていない間だけ候補を出す
  const slashQuery = /^\/[A-Za-z0-9_-]*$/.test(text) ? text.slice(1) : null;
  const slashItems = useSlashItems(slashQuery);
  const [attachments, setAttachments] = useState<PendingAttachment[]>([]);
  const [dragging, setDragging] = useState(false);
  const [sending, setSending] = useState(false);
  const textarea = useRef<HTMLTextAreaElement>(null);
  const fileInput = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (autoFocus) textarea.current?.focus();
  }, [autoFocus, conversationId]);

  useEffect(() => {
    const next = prefill ?? storedPrefill;
    if (!next) return;
    setText(next.text);
    textarea.current?.focus();
    // 一度入れたら消す。残すと会話を開き直すたびに同じ文が戻ってくる
    if (!prefill && conversationId) clearPrefill(conversationId);
  }, [prefill, storedPrefill, conversationId]);

  useEffect(() => setSlashIndex(0), [slashQuery]);

  useEffect(() => {
    const el = textarea.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${Math.min(el.scrollHeight, window.innerHeight * 0.4)}px`;
  }, [text]);

  async function addFiles(files: Iterable<File>) {
    const added: PendingAttachment[] = [];
    for (const file of files) {
      if (IMAGE_TYPES.includes(file.type)) {
        if (base64Length(file.size) > MAX_IMAGE_BYTES) {
          toast(`${file.name} は大きすぎるので添付できません。画像は 3.7MB くらいまでです`, "error");
          continue;
        }
        added.push({ kind: "image", mediaType: file.type, data: await readAsBase64(file), name: file.name });
      } else if (file.type === "application/pdf" || file.name.toLowerCase().endsWith(".pdf")) {
        if (file.size > MAX_PDF_BYTES) {
          toast(`${file.name} は 20MB を超えているので添付できません`, "error");
          continue;
        }
        added.push({ kind: "pdf", mediaType: "application/pdf", data: await readAsBase64(file), name: file.name });
      } else if (isTextFile(file)) {
        if (file.size > MAX_TEXT_BYTES) {
          toast(`${file.name} は 2MB を超えているので添付できません`, "error");
          continue;
        }
        added.push({ kind: "text", mediaType: "text/plain", data: await readAsText(file), name: file.name });
      } else {
        toast(`${file.name} は対応していない形式です。画像、PDF、テキストのファイルを使ってください`, "error");
      }
    }
    setAttachments((current) => [...current, ...added]);
  }

  async function submit() {
    const trimmed = text.trim();
    if ((!trimmed && attachments.length === 0) || sending) return;
    // /btw だけは応答中にも使える
    if (attachments.length === 0 && trimmed.startsWith("/") && (!running || trimmed.startsWith("/btw"))) {
      setSending(true);
      const outcome = await runCommand(trimmed, conversationId);
      setSending(false);
      if (outcome === "done") setText("");
      if (outcome !== null) return;
    }
    if (running) {
      if (trimmed.startsWith("/")) toast("応答中に使えるのは /btw だけです。終わってから使ってください", "error");
      return;
    }
    if (researching) {
      toast("リサーチの途中です。結果が届くまで待つか、止めてから送ってください", "error");
      return;
    }
    if (attachedChars(conversationId) + attachments.reduce((sum, a) => sum + a.data.length, 0) > MAX_REQUEST_ATTACHMENT_CHARS) {
      toast("この会話の添付ファイルの合計が大きすぎて送れません。添付を減らすか、新しいチャットで送ってください", "error");
      return;
    }
    // API の推奨どおり添付を本文より前に置く
    const content: string | WireBlock[] = attachments.length === 0
      ? trimmed
      : [...attachments.map(toBlock), ...(trimmed ? [{ type: "text", text: trimmed }] : [])];
    setSending(true);
    const ok = await send(conversationId, content);
    setSending(false);
    if (ok) {
      setText("");
      setAttachments([]);
    }
  }

  const canSend = (text.trim().length > 0 || attachments.length > 0) && !sending;

  return (
    <div
      className={`composer${dragging ? " dragging" : ""}${research ? " research" : ""}`}
      onDragOver={(e) => { e.preventDefault(); setDragging(true); }}
      onDragLeave={() => setDragging(false)}
      onDrop={(e) => { e.preventDefault(); setDragging(false); void addFiles([...e.dataTransfer.files]); }}
    >
      <TodoList conversationId={conversationId} />
      {slashQuery !== null && <SlashMenu query={slashQuery} selected={slashIndex} onPick={(name) => { setText(`/${name} `); textarea.current?.focus(); }} />}
      {attachments.length > 0 && (
        <div className="attachments">
          {attachments.map((a, i) => (
            <div className={`attachment${a.kind === "image" ? "" : " file"}`} key={i} title={a.name}>
              {a.kind === "image"
                ? <img src={`data:${a.mediaType};base64,${a.data}`} alt={a.name} />
                : <FileChip name={a.name} kind={a.kind} />}
              <button type="button" aria-label="添付を外す" onClick={() => setAttachments((list) => list.filter((_, j) => j !== i))}><X size={12} /></button>
            </div>
          ))}
        </div>
      )}
      <textarea
        ref={textarea}
        rows={1}
        value={text}
        placeholder={research ? "何を調べますか?" : conversationId ? "Claude に返信... (/ でコマンド)" : "今日はどんなお手伝いをしましょうか?"}
        onChange={(e) => setText(e.target.value)}
        onPaste={(e) => {
          const files = [...e.clipboardData.files];
          // Excel などは文字と一緒に画像も入れてくるので、文字があれば普通に貼る
          if (files.length && !e.clipboardData.getData("text/plain")) {
            e.preventDefault();
            void addFiles(files);
          }
        }}
        onKeyDown={(e) => {
          if (slashItems.length > 0 && !e.nativeEvent.isComposing) {
            if (e.key === "ArrowDown" || e.key === "ArrowUp") {
              e.preventDefault();
              const step = e.key === "ArrowDown" ? 1 : -1;
              setSlashIndex((i) => (i + step + slashItems.length) % slashItems.length);
              return;
            }
            // 候補を選んでいる途中の Enter と Tab は補完に使う
            // 打った名前がそのままコマンドなら、Enter は補完せずに実行する
            const typedExactly = slashItems.some((c) => c.name.toLowerCase() === text.slice(1).toLowerCase());
            if (e.key === "Tab" || (e.key === "Enter" && !typedExactly)) {
              e.preventDefault();
              setText(`/${slashItems[slashIndex].name} `);
              return;
            }
          }
          // IME 変換確定の Enter で送信しない
          if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
            e.preventDefault();
            void submit();
          }
        }}
      />
      <div className="composer-bar">
        <Dropdown
          direction="up"
          trigger={(open, toggle) => (
            <button type="button" className={`icon-button composer-tool${open ? " open" : ""}`} aria-label="ファイルを追加" title="ファイルを追加" onClick={toggle}>
              <Plus size={18} />
            </button>
          )}
        >
          {(close) => (
            <MenuItem icon={<Paperclip size={16} />} hint="画像、PDF、テキスト" onClick={() => { close(); fileInput.current?.click(); }}>ファイルを追加</MenuItem>
          )}
        </Dropdown>
        <ToolsMenu direction={conversationId ? "up" : "down"} />
        {research && (
          <button type="button" className="research-chip" title="リサーチをやめる" onClick={() => setChoice({ research: false })}>
            <Telescope size={14} /> リサーチ <X size={12} />
          </button>
        )}
        <ModeMenu conversationId={conversationId} />
        <span className="spacer" />
        <ModelMenu />
        {running ? (
          <button type="button" className="send-button stop" aria-label="停止" onClick={() => conversationId && void stop(conversationId)}>
            <Square size={14} fill="currentColor" />
          </button>
        ) : (
          <button type="button" className="send-button" aria-label="送信" disabled={!canSend} onClick={() => void submit()}>
            <ArrowUp size={18} strokeWidth={2.4} />
          </button>
        )}
      </div>
      <input
        ref={fileInput}
        type="file"
        accept={ACCEPT}
        multiple
        hidden
        onChange={(e) => {
          // 直後に value を空にすると FileList も空になるので先に配列へ写す
          if (e.target.files) void addFiles([...e.target.files]);
          e.target.value = "";
        }}
      />
    </div>
  );
}
