import { FolderOpen, Plus, TriangleAlert, X } from "lucide-react";
import { useState } from "react";
import { pickFolder, toast, updateSettings } from "../../actions";
import type { FolderAccess, Settings } from "../../types";
import { Toggle } from "../ui";
import { Section } from "./Settings";

export function Files({ settings }: { settings: Settings }) {
  const files = settings.files;
  const [draft, setDraft] = useState("");

  const saveFolders = (folders: FolderAccess[]) => updateSettings({ files: { folders } });

  async function addFolder(path: string) {
    const trimmed = path.trim().replace(/^"(.*)"$/, "$1");
    if (!trimmed) return;
    if (files.folders.some((f) => f.path.toLowerCase() === trimmed.toLowerCase())) {
      toast("そのフォルダは追加済みです", "error");
      return;
    }
    if (await saveFolders([...files.folders, { path: trimmed, readOnly: false }])) setDraft("");
  }

  async function browse() {
    const picked = await pickFolder();
    if (picked) await addFolder(picked);
  }

  return (
    <>
      <Section
        title="フォルダ操作"
        description="許可したフォルダの中だけ、Claude がファイルを読んだり、作ったり、書き換えたりできます。書き込みや削除の前には毎回確認が出ます。削除したものはごみ箱に入ります。"
      >
        <div className="switch-row">
          <div>
            <div>フォルダ操作を使う</div>
            <div className="field-hint">オンにすると、入力欄のツールメニューからチャットごとに切り替えられます</div>
          </div>
          <Toggle checked={files.enabled} onChange={(v) => void updateSettings({ files: { enabled: v } })} label="フォルダ操作を使う" />
        </div>
        <div className="switch-row">
          <div>
            <div>読み取りは確認なしで実行する</div>
            <div className="field-hint">一覧、読み取り、検索、情報の取得だけが対象です。書き込みや削除は常に確認します</div>
          </div>
          <Toggle checked={files.autoApproveReads} onChange={(v) => void updateSettings({ files: { autoApproveReads: v } })} label="読み取りは確認なしで実行する" />
        </div>
      </Section>

      <Section title="許可するフォルダ" description="一番上のフォルダが、相対パスの基準になります。ドキュメント全体のような広い場所より、作業用のフォルダを指定するのがおすすめです。">
        {files.enabled && files.folders.length === 0 && (
          <div className="warning-box"><TriangleAlert size={16} /> フォルダを 1 つ以上追加すると使えるようになります。</div>
        )}
        {files.folders.length > 0 && (
          <div className="list-card">
            {files.folders.map((folder, i) => (
              <div key={folder.path} className="list-row folder-row">
                <FolderOpen size={16} className="folder-icon" />
                <code className="folder-path" title={folder.path}>{folder.path}</code>
                {i === 0 && <span className="tool-badge">基準</span>}
                <label className="folder-readonly">
                  <span className="field-hint">読み取り専用</span>
                  <Toggle
                    checked={folder.readOnly}
                    label="読み取り専用"
                    onChange={(v) => void saveFolders(files.folders.map((f) => (f === folder ? { ...f, readOnly: v } : f)))}
                  />
                </label>
                <button type="button" className="icon-button small" title="外す" onClick={() => void saveFolders(files.folders.filter((f) => f !== folder))}>
                  <X size={15} />
                </button>
              </div>
            ))}
          </div>
        )}
        <div className="inline-form">
          <input
            className="input mono"
            placeholder="C:\Users\you\Documents\work"
            value={draft}
            onChange={(e) => setDraft(e.target.value)}
            onKeyDown={(e) => e.key === "Enter" && !e.nativeEvent.isComposing && void addFolder(draft)}
          />
          <button type="button" className="button" onClick={() => void browse()}><FolderOpen size={15} /> 参照...</button>
          <button type="button" className="button primary" disabled={!draft.trim()} onClick={() => void addFolder(draft)}><Plus size={15} /> 追加</button>
        </div>
      </Section>
    </>
  );
}
