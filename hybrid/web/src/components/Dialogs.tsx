import { LoaderCircle, Square } from "lucide-react";
import { useEffect, useState } from "react";
import { answerConfirm, btw, generateStyle, getStyle, navigate, rewind, rewindTurns, saveFolder, saveStyle, setChoice, stop, stopWorkflow, textOf } from "../actions";
import { allCommands } from "../commands";
import { getState, setState, useStore, type DialogState } from "../store";
import type { Folder } from "../types";
import { Markdown, parsePeer } from "./Messages";
import { STATUS_LABELS, StatusDot } from "./Sidebar";
import { Dialog } from "./ui";

const closeDialog = () => setState((s) => ({ ...s, dialog: null }));

function BtwDialog({ dialog }: { dialog: Extract<DialogState, { kind: "btw" }> }) {
  useEffect(() => {
    if (dialog.answer !== undefined || dialog.error) return;
    let cancelled = false;
    void (async () => {
      const result = await btw(dialog.conversationId, dialog.question);
      if (cancelled) return;
      setState((s) => (s.dialog === dialog ? { ...s, dialog: { ...dialog, ...(result ? { answer: result.answer } : { error: "答えを受け取れませんでした" }) } } : s));
    })();
    return () => { cancelled = true; };
  }, [dialog]);
  return (
    <Dialog title="/btw" onClose={closeDialog} wide footer={<button type="button" className="button primary" onClick={closeDialog}>閉じる</button>}>
      <div className="btw-question">{dialog.question}</div>
      {dialog.answer !== undefined ? <Markdown text={dialog.answer} /> : dialog.error ? <div className="warning-text">{dialog.error}</div> : <div className="loading inline"><LoaderCircle className="spin" size={18} /> 考えています...</div>}
      <div className="field-hint">この質問と答えは会話に残りません</div>
    </Dialog>
  );
}

const NOT_REWINDABLE = ["message", "task-notification", "idle-notification", "hook-feedback", "compact-summary"];

function RewindDialog({ dialog }: { dialog: Extract<DialogState, { kind: "rewind" }> }) {
  const conversation = useStore((s) => s.conversations[dialog.conversationId]);
  const [selected, setSelected] = useState<number | null>(dialog.messageIndex ?? null);
  const [restoreFiles, setRestoreFiles] = useState(true);
  const [turnsWithChanges, setTurnsWithChanges] = useState<number[]>([]);
  useEffect(() => {
    void (async () => setTurnsWithChanges((await rewindTurns(dialog.conversationId)) ?? []))();
  }, [dialog.conversationId]);
  const humanTurns = (conversation?.messages ?? [])
    .map((m, index) => ({ m, index }))
    .filter(({ m }) => m.role === "user" && !(Array.isArray(m.content) && m.content.every((b) => b.type === "tool_result")))
    // 届いた知らせや要約は戻り先にならない。依頼や予定、コマンドは自分の発言と同じく戻れる
    .filter(({ m }) => !NOT_REWINDABLE.includes(parsePeer(textOf(m.content))?.tag ?? ""));
  const changesAfter = selected === null ? [] : turnsWithChanges.filter((t) => t >= selected);
  return (
    <Dialog
      title="巻き戻す"
      onClose={closeDialog}
      wide
      footer={
        <>
          <button type="button" className="button ghost" onClick={closeDialog}>キャンセル</button>
          <button type="button" className="button primary" disabled={selected === null} onClick={async () => {
            closeDialog();
            await rewind(dialog.conversationId, selected!, restoreFiles && changesAfter.length > 0);
          }}>ここまで戻す</button>
        </>
      }
    >
      <p className="settings-desc">選んだ発言とそれより後を消して、その発言を入力欄に戻します。</p>
      <div className="radio-list rewind-list">
        {humanTurns.map(({ m, index }) => (
          <label key={index} className={`radio-row${selected === index ? " selected" : ""}`}>
            <input type="radio" checked={selected === index} onChange={() => setSelected(index)} />
            <span className="radio-title">{textOf(m.content).slice(0, 120) || "(添付のみ)"}</span>
            {turnsWithChanges.includes(index) && <span className="field-hint">ファイルの変更あり</span>}
          </label>
        ))}
      </div>
      {changesAfter.length > 0 && (
        <label className="switch-row checkbox-row">
          <input type="checkbox" checked={restoreFiles} onChange={(e) => setRestoreFiles(e.target.checked)} />
          <span>フォルダ操作で変えたファイルも元に戻す (今のファイルはごみ箱へ移します。ターミナルで変えたものは戻せません)</span>
        </label>
      )}
    </Dialog>
  );
}

