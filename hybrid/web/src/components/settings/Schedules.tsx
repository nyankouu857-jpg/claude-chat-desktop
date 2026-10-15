import { Plus, Trash } from "lucide-react";
import { useEffect, useState } from "react";
import { deleteSchedule, listSchedules, saveSchedule, setScheduleEnabled, toast } from "../../actions";
import { on } from "../../bridge";
import { useStore } from "../../store";
import type { Schedule } from "../../types";
import { Toggle } from "../ui";
import { Section } from "./Settings";

function describe(s: Schedule) {
  const when = s.intervalMinutes ? (s.intervalMinutes % 60 === 0 ? `${s.intervalMinutes / 60} 時間ごと` : `${s.intervalMinutes} 分ごと`) : `毎日 ${s.dailyTime}`;
  const next = s.nextRunAt ? ` ・ 次回 ${new Date(s.nextRunAt).toLocaleString("ja-JP", { month: "numeric", day: "numeric", hour: "2-digit", minute: "2-digit" })}` : "";
  const expires = s.expiresAt ? ` ・ ${new Date(s.expiresAt).toLocaleDateString("ja-JP")} に終了` : "";
  return `${when}${next}${expires}`;
}

const EMPTY: Partial<Schedule> = { title: "", prompt: "", intervalMinutes: null, dailyTime: "09:00", conversationId: null };

export function Schedules() {
  // 選ぶ側で絞る。セレクタで新しい配列を作ると再描画が止まらない
  const summaries = useStore((s) => s.summaries).filter((c) => c.kind === "chat");
  const [schedules, setSchedules] = useState<Schedule[]>([]);
  const [draft, setDraft] = useState<Partial<Schedule>>(EMPTY);
  const [mode, setMode] = useState<"daily" | "interval">("daily");

  useEffect(() => {
    const load = async () => setSchedules((await listSchedules()) ?? []);
    void load();
    const off = on("schedules.changed", () => void load());
    return () => { off(); };
  }, []);

  const save = async () => {
    const schedule = { ...draft, intervalMinutes: mode === "interval" ? draft.intervalMinutes ?? 60 : null, dailyTime: mode === "daily" ? draft.dailyTime : null };
    const updated = await saveSchedule(schedule);
    if (!updated) return;
    setSchedules(updated);
    setDraft(EMPTY);
    toast("予定を登録しました");
  };

  return (
    <>
      <Section title="予定" description="決まった時刻や間隔で、Claude に頼みごとを送ります。アプリを開いている間だけ動き、閉じている間に過ぎた分は次に開いたときに 1 回だけ動きます。チャットで /loop と打つか、Claude に頼んでも登録できます。">
        {schedules.length === 0 ? <div className="empty-note">まだありません</div> : (
          <div className="list-card">
            {schedules.map((s) => {
              const target = s.conversationId ? summaries.find((c) => c.id === s.conversationId)?.title ?? "(消えたチャット)" : "毎回新しいチャット";
              return (
                <div key={s.id} className="list-row schedule-row">
                  <span className="schedule-text">
                    <span>{s.title}</span>
                    <span className="field-hint">{describe(s)} ・ {target}</span>
                  </span>
                  <Toggle checked={s.enabled} label="有効" onChange={async (v) => setSchedules((await setScheduleEnabled(s.id, v)) ?? schedules)} />
                  <button type="button" className="icon-button small" title="消す" onClick={async () => setSchedules((await deleteSchedule(s.id)) ?? schedules)}><Trash size={14} /></button>
                </div>
              );
            })}
          </div>
        )}
      </Section>

      <Section title="予定を追加">
        <label className="field">
          <span className="field-label">名前</span>
          <input className="input" value={draft.title ?? ""} placeholder="例: 朝のニュースまとめ" onChange={(e) => setDraft({ ...draft, title: e.target.value })} />
        </label>
        <label className="field">
          <span className="field-label">頼むこと</span>
          <textarea className="input" rows={4} value={draft.prompt ?? ""} placeholder="例: 今日の AI 関連のニュースを 5 つ、Web で調べてまとめてください" onChange={(e) => setDraft({ ...draft, prompt: e.target.value })} />
        </label>
        <div className="field-row">
          <label className="field">
            <span className="field-label">いつ</span>
            <select className="input" value={mode} onChange={(e) => setMode(e.target.value as "daily" | "interval")}>
              <option value="daily">毎日決まった時刻</option>
              <option value="interval">一定の間隔</option>
            </select>
          </label>
          {mode === "daily" ? (
            <label className="field">
              <span className="field-label">時刻</span>
              <input className="input" type="time" value={draft.dailyTime ?? "09:00"} onChange={(e) => setDraft({ ...draft, dailyTime: e.target.value })} />
            </label>
          ) : (
            <label className="field">
              <span className="field-label">間隔 (分、5 以上)</span>
              <input className="input number" type="number" min={5} value={draft.intervalMinutes ?? 60} onChange={(e) => setDraft({ ...draft, intervalMinutes: Number(e.target.value) })} />
            </label>
          )}
        </div>
        <label className="field">
          <span className="field-label">送り先</span>
          <select className="input" value={draft.conversationId ?? ""} onChange={(e) => setDraft({ ...draft, conversationId: e.target.value || null })}>
            <option value="">毎回新しいチャットを作る</option>
            {summaries.map((c) => <option key={c.id} value={c.id}>{c.title}</option>)}
          </select>
        </label>
        <div className="settings-actions">
          <button type="button" className="button primary" disabled={!draft.title?.trim() || !draft.prompt?.trim()} onClick={() => void save()}><Plus size={14} /> 登録</button>
        </div>
      </Section>
    </>
  );
}
