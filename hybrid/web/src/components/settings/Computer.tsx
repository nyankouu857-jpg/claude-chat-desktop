import { Copy, FolderOpen, RefreshCw, TriangleAlert } from "lucide-react";
import { useEffect, useState } from "react";
import { askConfirm, browserExtensionStatus, resetBrowserToken, toast, updateSettings, writeBrowserExtension, type BrowserExtensionStatus } from "../../actions";
import type { Settings } from "../../types";
import { NumberField, Toggle } from "../ui";
import { Section } from "./Settings";

// 拡張機能は 30 秒ごとにつなぎに来るので、画面を開いている間だけ様子を見る
const STATUS_POLL_MS = 2000;

function ExtensionSetup() {
  const [status, setStatus] = useState<BrowserExtensionStatus | null>(null);
  const token = status?.token;
  const [folder, setFolder] = useState<string | null>(null);
  useEffect(() => {
    let alive = true;
    const poll = async () => {
      try {
        const next = await browserExtensionStatus();
        if (alive) setStatus(next);
      } catch {
        // 画面の切り替え中
      }
    };
    void poll();
    const timer = setInterval(poll, STATUS_POLL_MS);
    return () => { alive = false; clearInterval(timer); };
  }, []);
  return (
    <div className="extension-setup">
      <div className={`extension-status${status?.connected ? " on" : ""}`}>
        <span className="status-dot-inline" />
        {status?.connected ? `いつもの Chrome とつながっています (拡張機能 ${status.version})` : "まだつながっていません"}
      </div>
      {status?.error && <div className="warning-box"><TriangleAlert size={16} /> {status.error}</div>}
      <ol className="extension-steps">
        <li>
          <div>拡張機能のファイルを書き出します。</div>
          <button type="button" className="button small" onClick={async () => setFolder((await writeBrowserExtension()) ?? null)}><FolderOpen size={14} /> 書き出してフォルダを開く</button>
          {folder && <div className="field-hint mono">{folder}</div>}
        </li>
        <li>Chrome で <code>chrome://extensions</code> を開き、右上の「デベロッパー モード」をオンにします。</li>
        <li>「パッケージ化されていない拡張機能を読み込む」を押し、書き出したフォルダを選びます。</li>
        <li>
          <div>ツールバーの拡張機能のボタンから「ClaudeChat 連携」を開き、この接続コードを貼り付けて「保存してつなぐ」を押します。</div>
          <div className="inline-form">
            <input className="input mono" readOnly value={token ?? ""} />
            <button type="button" className="button small" disabled={!token} onClick={async () => { await navigator.clipboard.writeText(token ?? ""); toast("コピーしました"); }}><Copy size={14} /> コピー</button>
            <button type="button" className="button small ghost" title="接続コードを作り直す" onClick={async () => {
              if (!(await askConfirm("接続コードを作り直す", "今つながっている拡張機能は切れます。拡張機能に新しいコードを入れ直してください。"))) return;
              const next = await resetBrowserToken();
              if (next) setStatus(next);
            }}><RefreshCw size={14} /></button>
          </div>
        </li>
      </ol>
      <div className="field-hint">
        Claude が操作するのは、Claude が開いた「ClaudeChat」グループのタブだけです。ふだん開いているタブは一覧にも出しません。Claude が開いたタブが、開くのを許可していないサイト (メールなど) に移ったときは、もう一度確認するまで中身を読みません。いつもの Chrome では「常に許可」は使えず、ページを開く・クリック・入力は毎回確認します。操作中のタブには Chrome が「デバッグを開始しました」という帯を出し、そこで「キャンセル」を押すと、そのタブはもう操作しません。アプリを更新したら、もう一度書き出して chrome://extensions で再読み込みしてください。
      </div>
    </div>
  );
}

