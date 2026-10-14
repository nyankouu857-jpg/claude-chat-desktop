import { ChartColumn, Ellipsis, FolderOpen, MessagesSquare, PanelLeft, Pencil, Plug, Plus, Settings, Star, Trash } from "lucide-react";
import { useState } from "react";
import { deleteConversation, navigate, openPath, openSettings, renameConversation, starConversation, toggleSidebar } from "../actions";
import { useStore } from "../store";
import type { ConversationSummary } from "../types";
import { Dialog, Dropdown, MenuItem } from "./ui";

export function initialsOf(name: string) {
  const trimmed = name.trim();
  if (!trimmed) return "あ";
  const words = trimmed.split(/\s+/);
  return words.length > 1 ? (words[0][0] + words[1][0]).toUpperCase() : trimmed.slice(0, 2).toUpperCase();
}

export function ConversationMenu({ summary, onRename, align = "right" }: { summary: ConversationSummary; onRename: () => void; align?: "left" | "right" }) {
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

function ConversationRow({ summary, active, running }: { summary: ConversationSummary; active: boolean; running: boolean }) {
  const [renaming, setRenaming] = useState(false);
  return (
    <>
      <div className={`conversation-row${active ? " active" : ""}`} onClick={() => navigate({ name: "chat", id: summary.id })}>
        <span className="conversation-title">{summary.title}</span>
        {running && <span className="running-dot" title="応答中" />}
        <ConversationMenu summary={summary} onRename={() => setRenaming(true)} />
      </div>
      {renaming && <RenameDialog summary={summary} onClose={() => setRenaming(false)} />}
    </>
  );
}

export function Sidebar() {
  const open = useStore((s) => s.sidebarOpen);
  const summaries = useStore((s) => s.summaries);
  const view = useStore((s) => s.view);
  const live = useStore((s) => s.live);
  const profile = useStore((s) => s.settings?.profile);
  const secrets = useStore((s) => s.secrets);
  const activeId = view.name === "chat" ? view.id : null;
  const starred = summaries.filter((s) => s.starred);
  const recents = summaries.filter((s) => !s.starred);
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
        {starred.length > 0 && (
          <>
            <div className="sidebar-section">スター付き</div>
            {starred.map((s) => <ConversationRow key={s.id} summary={s} active={s.id === activeId} running={!!live[s.id]} />)}
          </>
        )}
        {recents.length > 0 && <div className="sidebar-section">最近の項目</div>}
        {recents.map((s) => <ConversationRow key={s.id} summary={s} active={s.id === activeId} running={!!live[s.id]} />)}
      </div>
      {userMenu}
    </nav>
  );
}
