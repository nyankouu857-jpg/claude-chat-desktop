import { RefreshCw, TriangleAlert } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { localUsage, navigate, openPath, orgUsage, reloadPricing, setSecret, toast, updateSettings } from "../../actions";
import { modelLabel } from "../../models";
import { useStore } from "../../store";
import { NumberField, tokens, usd } from "../ui";
import { Section } from "./Settings";

interface Totals {
  costUsd: number;
  requests: number;
  input: number;
  output: number;
  cacheRead: number;
  webSearches: number;
  unpriced: number;
}

interface LocalSummary {
  thisMonth: Totals;
  lastMonth: Totals;
  lifetime: Totals;
  pricingError?: string | null;
  since: string | null;
  daily: { date: string; costUsd: number }[];
  monthly: { month: string; costUsd: number }[];
  byModelThisMonth: { model: string; costUsd: number; requests: number; input: number; output: number }[];
  byModelLifetime: { model: string; costUsd: number; requests: number; input: number; output: number }[];
  // サブエージェントの分は起動した会話に含めてある
  topConversationsThisMonth?: { conversationId: string; title?: string | null; costUsd: number; requests: number }[];
}

interface OrgSummary {
  from: string;
  to: string;
  totalUsd: number;
  byModel: { model: string; costUsd: number }[];
  byType: { type: string; costUsd: number }[];
  daily: { date: string; costUsd: number }[];
  fetchedAt?: string;
}

function StatTile({ label, totals, note }: { label: string; totals: Totals; note?: string }) {
  return (
    <div className="stat-tile">
      <div className="stat-label">{label}</div>
      <div className="stat-value">{usd(totals.costUsd)}</div>
      <div className="stat-sub">{totals.requests} 回 / 入力 {tokens(totals.input)} / 出力 {tokens(totals.output)}</div>
      {note && <div className="stat-sub">{note}</div>}
    </div>
  );
}

// 1 系列だけなので凡例は置かず、見出しで何の値かを示す
export function DailyBars({ data, title }: { data: { date: string; costUsd: number }[]; title: string }) {
  const [hover, setHover] = useState<number | null>(null);
  const max = Math.max(...data.map((d) => d.costUsd), 0);
  const height = 140;
  const slot = 24;
  const barWidth = 16;
  const width = Math.max(data.length * slot, slot);
  const scaled = (v: number) => (max > 0 ? Math.max((v / max) * (height - 8), v > 0 ? 3 : 0) : 0);
  const tickIndexes = data.length <= 1 ? [0] : [0, Math.floor((data.length - 1) / 2), data.length - 1];
  return (
    <figure className="chart">
      <figcaption className="chart-title">{title}</figcaption>
      <div className="chart-frame">
        <div className="chart-max">{usd(max)}</div>
        <svg viewBox={`0 0 ${width} ${height}`} preserveAspectRatio="none" className="chart-svg" onMouseLeave={() => setHover(null)}>
          <line x1="0" x2={width} y1={height - 0.5} y2={height - 0.5} className="chart-axis" />
          {data.map((d, i) => {
            const h = scaled(d.costUsd);
            const x = i * slot + (slot - barWidth) / 2;
            return (
              <g key={d.date} onMouseEnter={() => setHover(i)}>
                <rect x={i * slot} y={0} width={slot} height={height} fill="transparent" />
                {h > 0 && <path className={`chart-bar${hover === i ? " hover" : ""}`} d={roundedTop(x, height - h, barWidth, h, Math.min(4, h))} />}
              </g>
            );
          })}
        </svg>
        {hover !== null && data[hover] && (
          <div className="chart-tooltip" style={{ left: `${((hover + 0.5) / data.length) * 100}%` }}>
            <div>{data[hover].date}</div>
            <b>{usd(data[hover].costUsd, 4)}</b>
          </div>
        )}
      </div>
      <div className="chart-ticks">
        {tickIndexes.map((i) => <span key={i} style={{ left: `${((i + 0.5) / data.length) * 100}%` }}>{data[i]?.date.slice(5)}</span>)}
      </div>
    </figure>
  );
}

function roundedTop(x: number, y: number, w: number, h: number, r: number) {
  return `M${x},${y + h} L${x},${y + r} Q${x},${y} ${x + r},${y} L${x + w - r},${y} Q${x + w},${y} ${x + w},${y + r} L${x + w},${y + h} Z`;
}

