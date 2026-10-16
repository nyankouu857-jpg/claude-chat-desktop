import { Archive, ArchiveRestore, Bot, ChartColumn, Shapes, Keyboard, ChevronRight, CircleArrowUp, EllipsisVertical, Folder as FolderIcon, FolderInput, FolderOpen, LoaderCircle, MessagesSquare, PanelLeft, Pencil, Plug, Plus, Settings, Star, Trash, Workflow, X } from "lucide-react";
import { useEffect, useMemo, useState } from "react";
import { archiveConversation, askConfirm, confirmDeleteConversations, type BudgetView, deleteConversation, installUpdate, setTags, moveConversation, navigate, openPath, openSettings, renameConversation, starConversation, toggleSidebar } from "../actions";
import { call, on } from "../bridge";
import { setState, useStore, type AppState } from "../store";
import type { ChatStatus, ConversationSummary } from "../types";
import { Dialog, Dropdown, MenuItem } from "./ui";

// 料金の動きは応答の終わりに知らせが来る。それとは別に、日付が変わったときのためにときどき読み直す
const BUDGET_REFRESH_MS = 60_000;
// 応答中の読み直しの間隔。問い合わせごとに読むと多すぎる
const BUDGET_COST_DEBOUNCE_MS = 5_000;
// これを超えたら色を変えて、上限が近いと分かるようにする
const BUDGET_WARN_RATIO = 0.8;

