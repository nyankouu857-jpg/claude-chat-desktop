import { useSyncExternalStore } from "react";
import type {
  ChatStatus,
  Conversation,
  ConversationSummary,
  Effort,
  Extensions,
  Folder,
  LiveReply,
  McpServerStatus,
  PermissionMode,
  SecretsView,
  Settings,
  View,
  WorkflowRun,
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
  webFetch: boolean;
  codeExecution: boolean;
  research: boolean;
  // 新しいチャットの権限モード。既存のチャットは会話の側に持つ
  permissionMode: PermissionMode;
  // 使わないコネクタ。空ならすべて使う
  disabledSources: string[];
}

export type DialogState =
  | { kind: "btw"; conversationId: string; question: string; answer?: string; error?: string }
  | { kind: "rewind"; conversationId: string; messageIndex?: number }
  | { kind: "tasks"; conversationId: string }
  | { kind: "help" }
  | { kind: "folder"; folderId?: string };

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
  folders: Folder[];
  statuses: Record<string, ChatStatus>;
  workflows: Record<string, WorkflowRun>;
  extensions: Extensions;
  // 巻き戻しで入力欄に戻す文
  prefill: Record<string, { text: string; nonce: number }>;
  artifact: { conversationId: string; id: string } | null;
  dialog: DialogState | null;
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
  folders: [],
  statuses: {},
  workflows: {},
  extensions: { commands: [], outputStyles: [] },
  prefill: {},
  artifact: null,
  dialog: null,
  view: { name: "new" },
  sidebarOpen: true,
  choice: { model: "claude-opus-5-5", effort: "medium", webSearch: false, webFetch: true, codeExecution: false, research: false, permissionMode: "default", disabledSources: [] },
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
