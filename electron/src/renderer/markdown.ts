import DOMPurify from "dompurify";
import hljs from "highlight.js/lib/common";
import { Marked } from "marked";

// チャットの改行をそのまま改行として見せたいので breaks を有効に
const marked = new Marked({ gfm: true, breaks: true });

export function renderMarkdown(target: HTMLElement, text: string) {
  target.innerHTML = DOMPurify.sanitize(marked.parse(text, { async: false }));
  for (const code of target.querySelectorAll<HTMLElement>("pre code")) {
    hljs.highlightElement(code);
    const copyButton = document.createElement("button");
    copyButton.type = "button";
    copyButton.className = "copy-code";
    copyButton.textContent = "コピー";
    copyButton.addEventListener("click", async () => {
      await navigator.clipboard.writeText(code.innerText);
      copyButton.textContent = "コピーしました";
      setTimeout(() => (copyButton.textContent = "コピー"), 1500);
    });
    code.parentElement?.append(copyButton);
  }
}
