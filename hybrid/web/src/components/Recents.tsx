import { Plus, Search, Star } from "lucide-react";
import { useState } from "react";
import { navigate } from "../actions";
import { useStore } from "../store";
import type { ConversationSummary } from "../types";
import { ConversationMenu, RenameDialog } from "./Sidebar";
import { relativeTime } from "./ui";

function Row({ summary }: { summary: ConversationSummary }) {
  const [renaming, setRenaming] = useState(false);
  return (
    <>
      <div className="recent-row" onClick={() => navigate({ name: "chat", id: summary.id })}>
        <div className="recent-main">
          <div className="recent-title">
            {summary.starred && <Star size={13} className="star-filled" />}
            {summary.title}
          </div>
          <div className="recent-meta">最終更新 {relativeTime(summary.updatedAt)}</div>
        </div>
        <ConversationMenu summary={summary} onRename={() => setRenaming(true)} />
      </div>
      {renaming && <RenameDialog summary={summary} onClose={() => setRenaming(false)} />}
    </>
  );
}

export function Recents() {
  const summaries = useStore((s) => s.summaries);
  const [query, setQuery] = useState("");
  const q = query.trim().toLowerCase();
  const filtered = q ? summaries.filter((s) => s.title.toLowerCase().includes(q) || s.preview.toLowerCase().includes(q)) : summaries;
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
        <div className="recent-count">{q ? `${filtered.length} 件見つかりました` : `${summaries.length} 件のチャット`}</div>
        <div className="recent-list">
          {filtered.map((s) => <Row key={s.id} summary={s} />)}
        </div>
      </div>
    </div>
  );
}
