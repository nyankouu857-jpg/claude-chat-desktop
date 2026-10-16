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
  SettingsTab,
  UpdateInfo,
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
  // 回答のスタイル。claude.ai と同じく入力欄から会話ごとに選ぶ
  style: string;
  // 新しいチャットの権限モード。既存のチャットは会話の側に持つ
  permissionMode: PermissionMode;
  // 使わないコネクタ。空ならすべて使う
  disabledSources: string[];
  // 高速モード。対応しているモデルのときだけ送る
  fast: boolean;
}

export type DialogState =
  | { kind: "btw"; conversationId: string; question: string; answer?: string; error?: string }
  | { kind: "rewind"; conversationId: string; messageIndex?: number }
  | { kind: "tasks"; conversationId: string }
  | { kind: "help" }
  | { kind: "folder"; folderId?: string }
  | { kind: "shortcuts" }
  | { kind: "style"; styleId?: string }
  | { kind: "confirm"; title: string; message: string; confirmLabel: string };

export interface AppState {
  ready: boolean;
  settings: Settings | null;
  secrets: SecretsView | null;
  paths: { root: string; isFallback: boolean } | null;
  version: string;
  commit: string | null;
  // 新しい版。installing は入れ替えて再起動するまでの間
  update: (UpdateInfo & { installing?: boolean }) | null;
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
  // 回答の一部を選んで「返信」したときの引用
  quotes: Record<string, string>;
  artifact: { conversationId: string; id: string } | null;
  artifactFull: boolean;
  // 設定はモーダルで開き、広げると窓いっぱいになる
  settingsOverlay: { tab: SettingsTab; full: boolean } | null;
  // Claude Code の右側の「環境・コンテキスト・コネクタ・成果物」
  infoPanelOpen: boolean;
  dialog: DialogState | null;
  view: View;
  sidebarOpen: boolean;
  choice: ComposerChoice;
  toasts: Toast[];
}

function readInfoPanel() {
  try {
    return localStorage.getItem("infoPanelOpen") !== "false";
  } catch {
    return true;
  }
}

let state: AppState = {
  ready: false,
  settings: null,
  secrets: null,
  paths: null,
  version: "",
  commit: null,
  update: null,
  summaries: [],
  conversations: {},
  live: {},
  errors: {},
  mcp: [],
  folders: [],
  statuses: {},
  workflows: {},
  extensions: { commands: [], outputStyles: [], skills: [] },
  prefill: {},
  quotes: {},
  artifact: null,
  artifactFull: false,
  settingsOverlay: null,
  infoPanelOpen: readInfoPanel(),
  dialog: null,
  view: { name: "new" },
  sidebarOpen: true,
  choice: { model: "claude-opus-5-5", effort: "medium", webSearch: false, webFetch: true, codeExecution: false, research: false, style: "default", permissionMode: "default", disabledSources: [], fast: false },
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