function BudgetGauge() {
  const [budget, setBudget] = useState<BudgetView>();
  const settingsBudget = useStore((s: AppState) => s.settings?.budget);
  useEffect(() => {
    // 読めなかったときは前の値を出し続ける。毎分エラーを出して邪魔しない
    const load = async () => {
      try {
        setBudget(await call<BudgetView>("usage.budget"));
      } catch {
        // 次の読み直しに任せる
      }
    };
    void load();
    const timer = setInterval(() => void load(), BUDGET_REFRESH_MS);
    const off = on("conversations.changed", () => void load());
    // 応答中も料金が増えるたびに読み直す。続けて届いても読み直しは 1 度にまとめる
    let pending: ReturnType<typeof setTimeout> | null = null;
    const offCost = on("chat.event", ({ event }: { event: { type: string } }) => {
      if (event.type !== "cost" || pending) return;
      pending = setTimeout(() => {
        pending = null;
        void load();
      }, BUDGET_COST_DEBOUNCE_MS);
    });
    return () => {
      clearInterval(timer);
      if (pending) clearTimeout(pending);
      off();
      offCost();
    };
  }, [settingsBudget?.dailyLimitUsd, settingsBudget?.monthlyLimitUsd]);
  const overForecast = !!budget && budget.forecastReliable && budget.monthlyLimitUsd > 0 && budget.forecastUsd > budget.monthlyLimitUsd;
  if (!budget) return null;
  const row = (label: string, spent: number, limit: number) => {
    const ratio = limit > 0 ? Math.min(1, spent / limit) : 0;
    return (
      <div key={label}>
        <div className="row">
          <span className="label">{label}</span>
          <span>${spent.toFixed(2)}{limit > 0 ? ` / $${limit}` : ""}</span>
        </div>
        {limit > 0 && <div className={`bar${ratio >= BUDGET_WARN_RATIO ? " near" : ""}`}><span style={{ width: `${ratio * 100}%` }} /></div>}
      </div>
    );
  };
  return (
    <button type="button" className="budget-gauge" title="使用量と料金を開く" onClick={() => openSettings("usage")}>
      {row("今日", budget.todayUsd, budget.dailyLimitUsd)}
      {row("今月", budget.monthUsd, budget.monthlyLimitUsd)}
      {budget.monthUsd > 0 && (
        <div className={`row forecast${overForecast ? " over" : ""}`}>
          <span className="label">今月の見込み</span>
          <span>${budget.forecastUsd.toFixed(2)}</span>
        </div>
      )}
    </button>
  );
}

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
  const allFolders = useStore((s) => s.folders);
  const folders = allFolders.filter((f) => !f.archived || f.id === summary.folderId);
  const [target, setTarget] = useState<string | null>(summary.folderId ?? null);
  return (
    <Dialog
      title="プロジェクトへ移動"
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
          <span className="radio-title">プロジェクトに入れない</span>
        </label>
        {folders.map((f) => (
          <label key={f.id} className={`radio-row${target === f.id ? " selected" : ""}`}>
            <input type="radio" checked={target === f.id} onChange={() => setTarget(f.id)} />
            <span className="radio-title">{f.name}</span>
          </label>
        ))}
      </div>
      {folders.length === 0 && <div className="field-hint">プロジェクトはサイドバーの「プロジェクト」から作れます</div>}
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
          <EllipsisVertical size={16} />
        </button>
      )}
    >
      {(close) => (
        <>
          <MenuItem icon={<Star size={16} />} onClick={() => { close(); void starConversation(summary.id, !summary.starred); }}>
            {summary.starred ? "スターを外す" : "スターを付ける"}
          </MenuItem>
          <MenuItem icon={<Pencil size={16} />} onClick={() => { close(); onRename(); }}>名前を変更</MenuItem>
          {!summary.parentId && <MenuItem icon={<FolderInput size={16} />} onClick={() => { close(); onMove(); }}>プロジェクトへ移動</MenuItem>}
          {!summary.parentId && (
            <MenuItem icon={summary.archived ? <ArchiveRestore size={16} /> : <Archive size={16} />} onClick={() => { close(); void archiveConversation(summary.id, !summary.archived); }}>
              {summary.archived ? "アーカイブから戻す" : "アーカイブ"}
            </MenuItem>
          )}
          <MenuItem icon={<Trash size={16} />} danger onClick={() => {
            close();
            void (async () => { if (await confirmDeleteConversations([summary.id], `「${summary.title}」`)) await deleteConversation(summary.id); })();
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

// カンマか読点で区切って入力する。今ある目印を押すと足せる
export function TagsDialog({ summary, onClose }: { summary: { id: string; tags?: string[] }; onClose: () => void }) {
  const [text, setText] = useState((summary.tags ?? []).join(", "));
  const summaries = useStore((s) => s.summaries);
  const known = useMemo(() => [...new Set(summaries.flatMap((c) => c.tags ?? []))].sort(), [summaries]);
  const parse = (value: string) => value.split(/[,、，]/).map((t) => t.trim()).filter(Boolean);
  const save = async () => {
    await setTags(summary.id, parse(text));
    onClose();
  };
  const current = parse(text);
  return (
    <Dialog
      title="タグを編集"
      onClose={onClose}
      footer={
        <>
          <button type="button" className="button ghost" onClick={onClose}>キャンセル</button>
          <button type="button" className="button primary" onClick={save}>保存</button>
        </>
      }
    >
      <input className="input" autoFocus placeholder="例: 仕事, 調べ物" value={text} onChange={(e) => setText(e.target.value)} onKeyDown={(e) => e.key === "Enter" && !e.nativeEvent.isComposing && save()} />
      {known.length > 0 && (
        <div className="tag-row">
          {known.filter((t) => !current.includes(t)).map((t) => (
            <button key={t} type="button" className="tag-chip" onClick={() => setText([...current, t].join(", "))}>+ {t}</button>
          ))}
        </div>
      )}
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

export function Sidebar() {
  const open = useStore((s) => s.sidebarOpen);
  const summaries = useStore((s) => s.summaries);
  const view = useStore((s) => s.view);
  const settingsTab = useStore((s) => s.settingsOverlay?.tab);
  const folders = useStore((s) => s.folders);
  const profile = useStore((s) => s.settings?.profile);
  const update = useStore((s) => s.update);
  const secrets = useStore((s) => s.secrets);
  const activeId = view.name === "chat" ? view.id : null;
  const known = new Set(summaries.map((s) => s.id));
  const childrenOf = new Map<string, ConversationSummary[]>();
  for (const s of summaries) {
    if (s.parentId && known.has(s.parentId) && !s.archived) childrenOf.set(s.parentId, [...(childrenOf.get(s.parentId) ?? []), s]);
  }
  // 親が消えた子は一番上に出す。アーカイブしたものは「チャット」の画面にだけ出す
  const roots = summaries.filter((s) => (!s.parentId || !known.has(s.parentId)) && !s.archived);
  const starredProjects = folders.filter((f) => f.starred && !f.archived);
  const starred = roots.filter((s) => s.starred);
  // claude.ai と同じく、プロジェクトのチャットも最近の項目に並べる
  // 目印で絞り込む。絞り込んでいる間はスター付きも含めて、その目印のものだけを出す
  const [tagFilter, setTagFilter] = useState<string | null>(null);
  const allTags = [...new Set(roots.flatMap((s) => s.tags ?? []))].sort();
  // 絞り込んでいたタグを最後の会話から外したら、絞り込みも外れたことにする
  const activeTag = tagFilter && allTags.includes(tagFilter) ? tagFilter : null;
  const recents = activeTag ? roots.filter((s) => s.tags?.includes(activeTag)) : roots.filter((s) => !s.starred);
  const displayName = profile?.fullName || profile?.nickname || "あなた";

  const updateBanner = update && (
    <div className="update-banner" title={update.changes.length > 0 ? `変更点:\n${update.changes.join("\n")}` : undefined}>
      <div className="update-banner-head">
        {update.installing ? <LoaderCircle size={15} className="spin" /> : <CircleArrowUp size={15} />}
        <span>{update.installing ? "更新しています。終わると起動し直します" : "新しい版があります"}</span>
        {!update.installing && <button type="button" className="icon-button small" aria-label="閉じる" onClick={() => setState((s) => ({ ...s, update: null }))}><X size={14} /></button>}
      </div>
      {!update.installing && (
        <>
          {update.changes.length > 0 && <ul className="update-changes">{update.changes.slice(0, 3).map((c) => <li key={c}>{c}</li>)}</ul>}
          <button type="button" className="button primary small" onClick={async () => {
            if (await askConfirm("更新して起動し直しますか?", "新しい版をダウンロードして入れ替え、アプリを起動し直します。応答中のチャットは止まります。", "更新する")) await installUpdate();
          }}>更新して起動し直す</button>
        </>
      )}
    </div>
  );

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
          <MenuItem icon={<Keyboard size={16} />} onClick={() => { close(); setState((s) => ({ ...s, dialog: { kind: "shortcuts" } })); }}>キーボードショートカット</MenuItem>
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
        <button type="button" className="icon-button" aria-label="プロジェクト" onClick={() => navigate({ name: "projects" })}><FolderIcon size={18} /></button>
        <button type="button" className="icon-button" aria-label="アーティファクト" onClick={() => navigate({ name: "artifacts" })}><Shapes size={18} /></button>
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
        <button type="button" className={`nav-link${view.name === "projects" || view.name === "project" ? " active" : ""}`} onClick={() => navigate({ name: "projects" })}>
          <FolderIcon size={17} /> プロジェクト
        </button>
        <button type="button" className={`nav-link${view.name === "artifacts" ? " active" : ""}`} onClick={() => navigate({ name: "artifacts" })}>
          <Shapes size={17} /> アーティファクト
        </button>
        <button type="button" className={`nav-link${settingsTab === "connectors" ? " active" : ""}`} onClick={() => openSettings("connectors")}>
          <Plug size={17} /> コネクタ
        </button>
        <button type="button" className={`nav-link${settingsTab === "usage" ? " active" : ""}`} onClick={() => openSettings("usage")}>
          <ChartColumn size={17} /> 使用量
        </button>
      </div>
      <div className="sidebar-scroll">
        {!activeTag && (starred.length > 0 || starredProjects.length > 0) && (
          <>
            <div className="sidebar-section">スター付き</div>
            {starredProjects.map((f) => (
              <div key={f.id} className={`folder-row${view.name === "project" && view.id === f.id ? " active" : ""}`} onClick={() => navigate({ name: "project", id: f.id })}>
                <FolderIcon size={15} />
                <span className="conversation-title">{f.name}</span>
              </div>
            ))}
            {starred.map((s) => <ConversationTree key={s.id} summary={s} depth={0} activeId={activeId} childrenOf={childrenOf} />)}
          </>
        )}
        {allTags.length > 0 && (
          <div className="tag-row sidebar-tags">
            {allTags.map((t) => (
              <button key={t} type="button" className={`tag-chip${activeTag === t ? " on" : ""}`} onClick={() => setTagFilter(activeTag === t ? null : t)}>{t}</button>
            ))}
          </div>
        )}
        {recents.length > 0 && <div className="sidebar-section">{activeTag ? `タグ: ${activeTag}` : "最近の項目"}</div>}
        {recents.map((s) => <ConversationTree key={s.id} summary={s} depth={0} activeId={activeId} childrenOf={childrenOf} />)}
      </div>
      <BudgetGauge />
      {updateBanner}
      {userMenu}
    </nav>
  );
}
