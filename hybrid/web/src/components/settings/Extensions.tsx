import { FolderOpen, Plus, RefreshCw, Trash } from "lucide-react";
import { useEffect, useState } from "react";
import { openPath, refreshExtensions, saveHooks, toast } from "../../actions";
import { useStore } from "../../store";
import type { HookEntry, Settings } from "../../types";
import { NumberField } from "../ui";
import { Section } from "./Settings";

const HOOK_EVENTS: { id: string; label: string; hint: string; matcher: boolean }[] = [
  { id: "PreToolUse", label: "ツールを使う前", hint: "終了コード 2 で止めると、標準エラーの内容が Claude に理由として伝わります", matcher: true },
  { id: "PostToolUse", label: "ツールを使った後", hint: "標準出力に書いたことが、ツールの結果に添えて Claude に伝わります", matcher: true },
  { id: "UserPromptSubmit", label: "送信したとき", hint: "終了コード 2 で送信を止めます。標準出力は Claude への補足になります", matcher: false },
  { id: "Stop", label: "応答が終わったとき", hint: "終了コード 2 で止めると、その理由を渡して Claude に続けさせます (3 回まで)", matcher: false },
  { id: "Notification", label: "確認待ちになったとき", hint: "通知を鳴らすなどに使えます", matcher: false },
];

function HookList({ event, entries, onChange }: { event: (typeof HOOK_EVENTS)[number]; entries: HookEntry[]; onChange: (entries: HookEntry[]) => void }) {
  const update = (i: number, patch: Partial<HookEntry>) => onChange(entries.map((e, j) => (j === i ? { ...e, ...patch } : e)));
  return (
    <div className="hook-event">
      <div className="hook-event-title">
        <code>{event.id}</code> {event.label}
        <button type="button" className="icon-button tiny" title="追加" onClick={() => onChange([...entries, { matcher: "", command: "", timeoutSeconds: 60 }])}><Plus size={14} /></button>
      </div>
      <div className="field-hint">{event.hint}</div>
      {entries.map((entry, i) => (
        <div key={i} className="hook-entry">
          {event.matcher && <input className="input" placeholder="ツール名 (正規表現、空ならすべて)" value={entry.matcher ?? ""} onChange={(e) => update(i, { matcher: e.target.value })} />}
          <input className="input mono" placeholder="実行するコマンド (Windows は PowerShell)" value={entry.command} onChange={(e) => update(i, { command: e.target.value })} />
          <NumberField value={entry.timeoutSeconds} min={1} max={600} onCommit={(v) => update(i, { timeoutSeconds: v })} />
          <button type="button" className="icon-button small" title="消す" onClick={() => onChange(entries.filter((_, j) => j !== i))}><Trash size={14} /></button>
        </div>
      ))}
    </div>
  );
}

export function Extensions({ settings }: { settings: Settings }) {
  const commands = useStore((s) => s.extensions.commands);
  const [hooks, setHooks] = useState(settings.hooks);
  useEffect(() => setHooks(settings.hooks), [settings.hooks]);
  useEffect(() => void refreshExtensions(), []);
  const dirty = JSON.stringify(hooks) !== JSON.stringify(settings.hooks);
  const skills = commands.filter((c) => c.kind === "skill");
  const custom = commands.filter((c) => c.kind === "command");
  return (
    <>
      <Section title="Skills" description={<>Claude Code と同じ形の Skills です。<code>config/skills/名前/SKILL.md</code> に、name と description の front matter と手順を書きます。Claude は必要なときに自分で読み込みます。入力欄で <code>/名前</code> と打っても使えます。</>}>
        {skills.length === 0 ? <div className="empty-note">まだありません</div> : (
          <div className="list-card">
            {skills.map((s) => <div key={s.name} className="list-row"><code>/{s.name}</code><span className="field-hint">{s.description}</span></div>)}
          </div>
        )}
        <div className="button-row">
          <button type="button" className="button" onClick={() => void openPath("skills")}><FolderOpen size={14} /> skills のフォルダを開く</button>
          <button type="button" className="button" onClick={() => void refreshExtensions()}><RefreshCw size={14} /> 読み直す</button>
        </div>
      </Section>

      <Section title="自作コマンド" description={<><code>config/commands/名前.md</code> に送りたい文を書くと、入力欄の <code>/名前</code> で呼べます。<code>$ARGUMENTS</code> と書いた所はコマンドの後ろに書いた文に置き換わります。</>}>
        {custom.length === 0 ? <div className="empty-note">まだありません</div> : (
          <div className="list-card">
            {custom.map((c) => <div key={c.name} className="list-row"><code>/{c.name}</code><span className="field-hint">{c.description}</span></div>)}
          </div>
        )}
        <div className="button-row">
          <button type="button" className="button" onClick={() => void openPath("commands")}><FolderOpen size={14} /> commands のフォルダを開く</button>
        </div>
      </Section>

      <Section title="フック" description="Claude Code のフックと同じく、決まったタイミングでコマンドを動かします。セッションの情報やツールの入力は JSON で標準入力に渡します。">
        {HOOK_EVENTS.map((event) => (
          <HookList key={event.id} event={event} entries={hooks[event.id] ?? []} onChange={(entries) => setHooks({ ...hooks, [event.id]: entries })} />
        ))}
        <div className="settings-actions">
          <button type="button" className="button primary" disabled={!dirty} onClick={async () => {
            // 空のコマンドは保存しない
            const cleaned = Object.fromEntries(HOOK_EVENTS.map((e) => [e.id, (hooks[e.id] ?? []).filter((h) => h.command.trim())]).filter(([, list]) => list.length > 0));
            if (await saveHooks(cleaned)) toast("保存しました");
          }}>フックを保存</button>
        </div>
      </Section>
    </>
  );
}
