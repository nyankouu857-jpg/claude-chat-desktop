import { LoaderCircle } from "lucide-react";
import { useEffect } from "react";
import { SINGLE_CHAT, navigate, toast, toggleSidebar, updateSettings } from "../actions";
import { getState, setState, useStore } from "../store";
import { ArtifactPanel } from "./ArtifactPanel";
import { ArtifactsPage } from "./Artifacts";
import { ChatView } from "./ChatView";
import { CompareView } from "./Compare";
import { GlobalDialog } from "./Dialogs";
import { NewChat } from "./NewChat";
import { ProjectPage, ProjectsPage } from "./Projects";
import { Recents } from "./Recents";
import { InfoPanel } from "./InfoPanel";
import { SettingsOverlay } from "./settings/Settings";
import { Sidebar } from "./Sidebar";
import { FRAMELESS, TitleBar } from "./TitleBar";
import { ZOOM_STEPS } from "./ui";

export function useTheme({ zoomable = true } = {}) {
  const theme = useStore((s) => s.settings?.appearance.theme ?? "system");
  const font = useStore((s) => s.settings?.appearance.chatFont ?? "default");
  const zoom = useStore((s) => s.settings?.appearance.zoom ?? 100);
  const chatWidth = useStore((s) => s.settings?.appearance.chatWidth ?? "standard");
  useEffect(() => {
    const media = window.matchMedia("(prefers-color-scheme: dark)");
    const apply = () => {
      const dark = theme === "dark" || (theme === "system" && media.matches);
      document.documentElement.dataset.theme = dark ? "dark" : "light";
    };
    apply();
    media.addEventListener("change", apply);
    return () => media.removeEventListener("change", apply);
  }, [theme]);
  useEffect(() => {
    document.documentElement.dataset.chatFont = font;
  }, [font]);
  useEffect(() => {
    document.documentElement.dataset.chatWidth = chatWidth;
    if (!zoomable) return;
    const factor = Math.min(2, Math.max(0.7, zoom / 100));
    // アプリの中では WebView の倍率で拡大する。開発用のブラウザでは代わりに CSS で
    const webview = (window as any).chrome?.webview;
    if (webview) webview.postMessage(JSON.stringify({ window: `zoom:${factor}` }));
    else document.documentElement.style.zoom = String(factor);
  }, [zoom, chatWidth, zoomable]);
}

// WebView2 の拡大は窓ごとに覚えないので、画面の側で持って設定に残す
function stepZoom(direction: -1 | 0 | 1) {
  const current = getState().settings?.appearance.zoom ?? 100;
  const next = direction === 0 ? 100 : direction > 0 ? ZOOM_STEPS.find((z) => z > current) : [...ZOOM_STEPS].reverse().find((z) => z < current);
  if (!next || next === current) return;
  void updateSettings({ appearance: { zoom: next } });
  toast(`表示 ${next}%`);
}

// 最後にホイールが来た時刻。間が空くまでは同じひねりとして扱う
let lastWheel = 0;

// claude.ai のキーボードショートカットにならう
function useShortcuts() {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey)) return;
      const key = e.key.toLowerCase();
      // 日本語配列の + は Shift + ; なので、; も拡大として受ける
      if (key === "+" || key === "=" || key === ";") {
        e.preventDefault();
        stepZoom(1);
      } else if (key === "-") {
        e.preventDefault();
        stepZoom(-1);
      } else if (key === "0") {
        e.preventDefault();
        stepZoom(0);
      } else if (e.shiftKey && key === "o") {
        e.preventDefault();
        navigate({ name: "new" });
      } else if (!e.shiftKey && key === "k") {
        e.preventDefault();
        navigate({ name: "recents" });
      } else if (e.shiftKey && key === "s") {
        e.preventDefault();
        toggleSidebar();
      } else if (key === "/") {
        e.preventDefault();
        setState((s) => ({ ...s, dialog: { kind: "shortcuts" } }));
      }
    };
    const onWheel = (e: WheelEvent) => {
      if (!e.ctrlKey) return;
      e.preventDefault();
      // タッチパッドは 1 回のひねりで何十回も送ってくるので、300ms 途切れるまでは 1 段だけ
      const continuing = e.timeStamp - lastWheel < 300;
      lastWheel = e.timeStamp;
      if (continuing || e.deltaY === 0) return;
      stepZoom(e.deltaY < 0 ? 1 : -1);
    };
    document.addEventListener("keydown", onKey);
    document.addEventListener("wheel", onWheel, { passive: false });
    return () => {
      document.removeEventListener("keydown", onKey);
      document.removeEventListener("wheel", onWheel);
    };
  }, []);
}

export function App() {
  const ready = useStore((s) => s.ready);
  const view = useStore((s) => s.view);
  const toasts = useStore((s) => s.toasts);
  // 開いているチャットのアーティファクトだけ横に出す
  const showArtifact = useStore((s) => !!s.artifact && s.view.name === "chat" && s.view.id === s.artifact.conversationId);
  // 横の欄は 1 つだけ。アーティファクトを開いている間は情報の欄を隠す
  const showInfo = useStore((s) => s.infoPanelOpen && s.view.name === "chat") && !showArtifact;
  useTheme();
  useShortcuts();

  if (!ready) {
    // 枠の無い窓なので、起動に手間取っても動かしたり閉じたりできるよう帯は先に出す
    return (
      <div className={`app${FRAMELESS ? " frameless" : ""}`}>
        {FRAMELESS && <TitleBar />}
        <div className="boot"><LoaderCircle className="spin" /></div>
      </div>
    );
  }

  return (
    <div className={`app${FRAMELESS ? " frameless" : ""}`}>
      {FRAMELESS && <TitleBar />}
      <div className="app-body">
      {!SINGLE_CHAT && <Sidebar />}
      <main className="main">
        {view.name === "new" && <NewChat />}
        {view.name === "chat" && <ChatView key={view.id} id={view.id} />}
        {view.name === "compare" && <CompareView ids={view.ids} />}
        {view.name === "recents" && <Recents />}
        {view.name === "projects" && <ProjectsPage />}
        {view.name === "artifacts" && <ArtifactsPage />}
        {view.name === "project" && <ProjectPage key={view.id} id={view.id} />}
      </main>
      {showArtifact && <ArtifactPanel />}
      {showInfo && view.name === "chat" && <InfoPanel conversationId={view.id} />}
      </div>
      <SettingsOverlay />
      <GlobalDialog />
      <div className="toasts">
        {toasts.map((t) => <div key={t.id} className={`toast ${t.kind}`}>{t.text}</div>)}
      </div>
    </div>
  );
}
