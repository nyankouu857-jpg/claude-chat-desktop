import { ChevronDown, ChevronRight, Cloud, FileCode, Folder as FolderIcon, FolderOpen, GitBranch, Plus, Settings2, Shapes } from "lucide-react";
import { useEffect, useMemo, useState, type ReactNode } from "react";
import { compact, openArtifact, openFile, openSessionFile, openSettings, sessionList, type SessionListing } from "../actions";
import { on } from "../bridge";
import { contextWindowOf } from "../models";
import { useStore } from "../store";
import { MODE_LABELS } from "./Composer";
import { tokens } from "./ui";

const FILE_WRITE_TOOLS = ["files__write_file", "files__edit_file"];

const formatSize = (bytes: number) => (bytes < 1024 ? `${bytes} B` : bytes < 1024 * 1024 ? `${(bytes / 1024).toFixed(1)} KB` : `${(bytes / 1024 / 1024).toFixed(1)} MB`);

// エージェント同士が受け渡したファイル。サブエージェントは親のフォルダを共有するので、親と同じ中身が出る
function SessionCard({ conversationId }: { conversationId: string }) {
  const [listing, setListing] = useState<SessionListing | null>(null);
  const [open, setOpen] = useState(true);
  useEffect(() => {
    let alive = true;
    const load = async () => {
      const next = await sessionList(conversationId);
      if (alive && next) setListing(next);
    };
    void load();
    const off = on("session.changed", (data: { conversationId: string }) => {
      if (!listing || data.conversationId === listing.rootId) void load();
    });
    return () => {
      alive = false;
      off();
    };
  }, [conversationId, listing?.rootId]);
  if (!listing || listing.files.length === 0) return null;
  return (
    <Card
      title="セッションのフォルダ"
      open={open}
      onToggle={() => setOpen((o) => !o)}
      aside={<span className="info-aside">{listing.files.length} 個</span>}
    >
      <ul className="info-list">
        {listing.files.map((f) => (
          <li key={f.id} className="clickable" title={`${f.id}\n${f.writtenByTitle ?? ""}`} onClick={() => void openSessionFile(conversationId, f.id)}>
            <FileCode size={15} />
            <span className="info-item-text">{f.path}</span>
            <span className="info-item-aside">{formatSize(f.size)}</span>
          </li>
        ))}
        <li className="clickable" onClick={() => void openSessionFile(conversationId)}>
          <FolderOpen size={15} />
          <span className="info-item-text muted">フォルダを開く</span>
        </li>
      </ul>
    </Card>
  );
}

function Card({ title, aside, open, onToggle, children }: { title: string; aside?: ReactNode; open?: boolean; onToggle: () => void; children?: ReactNode }) {
  // 中身のある札は畳め、中身の無い札は押すと詳しい画面へ移る
  const Chevron = children === undefined ? ChevronRight : open ? ChevronDown : ChevronRight;
  return (
    <section className="info-card">
      <button type="button" className="info-card-header" onClick={onToggle}>
        <span className="info-card-title">{title}</span>
        <Chevron size={14} className="info-card-chevron" />
        <span className="spacer" />
        {aside}
      </button>
      {open && children}
    </section>
  );
}

