import { Plus, Search, Star } from "lucide-react";
import { useEffect, useState } from "react";
import { askConfirm, deleteConversation, navigate, searchConversations } from "../actions";
import { useStore } from "../store";
import type { ConversationSummary } from "../types";
import { ConversationMenu, MoveDialog, RenameDialog } from "./Sidebar";
import { relativeTime } from "./ui";

function Row({ summary, snippet, selecting, selected, onSelect }: { summary: ConversationSummary; snippet?: string; selecting: boolean; selected: boolean; onSelect: (on: boolean) => void }) {
  const [renaming, setRenaming] = useState(false);
  const [moving, setMoving] = useState(false);
  const folder = useStore((s) => s.folders.find((f) => f.id === summary.folderId));
  return (
    <>
      <div className={`recent-row${selected ? " selected" : ""}`} onClick={() => (selecting ? onSelect(!selected) : navigate({ name: "chat", id: summary.id }))}>
        {selecting && <input type="checkbox" className="recent-check" checked={selected} onChange={(e) => onSelect(e.target.checked)} onClick={(e) => e.stopPropagation()} />}
        <div className="recent-main">
          <div className="recent-title">
            {summary.starred && <Star size={13} className="star-filled" />}
            {summary.title}
          </div>
          {snippet && <div className="recent-snippet">…{snippet}…</div>}
          <div className="recent-meta">{folder ? `${folder.name} ・ ` : ""}最終更新 {relativeTime(summary.updatedAt)}</div>
        </div>
        {!selecting && <ConversationMenu summary={summary} onRename={() => setRenaming(true)} onMove={() => setMoving(true)} />}
      </div>
      {renaming && <RenameDialog summary={summary} onClose={() => setRenaming(false)} />}
      {moving && <MoveDialog summary={summary} onClose={() => setMoving(false)} />}
    </>
  );
}

// 本文の検索は打ち終わるのを少し待ってから
const SEARCH_DELAY_MS = 250;

export function Recents() {
  const summaries = useStore((s) => s.summaries);
  const [query, setQuery] = useState("");
  const [bodyHits, setBodyHits] = useState<Record<string, string> | null>(null);
  const [selecting, setSelecting] = useState(false);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const q = query.trim().toLowerCase();

  // claude.ai と同じく本文まで探す。題名と最初の発言はすぐに絞り込み、本文の分は届いたら足す
  useEffect(() => {
    setBodyHits(null);
    if (!q) return;
    let cancelled = false;
    const timer = setTimeout(async () => {
      const hits = await searchConversations(q);
      if (!cancelled && hits) setBodyHits(Object.fromEntries(hits.map((h) => [h.id, h.snippet])));
    }, SEARCH_DELAY_MS);
    return () => { cancelled = true; clearTimeout(timer); };
  }, [q]);

  const filtered = q
    ? summaries.filter((s) => s.title.toLowerCase().includes(q) || s.preview.toLowerCase().includes(q) || (bodyHits && s.id in bodyHits))
    : summaries;
  const toggle = (id: string, on: boolean) => setSelected((current) => {
    const next = new Set(current);
    if (on) next.add(id);
    else next.delete(id);
    return next;
  });
  const deleteSelected = async () => {
    if (!(await askConfirm("チャットを削除", `${selected.size} 件のチャットを削除しますか? この操作は取り消せません。`))) return;
    for (const id of selected) await deleteConversation(id);
    setSelected(new Set());
    setSelecting(false);
  };

  return (
    <div className="page">
      <div className="page-inner">
        <div className="page-header">
          <h1 className="page-title">チャット</h1>
          <button type="button" className="button primary" onClick={() => navigate({ name: "new" })}><Plus size={16} /> 新しいチャット</button>
        </div>
        <label className="search-box">
          <Search size={16} />
          <input autoFocus placeholder="チャットを検索..." value={query} onChange={(e) => setQuery(e.target.value)} />
        </label>
        <div className="recent-toolbar">
          <span className="recent-count">{q ? `${filtered.length} 件見つかりました` : `${summaries.length} 件のチャット`}</span>
          <span className="spacer" />
          {selecting ? (
            <>
              <button type="button" className="link-button" onClick={() => setSelected(selected.size === filtered.length ? new Set() : new Set(filtered.map((s) => s.id)))}>
                {selected.size === filtered.length && filtered.length > 0 ? "選択を外す" : "すべて選択"}
              </button>
              <button type="button" className="button small danger" disabled={selected.size === 0} onClick={() => void deleteSelected()}>削除 ({selected.size})</button>
              <button type="button" className="button small ghost" onClick={() => { setSelecting(false); setSelected(new Set()); }}>キャンセル</button>
            </>
          ) : (
            <button type="button" className="link-button" onClick={() => setSelecting(true)}>選択</button>
          )}
        </div>
        <div className="recent-list">
          {filtered.map((s) => (
            <Row key={s.id} summary={s} snippet={bodyHits?.[s.id]} selecting={selecting} selected={selected.has(s.id)} onSelect={(on) => toggle(s.id, on)} />
          ))}
        </div>
      </div>
    </div>
  );
}
