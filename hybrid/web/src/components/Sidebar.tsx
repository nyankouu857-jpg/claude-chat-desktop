import { Bot, ChartColumn, ChevronRight, Ellipsis, Folder as FolderIcon, FolderInput, FolderOpen, MessagesSquare, PanelLeft, Pencil, Plug, Plus, Settings, Star, Trash, Workflow } from "lucide-react";
import { useState } from "react";
import { deleteConversation, deleteFolder, moveConversation, navigate, openPath, openSettings, renameConversation, starConversation, toggleSidebar } from "../actions";
import { setState, useStore } from "../store";
import type { ChatStatus, ConversationSummary, Folder } from "../types";
import { Dialog, Dropdown, MenuItem } from "./ui";

export const STATUS_LABELS: Record<ChatStatus, string> = {
  running: "応答中",
  waiting: "確認待ち",
  idle: "待機中",
  failed: "失敗",
  stopped: "停止",
};

export function StatusDot({ status }: { status?: ChatStatus }) {
  if (!status || status === "idle") return null;
  return <span className={`status-dot ${status}`} title={STATUS_LABELS[status]} />;
}

export function MoveDialog({ summary, onClose }: { summary: { id: string; title: string; folderId?: string | null }; onClose: () => void }) {
  const folders = useStore((s) => s.folders);
  const [target, setTarget] = useState<string | null>(summary.folderId ?? null);
  return (
    <Dialog
      title="フォルダへ移動"
      onClose={onClose}
      footer={
        <>
          <button type="button" className="button ghost" onClick={onClose}>キャンセル</button>
          <button type="button" className="button primary" onClick={async () => { await moveConversation(summary.id, target); onClose(); }}>移動</button>
        </>
      }
    >
      <div className="radio-list">
        <label className={`radio-row${target === null ? " selected" : ""}`}>
          <input type="radio" checked={target === null} onChange={() => setTarget(null)} />
          <span className="radio-title">フォルダに入れない</span>
        </label>
        {folders.map((f) => (
          <label key={f.id} className={`radio-row${target === f.id ? " selected" : ""}`}>
            <input type="radio" checked={target === f.id} onChange={() => setTarget(f.id)} />
            <span className="radio-title">{f.name}</span>
          </label>
        ))}
      </div>
      {folders.length === 0 && <div className="field-hint">フォルダはサイドバーの「フォルダ」の + から作れます</div>}
    </Dialog>
  );
}

export function initialsOf(name: string) {
  const trimmed = name.trim();
  if (!trimmed) return "あ";
  const words = trimmed.split(/\s+/);
  return words.length > 1 ? (words[0][0] + words[1][0]).toUpperCase() : trimmed.slice(0, 2).toUpperCase();
}

export function ConversationMenu({ summary, onRename, onMove, align = "right" }: { summary: ConversationSummary; onRename: () => void; onMove: () => void; align?: "left" | "right" }) {
  return (
    <Dropdown
      align={align}
      trigger={(open, toggle) => (
        <button type="button" className={`icon-button small row-menu${open ? " open" : ""}`} aria-label="メニュー" onClick={(e) => { e.stopPropagation(); toggle(); }}>
          <Ellipsis size={16} />
        </button>
      )}
    >
      {(close) => (
        <>
          <MenuItem icon={<Star size={16} />} onClick={() => { close(); void starConversation(summary.id, !summary.starred); }}>
            {summary.starred ? "スターを外す" : "スターを付ける"}
          </MenuItem>
          <MenuItem icon={<Pencil size={16} />} onClick={() => { close(); onRename(); }}>名前を変更</MenuItem>
          {!summary.parentId && <MenuItem icon={<FolderInput size={16} />} onClick={() => { close(); onMove(); }}>フォルダへ移動</MenuItem>}
          <MenuItem icon={<Trash size={16} />} danger onClick={() => {
            close();
            if (confirm(`${summary.title} を削除しますか?`)) void deleteConversation(summary.id);
          }}>削除</MenuItem>
        </>
      )}
    </Dropdown>
  );
}

export function RenameDialog({ summary, onClose }: { summary: { id: string; title: string }; onClose: () => void }) {
  const [title, setTitle] = useState(summary.title);
  const save = async () => {
    if (title.trim()) await renameConversation(summary.id, title.trim());
    onClose();
  };
  return (
    <Dialog
      title="チャットの名前を変更"
      onClose={onClose}
      footer={
        <>
          <button type="button" className="button ghost" onClick={onClose}>キャンセル</button>
          <button type="button" className="button primary" onClick={save}>保存</button>
        </>
      }
    >
      <input className="input" autoFocus value={title} onChange={(e) => setTitle(e.target.value)} onKeyDown={(e) => e.key === "Enter" && !e.nativeEvent.isComposing && save()} />
    </Dialog>
  );
}

