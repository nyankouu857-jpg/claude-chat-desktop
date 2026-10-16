import { useEffect, useState, type ReactNode } from "react";
import { SINGLE_CHAT, toast } from "../actions";
import { on } from "../bridge";
import { useStore } from "../store";

// WebView2 の中のときだけ、Windows の題名の帯の代わりを出す。開発用のブラウザでは出さない
export const FRAMELESS = !!(window as any).chrome?.webview;

// 押された窓だけで済む操作なので、AppHost を通さずに窓へ直接送る
const windowCommand = (action: "minimize" | "maximize" | "close" | "drag" | "fullscreen") =>
  (window as any).chrome?.webview?.postMessage(JSON.stringify({ window: action }));

// 字形は Segoe のフォント頼みだと入っていない環境で消えるので、Windows の ─ ☐ ✕ を線で描く。
// 10px の箱に 1px の線。0.5 ずらしは線を画素の境目に合わせてにじませないため
const Glyph = ({ children }: { children: ReactNode }) => (
  <svg width="10" height="10" viewBox="0 0 10 10" fill="none" stroke="currentColor" strokeWidth="1" aria-hidden="true">{children}</svg>
);
const MinimizeGlyph = () => <Glyph><path d="M0 5.5h10" /></Glyph>;
const MaximizeGlyph = () => <Glyph><rect x="0.5" y="0.5" width="9" height="9" rx="1" /></Glyph>;
const RestoreGlyph = () => <Glyph><rect x="0.5" y="2.5" width="7" height="7" rx="1" /><path d="M2.5 0.5h5.5a1.5 1.5 0 0 1 1.5 1.5v5.5" /></Glyph>;
const CloseGlyph = () => <Glyph><path d="M0.5 0.5l9 9M9.5 0.5l-9 9" /></Glyph>;

export function TitleBar() {
  const sidebarOpen = useStore((s) => s.sidebarOpen);
  const [state, setState] = useState({ maximized: false, fullscreen: false });

  useEffect(() => {
    const off = on("window.state", (next: { maximized: boolean; fullscreen: boolean }) => setState(next));
    return () => { off(); };
  }, []);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== "F11") return;
      e.preventDefault();
      windowCommand("fullscreen");
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, []);
  useEffect(() => {
    if (state.fullscreen) toast("全画面にしました。F11 で戻ります");
  }, [state.fullscreen]);

  // 全画面では帯ごと隠して、画面いっぱいをチャットに使う
  if (state.fullscreen) return null;
  return (
    <div
      className="titlebar"
      // CSS の app-region が効かない環境の予備。つかんだら Windows に窓を動かしてもらう
      onMouseDown={(e) => { if (e.button === 0 && e.detail === 1) windowCommand("drag"); }}
      onDoubleClick={() => windowCommand("maximize")}
    >
      {!SINGLE_CHAT && <div className={`titlebar-side${sidebarOpen ? "" : " collapsed"}`} />}
      <div className="titlebar-main" />
      <div className="window-controls" onMouseDown={(e) => e.stopPropagation()} onDoubleClick={(e) => e.stopPropagation()}>
        <button type="button" aria-label="最小化" onClick={() => windowCommand("minimize")}><MinimizeGlyph /></button>
        <button type="button" aria-label={state.maximized ? "元のサイズに戻す" : "最大化"} onClick={() => windowCommand("maximize")}>
          {state.maximized ? <RestoreGlyph /> : <MaximizeGlyph />}
        </button>
        <button type="button" className="close" aria-label="閉じる" onClick={() => windowCommand("close")}><CloseGlyph /></button>
      </div>
    </div>
  );
}