function TasksDialog({ dialog }: { dialog: Extract<DialogState, { kind: "tasks" }> }) {
  const summaries = useStore((s) => s.summaries);
  const statuses = useStore((s) => s.statuses);
  const workflows = useStore((s) => s.workflows);
  const agents = summaries.filter((s) => s.parentId === dialog.conversationId && s.kind === "agent");
  const runs = Object.values(workflows).filter((w) => w.parentId === dialog.conversationId).sort((a, b) => b.startedAt - a.startedAt);
  const go = (id: string) => {
    closeDialog();
    navigate({ name: "chat", id });
  };
  return (
    <Dialog title="タスク" onClose={closeDialog} wide footer={<button type="button" className="button primary" onClick={closeDialog}>閉じる</button>}>
      {agents.length === 0 && runs.length === 0 && <div className="empty-note">このチャットが始めたサブエージェントやワークフローはありません</div>}
      {runs.length > 0 && <div className="field-label">ワークフロー</div>}
      <div className="list-card">
        {runs.map((w) => (
          <div key={w.id} className="list-row">
            <span>{w.title}<span className="field-hint"> {w.phase ?? ""} ・ エージェント {w.agents.length} 個</span></span>
            <span className="tool-badge">{{ running: "実行中", completed: "完了", failed: "失敗", stopped: "停止" }[w.status]}</span>
            {w.status === "running" && <button type="button" className="button small" onClick={() => void stopWorkflow(w.id)}><Square size={12} /> 止める</button>}
          </div>
        ))}
      </div>
      {agents.length > 0 && <div className="field-label">サブエージェント</div>}
      <div className="list-card">
        {agents.map((a) => (
          <div key={a.id} className="list-row">
            <button type="button" className="link-button" onClick={() => go(a.id)}>{a.title}</button>
            <StatusDot status={statuses[a.id]} />
            <span className="field-hint">{STATUS_LABELS[statuses[a.id] ?? "idle"]}</span>
            {(statuses[a.id] === "running" || statuses[a.id] === "waiting") && <button type="button" className="button small" onClick={() => void stop(a.id)}><Square size={12} /> 止める</button>}
          </div>
        ))}
      </div>
    </Dialog>
  );
}

function HelpDialog() {
  return (
    <Dialog title="コマンド" onClose={closeDialog} wide footer={<button type="button" className="button primary" onClick={closeDialog}>閉じる</button>}>
      <div className="help-list">
        {allCommands().map((c) => (
          <div key={c.name} className="help-row">
            <code>/{c.name}{c.args ? ` ${c.args}` : ""}</code>
            <span>{c.description}</span>
          </div>
        ))}
      </div>
      <div className="field-hint">config/commands に .md を置くと自作コマンド、config/skills に SKILL.md を置くと Skill になります</div>
    </Dialog>
  );
}

const SCOPES: { id: Folder["messagingScope"]; label: string; hint: string }[] = [
  { id: "folder", label: "このプロジェクトの中だけ", hint: "同じプロジェクトのチャット同士で通信できます" },
  { id: "all", label: "すべてのチャット", hint: "プロジェクトの外のチャットにも送れます" },
  { id: "none", label: "通信しない", hint: "自分が始めたサブエージェントとだけやり取りします" },
];

function FolderDialog({ dialog }: { dialog: Extract<DialogState, { kind: "folder" }> }) {
  const existing = useStore((s) => s.folders.find((f) => f.id === dialog.folderId));
  const [draft, setDraft] = useState<Partial<Folder>>(existing ?? { name: "", description: "", instructions: "", messagingScope: "folder" });
  const save = async () => {
    if (!draft.name?.trim()) return;
    const before = new Set(getState().folders.map((f) => f.id));
    const folders = await saveFolder(draft);
    if (!folders) return;
    closeDialog();
    // claude.ai と同じく、作ったらそのプロジェクトを開く
    const created = folders.find((f) => !before.has(f.id));
    if (created) navigate({ name: "project", id: created.id });
  };
  return (
    <Dialog
      title={existing ? "プロジェクトの設定" : "新しいプロジェクト"}
      onClose={closeDialog}
      wide
      footer={
        <>
          <button type="button" className="button ghost" onClick={closeDialog}>キャンセル</button>
          <button type="button" className="button primary" disabled={!draft.name?.trim()} onClick={save}>保存</button>
        </>
      }
    >
      <label className="field">
        <span className="field-label">名前</span>
        <input className="input" autoFocus value={draft.name ?? ""} placeholder="何に取り組んでいますか?" onChange={(e) => setDraft({ ...draft, name: e.target.value })} />
      </label>
      <label className="field">
        <span className="field-label">説明 (省略可)</span>
        <input className="input" value={draft.description ?? ""} placeholder="何を目指すプロジェクトですか?" onChange={(e) => setDraft({ ...draft, description: e.target.value })} />
      </label>
      <label className="field">
        <span className="field-label">指示</span>
        <textarea className="input" rows={6} value={draft.instructions ?? ""} placeholder="例: 家計簿アプリを作っています。技術スタックは..." onChange={(e) => setDraft({ ...draft, instructions: e.target.value })} />
        <span className="field-hint">このプロジェクトのすべてのチャットに渡します</span>
      </label>
      <div className="field-label">チャット同士の通信</div>
      <div className="radio-list">
        {SCOPES.map((scope) => (
          <label key={scope.id} className={`radio-row${draft.messagingScope === scope.id ? " selected" : ""}`}>
            <input type="radio" checked={draft.messagingScope === scope.id} onChange={() => setDraft({ ...draft, messagingScope: scope.id })} />
            <span className="radio-title">{scope.label}</span>
            <span className="field-hint">{scope.hint}</span>
          </label>
        ))}
      </div>
    </Dialog>
  );
}