function ModelTable({ rows }: { rows: { model: string; costUsd: number; requests?: number; input?: number; output?: number }[] }) {
  if (rows.length === 0) return <div className="empty-note">まだ記録がありません</div>;
  const hasTokens = rows.some((r) => r.input !== undefined);
  return (
    <table className="data-table">
      <thead>
        <tr>
          <th>モデル</th>
          {hasTokens && <th className="num">回数</th>}
          {hasTokens && <th className="num">入力</th>}
          {hasTokens && <th className="num">出力</th>}
          <th className="num">料金</th>
        </tr>
      </thead>
      <tbody>
        {rows.map((r) => (
          <tr key={r.model}>
            <td>{modelLabel(r.model)}</td>
            {hasTokens && <td className="num">{r.requests}</td>}
            {hasTokens && <td className="num">{tokens(r.input ?? 0)}</td>}
            {hasTokens && <td className="num">{tokens(r.output ?? 0)}</td>}
            <td className="num">{usd(r.costUsd)}</td>
          </tr>
        ))}
      </tbody>
    </table>
  );
}

function LocalUsage() {
  const [summary, setSummary] = useState<LocalSummary | null>(null);
  const [scope, setScope] = useState<"month" | "lifetime">("month");
  useEffect(() => {
    void localUsage().then((s) => s && setSummary(s));
  }, []);
  if (!summary) return null;
  return (
    <Section
      title="このアプリでの利用"
      description="このアプリから送ったリクエストのトークン数を記録し、config/pricing.json の単価から計算した目安です。"
    >
      <div className="stat-grid">
        <StatTile label="今月" totals={summary.thisMonth} />
        <StatTile label="先月" totals={summary.lastMonth} />
        <StatTile label="累計" totals={summary.lifetime} note={summary.since ? `${new Date(summary.since).toLocaleDateString("ja-JP")} から` : undefined} />
      </div>
      {summary.pricingError && <div className="warning-box"><TriangleAlert size={16} /> {summary.pricingError}</div>}
      {summary.lifetime.unpriced > 0 && (
        <div className="warning-box"><TriangleAlert size={16} /> 料金表にないモデルの記録が {summary.lifetime.unpriced} 件あり、料金に含まれていません。pricing.json に単価を足すと反映されます。</div>
      )}
      <DailyBars data={summary.daily} title="今月の日別料金" />
      <div className="table-header">
        <h3>モデル別</h3>
        <div className="segmented small">
          <button type="button" className={scope === "month" ? "on" : ""} onClick={() => setScope("month")}>今月</button>
          <button type="button" className={scope === "lifetime" ? "on" : ""} onClick={() => setScope("lifetime")}>累計</button>
        </div>
      </div>
      <ModelTable rows={scope === "month" ? summary.byModelThisMonth : summary.byModelLifetime} />
      {!!summary.topConversationsThisMonth?.length && (
        <>
          <h3>今月高くついた会話</h3>
          <div className="top-conversations">
            {summary.topConversationsThisMonth.map((c, i) => (
              <button
                key={c.conversationId}
                type="button"
                className="row"
                disabled={!c.title}
                title={c.title ? "この会話を開く" : "削除した会話です"}
                onClick={() => c.title && navigate({ name: "chat", id: c.conversationId })}
              >
                <span className="rank">{i + 1}</span>
                <span className={`title${c.title ? "" : " deleted"}`}>{c.title ?? "削除したチャット"}</span>
                <span className="field-hint">{c.requests} 回</span>
                <span className="cost">{usd(c.costUsd)}</span>
              </button>
            ))}
          </div>
        </>
      )}
      {summary.monthly.length > 1 && (
        <>
          <h3>月別</h3>
          <table className="data-table">
            <thead><tr><th>月</th><th className="num">料金</th></tr></thead>
            <tbody>{[...summary.monthly].reverse().map((m) => <tr key={m.month}><td>{m.month}</td><td className="num">{usd(m.costUsd)}</td></tr>)}</tbody>
          </table>
        </>
      )}
      <div className="button-row">
        <button type="button" className="button" onClick={() => void openPath("pricing")}>料金表を開く</button>
        <button type="button" className="button" onClick={async () => {
          const s = await reloadPricing();
          if (s) {
            setSummary(s);
            toast("料金表を読み直しました");
          }
        }}><RefreshCw size={15} /> 料金表を再読み込み</button>
      </div>
    </Section>
  );
}

const RANGES = [
  { id: "month", label: "今月" },
  { id: "lastMonth", label: "先月" },
  { id: "30d", label: "過去30日" },
  { id: "all", label: "全期間" },
];

