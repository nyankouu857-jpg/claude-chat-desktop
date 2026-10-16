import { AppWindow, Tag, ArrowDown, ArrowLeft, ChevronDown, FileDown, Folder as FolderIcon, FolderInput, Ghost, GitBranch, ListTree, LoaderCircle, PanelRight, Pencil, Play, Square, Star, Trash, Workflow, X } from "lucide-react";
import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { SINGLE_CHAT, askConfirm, compact, confirmDeleteConversations, deleteConversation, dismissInterrupted, exportConversation, navigate, openChatWindow, resume, starConversation, stop, stopWorkflow, toggleInfoPanel } from "../actions";
import { setState, useStore } from "../store";
import { Composer } from "./Composer";
import { ChatSearch } from "./ChatSearch";
import { MessageList } from "./Messages";
import { MoveDialog, RenameDialog, STATUS_LABELS, StatusDot, TagsDialog } from "./Sidebar";
import { Dropdown, MenuItem, tokens, usd } from "./ui";

// リサーチのように、会話の外で動いているワークフローの進み具合
function RunningWorkflows({ conversationId }: { conversationId: string }) {
  const workflows = useStore((s) => s.workflows);
  const runs = Object.values(workflows).filter((w) => w.parentId === conversationId && w.status === "running");
  if (runs.length === 0) return null;
  return (
    <div className="running-workflows">
      {runs.map((w) => {
        const done = w.agents.filter((a) => a.status === "done").length;
        return (
          <div key={w.id} className="running-workflow">
            <LoaderCircle size={14} className="spin" />
            <span className="running-workflow-title">{w.title}</span>
            <span className="field-hint">{w.phase ?? ""}{w.agents.length > 0 ? ` ・ エージェント ${done}/${w.agents.length}` : ""}</span>
            <button type="button" className="button small" onClick={() => void stopWorkflow(w.id)}><Square size={11} /> 止める</button>
          </div>
        );
      })}
    </div>
  );
}

