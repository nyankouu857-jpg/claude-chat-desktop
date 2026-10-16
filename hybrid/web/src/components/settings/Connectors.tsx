import { Braces, ChevronRight, FileText, Pencil, Plus, RefreshCw, Trash } from "lucide-react";
import { useState } from "react";
import { askConfirm, openMcpLog, restartMcp, saveMcpServers, toast } from "../../actions";
import { useStore } from "../../store";
import type { McpServerConfig, McpServerStatus, Settings } from "../../types";
import { Dialog, Toggle } from "../ui";
import { Section } from "./Settings";

const STATUS_LABEL: Record<McpServerStatus["status"], string> = {
  running: "接続中",
  starting: "起動中...",
  error: "エラー",
  stopped: "停止",
  disabled: "無効",
};

function linesToRecord(text: string, separator: "=" | ":") {
  const record: Record<string, string> = {};
  for (const line of text.split("\n")) {
    const at = line.indexOf(separator);
    if (at <= 0) continue;
    record[line.slice(0, at).trim()] = line.slice(at + 1).trim();
  }
  return Object.keys(record).length ? record : undefined;
}

const recordToLines = (record: Record<string, string> | undefined, separator: string) =>
  Object.entries(record ?? {}).map(([k, v]) => `${k}${separator}${v}`).join("\n");

function ServerDialog({ name: initialName, config, existing, onClose }: {
  name?: string;
  config?: McpServerConfig;
  existing: Record<string, McpServerConfig>;
  onClose: () => void;
}) {
  const [name, setName] = useState(initialName ?? "");
  const [kind, setKind] = useState<"local" | "remote">(config?.url ? "remote" : "local");
  const [command, setCommand] = useState(config?.command ?? "");
  const [args, setArgs] = useState((config?.args ?? []).join("\n"));
  const [env, setEnv] = useState(recordToLines(config?.env, "="));
  const [cwd, setCwd] = useState(config?.cwd ?? "");
  const [url, setUrl] = useState(config?.url ?? "");
  const [headers, setHeaders] = useState(recordToLines(config?.headers, ": "));

  async function save() {
    const trimmed = name.trim();
    if (!trimmed) return toast("名前を入力してください", "error");
    if (trimmed !== initialName && existing[trimmed]) return toast("同じ名前のコネクタがあります", "error");
    const next: McpServerConfig = kind === "local"
      ? {
          command: command.trim(),
          args: args.split("\n").map((a) => a.trim()).filter(Boolean),
          env: linesToRecord(env, "="),
          cwd: cwd.trim() || undefined,
          disabled: config?.disabled,
        }
      : { type: "http", url: url.trim(), headers: linesToRecord(headers, ":"), disabled: config?.disabled };
    const servers = { ...existing };
    if (initialName) delete servers[initialName];
    servers[trimmed] = next;
    if (await saveMcpServers(servers)) onClose();
  }

  return (
    <Dialog
      title={initialName ? "コネクタを編集" : "カスタムコネクタを追加"}
      onClose={onClose}
      wide
      footer={
        <>
          <button type="button" className="button ghost" onClick={onClose}>キャンセル</button>
          <button type="button" className="button primary" onClick={save}>保存</button>
        </>
      }
    >
      <label className="field">
        <span className="field-label">名前</span>
        <input className="input" value={name} placeholder="例: filesystem" onChange={(e) => setName(e.target.value)} />
      </label>
      <div className="segmented">
        <button type="button" className={kind === "local" ? "on" : ""} onClick={() => setKind("local")}>ローカル (コマンドで起動)</button>
        <button type="button" className={kind === "remote" ? "on" : ""} onClick={() => setKind("remote")}>リモート (URL)</button>
      </div>
      {kind === "local" ? (
        <>
          <label className="field">
            <span className="field-label">コマンド</span>
            <input className="input mono" value={command} placeholder="npx" onChange={(e) => setCommand(e.target.value)} />
          </label>
          <label className="field">
            <span className="field-label">引数 (1 行に 1 つ)</span>
            <textarea className="input mono" rows={4} value={args} placeholder={"-y\n@modelcontextprotocol/server-filesystem\nC:\\Users\\me\\Documents"} onChange={(e) => setArgs(e.target.value)} />
          </label>
          <label className="field">
            <span className="field-label">環境変数 (KEY=VALUE を 1 行に 1 つ)</span>
            <textarea className="input mono" rows={3} value={env} onChange={(e) => setEnv(e.target.value)} />
          </label>
          <label className="field">
            <span className="field-label">作業フォルダ (省略可)</span>
            <input className="input mono" value={cwd} onChange={(e) => setCwd(e.target.value)} />
          </label>
        </>
      ) : (
        <>
          <label className="field">
            <span className="field-label">URL</span>
            <input className="input mono" value={url} placeholder="https://example.com/mcp" onChange={(e) => setUrl(e.target.value)} />
          </label>
          <label className="field">
            <span className="field-label">ヘッダー (Name: Value を 1 行に 1 つ)</span>
            <textarea className="input mono" rows={3} value={headers} placeholder="Authorization: Bearer xxxxx" onChange={(e) => setHeaders(e.target.value)} />
          </label>
        </>
      )}
    </Dialog>
  );
}