export function Computer({ settings }: { settings: Settings }) {
  const { terminal, programs, browser } = settings;
  const [browserPath, setBrowserPath] = useState(browser.executablePath ?? "");
  useEffect(() => setBrowserPath(browser.executablePath ?? ""), [browser.executablePath]);

  return (
    <>
      <div className="warning-box computer-warning">
        <TriangleAlert size={16} />
        ここで有効にした操作は、Claude があなたの PC で実際に行います。Web ページやファイルに書かれた指示に Claude がだまされることもあるので、確認のカードは中身を読んでから許可してください。
      </div>

      <Section title="ターミナル" description="Claude がコマンドを実行できます。Windows では PowerShell で動きます。実行の前には毎回確認が出ます。">
        <div className="switch-row">
          <div>
            <div>ターミナルを使う</div>
            <div className="field-hint">最初の作業フォルダは、フォルダ操作の基準フォルダです。未設定ならユーザーのホームフォルダです</div>
          </div>
          <Toggle checked={terminal.enabled} onChange={(v) => void updateSettings({ terminal: { enabled: v } })} label="ターミナルを使う" />
        </div>
        <div className="switch-row">
          <div>
            <div>1 回のコマンドで待つ時間 (秒)</div>
            <div className="field-hint">これを過ぎると止めます。サーバーのように動き続けるものは、Claude がバックグラウンドで実行します</div>
          </div>
          <NumberField value={terminal.timeoutSeconds} min={1} max={600} onCommit={(v) => void updateSettings({ terminal: { timeoutSeconds: v } })} />
        </div>
      </Section>

      <Section title="アプリの起動" description="Claude が exe やショートカットを起動したり、ファイルやフォルダを既定のアプリで開いたりできます。開いているウィンドウを閉じることもできます。">
        <div className="switch-row">
          <div>
            <div>アプリの起動を使う</div>
            <div className="field-hint">アプリを探す、開いているウィンドウを見る、は確認なしで実行します。起動と閉じる操作は毎回確認します</div>
          </div>
          <Toggle checked={programs.enabled} onChange={(v) => void updateSettings({ programs: { enabled: v } })} label="アプリの起動を使う" />
        </div>
      </Section>

      <Section title="ブラウザ操作" description="Claude が Chrome を開いて、ページを読んだり、クリックや入力をしたりできます。">
        <div className="switch-row">
          <div>
            <div>ブラウザ操作を使う</div>
            <div className="field-hint">ページを読む、スクリーンショット、スクロールは確認なしで実行します。クリックや入力は毎回確認します</div>
          </div>
          <Toggle checked={browser.enabled} onChange={(v) => void updateSettings({ browser: { enabled: v } })} label="ブラウザ操作を使う" />
        </div>
        <div className="field-label browser-path-label">使うブラウザ</div>
        <div className="segmented">
          <button type="button" className={browser.mode !== "extension" ? "on" : ""} onClick={() => void updateSettings({ browser: { mode: "dedicated" } })}>アプリ専用の Chrome</button>
          <button type="button" className={browser.mode === "extension" ? "on" : ""} onClick={() => void updateSettings({ browser: { mode: "extension" } })}>いつもの Chrome</button>
        </div>
        {browser.mode === "extension" ? (
          browser.enabled ? <ExtensionSetup /> : <div className="field-hint">ブラウザ操作をオンにすると、つなぎ方が出ます</div>
        ) : (
          <>
            <div className="field-hint">ふだんのブラウザとは別の窓で動きます。ログインが必要なサイトは、その窓で一度ログインしてください。ログイン情報などは代表フォルダの data/browser-profile に保存されます</div>
            <div className="field-label">ブラウザの場所</div>
            <div className="inline-form">
              <input className="input" value={browserPath} placeholder="空なら Chrome、無ければ Edge を自動で探します" spellCheck={false} onChange={(e) => setBrowserPath(e.target.value)} />
              <button type="button" className="button" disabled={browserPath === (browser.executablePath ?? "")}
                onClick={() => void updateSettings({ browser: { executablePath: browserPath.trim().replace(/^"(.*)"$/, "$1") || null } })}>保存</button>
            </div>
          </>
        )}
      </Section>
    </>
  );
}