function KindIcon({ kind }: { kind: ConversationSummary["kind"] }) {
  if (kind === "agent") return <Bot size={14} className="row-kind" />;
  if (kind === "workflow") return <Workflow size={14} className="row-kind" />;
  return null;
}

// 子のエージェントは親の下に畳んで並べる。動いている子がいれば開いておく
function ConversationTree({ summary, depth, activeId, childrenOf }: { summary: ConversationSummary; depth: number; activeId: string | null; childrenOf: Map<string, ConversationSummary[]> }) {
  const statuses = useStore((s) => s.statuses);
  const [renaming, setRenaming] = useState(false);
  const [moving, setMoving] = useState(false);
  const kids = childrenOf.get(summary.id) ?? [];
  const busyChild = kids.some((k) => statuses[k.id] === "running" || statuses[k.id] === "waiting" || k.id === activeId);
  const [expanded, setExpanded] = useState<boolean | null>(null);
  const open = expanded ?? busyChild;
  return (
    <>
      <div className={`conversation-row${summary.id === activeId ? " active" : ""}`} style={{ paddingLeft: 10 + depth * 16 }} onClick={() => navigate({ name: "chat", id: summary.id })}>
        {kids.length > 0 ? (
          <button type="button" className="icon-button tiny tree-toggle" aria-label={open ? "畳む" : "開く"} onClick={(e) => { e.stopPropagation(); setExpanded(!open); }}>
            <ChevronRight size={13} className={open ? "rotated" : ""} />
          </button>
        ) : depth > 0 && <span className="tree-spacer" />}
        <KindIcon kind={summary.kind} />
        <span className="conversation-title">{summary.title}</span>
        <StatusDot status={statuses[summary.id]} />
        <ConversationMenu summary={summary} onRename={() => setRenaming(true)} onMove={() => setMoving(true)} />
      </div>
      {open && kids.map((k) => <ConversationTree key={k.id} summary={k} depth={depth + 1} activeId={activeId} childrenOf={childrenOf} />)}
      {renaming && <RenameDialog summary={summary} onClose={() => setRenaming(false)} />}
      {moving && <MoveDialog summary={summary} onClose={() => setMoving(false)} />}
    </>
  );
}

function FolderSection({ folder, chats, activeId, childrenOf }: { folder: Folder; chats: ConversationSummary[]; activeId: string | null; childrenOf: Map<string, ConversationSummary[]> }) {
  const view = useStore((s) => s.view);
  // 子の会話を開いているときも、親のいるフォルダは開いたままにする
  const containsActive = chats.some((c) => c.id === activeId || (childrenOf.get(c.id) ?? []).some((k) => k.id === activeId))
    || (view.name === "new" && view.folderId === folder.id);
  const [expanded, setExpanded] = useState<boolean | null>(null);
  const open = expanded ?? containsActive;
  return (
    <>
      <div className={`folder-row${view.name === "new" && view.folderId === folder.id ? " active" : ""}`} onClick={() => setExpanded(!open)}>
        <ChevronRight size={13} className={`folder-chevron${open ? " rotated" : ""}`} />
        <FolderIcon size={15} />
        <span className="conversation-title">{folder.name}</span>
        <span className="folder-count">{chats.length}</span>
        <Dropdown
          align="right"
          trigger={(menuOpen, toggle) => (
            <button type="button" className={`icon-button small row-menu${menuOpen ? " open" : ""}`} aria-label="フォルダのメニュー" onClick={(e) => { e.stopPropagation(); toggle(); }}>
              <Ellipsis size={16} />
            </button>
          )}
        >
          {(close) => (
            <>
              <MenuItem icon={<Plus size={16} />} onClick={() => { close(); navigate({ name: "new", folderId: folder.id }); }}>このフォルダで新しいチャット</MenuItem>
              <MenuItem icon={<Pencil size={16} />} onClick={() => { close(); setState((s) => ({ ...s, dialog: { kind: "folder", folderId: folder.id } })); }}>指示と通信の設定</MenuItem>
              <MenuItem icon={<Trash size={16} />} danger onClick={() => {
                close();
                if (confirm(`フォルダ「${folder.name}」を削除しますか? 中のチャットは消えずにフォルダの外へ出ます`)) void deleteFolder(folder.id);
              }}>フォルダを削除</MenuItem>
            </>
          )}
        </Dropdown>
      </div>
      {open && (
        <div className="folder-children">
          {chats.length === 0 && <div className="folder-empty">チャットはまだありません</div>}
          {chats.map((c) => <ConversationTree key={c.id} summary={c} depth={1} activeId={activeId} childrenOf={childrenOf} />)}
        </div>
      )}
    </>
  );
}