export function ChatView({ id }: { id: string }) {
  const infoOpen = useStore((s) => s.infoPanelOpen);
  const conversation = useStore((s) => s.conversations[id]);
  const live = useStore((s) => s.live[id]);
  const [renaming, setRenaming] = useState(false);
  const [tagging, setTagging] = useState(false);
  const [resuming, setResuming] = useState(false);
  const [moving, setMoving] = useState(false);
  const folder = useStore((s) => s.folders.find((f) => f.id === conversation?.folderId));
  const parent = useStore((s) => (conversation?.parentId ? s.summaries.find((p) => p.id === conversation.parentId) : undefined));
  const status = useStore((s) => s.statuses[id]);
  const scroller = useRef<HTMLDivElement>(null);
  const pinned = useRef(true);
  const [awayFromBottom, setAwayFromBottom] = useState(false);

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
    <div className={`chat-view${conversation?.incognito ? " incognito" : ""}`}>
      <header className="chat-header">
        {parent && (
          <button type="button" className="crumb" title="親のチャットへ" onClick={() => navigate({ name: "chat", id: parent.id })}>
            <ArrowLeft size={14} /> {parent.title}
          </button>
        )}
        {!parent && folder && (
          <button type="button" className="crumb" title="プロジェクトを開く" onClick={() => navigate({ name: "project", id: folder.id })}>
            <FolderIcon size={14} /> {folder.name}
          </button>
        )}
        {conversation?.incognito && <span className="incognito-label"><Ghost size={15} /> シークレットチャット</span>}
        {conversation && !conversation.incognito && (
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
                {!conversation.incognito && <MenuItem icon={<Tag size={16} />} onClick={() => { close(); setTagging(true); }}>タグを編集</MenuItem>}
                {!conversation.parentId && <MenuItem icon={<FolderInput size={16} />} onClick={() => { close(); setMoving(true); }}>プロジェクトへ移動</MenuItem>}
                <MenuItem icon={<Workflow size={16} />} onClick={() => { close(); setState((s) => ({ ...s, dialog: { kind: "tasks", conversationId: id } })); }}>タスク一覧</MenuItem>
                <MenuItem icon={<ListTree size={16} />} onClick={() => { close(); void compact(id); }}>ここまでを要約する</MenuItem>
                <MenuItem icon={<FileDown size={16} />} onClick={() => { close(); void exportConversation(id); }}>Markdown に書き出す</MenuItem>
                <MenuItem icon={<FileDown size={16} />} onClick={() => { close(); void exportConversation(id, "html"); }}>HTML に書き出す</MenuItem>
                <MenuItem icon={<FileDown size={16} />} onClick={() => { close(); void exportConversation(id, "pdf"); }}>PDF に書き出す</MenuItem>
                {!SINGLE_CHAT && !conversation.incognito && <MenuItem icon={<AppWindow size={16} />} onClick={() => { close(); void openChatWindow(id); }}>別の窓で開く</MenuItem>}
                <MenuItem icon={<Trash size={16} />} danger onClick={() => {
                  close();
                  void (async () => { if (await confirmDeleteConversations([id], `「${title}」`)) await deleteConversation(id); })();
                }}>削除</MenuItem>
              </>
            )}
          </Dropdown>
        )}
        {conversation?.tags?.map((t) => <span key={t} className="tag-chip static">{t}</span>)}
        {status && status !== "idle" && status !== "running" && <span className="status-chip"><StatusDot status={status} /> {STATUS_LABELS[status]}</span>}
        <span className="spacer" />
        {conversation?.worktreePath && <span className="cost-chip" title={conversation.worktreePath}><GitBranch size={13} /> ワークツリー</span>}
        {conversation && conversation.usage.costUsd > 0 && (
          <span
            className="cost-chip"
            title={`入力 ${tokens(conversation.usage.input)} / 出力 ${tokens(conversation.usage.output)} / キャッシュ読み込み ${tokens(conversation.usage.cacheRead)} トークン`
              + (conversation.usage.agentsCostUsd ? `\nうちサブエージェント ${usd(conversation.usage.agentsCostUsd)}` : "")}
          >
            {usd(conversation.usage.costUsd)}
          </span>
        )}
        {conversation?.incognito && (
          <button type="button" className="icon-button" title="閉じて消す" onClick={async () => {
            if (live) await stop(id);
            await deleteConversation(id);
            navigate({ name: "new", incognito: true });
          }}><X size={16} /></button>
        )}
        <button type="button" className={`icon-button${infoOpen ? " on" : ""}`} title={infoOpen ? "右の欄を閉じる" : "右の欄を開く"} onClick={toggleInfoPanel}><PanelRight size={17} /></button>
      </header>
      <ChatSearch scroller={scroller} />
      <div
        className="chat-scroll"
        ref={scroller}
        onScroll={(e) => {
          const el = e.currentTarget;
          pinned.current = el.scrollHeight - el.scrollTop - el.clientHeight < 120;
          setAwayFromBottom(!pinned.current);
        }}
      >
        <MessageList conversationId={id} />
      </div>
      <div className="composer-dock">
        {awayFromBottom && (
          <button type="button" className="scroll-bottom" aria-label="いちばん下へ" onClick={() => {
            pinned.current = true;
            setAwayFromBottom(false);
            scroller.current?.scrollTo({ top: scroller.current.scrollHeight, behavior: "smooth" });
          }}><ArrowDown size={16} /></button>
        )}
        <RunningWorkflows conversationId={id} />
        {conversation?.lastRunStatus === "interrupted" && !live && (
          <div className="resume-banner">
            <span>前回、応答の途中でアプリが終了しました。</span>
            <button type="button" className="button small primary" disabled={resuming} onClick={async () => {
              setResuming(true);
              await resume(id);
              setResuming(false);
            }}><Play size={13} /> 続きから再開</button>
            <button type="button" className="icon-button" title="再開しない" onClick={() => void dismissInterrupted(id)}><X size={14} /></button>
          </div>
        )}
        <Composer conversationId={id} autoFocus={!live} />
      </div>
      {renaming && conversation && <RenameDialog summary={conversation} onClose={() => setRenaming(false)} />}
      {tagging && conversation && <TagsDialog summary={conversation} onClose={() => setTagging(false)} />}
      {moving && conversation && <MoveDialog summary={conversation} onClose={() => setMoving(false)} />}
    </div>
  );
}