function OrgUsage() {
  const secrets = useStore((s) => s.secrets);
  const [range, setRange] = useState("month");
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState<{ data?: OrgSummary; error?: string } | null>(null);
  const [adminKey, setAdminKey] = useState("");

  // 期間を続けて切り替えたとき、遅れて返った古い結果で上書きしない
  const latestRequest = useRef(0);
  async function fetchCost(r = range) {
    const request = ++latestRequest.current;
    setLoading(true);
    const fetched = await orgUsage(r);
    if (request !== latestRequest.current) return;
    setResult(fetched);
    setLoading(false);
  }

  return (
    <Section
      title="Anthropic の請求データ"
      description="Console の Cost 画面と同じデータです。このアプリを使う前の利用分や、他のツールからの利用分も含みます。反映まで数分かかります。"
    >
      <div className="key-status">
        {secrets?.hasAdminKey ? <>Admin API キー: <code>{secrets.adminKeyMasked}</code></> : "Admin API キーは未登録です。まずは通常の APIキーで取得を試みます"}
      </div>
      <div className="inline-form">
        <input className="input" type="password" placeholder="sk-ant-admin..." value={adminKey} onChange={(e) => setAdminKey(e.target.value)} autoComplete="off" />
        <button type="button" className="button" disabled={!adminKey.trim()} onClick={async () => {
          if (await setSecret("adminKey", adminKey)) {
            setAdminKey("");
            toast("Admin API キーを保存しました");
          }
        }}>保存</button>
        {secrets?.hasAdminKey && <button type="button" className="button ghost" onClick={() => void setSecret("adminKey", "")}>削除</button>}
      </div>
      <div className="inline-form">
        <div className="segmented">
          {RANGES.map((r) => (
            <button key={r.id} type="button" className={range === r.id ? "on" : ""} onClick={() => { setRange(r.id); if (result?.data) void fetchCost(r.id); }}>{r.label}</button>
          ))}
        </div>
        <button type="button" className="button primary" disabled={loading} onClick={() => void fetchCost()}>{loading ? "取得中..." : "取得する"}</button>
      </div>
      {range === "all" && !result && <div className="field-hint">全期間は初回だけ時間がかかります。2 回目からは保存済みの日の分を使います。</div>}
      {result?.error && <div className="warning-box"><TriangleAlert size={16} /> {result.error}</div>}
      {result?.data && (
        <>
          <div className="stat-grid">
            <div className="stat-tile">
              <div className="stat-label">{result.data.from} から {result.data.to}</div>
              <div className="stat-value">{usd(result.data.totalUsd)}</div>
              {result.data.fetchedAt && <div className="stat-sub">取得 {new Date(result.data.fetchedAt).toLocaleString("ja-JP")}</div>}
            </div>
          </div>
          {result.data.daily.length > 1 && <DailyBars data={result.data.daily} title="日別の請求額" />}
          <h3>モデル別</h3>
          <ModelTable rows={result.data.byModel} />
        </>
      )}
    </Section>
  );
}

function Budget() {
  const budget = useStore((s) => s.settings?.budget);
  if (!budget) return null;
  return (
    <Section title="料金の歯止め" description="金額は米ドルで、このアプリで記録した料金から数えます。0 にするとその歯止めは使いません。">
      <div className="switch-row">
        <div>
          <div>この金額を超えそうな送信は確認する</div>
          <div className="field-hint">送る量から入力の料金を見積もり、超えそうなら送る前にチャットに確認を出します。サブエージェントには出しません</div>
        </div>
        <NumberField value={budget.confirmRequestUsd} min={0} max={1000} step={0.01} onCommit={(v) => void updateSettings({ budget: { confirmRequestUsd: v } })} />
      </div>
      <div className="switch-row">
        <div>
          <div>1 日の上限</div>
          <div className="field-hint">今日の合計が届いたら、明日まで送りません。サブエージェントや予定の実行も止まります</div>
        </div>
        <NumberField value={budget.dailyLimitUsd} min={0} max={100000} step={0.01} onCommit={(v) => void updateSettings({ budget: { dailyLimitUsd: v } })} />
      </div>
      <div className="switch-row">
        <div>
          <div>1 か月の上限</div>
          <div className="field-hint">今月の合計が届いたら、来月まで送りません</div>
        </div>
        <NumberField value={budget.monthlyLimitUsd} min={0} max={1000000} step={0.01} onCommit={(v) => void updateSettings({ budget: { monthlyLimitUsd: v } })} />
      </div>
    </Section>
  );
}

export function Usage() {
  return (
    <>
      <Budget />
      <LocalUsage />
      <OrgUsage />
    </>
  );
}
