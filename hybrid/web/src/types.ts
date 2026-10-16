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
  // costUsd のうちサブエージェントの分
  agentsCostUsd?: number;
  // 最後の問い合わせで送った会話の大きさ (トークン)
  context?: number;
}

export type ChatKind = "chat" | "agent" | "workflow";
export type PermissionMode = "default" | "acceptEdits" | "auto" | "plan" | "bypass";
export type ChatStatus = "running" | "waiting" | "idle" | "failed" | "stopped";

export interface Todo {
  content: string;
  status: "pending" | "in_progress" | "completed";
}

export interface Artifact {
  id: string;
  title: string;
  type: "html" | "react" | "markdown" | "svg" | "mermaid" | "code";
  language?: string | null;
  content: string;
  version: number;
  updatedAt: number;
  // 前の版。history[n] が版 n + 1
  history?: { title: string; content: string; updatedAt: number }[];
}

export interface ArtifactListItem {
  conversationId: string;
  conversationTitle: string;
  id: string;
  title: string;
  type: Artifact["type"];
  version: number;
  updatedAt: number;
}

export interface Conversation {
  id: string;
  title: string;
  starred: boolean;
  archived?: boolean;
  model?: string;
  createdAt: number;
  updatedAt: number;
  messages: WireMessage[];
  usage: ConversationUsage;
  folderId?: string | null;
  parentId?: string | null;
  kind: ChatKind;
  workflowId?: string | null;
  permissionMode: PermissionMode;
  apiStart: number;
  compactionBlock?: WireBlock | null;
  todos?: Todo[] | null;
  artifacts: Artifact[];
  worktreePath?: string | null;
  lastRunStatus?: string | null;
  files: Record<string, string>;
  // 編集ややり直しで分かれた位置ごとの版の数と、表示中の版
  forkView?: Record<string, { count: number; active: number }>;
  incognito?: boolean;
  topEffort?: string | null;
}

export interface ConversationSummary {
  id: string;
  title: string;
  starred: boolean;
  archived?: boolean;
  updatedAt: number;
  preview: string;
  folderId?: string | null;
  parentId?: string | null;
  kind: ChatKind;
  lastRunStatus?: string | null;
}

export interface Folder {
  id: string;
  name: string;
  description: string;
  instructions: string;
  starred: boolean;
  archived: boolean;
  messagingScope: "folder" | "all" | "none";
  createdAt: number;
  // このプロジェクトで新しいチャットを始めるときの入力欄の設定
  defaults?: { model?: string; effort?: Effort; webSearch?: boolean; webFetch?: boolean; codeExecution?: boolean; disabledSources?: string[] } | null;
}

export interface WorkflowAgent {
  // 起動前は pending_ で始まる仮の値
  chatId: string;
  label: string;
  phase: string;
  status: string;
}

export interface WorkflowRun {
  id: string;
  name?: string | null;
  parentId: string;
  title: string;
  status: "running" | "completed" | "failed" | "stopped";
  phase?: string | null;
  logs: string[];
  agents: WorkflowAgent[];
  error?: string | null;
  startedAt: number;
}

export interface CommandInfo {
  name: string;
  description: string;
  kind: "skill" | "command";
}

export interface Extensions {
  commands: CommandInfo[];
  outputStyles: { id: string; label: string; builtin: boolean }[];
  skills: { name: string; description: string; enabled: boolean }[];
}

export interface Schedule {
  id: string;
  title: string;
  prompt: string;
  intervalMinutes?: number | null;
  dailyTime?: string | null;
  conversationId?: string | null;
  folderId?: string | null;
  enabled: boolean;
  nextRunAt?: number | null;
  lastRunAt?: number | null;
  expiresAt?: number | null;
  createdAt: number;
  batch?: boolean;
}

export interface HookEntry {
  matcher?: string | null;
  command: string;
  timeoutSeconds: number;
}

