import { humanText } from "./actions";
import { ARTIFACT_ID, parsePeer, resultText } from "./components/Messages";
import { renderMarkdown } from "./markdown";
import type { Artifact, Conversation, WireBlock } from "./types";

const escapeHtml = (s: string) => s.replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`);

// 書き出したファイルを開いた人の PC で、会話の中身が外へ送られないようにする。画像は埋め込んだものだけ
const PAGE_CSP = "default-src 'none'; style-src 'unsafe-inline'; img-src data:; font-src data:; frame-src 'self' about: data:";
// アーティファクトは画面と同じく、外とは通信させずに動かす
const ARTIFACT_CSP = "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src data: blob:; font-src data:; connect-src 'none'";

// 画面と同じ見た目にするため、読み込んでいるスタイルをそのまま写す
function appStyles() {
  const rules: string[] = [];
  for (const sheet of [...document.styleSheets]) {
    try {
      for (const rule of [...sheet.cssRules]) rules.push(rule.cssText);
    } catch {
      // 読めない外部のスタイルは無くても形は崩れない
    }
  }
  return rules.join("\n");
}

function artifactHtml(artifact: Artifact) {
  const head = `<div class="export-artifact-title">${escapeHtml(artifact.title)}</div>`;
  if (artifact.type === "html" || artifact.type === "svg") {
    const page = `<meta http-equiv="Content-Security-Policy" content="${ARTIFACT_CSP}">${artifact.content}`;
    return `<div class="export-artifact">${head}<iframe sandbox="allow-scripts" srcdoc="${escapeHtml(page)}"></iframe></div>`;
  }
  if (artifact.type === "markdown") return `<div class="export-artifact">${head}<div class="markdown">${renderMarkdown(artifact.content).html}</div></div>`;
  const language = artifact.type === "code" ? artifact.language ?? "" : artifact.type === "react" ? "jsx" : artifact.type;
  return `<div class="export-artifact">${head}<div class="markdown">${renderMarkdown(`\`\`\`${language}\n${artifact.content}\n\`\`\``).html}</div></div>`;
}

function humanHtml(content: Conversation["messages"][number]["content"]) {
  const blocks: WireBlock[] = typeof content === "string" ? [{ type: "text", text: content }] : content;
  const files = blocks.map((b) => {
    if (b.type === "image" && b.source?.type === "base64") return `<img src="data:${escapeHtml(b.source.media_type)};base64,${escapeHtml(b.source.data)}" alt="">`;
    if (b.type === "document") return `<div class="export-file">添付: ${escapeHtml(b.title ?? "ファイル")}</div>`;
    return "";
  }).join("");
  const text = humanText(content).trim();
  return `<div class="user-turn"><div class="user-bubble">${files}${text ? `<div class="user-text">${escapeHtml(text)}</div>` : ""}</div></div>`;
}

export function conversationHtml(conversation: Conversation, theme: "light" | "dark") {
  const artifacts = new Map((conversation.artifacts ?? []).map((a) => [a.id, a]));
  const shown = new Set<string>();
  // 作ったアーティファクトの ID は、ツールの結果の文に入っている
  const results = new Map<string, WireBlock>();
  for (const message of conversation.messages) {
    if (Array.isArray(message.content)) for (const b of message.content) if (b.type === "tool_result") results.set(b.tool_use_id, b);
  }
  const parts: string[] = [];
  let reply: string[] = [];
  const flush = () => {
    if (reply.length) parts.push(`<div class="assistant-turn">${reply.join("")}</div>`);
    reply = [];
  };
  for (const message of conversation.messages) {
    const blocks: WireBlock[] = typeof message.content === "string" ? [{ type: "text", text: message.content }] : message.content;
    if (message.role === "user") {
      // ツールの結果と、ほかのチャットや予定から届いた知らせは、人の発言として出さない
      if (blocks.every((b) => b.type === "tool_result") || parsePeer(humanText(message.content))) continue;
      flush();
      parts.push(humanHtml(message.content));
      continue;
    }
    for (const block of blocks) {
      if (block.type === "text" && block.text?.trim()) reply.push(`<div class="markdown">${renderMarkdown(block.text).html}</div>`);
      if (block.type !== "tool_use") continue;
      const id = ARTIFACT_ID.exec(resultText(results.get(block.id)))?.[1] ?? block.input?.id;
      const artifact = typeof id === "string" ? artifacts.get(id) : undefined;
      if ((block.name === "artifacts__create" || block.name === "artifacts__update") && artifact) {
        // 何度も直したアーティファクトは、最後の版を最初に出てきた所に 1 回だけ置く
        if (!shown.has(artifact.id)) reply.push(artifactHtml(artifact));
        shown.add(artifact.id);
      } else {
        reply.push(`<div class="export-tool">ツール: ${escapeHtml(block.name ?? "")}</div>`);
      }
    }
  }
  flush();
  const title = escapeHtml(conversation.title || "チャット");
  const exportStyles = `
body { background: var(--bg-100); margin: 0; }
.export-page { max-width: 820px; margin: 0 auto; padding: 32px 24px 60px; }
.export-page h1.export-title { font-family: var(--font-serif); font-weight: 500; font-size: 26px; color: var(--text-100); margin: 0 0 28px; }
.export-page .user-turn { display: flex; justify-content: flex-end; margin-bottom: 22px; }
.export-file, .export-tool { font-size: 12.5px; color: var(--text-300); margin: 4px 0 8px; }
.export-artifact { border: 0.5px solid var(--border); border-radius: 12px; overflow: hidden; margin: 10px 0 16px; background: var(--bg-000); }
.export-artifact-title { padding: 8px 14px; font-size: 13px; color: var(--text-200); border-bottom: 0.5px solid var(--border); }
.export-artifact iframe { display: block; width: 100%; height: 520px; border: 0; background: #fff; }
.export-artifact .markdown { padding: 4px 14px; }
@media print { .export-artifact iframe { height: 420px; } .assistant-turn, .user-turn { break-inside: avoid-page; } }`;
  return `<!doctype html>
<html lang="ja" data-theme="${theme}">
<head>
<meta charset="utf-8">
<meta http-equiv="Content-Security-Policy" content="${PAGE_CSP}">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${title}</title>
<style>${appStyles()}
${exportStyles}</style>
</head>
<body>
<div class="export-page">
<h1 class="export-title">${title}</h1>
${parts.join("\n")}
</div>
</body>
</html>`;
}
