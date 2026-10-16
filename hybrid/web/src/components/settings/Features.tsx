import { FolderOpen, Pencil, Plus, Save, Trash, TriangleAlert } from "lucide-react";
import { useEffect, useState } from "react";
import { askConfirm, deleteMemory, deleteStyle, listMemory, openPath, readMemory, toast, updateSettings, writeMemory } from "../../actions";
import { setState, useStore } from "../../store";
import type { MemoryFile, Settings } from "../../types";
import { NumberField, Toggle, relativeTime } from "../ui";
import { Section } from "./Settings";

function SwitchRow({ title, hint, checked, onChange }: { title: string; hint?: string; checked: boolean; onChange: (v: boolean) => void }) {
  return (
    <div className="switch-row">
      <div>
        <div>{title}</div>
        {hint && <div className="field-hint">{hint}</div>}
      </div>
      <Toggle checked={checked} onChange={onChange} label={title} />
    </div>
  );
}

// folderId を渡すとプロジェクトのメモリ。compact はプロジェクトの横の狭い枠で使う
export function MemoryFiles({ folderId, compact }: { folderId?: string; compact?: boolean }) {
  const [files, setFiles] = useState<MemoryFile[]>([]);
  const [selected, setSelected] = useState<string | null>(null);
  const [draft, setDraft] = useState("");
  const [original, setOriginal] = useState("");
  const [newPath, setNewPath] = useState("");

  useEffect(() => {
    void (async () => setFiles((await listMemory(folderId)) ?? []))();
  }, [folderId]);

  const open = async (path: string) => {
    const content = await readMemory(path, folderId);
    if (content === undefined) return;
    setSelected(path);
    setDraft(content);
    setOriginal(content);
  };

  return (
    <>
      <div className="list-card">
        {files.length === 0 && <div className="empty-note">まだ何も覚えていません。Claude が覚えておくべきことを見つけると、ここにファイルを作ります</div>}
        {files.map((f) => (
          <div key={f.path} className={`list-row clickable${selected === f.path ? " selected" : ""}`} onClick={() => void open(f.path)}>
            <code>{f.path}</code>
            <span className="field-hint">{(f.size / 1024).toFixed(1)} KB ・ {relativeTime(f.updatedAt)}</span>
            <button type="button" className="icon-button small" title="ごみ箱へ" onClick={async (e) => {
              e.stopPropagation();
              if (!(await askConfirm("メモリを削除", `${f.path} を消しますか?`))) return;
              setFiles((await deleteMemory(f.path, folderId)) ?? files);
              if (selected === f.path) setSelected(null);
            }}><Trash size={14} /></button>
          </div>
        ))}
      </div>
      {!compact && <div className="inline-form">
        <input className="input" placeholder="新しいファイル (例: preferences.md)" value={newPath} onChange={(e) => setNewPath(e.target.value)} />
        <button type="button" className="button" disabled={!newPath.trim()} onClick={async () => {
          const path = newPath.trim();
          const updated = await writeMemory(path, "", folderId);
          if (!updated) return;
          setFiles(updated);
          setNewPath("");
          await open(path);
        }}><Plus size={14} /> 作る</button>
        <button type="button" className="button" onClick={() => void openPath("memory")}><FolderOpen size={14} /> フォルダを開く</button>
      </div>}
      {selected && (
        <div className="memory-editor">
          <div className="field-label">{selected}</div>
          <textarea className="input mono" rows={compact ? 8 : 12} value={draft} onChange={(e) => setDraft(e.target.value)} />
          <div className="settings-actions">
            <button type="button" className="button primary" disabled={draft === original} onClick={async () => {
              const updated = await writeMemory(selected, draft, folderId);
              if (!updated) return;
              setFiles(updated);
              setOriginal(draft);
              toast("保存しました");
            }}><Save size={14} /> 保存</button>
          </div>
        </div>
      )}
    </>
  );
}

