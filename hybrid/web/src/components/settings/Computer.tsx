import { TriangleAlert } from "lucide-react";
import { useEffect, useState } from "react";
import { updateSettings } from "../../actions";
import type { Settings } from "../../types";
import { NumberField, Toggle } from "../ui";
import { Section } from "./Settings";

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

      <Section title="ブラウザ操作" description="Claude が Chrome (無ければ Edge) を開いて、ページを読んだり、クリックや入力をしたりできます。ふだん使っているブラウザとは別の、このアプリ専用の窓で動きます。">
        <div className="switch-row">
          <div>
            <div>ブラウザ操作を使う</div>
            <div className="field-hint">ページを読む、スクリーンショット、スクロールは確認なしで実行します。ログインが必要なサイトは、専用の窓で一度ログインしてください</div>
          </div>
          <Toggle checked={browser.enabled} onChange={(v) => void updateSettings({ browser: { enabled: v } })} label="ブラウザ操作を使う" />
        </div>
        <div className="field-label browser-path-label">ブラウザの場所</div>
        <div className="inline-form">
          <input className="input" value={browserPath} placeholder="空なら Chrome、無ければ Edge を自動で探します" spellCheck={false} onChange={(e) => setBrowserPath(e.target.value)} />
          <button type="button" className="button" disabled={browserPath === (browser.executablePath ?? "")}
            onClick={() => void updateSettings({ browser: { executablePath: browserPath.trim().replace(/^"(.*)"$/, "$1") || null } })}>保存</button>
        </div>
        <div className="field-hint">専用の窓のログイン情報などは、代表フォルダの data/browser-profile に保存されます</div>
      </Section>
    </>
  );
}
