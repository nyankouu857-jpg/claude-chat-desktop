import { ArrowUp, ChevronDown, FolderOpen, Globe, ImagePlus, Plug, Plus, SlidersHorizontal, Square, X } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { FILES_SOURCE, openSettings, send, setChoice, stop, toast } from "../actions";
import { EFFORTS, MODELS, modelLabel } from "../models";
import { useStore } from "../store";
import type { WireBlock } from "../types";
import { Dropdown, MenuItem, Toggle } from "./ui";

const IMAGE_TYPES = ["image/png", "image/jpeg", "image/gif", "image/webp"];
// API 側の1枚あたり上限
const MAX_IMAGE_BYTES = 5 * 1024 * 1024;

interface PendingImage {
  mediaType: string;
  data: string;
  name: string;
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

function ToolsMenu() {
  const choice = useStore((s) => s.choice);
  const mcp = useStore((s) => s.mcp);
  const files = useStore((s) => s.settings?.files);
  const filesAvailable = !!files?.enabled && files.folders.length > 0;
  const filesOn = filesAvailable && !choice.disabledSources.includes(FILES_SOURCE);
  const activeConnectors = mcp.filter((m) => m.status === "running" && !choice.disabledSources.includes(m.name)).length + (filesOn ? 1 : 0);
  const toggleSource = (name: string, enabled: boolean) =>
    setChoice({
      disabledSources: enabled ? choice.disabledSources.filter((n) => n !== name) : [...choice.disabledSources, name],
    });
  return (
    <Dropdown
      direction="up"
      className="tools-menu"
      trigger={(open, toggle) => (
        <button type="button" className={`icon-button composer-tool${open ? " open" : ""}${choice.webSearch || activeConnectors ? " active" : ""}`} aria-label="検索とツール" title="検索とツール" onClick={toggle}>
          <SlidersHorizontal size={17} />
          {(choice.webSearch || activeConnectors > 0) && <span className="tool-count">{(choice.webSearch ? 1 : 0) + activeConnectors}</span>}
        </button>
      )}
    >
      {(close) => (
        <>
          <div className="menu-row">
            <Globe size={16} />
            <span className="menu-label">Web 検索</span>
            <Toggle checked={choice.webSearch} onChange={(v) => setChoice({ webSearch: v })} label="Web 検索" />
          </div>
          {filesAvailable && (
            <div className="menu-row">
              <FolderOpen size={16} />
              <span className="menu-label">
                フォルダ
                <span className="menu-hint">{files!.folders.length} 個のフォルダを操作</span>
              </span>
              <Toggle checked={filesOn} onChange={(v) => toggleSource(FILES_SOURCE, v)} label="フォルダ" />
            </div>
          )}
          {mcp.length > 0 && <div className="menu-separator" />}
          {mcp.map((server) => (
            <div className="menu-row" key={server.name}>
              <Plug size={16} />
              <span className="menu-label">
                {server.name}
                <span className="menu-hint">
                  {server.status === "running" ? `ツール ${server.tools.length} 個` : server.status === "starting" ? "接続中..." : server.status === "disabled" ? "無効" : "接続できません"}
                </span>
              </span>
              <Toggle
                checked={server.status === "running" && !choice.disabledSources.includes(server.name)}
                onChange={(v) => toggleSource(server.name, v)}
                label={server.name}
              />
            </div>
          ))}
          <div className="menu-separator" />
          {!filesAvailable && <MenuItem icon={<FolderOpen size={16} />} onClick={() => { close(); openSettings("files"); }}>フォルダ操作を設定</MenuItem>}
          <MenuItem icon={<Plug size={16} />} onClick={() => { close(); openSettings("connectors"); }}>コネクタを管理</MenuItem>
        </>
      )}
    </Dropdown>
  );
}

export function Composer({ conversationId, autoFocus, prefill }: { conversationId: string | null; autoFocus?: boolean; prefill?: { text: string; nonce: number } }) {
  const running = useStore((s) => (conversationId ? !!s.live[conversationId] : false));
  const [text, setText] = useState("");
  const [images, setImages] = useState<PendingImage[]>([]);
  const [dragging, setDragging] = useState(false);
  const [sending, setSending] = useState(false);
  const textarea = useRef<HTMLTextAreaElement>(null);
  const fileInput = useRef<HTMLInputElement>(null);

  useEffect(() => {
    if (autoFocus) textarea.current?.focus();
  }, [autoFocus, conversationId]);

  useEffect(() => {
    if (!prefill) return;
    setText(prefill.text);
    textarea.current?.focus();
  }, [prefill]);

  useEffect(() => {
    const el = textarea.current;
    if (!el) return;
    el.style.height = "auto";
    el.style.height = `${Math.min(el.scrollHeight, window.innerHeight * 0.4)}px`;
  }, [text]);

  async function addFiles(files: Iterable<File>) {
    const added: PendingImage[] = [];
    for (const file of files) {
      if (!IMAGE_TYPES.includes(file.type)) {
        toast(`${file.name} は対応していない形式です。PNG / JPEG / GIF / WebP を使ってください`, "error");
        continue;
      }
      if (file.size > MAX_IMAGE_BYTES) {
        toast(`${file.name} は 5MB を超えているので添付できません`, "error");
        continue;
      }
      added.push({ mediaType: file.type, data: await readAsBase64(file), name: file.name });
    }
    setImages((current) => [...current, ...added]);
  }

  async function submit() {
    const trimmed = text.trim();
    if ((!trimmed && images.length === 0) || running || sending) return;
    const content: string | WireBlock[] = images.length === 0
      ? trimmed
      : [
          ...images.map((img) => ({ type: "image", source: { type: "base64", media_type: img.mediaType, data: img.data } })),
          ...(trimmed ? [{ type: "text", text: trimmed }] : []),
        ];
    setSending(true);
    const ok = await send(conversationId, content);
    setSending(false);
    if (ok) {
      setText("");
      setImages([]);
    }
  }

  const canSend = (text.trim().length > 0 || images.length > 0) && !sending;

  return (
    <div
      className={`composer${dragging ? " dragging" : ""}`}
      onDragOver={(e) => { e.preventDefault(); setDragging(true); }}
      onDragLeave={() => setDragging(false)}
      onDrop={(e) => { e.preventDefault(); setDragging(false); void addFiles(e.dataTransfer.files); }}
    >
      {images.length > 0 && (
        <div className="attachments">
          {images.map((img, i) => (
            <div className="attachment" key={i} title={img.name}>
              <img src={`data:${img.mediaType};base64,${img.data}`} alt={img.name} />
              <button type="button" aria-label="添付を外す" onClick={() => setImages((list) => list.filter((_, j) => j !== i))}><X size={12} /></button>
            </div>
          ))}
        </div>
      )}
      <textarea
        ref={textarea}
        rows={1}
        value={text}
        placeholder={conversationId ? "Claude に返信..." : "今日はどんなお手伝いをしましょうか?"}
        onChange={(e) => setText(e.target.value)}
        onPaste={(e) => {
          const files = [...e.clipboardData.files];
          if (files.length) {
            e.preventDefault();
            void addFiles(files);
          }
        }}
        onKeyDown={(e) => {
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
            <MenuItem icon={<ImagePlus size={16} />} onClick={() => { close(); fileInput.current?.click(); }}>画像を追加</MenuItem>
          )}
        </Dropdown>
        <ToolsMenu />
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
        accept={IMAGE_TYPES.join(",")}
        multiple
        hidden
        onChange={(e) => {
          if (e.target.files) void addFiles(e.target.files);
          e.target.value = "";
        }}
      />
    </div>
  );
}