function ConfirmDialog({ dialog }: { dialog: Extract<DialogState, { kind: "confirm" }> }) {
  return (
    <Dialog
      title={dialog.title}
      onClose={() => answerConfirm(false)}
      footer={
        <>
          <button type="button" className="button ghost" onClick={() => answerConfirm(false)}>キャンセル</button>
          <button type="button" className="button danger" autoFocus onClick={() => answerConfirm(true)}>{dialog.confirmLabel}</button>
        </>
      }
    >
      <p className="settings-desc confirm-message">{dialog.message}</p>
    </Dialog>
  );
}

const SHORTCUTS: [string, string][] = [
  ["Ctrl + Shift + O", "新しいチャット"],
  ["Ctrl + K", "チャットを検索"],
  ["Ctrl + F", "開いている会話の中を探す"],
  ["Ctrl + Shift + S", "サイドバーを開く・閉じる"],
  ["Ctrl + /", "ショートカットの一覧"],
  ["Esc", "応答を止める (入力欄で)"],
  ["Enter / Shift + Enter", "送信 / 改行"],
  ["/", "コマンドの候補"],
];

function ShortcutsDialog() {
  return (
    <Dialog title="キーボードショートカット" onClose={closeDialog} footer={<button type="button" className="button primary" onClick={closeDialog}>閉じる</button>}>
      <div className="help-list">
        {SHORTCUTS.map(([keys, label]) => (
          <div key={keys} className="help-row"><kbd>{keys}</kbd><span>{label}</span></div>
        ))}
      </div>
    </Dialog>
  );
}

function StyleDialog({ dialog }: { dialog: Extract<DialogState, { kind: "style" }> }) {
  const [source, setSource] = useState<"describe" | "sample">("describe");
  const [request, setRequest] = useState("");
  const [name, setName] = useState("");
  const [text, setText] = useState("");
  const [busy, setBusy] = useState(false);
  const editing = !!dialog.styleId;

  useEffect(() => {
    if (!dialog.styleId) return;
    void (async () => {
      const style = await getStyle(dialog.styleId!);
      if (!style) return;
      setName(style.name);
      setText(style.text);
    })();
  }, [dialog.styleId]);

  const generate = async () => {
    setBusy(true);
    const prefix = source === "sample" ? "この文章の書き方をまねたスタイル:\n\n" : "";
    const style = await generateStyle(prefix + request.trim());
    setBusy(false);
    if (!style) return;
    setName(style.name);
    setText(style.text);
  };
  const save = async () => {
    const id = await saveStyle(dialog.styleId, name, text);
    if (!id) return;
    setChoice({ style: id });
    closeDialog();
  };

  return (
    <Dialog
      title={editing ? "スタイルを編集" : "スタイルを作る"}
      onClose={closeDialog}
      wide
      footer={
        <>
          <button type="button" className="button ghost" onClick={closeDialog}>キャンセル</button>
          <button type="button" className="button primary" disabled={!name.trim() || !text.trim()} onClick={save}>保存して使う</button>
        </>
      }
    >
      {!editing && (
        <>
          <div className="segmented">
            <button type="button" className={source === "describe" ? "on" : ""} onClick={() => setSource("describe")}>説明から作る</button>
            <button type="button" className={source === "sample" ? "on" : ""} onClick={() => setSource("sample")}>見本の文章から作る</button>
          </div>
          <textarea
            className="input"
            rows={source === "sample" ? 8 : 3}
            value={request}
            placeholder={source === "sample" ? "まねしたい文章を貼り付けてください" : "例: 結論から先に、敬語で、箇条書きを多めに"}
            onChange={(e) => setRequest(e.target.value)}
          />
          <div className="button-row">
            <button type="button" className="button" disabled={!request.trim() || busy} onClick={generate}>
              {busy ? <LoaderCircle size={14} className="spin" /> : null} Claude に書いてもらう
            </button>
          </div>
        </>
      )}
      <label className="field">
        <span className="field-label">名前</span>
        <input className="input" value={name} onChange={(e) => setName(e.target.value)} />
      </label>
      <label className="field">
        <span className="field-label">指示</span>
        <textarea className="input" rows={8} value={text} placeholder="Claude にどう書いてほしいか" onChange={(e) => setText(e.target.value)} />
      </label>
    </Dialog>
  );
}

export function GlobalDialog() {
  const dialog = useStore((s) => s.dialog);
  if (!dialog) return null;
  switch (dialog.kind) {
    case "btw": return <BtwDialog dialog={dialog} />;
    case "rewind": return <RewindDialog dialog={dialog} />;
    case "tasks": return <TasksDialog dialog={dialog} />;
    case "help": return <HelpDialog />;
    case "folder": return <FolderDialog key={dialog.folderId ?? "new"} dialog={dialog} />;
    case "confirm": return <ConfirmDialog dialog={dialog} />;
    case "shortcuts": return <ShortcutsDialog />;
    case "style": return <StyleDialog key={dialog.styleId ?? "new"} dialog={dialog} />;
  }
}