export function Sidebar() {
  const open = useStore((s) => s.sidebarOpen);
  const summaries = useStore((s) => s.summaries);
  const view = useStore((s) => s.view);
  const folders = useStore((s) => s.folders);
  const profile = useStore((s) => s.settings?.profile);
  const secrets = useStore((s) => s.secrets);
  const activeId = view.name === "chat" ? view.id : null;
  const known = new Set(summaries.map((s) => s.id));
  const childrenOf = new Map<string, ConversationSummary[]>();
  for (const s of summaries) {
    if (s.parentId && known.has(s.parentId)) childrenOf.set(s.parentId, [...(childrenOf.get(s.parentId) ?? []), s]);
  }
  // 親が消えた子は一番上に出す
  const roots = summaries.filter((s) => !s.parentId || !known.has(s.parentId));
  const folderIds = new Set(folders.map((f) => f.id));
  const starred = roots.filter((s) => s.starred);
  const recents = roots.filter((s) => !s.starred && !(s.folderId && folderIds.has(s.folderId)));
  const displayName = profile?.fullName || profile?.nickname || "あなた";

  const userMenu = (
    <Dropdown
      direction="up"
      className="user-menu"
      trigger={(_, toggle) => (
        <button type="button" className="user-button" onClick={toggle}>
          <span className="avatar">{initialsOf(displayName)}</span>
          {open && (
            <span className="user-text">
              <span className="user-name">{displayName}</span>
              <span className="user-plan">{secrets?.hasApiKey ? "API 従量課金" : "APIキー未設定"}</span>
            </span>
          )}
        </button>
      )}
    >
      {(close) => (
        <>
          <MenuItem icon={<Settings size={16} />} onClick={() => { close(); openSettings("general"); }}>設定</MenuItem>
          <MenuItem icon={<ChartColumn size={16} />} onClick={() => { close(); openSettings("usage"); }}>使用量と料金</MenuItem>
          <MenuItem icon={<FolderOpen size={16} />} onClick={() => { close(); void openPath("root"); }}>アプリのフォルダを開く</MenuItem>
        </>
      )}
    </Dropdown>
  );

  if (!open) {
    return (
      <nav className="sidebar collapsed">
        <button type="button" className="icon-button" aria-label="サイドバーを開く" onClick={toggleSidebar}><PanelLeft size={18} /></button>
        <button type="button" className="new-chat-icon" aria-label="新しいチャット" onClick={() => navigate({ name: "new" })}><Plus size={16} /></button>
        <button type="button" className="icon-button" aria-label="チャット" onClick={() => navigate({ name: "recents" })}><MessagesSquare size={18} /></button>
        <div className="sidebar-spacer" />
        {userMenu}
      </nav>
    );
  }

  return (
    <nav className="sidebar">
      <div className="sidebar-header">
        <span className="wordmark">Claude</span>
        <button type="button" className="icon-button" aria-label="サイドバーを閉じる" onClick={toggleSidebar}><PanelLeft size={18} /></button>
      </div>
      <button type="button" className="new-chat" onClick={() => navigate({ name: "new" })}>
        <span className="new-chat-icon"><Plus size={14} /></span>
        新しいチャット
      </button>
      <div className="nav-links">
        <button type="button" className={`nav-link${view.name === "recents" ? " active" : ""}`} onClick={() => navigate({ name: "recents" })}>
          <MessagesSquare size={17} /> チャット
        </button>
        <button type="button" className={`nav-link${view.name === "settings" && view.tab === "connectors" ? " active" : ""}`} onClick={() => openSettings("connectors")}>
          <Plug size={17} /> コネクタ
        </button>
        <button type="button" className={`nav-link${view.name === "settings" && view.tab === "usage" ? " active" : ""}`} onClick={() => openSettings("usage")}>
          <ChartColumn size={17} /> 使用量
        </button>
      </div>
      <div className="sidebar-scroll">
        <div className="sidebar-section with-action">
          フォルダ
          <button type="button" className="icon-button tiny" aria-label="フォルダを作る" title="フォルダを作る" onClick={() => setState((s) => ({ ...s, dialog: { kind: "folder" } }))}><Plus size={14} /></button>
        </div>
        {folders.map((f) => (
          <FolderSection key={f.id} folder={f} chats={roots.filter((s) => s.folderId === f.id && !s.starred)} activeId={activeId} childrenOf={childrenOf} />
        ))}
        {starred.length > 0 && (
          <>
            <div className="sidebar-section">スター付き</div>
            {starred.map((s) => <ConversationTree key={s.id} summary={s} depth={0} activeId={activeId} childrenOf={childrenOf} />)}
          </>
        )}
        {recents.length > 0 && <div className="sidebar-section">最近の項目</div>}
        {recents.map((s) => <ConversationTree key={s.id} summary={s} depth={0} activeId={activeId} childrenOf={childrenOf} />)}
      </div>
      {userMenu}
    </nav>
  );
}
