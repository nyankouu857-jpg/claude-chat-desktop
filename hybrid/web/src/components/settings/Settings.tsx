import { ExternalLink, FolderOpen, RefreshCw, TriangleAlert } from "lucide-react";
import { useEffect, useState, type ReactNode } from "react";
import { navigate, openPath, previewSystemPrompt, reloadSettings, revokePermission, setSecret, testApiKey, toast, updateSettings } from "../../actions";
import { EFFORTS, MODELS } from "../../models";
import { useStore } from "../../store";
import type { Settings, SettingsTab } from "../../types";
import { Toggle } from "../ui";
import { Connectors } from "./Connectors";
import { Files } from "./Files";
import { Usage } from "./Usage";

const TABS: { id: SettingsTab; label: string }[] = [
  { id: "general", label: "一般" },
  { id: "appearance", label: "外観" },
  { id: "api", label: "API とモデル" },
  { id: "files", label: "フォルダ" },
  { id: "connectors", label: "コネクタ" },
  { id: "permissions", label: "ツールの権限" },
  { id: "usage", label: "使用量と料金" },
  { id: "data", label: "データ" },
];

export function Section({ title, description, children }: { title: string; description?: ReactNode; children: ReactNode }) {
  return (
    <section className="settings-section">
      <h2>{title}</h2>
      {description && <p className="settings-desc">{description}</p>}
      {children}
    </section>
  );
}

function Field({ label, hint, children }: { label: string; hint?: string; children: ReactNode }) {
  return (
    <label className="field">
      <span className="field-label">{label}</span>
      {children}
      {hint && <span className="field-hint">{hint}</span>}
    </label>
  );
}

function General({ settings }: { settings: Settings }) {
  const [profile, setProfile] = useState(settings.profile);
  useEffect(() => setProfile(settings.profile), [settings.profile]);
  const dirty = JSON.stringify(profile) !== JSON.stringify(settings.profile);
  return (
    <>
      <Section title="プロフィール">
        <div className="field-row">
          <Field label="氏名">
            <input className="input" value={profile.fullName} onChange={(e) => setProfile({ ...profile, fullName: e.target.value })} />
          </Field>
          <Field label="Claude にどう呼んでほしいですか?">
            <input className="input" value={profile.nickname} placeholder="例: たろう" onChange={(e) => setProfile({ ...profile, nickname: e.target.value })} />
          </Field>
        </div>
      </Section>
      <Section title="個人設定" description="Claude に考慮してほしい好みや前提を書いてください。すべての会話に反映されます。">
        <textarea
          className="input"
          rows={7}
          value={profile.preferences}
          placeholder={"例: 回答は簡潔に、結論から書いてください。\nコードは TypeScript で書いてください。"}
          onChange={(e) => setProfile({ ...profile, preferences: e.target.value })}
        />
      </Section>
      <div className="settings-actions">
        <button type="button" className="button primary" disabled={!dirty} onClick={async () => {
          if (await updateSettings({ profile })) toast("保存しました");
        }}>変更を保存</button>
      </div>
    </>
  );
}

function ThemeCard({ id, label, current }: { id: Settings["appearance"]["theme"]; label: string; current: string }) {
  return (
    <button type="button" className={`theme-card${current === id ? " selected" : ""}`} onClick={() => void updateSettings({ appearance: { theme: id } })}>
      <span className={`theme-preview preview-${id}`}>
        <span className="preview-sidebar" />
        <span className="preview-main"><span className="preview-bubble" /><span className="preview-line" /><span className="preview-line short" /></span>
      </span>
      <span>{label}</span>
    </button>
  );
}

function Appearance({ settings }: { settings: Settings }) {
  const { theme, chatFont } = settings.appearance;
  const fonts: { id: Settings["appearance"]["chatFont"]; label: string; sample: string }[] = [
    { id: "default", label: "デフォルト", sample: "明朝体で読む" },
    { id: "sans", label: "システム", sample: "ゴシック体で読む" },
    { id: "large", label: "大きめ", sample: "文字を大きく" },
  ];
  return (
    <>
      <Section title="カラーモード">
        <div className="theme-cards">
          <ThemeCard id="light" label="ライト" current={theme} />
          <ThemeCard id="system" label="自動" current={theme} />
          <ThemeCard id="dark" label="ダーク" current={theme} />
        </div>
      </Section>
      <Section title="チャットのフォント">
        <div className="font-options">
          {fonts.map((f) => (
            <button key={f.id} type="button" className={`font-option font-${f.id}${chatFont === f.id ? " selected" : ""}`} onClick={() => void updateSettings({ appearance: { chatFont: f.id } })}>
              <span className="font-sample">Aa あ</span>
              <span>{f.label}</span>
              <span className="field-hint">{f.sample}</span>
            </button>
          ))}
        </div>
      </Section>
    </>
  );
}

