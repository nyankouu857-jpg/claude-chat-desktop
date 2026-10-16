import { Archive, ArchiveRestore, ArrowLeft, EllipsisVertical, FileText, Pencil, Plus, Search, Star, Trash, Type, Upload } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { addKnowledge, askConfirm, deleteFolder, deleteKnowledge, listKnowledge, navigate, readKnowledge, saveFolder, setChoice, toast } from "../actions";
import { ALL_MODELS } from "../models";
import { setState, useStore } from "../store";
import type { Folder, KnowledgeFile } from "../types";
import { Composer, KNOWLEDGE_ACCEPT, fileToText } from "./Composer";
import { MemoryFiles } from "./settings/Features";
import { Dialog, Dropdown, MenuItem, relativeTime } from "./ui";

const openProjectDialog = (folderId?: string) => setState((s) => ({ ...s, dialog: { kind: "folder", folderId } }));

function useLastActivity() {
  const summaries = useStore((s) => s.summaries);
  return (folder: Folder) => Math.max(folder.createdAt, ...summaries.filter((s) => s.folderId === folder.id).map((s) => s.updatedAt));
}

export function ProjectsPage() {
  const folders = useStore((s) => s.folders);
  const lastActivity = useLastActivity();
  const [query, setQuery] = useState("");
  const [sort, setSort] = useState<"activity" | "name">("activity");
  const [showArchived, setShowArchived] = useState(false);
  const q = query.trim().toLowerCase();
  const shown = folders
    .filter((f) => f.archived === showArchived)
    .filter((f) => !q || f.name.toLowerCase().includes(q) || f.description.toLowerCase().includes(q))
    .sort((a, b) => (sort === "name" ? a.name.localeCompare(b.name, "ja") : lastActivity(b) - lastActivity(a)));
  const archivedCount = folders.filter((f) => f.archived).length;

  return (
    <div className="page">
      <div className="page-inner">
        <div className="page-header">
          <h1 className="page-title">プロジェクト</h1>
          <button type="button" className="button primary" onClick={() => openProjectDialog()}><Plus size={16} /> 新しいプロジェクト</button>
        </div>
        <label className="search-box">
          <Search size={16} />
          <input placeholder="プロジェクトを検索..." value={query} onChange={(e) => setQuery(e.target.value)} />
        </label>
        <div className="recent-toolbar">
          <span className="recent-count">{showArchived ? "アーカイブ済み" : `${shown.length} 件のプロジェクト`}</span>
          <span className="spacer" />
          {(archivedCount > 0 || showArchived) && (
            <button type="button" className="link-button" onClick={() => setShowArchived(!showArchived)}>
              {showArchived ? "進行中のプロジェクトに戻る" : `アーカイブ済み (${archivedCount})`}
            </button>
          )}
          <select className="input small" value={sort} onChange={(e) => setSort(e.target.value as typeof sort)}>
            <option value="activity">最近の操作順</option>
            <option value="name">名前順</option>
          </select>
        </div>
        {shown.length === 0 && !q && !showArchived && (
          <div className="empty-note">プロジェクトにすると、チャットをまとめて、共通の指示やナレッジを渡せます</div>
        )}
        <div className="project-grid">
          {shown.map((f) => (
            <button key={f.id} type="button" className="project-card" onClick={() => navigate({ name: "project", id: f.id })}>
              <div className="project-card-title">
                <span>{f.name}</span>
                {f.starred && <Star size={14} className="star-filled" />}
              </div>
              {f.description && <div className="project-card-desc">{f.description}</div>}
              <div className="project-card-meta">最終更新 {relativeTime(lastActivity(f))}</div>
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}

function TextKnowledgeDialog({ folderId, onAdded, onClose }: { folderId: string; onAdded: (files: KnowledgeFile[]) => void; onClose: () => void }) {
  const [name, setName] = useState("");
  const [text, setText] = useState("");
  const save = async () => {
    const files = await addKnowledge(folderId, name.trim().includes(".") ? name.trim() : `${name.trim()}.md`, text);
    if (!files) return;
    onAdded(files);
    onClose();
  };
  return (
    <Dialog
      title="テキストを追加"
      onClose={onClose}
      wide
      footer={
        <>
          <button type="button" className="button ghost" onClick={onClose}>キャンセル</button>
          <button type="button" className="button primary" disabled={!name.trim() || !text.trim()} onClick={save}>追加</button>
        </>
      }
    >
      <label className="field">
        <span className="field-label">タイトル</span>
        <input className="input" autoFocus value={name} placeholder="例: 用語集" onChange={(e) => setName(e.target.value)} />
      </label>
      <label className="field">
        <span className="field-label">内容</span>
        <textarea className="input" rows={14} value={text} onChange={(e) => setText(e.target.value)} />
      </label>
    </Dialog>
  );
}

function Knowledge({ folderId }: { folderId: string }) {
  const [files, setFiles] = useState<KnowledgeFile[]>([]);
  const [adding, setAdding] = useState(false);
  const [viewing, setViewing] = useState<{ name: string; text: string } | null>(null);
  const input = useRef<HTMLInputElement>(null);

  useEffect(() => {
    void (async () => setFiles((await listKnowledge(folderId)) ?? []))();
  }, [folderId]);

  const upload = async (picked: FileList) => {
    for (const file of Array.from(picked)) {
      try {
        const text = await fileToText(file);
        if (text === null) {
          toast(`${file.name} は文字にできない形式です。テキストか Word / Excel / PowerPoint のファイルを使ってください`, "error");
          continue;
        }
        const updated = await addKnowledge(folderId, file.name, text);
        if (updated) setFiles(updated);
      } catch (e) {
        toast(e instanceof Error ? e.message : String(e), "error");
      }
    }
  };

  return (
    <section className="project-side-card">
      <div className="project-side-head">
        <span>ナレッジ</span>
        <Dropdown
          align="right"
          trigger={(open, toggle) => <button type="button" className={`icon-button small${open ? " open" : ""}`} aria-label="ナレッジを追加" onClick={toggle}><Plus size={16} /></button>}
        >
          {(close) => (
            <>
              <MenuItem icon={<Upload size={16} />} onClick={() => { close(); input.current?.click(); }}>ファイルを追加</MenuItem>
              <MenuItem icon={<Type size={16} />} onClick={() => { close(); setAdding(true); }}>テキストを追加</MenuItem>
            </>
          )}
        </Dropdown>
      </div>
      <input ref={input} type="file" multiple hidden accept={KNOWLEDGE_ACCEPT} onChange={(e) => {
        if (e.target.files) void upload(e.target.files);
        e.target.value = "";
      }} />
      {files.length === 0 && <div className="field-hint">資料や用語集を置くと、このプロジェクトのチャットで毎回 Claude が参照します</div>}
      {files.map((f) => (
        <div key={f.name} className="knowledge-row" onClick={async () => {
          const text = await readKnowledge(folderId, f.name);
          if (text !== undefined) setViewing({ name: f.name, text });
        }}>
          <FileText size={15} />
          <span className="knowledge-name">{f.name}</span>
          <span className="field-hint">{(f.size / 1024).toFixed(1)} KB</span>
          <button type="button" className="icon-button tiny" title="削除" onClick={async (e) => {
            e.stopPropagation();
            if (!(await askConfirm("ナレッジを削除", `${f.name} を削除しますか?`))) return;
            setFiles((await deleteKnowledge(folderId, f.name)) ?? files);
          }}><Trash size={13} /></button>
        </div>
      ))}
      {adding && <TextKnowledgeDialog folderId={folderId} onAdded={setFiles} onClose={() => setAdding(false)} />}
      {viewing && (
        <Dialog title={viewing.name} wide onClose={() => setViewing(null)} footer={<button type="button" className="button primary" onClick={() => setViewing(null)}>閉じる</button>}>
          <pre className="knowledge-view">{viewing.text}</pre>
        </Dialog>
      )}
    </section>
  );
}

function ProjectDefaults({ folder }: { folder: Folder }) {
  const choice = useStore((s) => s.choice);
  const defaults = folder.defaults;
  const describe = () => {
    if (!defaults) return "決めていません。入力欄で選んだものがそのまま使われます";
    const model = ALL_MODELS.find((m) => m.id === defaults.model)?.label ?? defaults.model;
    const tools = [defaults.webSearch && "Web 検索", defaults.codeExecution && "コード実行"].filter(Boolean).join("、");
    const off = defaults.disabledSources?.length ? `、オフにしたツール ${defaults.disabledSources.length} 個` : "";
    return `${model} ・ 思考 ${defaults.effort}${tools ? ` ・ ${tools}` : ""}${off}`;
  };
  return (
    <section className="project-side-card">
      <div className="project-side-head"><span>新しいチャットの既定</span></div>
      <div className="field-hint">{describe()}</div>
      <div className="button-row">
        <button type="button" className="button small" onClick={() => void saveFolder({
          ...folder,
          defaults: { model: choice.model, effort: choice.effort, webSearch: choice.webSearch, webFetch: choice.webFetch, codeExecution: choice.codeExecution, disabledSources: choice.disabledSources },
        })}>今の入力欄の設定を既定にする</button>
        {defaults && <button type="button" className="button small ghost" onClick={() => void saveFolder({ ...folder, defaults: null })}>消す</button>}
      </div>
    </section>
  );
}

export function ProjectPage({ id }: { id: string }) {
  const folder = useStore((s) => s.folders.find((f) => f.id === id));
  const memoryOn = useStore((s) => !!s.settings?.features.memory);
  const summaries = useStore((s) => s.summaries);
  const chats = summaries.filter((c) => c.folderId === id && !c.parentId);
  const defaults = folder?.defaults;
  // プロジェクトを開いたら、ここで始めるチャットの入力欄をこのプロジェクトの既定にそろえる
  useEffect(() => {
    if (defaults) setChoice(defaults);
  }, [id]);

  if (!folder) {
    return (
      <div className="page">
        <div className="page-inner">
          <div className="empty-note">プロジェクトが見つかりません</div>
          <button type="button" className="link-button" onClick={() => navigate({ name: "projects" })}>すべてのプロジェクト</button>
        </div>
      </div>
    );
  }

  return (
    <div className="page">
      <div className="project-layout">
        <div className="project-main">
          <button type="button" className="crumb" onClick={() => navigate({ name: "projects" })}><ArrowLeft size={14} /> すべてのプロジェクト</button>
          <div className="project-title-row">
            <h1 className="page-title">{folder.name}</h1>
            <button type="button" className="icon-button" title={folder.starred ? "スターを外す" : "スターを付ける"} onClick={() => void saveFolder({ ...folder, starred: !folder.starred })}>
              <Star size={17} className={folder.starred ? "star-filled" : ""} />
            </button>
            <Dropdown
              align="right"
              trigger={(open, toggle) => <button type="button" className={`icon-button${open ? " open" : ""}`} aria-label="プロジェクトのメニュー" onClick={toggle}><EllipsisVertical size={17} /></button>}
            >
              {(close) => (
                <>
                  <MenuItem icon={<Pencil size={16} />} onClick={() => { close(); openProjectDialog(folder.id); }}>詳細を編集</MenuItem>
                  <MenuItem icon={folder.archived ? <ArchiveRestore size={16} /> : <Archive size={16} />} onClick={() => { close(); void saveFolder({ ...folder, archived: !folder.archived }); }}>
                    {folder.archived ? "アーカイブから戻す" : "アーカイブ"}
                  </MenuItem>
                  <MenuItem icon={<Trash size={16} />} danger onClick={() => {
                    close();
                    void (async () => {
                      if (!(await askConfirm("プロジェクトを削除", `「${folder.name}」を削除しますか? ナレッジとメモリも消えます。中のチャットは消えずにプロジェクトの外へ出ます。`))) return;
                      await deleteFolder(folder.id);
                      navigate({ name: "projects" });
                    })();
                  }}>削除</MenuItem>
                </>
              )}
            </Dropdown>
          </div>
          {folder.description && <p className="project-desc">{folder.description}</p>}
          {folder.archived && <div className="field-hint">アーカイブ済みのプロジェクトです</div>}
          <Composer conversationId={null} autoFocus />
          <div className="project-chats">
            {chats.length === 0 && <div className="empty-note">ここで始めたチャットがこのプロジェクトに入ります</div>}
            {chats.map((c) => (
              <div key={c.id} className="recent-row" onClick={() => navigate({ name: "chat", id: c.id })}>
                <div className="recent-main">
                  <div className="recent-title">{c.starred && <Star size={13} className="star-filled" />}{c.title}</div>
                  <div className="recent-meta">最終更新 {relativeTime(c.updatedAt)}</div>
                </div>
              </div>
            ))}
          </div>
        </div>
        <aside className="project-side">
          <section className="project-side-card">
            <div className="project-side-head">
              <span>指示</span>
              <button type="button" className="icon-button small" aria-label="指示を編集" onClick={() => openProjectDialog(folder.id)}><Pencil size={15} /></button>
            </div>
            <div className={folder.instructions.trim() ? "project-instructions" : "field-hint"}>
              {folder.instructions.trim() || "Claude への指示を書くと、このプロジェクトのすべてのチャットに渡します"}
            </div>
          </section>
          <ProjectDefaults folder={folder} />
          <Knowledge folderId={folder.id} />
          {memoryOn && (
            <section className="project-side-card">
              <div className="project-side-head"><span>メモリ</span></div>
              <div className="field-hint">このプロジェクトのチャットだけが読み書きするメモリです</div>
              <MemoryFiles folderId={folder.id} compact />
            </section>
          )}
        </aside>
      </div>
    </div>
  );
}
