export type Role = "user" | "assistant";

export interface WireBlock {
  type: string;
  [key: string]: any;
}

export interface WireMessage {
  role: Role;
  content: string | WireBlock[];
}

export interface ConversationUsage {
  input: number;
  output: number;
  cacheRead: number;
  cacheWrite: number;
  costUsd: number;
}

export interface Conversation {
  id: string;
  title: string;
  starred: boolean;
  model?: string;
  createdAt: number;
  updatedAt: number;
  messages: WireMessage[];
  usage: ConversationUsage;
}

export interface ConversationSummary {
  id: string;
  title: string;
  starred: boolean;
  updatedAt: number;
  preview: string;
}

export type Effort = "low" | "medium" | "high" | "xhigh" | "max";

export interface McpServerConfig {
  type?: string;
  command?: string;
  args?: string[];
  env?: Record<string, string>;
  cwd?: string;
  url?: string;
  headers?: Record<string, string>;
  disabled?: boolean;
}

export interface Settings {
  profile: { fullName: string; nickname: string; preferences: string };
  appearance: { theme: "light" | "dark" | "system"; chatFont: "default" | "sans" | "large" };
  chat: { model: string; effort: Effort; webSearch: boolean; showThinking: boolean; maxToolTurns: number; systemPrompt: string; appendAppContext: boolean };
  files: FileAccess;
  mcpServers: Record<string, McpServerConfig>;
  toolPermissions: Record<string, string>;
}

export interface FolderAccess {
  path: string;
  readOnly: boolean;
}

export interface FileAccess {
  enabled: boolean;
  autoApproveReads: boolean;
  folders: FolderAccess[];
}

export interface SecretsView {
  hasApiKey: boolean;
  apiKeyMasked: string;
  hasAdminKey: boolean;
  adminKeyMasked: string;
}

export interface McpTool {
  name: string;
  apiName: string;
  title?: string;
  description?: string;
}

export interface McpServerStatus {
  name: string;
  kind: "local" | "remote";
  target: string;
  status: "starting" | "running" | "error" | "stopped" | "disabled";
  error?: string;
  tools: McpTool[];
}

export interface Approval {
  requestId: string;
  toolUseId: string;
  apiName: string;
  source: string;
  tool: string;
  title?: string;
  description?: string;
  input: unknown;
}

export interface LiveReply {
  blocks: WireBlock[];
  toolResults: Record<string, WireBlock>;
  runningTools: string[];
  approvals: Approval[];
  notices: string[];
  startedAt: number;
}

export type ChatEvent =
  | { type: "started" }
  | { type: "message-start" }
  | { type: "block-start"; index: number; block: WireBlock }
  | { type: "delta"; index: number; text: string }
  | { type: "block-stop"; index: number; input: unknown }
  | { type: "message"; message: WireMessage }
  | ({ type: "approval" } & Approval)
  | { type: "tool-running"; toolUseId: string }
  | { type: "tool-result"; toolUseId: string; result: WireBlock }
  | { type: "notice"; text: string }
  | { type: "done"; status: "done" | "aborted" | "failed"; error?: string; conversation: Conversation };

export type View =
  | { name: "new" }
  | { name: "chat"; id: string }
  | { name: "recents" }
  | { name: "settings"; tab: SettingsTab };

export type SettingsTab = "general" | "appearance" | "api" | "files" | "connectors" | "permissions" | "usage" | "data";
