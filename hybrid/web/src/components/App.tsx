import { LoaderCircle } from "lucide-react";
import { useEffect } from "react";
import { useStore } from "../store";
import { ArtifactPanel } from "./ArtifactPanel";
import { ChatView } from "./ChatView";
import { GlobalDialog } from "./Dialogs";
import { NewChat } from "./NewChat";
import { Recents } from "./Recents";
import { SettingsPage } from "./settings/Settings";
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

export function App() {
  const ready = useStore((s) => s.ready);
  const view = useStore((s) => s.view);
  const toasts = useStore((s) => s.toasts);
  // 開いているチャットのアーティファクトだけ横に出す
  const showArtifact = useStore((s) => !!s.artifact && s.view.name === "chat" && s.view.id === s.artifact.conversationId);
  useTheme();

  if (!ready) {
    return <div className="boot"><LoaderCircle className="spin" /></div>;
  }

  return (
    <div className="app">
      <Sidebar />
      <main className="main">
        {view.name === "new" && <NewChat />}
        {view.name === "chat" && <ChatView key={view.id} id={view.id} />}
        {view.name === "recents" && <Recents />}
        {view.name === "settings" && <SettingsPage tab={view.tab} />}
      </main>
      {showArtifact && <ArtifactPanel />}
      <GlobalDialog />
      <div className="toasts">
        {toasts.map((t) => <div key={t.id} className={`toast ${t.kind}`}>{t.text}</div>)}
      </div>
    </div>
  );
}