export function Features({ settings }: { settings: Settings }) {
  const { chat, features } = settings;
  const styles = useStore((s) => s.extensions.outputStyles);
  // リセットした後に一覧を読み直させる
  const [memoryVersion, setMemoryVersion] = useState(0);
  return (
    <>
      <Section title="Web" description="Web 検索とページの取得は、入力欄のツールメニューでチャットごとに切り替えられます。ここは最初の状態と回数の上限です。">
        <SwitchRow title="Web 検索を最初からオンにする" hint="検索 1,000 回ごとに $10 かかります" checked={chat.webSearch} onChange={(v) => void updateSettings({ chat: { webSearch: v } })} />
        <div className="switch-row">
          <div>
            <div>1 回の返答で検索できる回数</div>
            <div className="field-hint">多いほど調べ物が深くなる代わりに、料金がかさみます</div>
          </div>
          <NumberField value={chat.webSearchMaxUses} min={1} max={50} onCommit={(v) => void updateSettings({ chat: { webSearchMaxUses: v } })} />
        </div>
        <SwitchRow title="Web ページの取得を最初からオンにする" hint="URL を渡すと Claude が中身を読みます。取得そのものは無料で、読んだ分のトークンだけかかります" checked={chat.webFetch} onChange={(v) => void updateSettings({ chat: { webFetch: v } })} />
        <div className="switch-row">
          <div>
            <div>1 回の返答でページを取得できる回数</div>
          </div>
          <NumberField value={chat.webFetchMaxUses} min={1} max={50} onCommit={(v) => void updateSettings({ chat: { webFetchMaxUses: v } })} />
        </div>
      </Section>

      <Section title="コード実行とファイル作成" description="Anthropic のサーバーにある隔離された環境で、Claude が Python やコマンドを動かします。グラフや Excel、Word、PowerPoint、PDF のファイルを作ると、代表フォルダの data/files に保存されます。">
        <SwitchRow title="コード実行を最初からオンにする" hint="1 日 50 時間までは無料、それを超えると 1 時間 $0.05 かかります" checked={chat.codeExecution} onChange={(v) => void updateSettings({ chat: { codeExecution: v } })} />
        <SwitchRow title="Office 文書と PDF を作るスキルを使う" hint="Anthropic 公式の docx / xlsx / pptx / pdf スキルを読み込みます" checked={chat.officeSkills} onChange={(v) => void updateSettings({ chat: { officeSkills: v } })} />
      </Section>

      <Section title="スタイル" description="新しいチャットで最初に使うスタイルです。入力欄の + からチャットごとに切り替えられます。">
        <select className="input" value={chat.outputStyle} onChange={(e) => void updateSettings({ chat: { outputStyle: e.target.value } })}>
          {styles.map((s) => <option key={s.id} value={s.id}>{s.label}{s.builtin ? "" : " (自作)"}</option>)}
          {!styles.some((s) => s.id === chat.outputStyle) && <option value={chat.outputStyle}>{chat.outputStyle}</option>}
        </select>
        <div className="list-card">
          {styles.filter((s) => !s.builtin).map((s) => (
            <div key={s.id} className="list-row">
              <span>{s.label}</span>
              <span className="spacer" />
              <button type="button" className="icon-button small" title="編集" onClick={() => setState((st) => ({ ...st, dialog: { kind: "style", styleId: s.id } }))}><Pencil size={14} /></button>
              <button type="button" className="icon-button small" title="削除" onClick={async () => {
                if (await askConfirm("スタイルを削除", `「${s.label}」を削除しますか?`)) await deleteStyle(s.id);
              }}><Trash size={14} /></button>
            </div>
          ))}
          {!styles.some((s) => !s.builtin) && <div className="empty-note">自作のスタイルはまだありません</div>}
        </div>
        <div className="button-row">
          <button type="button" className="button" onClick={() => setState((st) => ({ ...st, dialog: { kind: "style" } }))}><Plus size={14} /> スタイルを作る</button>
          <button type="button" className="button" onClick={() => void openPath("outputStyles")}><FolderOpen size={14} /> スタイルのフォルダを開く</button>
        </div>
      </Section>

      <Section title="Claude の機能">
        <SwitchRow title="アーティファクトを作成" hint="ページや文書、図、React のアプリを回答の横に出します" checked={features.artifacts} onChange={(v) => void updateSettings({ features: { artifacts: v } })} />
        <SwitchRow title="過去のチャットを検索して参照" hint="Claude が以前の会話を探して読めるようにします" checked={features.chatSearch} onChange={(v) => void updateSettings({ features: { chatSearch: v } })} />
        <SwitchRow title="チャット履歴からメモリを作成" hint="会話をまたいで覚えておきたいことを、Claude がファイルに書き残します。プロジェクトのチャットはプロジェクトごとのメモリを使います" checked={features.memory} onChange={(v) => void updateSettings({ features: { memory: v } })} />
      </Section>

      {features.memory && (
        <Section title="メモリの中身" description="Claude が書いたファイルです。自分で直したり消したりできます。シークレットチャットでは読み書きしません。">
          <SwitchRow title="書き込む前に確認する" hint="Claude がメモリを書き換えるたびに承認を求めます" checked={features.confirmMemoryWrites} onChange={(v) => void updateSettings({ features: { confirmMemoryWrites: v } })} />
          <MemoryFiles key={memoryVersion} />
          <div className="button-row">
            <button type="button" className="button danger" onClick={async () => {
              if (!(await askConfirm("メモリをリセット", "全体のメモリのファイルをすべてごみ箱へ移します。プロジェクトのメモリは残ります。"))) return;
              for (const f of (await listMemory()) ?? []) await deleteMemory(f.path);
              toast("メモリをリセットしました");
              setMemoryVersion((v) => v + 1);
            }}><Trash size={14} /> メモリをリセット</button>
          </div>
        </Section>
      )}

      <Section title="確認なしモード">
        <div className="warning-box">
          <TriangleAlert size={16} />
          オンにすると、入力欄の権限モードで「確認なし」を選べるようになります。そのチャットでは Claude がファイルの削除やコマンドの実行も確認なしで行います。
        </div>
        <SwitchRow title="確認なしモードを選べるようにする" checked={features.allowBypassMode} onChange={(v) => void updateSettings({ features: { allowBypassMode: v } })} />
      </Section>
    </>
  );
}
