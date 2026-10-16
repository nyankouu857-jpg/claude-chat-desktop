import { Search } from "lucide-react";
import { useEffect, useState } from "react";
import { listArtifacts, navigate, openArtifact } from "../actions";
import type { ArtifactListItem } from "../types";
import { ARTIFACT_TYPE_LABELS } from "./Messages";
import { relativeTime } from "./ui";

export function ArtifactsPage() {
  const [items, setItems] = useState<ArtifactListItem[] | null>(null);
  const [query, setQuery] = useState("");

  useEffect(() => {
    void (async () => setItems((await listArtifacts()) ?? []))();
  }, []);

  const q = query.trim().toLowerCase();
  const shown = (items ?? []).filter((a) => !q || a.title.toLowerCase().includes(q) || a.conversationTitle.toLowerCase().includes(q));
  return (
    <div className="page">
      <div className="page-inner">
        <h1 className="page-title">アーティファクト</h1>
        <label className="search-box">
          <Search size={16} />
          <input placeholder="アーティファクトを検索..." value={query} onChange={(e) => setQuery(e.target.value)} />
        </label>
        {items && items.length === 0 && <div className="empty-note">まだアーティファクトはありません。ページや図、アプリを頼むと Claude が回答の横に作ります</div>}
        <div className="project-grid">
          {shown.map((a) => (
            <button key={`${a.conversationId}/${a.id}`} type="button" className="project-card" onClick={() => {
              navigate({ name: "chat", id: a.conversationId });
              openArtifact(a.conversationId, a.id);
            }}>
              <div className="project-card-title"><span>{a.title}</span></div>
              <div className="project-card-desc">{ARTIFACT_TYPE_LABELS[a.type] ?? a.type} ・ 版 {a.version}</div>
              <div className="project-card-meta">{a.conversationTitle} ・ {relativeTime(a.updatedAt)}</div>
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}