function JsonDialog({ servers, onClose }: { servers: Record<string, McpServerConfig>; onClose: () => void }) {
  const [text, setText] = useState(JSON.stringify({ mcpServers: servers }, null, 2));
  async function save() {
    let parsed: any;
    try {
      parsed = JSON.parse(text);
    } catch (e) {
      return toast(`JSON として読めません: ${(e as Error).message}`, "error");
    }
    // Claude Desktop の設定ファイルを丸ごと貼っても、mcpServers だけを貼っても受け付ける
    const next = parsed?.mcpServers ?? parsed;
    if (typeof next !== "object" || Array.isArray(next)) return toast("mcpServers の形になっていません", "error");
    if (await saveMcpServers(next)) onClose();
  }
  return (
    <Dialog
      title="JSON で編集"
      onClose={onClose}
      wide
      footer={
        <>
          <button type="button" className="button ghost" onClick={onClose}>キャンセル</button>
          <button type="button" className="button primary" onClick={save}>保存</button>
        </>
      }
    >
      <p className="settings-desc">Claude Desktop の claude_desktop_config.json と同じ書き方です。MCP サーバーの説明書にある設定をそのまま貼り付けられます。</p>
      <textarea className="input mono json-editor" rows={18} value={text} spellCheck={false} onChange={(e) => setText(e.target.value)} />
    </Dialog>
  );
}

function ServerCard({ status, config, servers, onEdit }: {
  status?: McpServerStatus;
  config: McpServerConfig;
  servers: Record<string, McpServerConfig>;
  onEdit: () => void;
}) {
  const [open, setOpen] = useState(false);
  const name = status?.name ?? "";
  const state = config.disabled ? "disabled" : status?.status ?? "stopped";
  return (
    <div className="server-card">
      <div className="server-head">
        <span className={`status-dot ${state}`} />
        <div className="server-main">
          <div className="server-name">{name}</div>
          <div className="server-target">{config.url ?? [config.command, ...(config.args ?? [])].join(" ")}</div>
        </div>
        <span className={`status-label ${state}`}>{STATUS_LABEL[state]}</span>
        <Toggle
          checked={!config.disabled}
          label={`${name} を有効にする`}
          onChange={(enabled) => void saveMcpServers({ ...servers, [name]: { ...config, disabled: !enabled } })}
        />
      </div>
      {status?.error && state === "error" && <div className="server-error">{status.error}</div>}
      <div className="server-actions">
        <button type="button" className="link-button" disabled={!status?.tools.length} onClick={() => setOpen((o) => !o)}>
          <ChevronRight size={14} className={`chevron${open ? " open" : ""}`} /> ツール {status?.tools.length ?? 0} 個
        </button>
        <span className="spacer" />
        <button type="button" className="icon-button small" title="再起動" disabled={!!config.disabled} onClick={() => void restartMcp(name)}><RefreshCw size={15} /></button>
        <button type="button" className="icon-button small" title="ログを開く" onClick={() => void openMcpLog(name)}><FileText size={15} /></button>
        <button type="button" className="icon-button small" title="編集" onClick={onEdit}><Pencil size={15} /></button>
        <button type="button" className="icon-button small" title="削除" onClick={async () => {
          if (!(await askConfirm("コネクタを削除", `${name} を削除しますか?`))) return;
          const { [name]: _, ...rest } = servers;
          void saveMcpServers(rest);
        }}><Trash size={15} /></button>
      </div>
      {open && (
        <ul className="tool-list">
          {status?.tools.map((t) => (
            <li key={t.name}>
              <span className="tool-name">{t.title || t.name}</span>
              {t.description && <span className="field-hint">{t.description}</span>}
            </li>
          ))}
        </ul>
      )}
    </div>
  );
}

export function Connectors({ settings }: { settings: Settings }) {
  const statuses = useStore((s) => s.mcp);
  const [editing, setEditing] = useState<{ name?: string } | null>(null);
  const [jsonOpen, setJsonOpen] = useState(false);
  const servers = settings.mcpServers;
  const names = Object.keys(servers).sort();
  return (
    <>
      <Section
        title="コネクタ"
        description="MCP サーバーをつなぐと、Claude がファイル操作や外部サービスなどのツールを使えるようになります。ツールを実行する前には毎回確認が出ます。"
      >
        <div className="button-row">
          <button type="button" className="button primary" onClick={() => setEditing({})}><Plus size={15} /> カスタムコネクタを追加</button>
          <button type="button" className="button" onClick={() => setJsonOpen(true)}><Braces size={15} /> JSON で編集</button>
        </div>
        {names.length === 0 ? (
          <div className="empty-note">
            まだコネクタがありません。たとえばファイルを読み書きさせたいなら、コマンドに npx、引数に -y と @modelcontextprotocol/server-filesystem と許可するフォルダを指定します。Node.js が必要です。
          </div>
        ) : (
          <div className="server-list">
            {names.map((name) => (
              <ServerCard
                key={name}
                status={statuses.find((s) => s.name === name) ?? { name, kind: "local", target: "", status: "stopped", tools: [] }}
                config={servers[name]}
                servers={servers}
                onEdit={() => setEditing({ name })}
              />
            ))}
          </div>
        )}
      </Section>
      {editing && <ServerDialog name={editing.name} config={editing.name ? servers[editing.name] : undefined} existing={servers} onClose={() => setEditing(null)} />}
      {jsonOpen && <JsonDialog servers={servers} onClose={() => setJsonOpen(false)} />}
    </>
  );
}
