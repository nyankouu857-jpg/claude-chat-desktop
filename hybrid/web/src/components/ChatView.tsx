import { ArrowLeft, ChevronDown, FileDown, Folder as FolderIcon, FolderInput, GitBranch, ListTree, Pencil, Star, Trash, Workflow } from "lucide-react";
import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { compact, deleteConversation, exportConversation, navigate, starConversation } from "../actions";
import { setState, useStore } from "../store";
import { Composer } from "./Composer";
import { MessageList } from "./Messages";
import { MoveDialog, RenameDialog, STATUS_LABELS, StatusDot } from "./Sidebar";
import { Dropdown, MenuItem, tokens, usd } from "./ui";

export function ChatView({ id }: { id: string }) {
  const conversation = useStore((s) => s.conversations[id]);
  const live = useStore((s) => s.live[id]);
  const [renaming, setRenaming] = useState(false);
  const [moving, setMoving] = useState(false);
  const folder = useStore((s) => s.folders.find((f) => f.id === conversation?.folderId));
  const parent = useStore((s) => (conversation?.parentId ? s.summaries.find((p) => p.id === conversation.parentId) : undefined));
  const status = useStore((s) => s.statuses[id]);
  const scroller = useRef<HTMLDivElement>(null);
  const pinned = useRef(true);

  useEffect(() => {
    pinned.current = true;
  }, [id]);

  // 読み返しているときに勝手に下へ飛ばないよう、最下部付近にいるときだけ追従する
  useLayoutEffect(() => {
    const el = scroller.current;
    if (el && pinned.current) el.scrollTop = el.scrollHeight;
  });

  const title = conversation?.title ?? "";
  return (
    <div className="chat-view">
      <header className="chat-header">
        {parent && (
          <button type="button" className="crumb" title="親のチャットへ" onClick={() => navigate({ name: "chat", id: parent.id })}>
            <ArrowLeft size={14} /> {parent.title}
          </button>
        )}
        {!parent && folder && (
          <button type="button" className="crumb" title="このフォルダで新しいチャット" onClick={() => navigate({ name: "new", folderId: folder.id })}>
            <FolderIcon size={14} /> {folder.name}
          </button>
        )}
        {conversation && (
          <Dropdown
            trigger={(open, toggle) => (
              <button type="button" className={`title-button${open ? " open" : ""}`} onClick={toggle}>
                {conversation.starred && <Star size={14} className="star-filled" />}
                <span className="title-text">{title}</span>
                <ChevronDown size={14} />
              </button>
            )}
          >
            {(close) => (
              <>
                <MenuItem icon={<Star size={16} />} onClick={() => { close(); void starConversation(id, !conversation.starred); }}>
                  {conversation.starred ? "スターを外す" : "スターを付ける"}
                </MenuItem>
                <MenuItem icon={<Pencil size={16} />} onClick={() => { close(); setRenaming(true); }}>名前を変更</MenuItem>
                {!conversation.parentId && <MenuItem icon={<FolderInput size={16} />} onClick={() => { close(); setMoving(true); }}>フォルダへ移動</MenuItem>}
                <MenuItem icon={<Workflow size={16} />} onClick={() => { close(); setState((s) => ({ ...s, dialog: { kind: "tasks", conversationId: id } })); }}>タスク一覧</MenuItem>
                <MenuItem icon={<ListTree size={16} />} onClick={() => { close(); void compact(id); }}>ここまでを要約する</MenuItem>
                <MenuItem icon={<FileDown size={16} />} onClick={() => { close(); void exportConversation(id); }}>Markdown に書き出す</MenuItem>
                <MenuItem icon={<Trash size={16} />} danger onClick={() => {
                  close();
                  if (confirm(`${title} を削除しますか?`)) void deleteConversation(id);
                }}>削除</MenuItem>
              </>
            )}
          </Dropdown>
        )}
        {status && status !== "idle" && status !== "running" && <span className="status-chip"><StatusDot status={status} /> {STATUS_LABELS[status]}</span>}
        <span className="spacer" />
        {conversation?.worktreePath && <span className="cost-chip" title={conversation.worktreePath}><GitBranch size={13} /> ワークツリー</span>}
        {conversation && conversation.usage.costUsd > 0 && (
          <span
            className="cost-chip"
            title={`入力 ${tokens(conversation.usage.input)} / 出力 ${tokens(conversation.usage.output)} / キャッシュ読み込み ${tokens(conversation.usage.cacheRead)} トークン`}
          >
            {usd(conversation.usage.costUsd)}
          </span>
        )}
      </header>
      <div
        className="chat-scroll"
        ref={scroller}
        onScroll={(e) => {
          const el = e.currentTarget;
          pinned.current = el.scrollHeight - el.scrollTop - el.clientHeight < 120;
        }}
      >
        <MessageList conversationId={id} />
      </div>
      <div className="composer-dock">
        <Composer conversationId={id} autoFocus={!live} />
      </div>
      {renaming && conversation && <RenameDialog summary={conversation} onClose={() => setRenaming(false)} />}
      {moving && conversation && <MoveDialog summary={conversation} onClose={() => setMoving(false)} />}
    </div>
  );
}
