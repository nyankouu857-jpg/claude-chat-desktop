import { ChevronDown, ChevronUp, X } from "lucide-react";
import { useEffect, useRef, useState, type RefObject } from "react";

// 流れてくる応答で何度も探し直さないよう、変化が落ち着くのを待つ
const RESEARCH_DELAY_MS = 200;

// CSS の Highlight API。DOM を書き換えずに色を付けられるので、React の描画とぶつからない
type HighlightRegistry = { set(name: string, highlight: unknown): void; delete(name: string): void };
const highlights = (globalThis as { CSS?: { highlights?: HighlightRegistry } }).CSS?.highlights;
const HighlightClass = (globalThis as { Highlight?: new (...ranges: Range[]) => unknown }).Highlight;

function findRanges(root: HTMLElement, query: string): Range[] {
  const needle = query.toLowerCase();
  const ranges: Range[] = [];
  const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
  for (let node = walker.nextNode(); node; node = walker.nextNode()) {
    const text = node.textContent?.toLowerCase() ?? "";
    for (let at = text.indexOf(needle); at >= 0; at = text.indexOf(needle, at + needle.length)) {
      const range = document.createRange();
      range.setStart(node, at);
      range.setEnd(node, at + needle.length);
      ranges.push(range);
    }
  }
  return ranges;
}

function paint(ranges: Range[], current: number) {
  if (!highlights || !HighlightClass) return;
  highlights.set("chat-search", new HighlightClass(...ranges));
  if (ranges[current]) highlights.set("chat-search-current", new HighlightClass(ranges[current]));
  else highlights.delete("chat-search-current");
}

function clear() {
  highlights?.delete("chat-search");
  highlights?.delete("chat-search-current");
}

// 開いている会話の中を探す。Ctrl+F で開き、Enter で次、Shift+Enter で前、Esc で閉じる
export function ChatSearch({ scroller }: { scroller: RefObject<HTMLDivElement | null> }) {
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState("");
  const [ranges, setRanges] = useState<Range[]>([]);
  const [current, setCurrentState] = useState(0);
  // 探し直すときに今の位置を読むため。state だけだと監視の中から最新の値が見えない
  const currentRef = useRef(0);
  const setCurrent = (value: number) => {
    currentRef.current = value;
    setCurrentState(value);
  };
  const input = useRef<HTMLInputElement>(null);

  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && !e.shiftKey && e.key.toLowerCase() === "f") {
        e.preventDefault();
        window.dispatchEvent(new Event("chat-show-all"));
        setOpen(true);
        setTimeout(() => input.current?.select());
      }
    };
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("keydown", onKey);
      clear();
    };
  }, []);

  useEffect(() => {
    const root = scroller.current;
    if (!open || !query.trim() || !root) {
      setRanges([]);
      clear();
      return;
    }
    const found = findRanges(root, query.trim());
    setRanges(found);
    setCurrent(0);
    paint(found, 0);
    found[0]?.startContainer.parentElement?.scrollIntoView({ block: "center" });
    // 応答が流れてきたり描き直されたりすると前の範囲は無効になるので、少し間を置いて探し直す。位置は動かさない
    let timer = 0;
    const observer = new MutationObserver(() => {
      clearTimeout(timer);
      timer = window.setTimeout(() => {
        const again = findRanges(root, query.trim());
        const kept = Math.min(currentRef.current, Math.max(0, again.length - 1));
        setRanges(again);
        setCurrent(kept);
        paint(again, kept);
      }, RESEARCH_DELAY_MS);
    });
    observer.observe(root, { childList: true, subtree: true, characterData: true });
    return () => {
      observer.disconnect();
      clearTimeout(timer);
    };
  }, [open, query, scroller]);

  const move = (step: number) => {
    if (ranges.length === 0) return;
    const next = (current + step + ranges.length) % ranges.length;
    setCurrent(next);
    paint(ranges, next);
    ranges[next].startContainer.parentElement?.scrollIntoView({ block: "center", behavior: "smooth" });
  };

  const close = () => {
    setOpen(false);
    setQuery("");
    clear();
  };

  if (!open) return null;
  return (
    <div className="chat-search">
      <input
        ref={input}
        className="input"
        placeholder="この会話の中を探す"
        value={query}
        autoFocus
        onChange={(e) => setQuery(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Enter") {
            e.preventDefault();
            move(e.shiftKey ? -1 : 1);
          } else if (e.key === "Escape") {
            close();
          }
        }}
      />
      <span className="chat-search-count">{query.trim() ? (ranges.length ? `${current + 1}/${ranges.length}` : "0 件") : ""}</span>
      <button type="button" className="icon-button small" aria-label="前" onClick={() => move(-1)}><ChevronUp size={15} /></button>
      <button type="button" className="icon-button small" aria-label="次" onClick={() => move(1)}><ChevronDown size={15} /></button>
      <button type="button" className="icon-button small" aria-label="閉じる" onClick={close}><X size={15} /></button>
    </div>
  );
}