function Api({ settings }: { settings: Settings }) {
  const secrets = useStore((s) => s.secrets);
  const [key, setKey] = useState("");
  const [test, setTest] = useState<{ ok: boolean; message: string } | null>(null);
  const [testing, setTesting] = useState(false);
  const chat = settings.chat;
  return (
    <>
      <Section
        title="APIキー"
        description={<>console.anthropic.com の API Keys で発行したキー (sk-ant- で始まる文字列) を登録します。キーは Windows の DPAPI で暗号化して config/secrets.json に保存されます。</>}
      >
        <div className="key-status">
          {secrets?.hasApiKey ? <>登録済み: <code>{secrets.apiKeyMasked}</code></> : <span className="warning-text">未登録です</span>}
        </div>
        <div className="inline-form">
          <input className="input" type="password" placeholder={secrets?.hasApiKey ? "変更するときだけ入力" : "sk-ant-..."} value={key} onChange={(e) => setKey(e.target.value)} autoComplete="off" spellCheck={false} />
          <button type="button" className="button primary" disabled={!key.trim()} onClick={async () => {
            if (await setSecret("apiKey", key)) {
              setKey("");
              setTest(null);
              toast("APIキーを保存しました");
            }
          }}>保存</button>
          <button type="button" className="button" disabled={!secrets?.hasApiKey || testing} onClick={async () => {
            setTesting(true);
            setTest((await testApiKey()) ?? null);
            setTesting(false);
          }}>{testing ? "確認中..." : "接続テスト"}</button>
        </div>
        {test && <div className={`test-result${test.ok ? " ok" : " ng"}`}>{test.message}</div>}
        <button type="button" className="link-button" onClick={() => window.open("https://console.anthropic.com/settings/keys", "_blank")}>
          キーを発行する <ExternalLink size={13} />
        </button>
      </Section>

      <Section title="既定のモデル" description="新しいチャットで最初に選ばれるモデルです。入力欄のモデル選択でいつでも切り替えられます。">
        <div className="radio-list">
          {MODELS.map((m) => (
            <label key={m.id} className={`radio-row${chat.model === m.id ? " selected" : ""}`}>
              <input type="radio" checked={chat.model === m.id} onChange={() => void updateSettings({ chat: { model: m.id } })} />
              <span className="radio-title">{m.label}</span>
              <span className="field-hint">{m.description}</span>
            </label>
          ))}
        </div>
      </Section>

      <Section title="思考の深さ" description="上げるほど難しい問題に強くなる代わりに、時間と料金がかかります。">
        <div className="segmented">
          {EFFORTS.map((e) => (
            <button key={e.id} type="button" className={chat.effort === e.id ? "on" : ""} title={e.description} onClick={() => void updateSettings({ chat: { effort: e.id } })}>{e.label}</button>
          ))}
        </div>
      </Section>

      <Section title="動作">
        <div className="switch-row">
          <div>
            <div>Web 検索を最初からオンにする</div>
            <div className="field-hint">検索にはトークンとは別に料金がかかります</div>
          </div>
          <Toggle checked={chat.webSearch} onChange={(v) => void updateSettings({ chat: { webSearch: v } })} />
        </div>
        <div className="switch-row">
          <div>
            <div>思考プロセスを表示する</div>
            <div className="field-hint">オフにすると、考え中の表示だけになります</div>
          </div>
          <Toggle checked={chat.showThinking} onChange={(v) => void updateSettings({ chat: { showThinking: v } })} />
        </div>
        <div className="switch-row">
          <div>
            <div>1 回の返答で続けて呼べるツールの上限</div>
            <div className="field-hint">コネクタのツールを使うときに、止まらなくなるのを防ぎます</div>
          </div>
          <input className="input number" type="number" min={1} max={200} value={chat.maxToolTurns}
            onChange={(e) => void updateSettings({ chat: { maxToolTurns: Math.max(1, Number(e.target.value) || 1) } })} />
        </div>
      </Section>

      <SystemPrompt chat={chat} />
    </>
  );
}

