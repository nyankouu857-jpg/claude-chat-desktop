import DOMPurify from "dompurify";
import hljs from "highlight.js/lib/common";
import { Marked } from "marked";
import markedKatex from "marked-katex-extension";

const escapeHtml = (s: string) => s.replace(/[&<>"']/g, (c) => `&#${c.charCodeAt(0)};`);

// コピーボタンが写すのは、Markdown のコードブロックの元の文字だけ。本文に書かれた偽のコードブロックの、隠した文字を写させない
let codes: string[] = [];
let nonce = "";

// チャットの改行をそのまま改行として見せたいので breaks を有効に
const marked = new Marked({ gfm: true, breaks: true });
marked.use(markedKatex({ throwOnError: false, nonStandard: true }));
marked.use({
  renderer: {
    code({ text, lang }) {
      const language = (lang ?? "").split(/\s/)[0];
      const highlighted = language && hljs.getLanguage(language)
        ? hljs.highlight(text, { language }).value
        : escapeHtml(text);
      const index = codes.push(text) - 1;
      return `<div class="code-block" data-code="${nonce}-${index}"><div class="code-header"><span>${escapeHtml(language || "text")}</span><button type="button" class="code-copy" data-copy>コピー</button></div><pre><code class="hljs">${highlighted}</code></pre></div>`;
    },
  },
});

// 外部の画像は表示した瞬間に URL へアクセスが飛ぶ。注入された指示で会話の中身を URL に埋めて送られないよう、リンクに置き換える
DOMPurify.addHook("afterSanitizeAttributes", (node) => {
  // SVG の image や画像ボタンも読み込んだ瞬間に外へ飛ぶ
  if (node.tagName.toLowerCase() === "image" || (node.tagName === "INPUT" && node.getAttribute("type") === "image")) {
    node.remove();
    return;
  }
  if (node.tagName !== "IMG") return;
  const src = node.getAttribute("src") ?? "";
  if (/^(data|blob):/i.test(src)) return;
  const link = document.createElement("a");
  link.href = src;
  link.textContent = `[画像: ${node.getAttribute("alt") || src}]`;
  node.replaceWith(link);
});

export interface RenderedMarkdown {
  html: string;
  codes: string[];
  nonce: string;
}

export function renderMarkdown(text: string): RenderedMarkdown {
  codes = [];
  nonce = crypto.randomUUID();
  // 回答の中のフォームから別のページへ送られないように。style はアプリ全体の見た目を変えて、承認のボタンを偽装できてしまう
  const html = DOMPurify.sanitize(marked.parse(text, { async: false }) as string, {
    ADD_ATTR: ["data-copy", "target"],
    FORBID_TAGS: ["form", "iframe", "object", "embed", "video", "audio", "source", "picture", "style", "area", "map", "link", "meta", "base"],
  });
  return { html, codes, nonce };
}