export interface MemoryFile {
  path: string;
  size: number;
  updatedAt: number;
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
  profile: { fullName: string; nickname: string; occupation: string; preferences: string; language?: string | null };
  appearance: { theme: "light" | "dark" | "system"; chatFont: "default" | "sans" | "large" };
  chat: {
    model: string;
    effort: Effort;
    webSearch: boolean;
    showThinking: boolean;
    thinkingDisplay: "summarized" | "updates";
    maxToolTurns: number;
    compactThreshold: number;
    longCacheAbove: number;
    archiveAfterDays: number;
    systemPrompt: string;
    appendAppContext: boolean;
    webSearchMaxUses: number;
    webFetch: boolean;
    webFetchMaxUses: number;
    codeExecution: boolean;
    officeSkills: boolean;
    outputStyle: string;
  };
  files: FileAccess;
  terminal: { enabled: boolean; timeoutSeconds: number; allowedCommands: string[] };
  programs: { enabled: boolean };
  browser: { enabled: boolean; executablePath?: string | null; mode: "dedicated" | "extension" };
  agents: { enabled: boolean; maxConcurrent: number; maxWorkflowAgents: number; maxMessageHops: number; autoApproveChildren: boolean; deleteFinishedChildren: boolean };
  updates: { checkOnStartup: boolean; autoInstall: boolean };
  desktop: { closeToTray: boolean; startWithWindows: boolean; notifyWhenDone: boolean; notifyWhenWaiting: boolean };
  backup: { enabled: boolean; folder: string; keep: number };
  budget: { confirmRequestUsd: number; dailyLimitUsd: number; monthlyLimitUsd: number };
  features: { memory: boolean; confirmMemoryWrites: boolean; artifacts: boolean; chatSearch: boolean; disabledSkills: string[]; allowBypassMode: boolean };
  hooks: Record<string, HookEntry[]>;
  mcpServers: Record<string, McpServerConfig>;
  toolPermissions: Record<string, string>;
}

export interface KnowledgeFile {
  name: string;
  size: number;
  updatedAt: number;
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

export interface UpdateInfo {
  available: boolean;
  current: string | null;
  latest: string;
  publishedAt: string | null;
  changes: string[];
}

export interface Approval {
  requestId: string;
  kind?: "question" | "cost";
  toolUseId: string;
  apiName: string;
  source: string;
  tool: string;
  title?: string;
  description?: string;
  allowAlways?: boolean;
  autoReason?: string;
  input: unknown;
  // 料金の確認のときだけ
  costUsd?: number;
  tokens?: number;
  cached?: boolean;
  limitUsd?: number;
}

export interface LiveReply {
  blocks: WireBlock[];
  toolResults: Record<string, WireBlock>;
  runningTools: string[];
  approvals: Approval[];
  notices: string[];
  startedAt: number;
  // 返答を受け取り終えた問い合わせの本当の数と、受け取り中の分の見積もり
  outputTokens: number;
  streamingTokens: number;
  // この応答でかかった料金。サブエージェントの分も含む
  costUsd: number;
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
  | { type: "compacted"; apiStart: number; compactionBlock: WireBlock | null }
  | { type: "usage"; output: number }
  | { type: "cost"; usage: ConversationUsage; added: number }
  | { type: "options"; model?: string; effort?: Effort }
  | { type: "inbound"; message: WireMessage }
  | { type: "discard" }
  | { type: "state"; state: Pick<Conversation, "permissionMode" | "todos" | "artifacts"> }
  | { type: "file"; fileId: string; name: string; path: string }
  | { type: "done"; status: "done" | "aborted" | "failed"; error?: string; conversation: Conversation };

export type View =
  | { name: "new"; folderId?: string; incognito?: boolean }
  | { name: "chat"; id: string }
  | { name: "recents" }
  | { name: "projects" }
  | { name: "artifacts" }
  | { name: "project"; id: string }
  | { name: "settings"; tab: SettingsTab };

export type SettingsTab = "general" | "appearance" | "api" | "features" | "files" | "computer" | "agents" | "extensions" | "schedules" | "connectors" | "permissions" | "usage" | "data";
