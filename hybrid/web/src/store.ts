import { useSyncExternalStore } from "react";
import type {
  Conversation,
  ConversationSummary,
  Effort,
  LiveReply,
  McpServerStatus,
  SecretsView,
  Settings,
  View,
} from "./types";

export interface Toast {
  id: number;
  text: string;
  kind: "info" | "error";
}

export interface ComposerChoice {
  model: string;
  effort: Effort;
  webSearch: boolean;
  // 使わないコネクタ。空ならすべて使う
  disabledSources: string[];
}

export interface AppState {
  ready: boolean;
  settings: Settings | null;
  secrets: SecretsView | null;
  paths: { root: string; isFallback: boolean } | null;
  version: string;
  summaries: ConversationSummary[];
  conversations: Record<string, Conversation>;
  live: Record<string, LiveReply>;
  errors: Record<string, string>;
  mcp: McpServerStatus[];
  view: View;
  sidebarOpen: boolean;
  choice: ComposerChoice;
  toasts: Toast[];
}

let state: AppState = {
  ready: false,
  settings: null,
  secrets: null,
  paths: null,
  version: "",
  summaries: [],
  conversations: {},
  live: {},
  errors: {},
  mcp: [],
  view: { name: "new" },
  sidebarOpen: true,
  choice: { model: "claude-opus-5-5", effort: "medium", webSearch: false, disabledSources: [] },
  toasts: [],
};

const listeners = new Set<() => void>();
let frame = 0;

function notify() {
  for (const listener of listeners) listener();
}

export function getState() {
  return state;
}

// ストリーミング中は1トークンごとに再描画すると重いので、deferred のときはフレーム単位にまとめる
export function setState(update: (s: AppState) => AppState, deferred = false) {
  state = update(state);
  if (!deferred) {
    if (frame) cancelAnimationFrame(frame);
    frame = 0;
    notify();
  } else if (!frame) {
    frame = requestAnimationFrame(() => {
      frame = 0;
      notify();
    });
  }
}

export function useStore<T>(selector: (s: AppState) => T): T {
  return useSyncExternalStore(
    (listener) => {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    () => selector(state),
  );
}
