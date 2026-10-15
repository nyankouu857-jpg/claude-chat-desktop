import { Copy, Download, X } from "lucide-react";
import { closeArtifact, exportArtifact, toast } from "../actions";
import { useStore } from "../store";
import { Markdown } from "./Messages";

// html / svg / mermaid は別ページとして iframe に出す。sandbox で別オリジンにして、アプリの画面や中身には触らせない
const FRAMED = ["html", "svg", "mermaid"];

// 中身に ``` があってもコードブロックが途中で閉じないよう、それより長い囲みにする
function fenced(content: string, language: string) {
  const longest = Math.max(0, ...[...content.matchAll(/`+/g)].map((m) => m[0].length));
  const fence = "`".repeat(Math.max(3, longest + 1));
  return `${fence}${language}\n${content}\n${fence}`;
}

export function ArtifactPanel() {
  const target = useStore((s) => s.artifact);
  const artifact = useStore((s) => (target ? s.conversations[target.conversationId]?.artifacts?.find((a) => a.id === target.id) : undefined));
  if (!target || !artifact) return null;
  const framed = FRAMED.includes(artifact.type);
  return (
    <aside className="artifact-panel">
      <header className="artifact-header">
        <span className="artifact-title">{artifact.title}</span>
        <span className="field-hint">版 {artifact.version}</span>
        <span className="spacer" />
        <button type="button" className="icon-button small" title="コピー" onClick={async () => { await navigator.clipboard.writeText(artifact.content); toast("コピーしました"); }}><Copy size={15} /></button>
        <button type="button" className="icon-button small" title="ファイルに保存" onClick={() => void exportArtifact(target.conversationId, artifact.id)}><Download size={15} /></button>
        <button type="button" className="icon-button small" title="閉じる" onClick={closeArtifact}><X size={16} /></button>
      </header>
      <div className="artifact-body">
        {framed ? (
          <iframe
            key={`${artifact.id}-${artifact.version}`}
            title={artifact.title}
            sandbox="allow-scripts allow-popups allow-modals"
            src={`/artifact/${target.conversationId}/${artifact.id}?v=${artifact.version}`}
          />
        ) : artifact.type === "markdown" ? (
          <div className="artifact-doc"><Markdown text={artifact.content} /></div>
        ) : (
          <div className="artifact-doc"><Markdown text={fenced(artifact.content, artifact.language ?? "")} /></div>
        )}
      </div>
    </aside>
  );
}