function SystemPrompt({ chat }: { chat: Settings["chat"] }) {
  const [draft, setDraft] = useState(chat.systemPrompt);
  const [preview, setPreview] = useState<string | null>(null);
  useEffect(() => setDraft(chat.systemPrompt), [chat.systemPrompt]);
  const dirty = draft !== chat.systemPrompt;
  return (
    <Section
      title="システムプロンプト"
      description={<>すべての会話で、最初に Claude へ渡す指示です。空のままでも使えます。<code>{"{{currentDateTime}}"}</code> と書いた所は今日の日付に置き換わります。</>}
    >
      <textarea
        className="input"
        rows={10}
        value={draft}
        placeholder={"例: あなたは丁寧な日本語で答えるアシスタントです。\n箇条書きは必要なときだけ使ってください。"}
        onChange={(e) => setDraft(e.target.value)}
      />
      <div className="switch-row">
        <div>
          <div>日付、呼び名、フォルダの一覧を自動で付ける</div>
          <div className="field-hint">オフにすると、Claude は今日の日付が分からなくなります。個人設定はオンオフに関係なく付きます</div>
        </div>
        <Toggle checked={chat.appendAppContext} onChange={(v) => void updateSettings({ chat: { appendAppContext: v } })} />
      </div>
      <div className="settings-actions">
        <button type="button" className="button" disabled={dirty} title={dirty ? "保存してから確認できます" : undefined} onClick={async () => {
          setPreview((await previewSystemPrompt()) ?? null);
        }}>送る内容を確認</button>
        <button type="button" className="button primary" disabled={!dirty} onClick={async () => {
          if (await updateSettings({ chat: { systemPrompt: draft } })) {
            setPreview(null);
            toast("保存しました");
          }
        }}>変更を保存</button>
      </div>
      {preview !== null && <pre className="prompt-preview">{preview || "何も送りません"}</pre>}
    </Section>
  );
}

function Permissions({ settings }: { settings: Settings }) {
  const mcp = useStore((s) => s.mcp);
  const entries = Object.entries(settings.toolPermissions);
  const describe = (apiName: string) => {
    for (const server of mcp) {
      const tool = server.tools.find((t) => t.apiName === apiName);
      if (tool) return `${server.name} / ${tool.title || tool.name}`;
    }
    return apiName;
  };
  return (
    <Section title="常に許可しているツール" description="ここにないツールは、Claude が使おうとするたびに確認が出ます。">
      {entries.length === 0 ? (
        <div className="empty-note">まだありません。ツールの確認で「常に許可」を選ぶとここに追加されます。</div>
      ) : (
        <div className="list-card">
          {entries.map(([apiName]) => (
            <div key={apiName} className="list-row">
              <span>{describe(apiName)}</span>
              <button type="button" className="button small" onClick={() => void revokePermission(apiName)}>取り消す</button>
            </div>
          ))}
        </div>
      )}
    </Section>
  );
}

function Data() {
  const paths = useStore((s) => s.paths);
  const version = useStore((s) => s.version);
  return (
    <>
      <Section title="アプリのフォルダ" description="設定、会話履歴、使用量の記録、ログは、すべてこのフォルダの中にあります。フォルダごとコピーすればバックアップになります。">
        {paths?.isFallback && (
          <div className="warning-box">
            <TriangleAlert size={16} />
            exe の置き場所に書き込めなかったため、代わりにこの場所を使っています。exe をドキュメントなど書き込める場所へ移すと、exe と同じフォルダにまとまります。
          </div>
        )}
        <code className="path">{paths?.root}</code>
        <div className="button-row">
          <button type="button" className="button" onClick={() => void openPath("root")}><FolderOpen size={15} /> フォルダを開く</button>
          <button type="button" className="button" onClick={() => void openPath("logs")}><FolderOpen size={15} /> ログ</button>
        </div>
      </Section>
      <Section title="設定ファイル" description="config/settings.json を直接編集できます。編集したら再読み込みしてください。コネクタは Claude Desktop と同じ mcpServers の書き方が使えます。">
        <div className="button-row">
          <button type="button" className="button" onClick={() => void openPath("settings")}>settings.json を開く</button>
          <button type="button" className="button" onClick={() => void openPath("pricing")}>pricing.json を開く</button>
          <button type="button" className="button" onClick={() => void reloadSettings()}><RefreshCw size={15} /> 再読み込み</button>
        </div>
      </Section>
      <Section title="このアプリについて">
        <div className="field-hint">ClaudeChat バージョン {version}</div>
      </Section>
    </>
  );
}

export function SettingsPage({ tab }: { tab: SettingsTab }) {
  const settings = useStore((s) => s.settings);
  if (!settings) return null;
  return (
    <div className="page">
      <div className="page-inner settings-page">
        <h1 className="page-title">設定</h1>
        <div className="settings-layout">
          <nav className="settings-nav">
            {TABS.map((t) => (
              <button key={t.id} type="button" className={t.id === tab ? "active" : ""} onClick={() => navigate({ name: "settings", tab: t.id })}>{t.label}</button>
            ))}
          </nav>
          <div className="settings-content">
            {tab === "general" && <General settings={settings} />}
            {tab === "appearance" && <Appearance settings={settings} />}
            {tab === "api" && <Api settings={settings} />}
            {tab === "files" && <Files settings={settings} />}
            {tab === "connectors" && <Connectors settings={settings} />}
            {tab === "permissions" && <Permissions settings={settings} />}
            {tab === "usage" && <Usage />}
            {tab === "data" && <Data />}
          </div>
        </div>
      </div>
    </div>
  );
}
