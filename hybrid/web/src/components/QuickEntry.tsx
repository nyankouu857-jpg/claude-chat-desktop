import { ArrowUp, LoaderCircle } from "lucide-react";
import { useEffect, useRef, useState } from "react";
import { sendQuick } from "../actions";
import { on } from "../bridge";
import { ALL_MODELS } from "../models";
import { useStore } from "../store";
import { useTheme } from "./App";
import { Spark } from "./ui";

const DRAFT_KEY = "draft:quick";

function readDraft() {
  try {
    return localStorage.getItem(DRAFT_KEY) ?? "";
  } catch {
    return "";
  }
}

const close = () => (window as any).chrome?.webview?.postMessage(JSON.stringify({ window: "close" }));

// どのアプリからでも呼べる入力の小窓。送ると新しいチャットになり、メインの窓で続きを見る
export function QuickEntry() {
  const ready = useStore((s) => s.ready);
  const model = useStore((s) => ALL_MODELS.find((m) => m.id === s.choice.model)?.label ?? s.choice.model);
  const toasts = useStore((s) => s.toasts);
  const [text, setText] = useState(readDraft);
  const [sending, setSending] = useState(false);
  const textarea = useRef<HTMLTextAreaElement>(null);
  useTheme();

  useEffect(() => {
    const focus = () => textarea.current?.focus();
    focus();
    const off = on("quick.shown", focus);
    return () => { off(); };
  }, []);
  useEffect(() => {
    try {
      if (text) localStorage.setItem(DRAFT_KEY, text);
      else localStorage.removeItem(DRAFT_KEY);
    } catch {
      // 残せなくても送るのは困らない
    }
  }, [text]);

  async function submit() {
    const trimmed = text.trim();
    if (!trimmed || sending || !ready) return;
    setSending(true);
    const sent = await sendQuick(trimmed);
    setSending(false);
    if (!sent) return;
    setText("");
    close();
  }

  return (
    <div className="quick-entry">
      <div className="quick-box">
        <span className="quick-spark"><Spark size={22} /></span>
        <textarea
          ref={textarea}
          className="quick-input"
          rows={2}
          value={text}
          placeholder="Claude に聞く…"
          onChange={(e) => setText(e.target.value)}
          onKeyDown={(e) => {
            if (e.key === "Escape") {
              e.preventDefault();
              close();
            } else if (e.key === "Enter" && !e.shiftKey && !e.nativeEvent.isComposing) {
              e.preventDefault();
              void submit();
            }
          }}
        />
        <button type="button" className="send-button" aria-label="送信" disabled={!text.trim() || sending || !ready} onClick={() => void submit()}>
          {sending ? <LoaderCircle size={16} className="spin" /> : <ArrowUp size={16} />}
        </button>
      </div>
      <div className="quick-hint">
        <span>{model}</span>
        <span>Enter で送信 ・ Shift + Enter で改行 ・ Esc で閉じる</span>
      </div>
      {toasts.length > 0 && <div className="quick-toast">{toasts[toasts.length - 1].text}</div>}
    </div>
  );
}
