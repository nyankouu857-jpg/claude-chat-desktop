import { LoaderCircle } from "lucide-react";
import { useEffect } from "react";
import { SINGLE_CHAT, navigate, toggleSidebar } from "../actions";
import { setState, useStore } from "../store";
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

function useTheme() {
  const theme = useStore((s) => s.settings?.appearance.theme ?? "system");
  const font = useStore((s) => s.settings?.appearance.chatFont ?? "default");
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
}

// claude.ai のキーボードショートカットにならう
function useShortcuts() {
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (!(e.ctrlKey || e.metaKey)) return;
      const key = e.key.toLowerCase();
      if (e.shiftKey && key === "o") {
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
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
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
    return <div className="boot"><LoaderCircle className="spin" /></div>;
  }

  return (
    <div className="app">
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
      <SettingsOverlay />
      <GlobalDialog />
      <div className="toasts">
        {toasts.map((t) => <div key={t.id} className={`toast ${t.kind}`}>{t.text}</div>)}
      </div>
    </div>
  );
}