// Claude Code の右側の欄。環境、コンテキストの使用率、コネクタ、成果物を並べる
export function InfoPanel({ conversationId }: { conversationId: string }) {
  const conversation = useStore((s) => s.conversations[conversationId]);
  const settings = useStore((s) => s.settings);
  const mcp = useStore((s) => s.mcp);
  const folder = useStore((s) => s.folders.find((f) => f.id === conversation?.folderId));
  const model = useStore((s) => s.choice.model);
  const [envOpen, setEnvOpen] = useState(true);
  const [contextOpen, setContextOpen] = useState(false);
  const [outputsOpen, setOutputsOpen] = useState(true);

  // フォルダ操作で書いたファイルも、Claude Code と同じく成果物に並べる
  const writtenFiles = useMemo(() => {
    const paths = new Set<string>();
    for (const message of conversation?.messages ?? []) {
      if (!Array.isArray(message.content)) continue;
      for (const block of message.content) {
        if (block.type === "tool_use" && FILE_WRITE_TOOLS.includes(block.name) && typeof block.input?.path === "string") paths.add(block.input.path);
      }
    }
    return [...paths];
  }, [conversation?.messages]);

  if (!conversation || !settings) return null;
  const connected = mcp.filter((m) => m.status === "running").length;
  const limit = contextWindowOf(conversation.model ?? model);
  const used = conversation.usage.context ?? 0;
  const percent = Math.min(100, Math.round((used / limit) * 100));
  const folders = settings.files.enabled ? settings.files.folders : [];
  const codeFiles = Object.entries(conversation.files ?? {});
  const baseName = (path: string) => path.split(/[\\/]/).filter(Boolean).pop() ?? path;

  return (
    <aside className="info-panel">
      <Card title="環境" open={envOpen} onToggle={() => setEnvOpen((o) => !o)}>
        <ul className="info-list">
          <li>
            <Cloud size={15} />
            <span className="info-item-text">{MODE_LABELS[conversation.permissionMode]?.label ?? conversation.permissionMode}</span>
            <button type="button" className="icon-button tiny" title="権限の設定" onClick={() => openSettings("permissions")}><Settings2 size={13} /></button>
          </li>
          {folder && <li><FolderIcon size={15} /><span className="info-item-text">{folder.name}</span><span className="info-item-aside">プロジェクト</span></li>}
          {folders.map((f, i) => (
            <li key={f.path}>
              <FolderOpen size={15} />
              <span className="info-item-text" title={f.path}>{baseName(f.path)}</span>
              {i === 0 && <button type="button" className="icon-button tiny" title="フォルダを足す" onClick={() => openSettings("files")}><Plus size={13} /></button>}
            </li>
          ))}
          {folders.length === 0 && (
            <li>
              <FolderOpen size={15} />
              <span className="info-item-text muted">フォルダはまだありません</span>
              <button type="button" className="icon-button tiny" title="フォルダを足す" onClick={() => openSettings("files")}><Plus size={13} /></button>
            </li>
          )}
          {conversation.worktreePath && <li><GitBranch size={15} /><span className="info-item-text" title={conversation.worktreePath}>{baseName(conversation.worktreePath)}</span></li>}
        </ul>
      </Card>
      <Card
        title="コンテキスト"
        open={contextOpen}
        onToggle={() => setContextOpen((o) => !o)}
        aside={<span className="info-aside">{percent}%<span className="info-meter"><span style={{ width: `${Math.max(percent, used > 0 ? 4 : 0)}%` }} /></span></span>}
      >
        <div className="info-detail">
          <div>{tokens(used)} / {tokens(limit)} トークン</div>
          <div className="field-hint">最後に送った会話の大きさです。いっぱいに近づくと、古いところから自動で要約されます。</div>
          <button type="button" className="button small" onClick={() => void compact(conversationId)}>ここまでを要約する</button>
        </div>
      </Card>
      <Card title="コネクタ" onToggle={() => openSettings("connectors")} aside={<span className="info-aside">{connected}件接続済み</span>} />
      <SessionCard conversationId={conversationId} />
      {(conversation.artifacts.length > 0 || codeFiles.length > 0 || writtenFiles.length > 0) && (
        <Card title="成果物" open={outputsOpen} onToggle={() => setOutputsOpen((o) => !o)}>
          <ul className="info-list">
            {conversation.artifacts.map((a) => (
              <li key={a.id} className="clickable" onClick={() => openArtifact(conversationId, a.id)}>
                <Shapes size={15} />
                <span className="info-item-text">{a.title}</span>
                <span className="info-item-aside">Artifacts</span>
              </li>
            ))}
            {codeFiles.map(([id, path]) => (
              <li key={id} className="clickable" title={path} onClick={() => void openFile(conversationId, id)}>
                <FileCode size={15} />
                <span className="info-item-text">{baseName(path)}</span>
              </li>
            ))}
            {writtenFiles.map((path) => (
              <li key={path} title={path}>
                <FileCode size={15} />
                <span className="info-item-text">{baseName(path)}</span>
              </li>
            ))}
          </ul>
        </Card>
      )}
    </aside>
  );
}
