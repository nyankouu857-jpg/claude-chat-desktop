import type Anthropic from "@anthropic-ai/sdk";

export type ChatMessage = Anthropic.Beta.BetaMessageParam;

export type Effort = "low" | "medium" | "high" | "xhigh" | "max";

export const MODELS = [
  { id: "claude-opus-5-5", label: "Opus 5.5" },
  { id: "claude-fable-5-1", label: "Fable 5.1" },
  { id: "claude-sonnet-5-5", label: "Sonnet 5.5" },
  { id: "claude-haiku-5-5", label: "Haiku 5.5" },
] as const;

export const EFFORTS: Effort[] = ["low", "medium", "high", "xhigh", "max"];

export const DEFAULT_TITLE = "新しいチャット";

export interface TokenUsage {
  input: number;
  output: number;
  cacheRead: number;
}

export interface Conversation {
  id: string;
  title: string;
  createdAt: number;
  updatedAt: number;
  messages: ChatMessage[];
  usage: TokenUsage;
}

export interface ConversationSummary {
  id: string;
  title: string;
  updatedAt: number;
}

export interface Settings {
  model: string;
  effort: Effort;
  systemPrompt: string;
  webSearch: boolean;
}

export interface SettingsView extends Settings {
  hasApiKey: boolean;
}

export type SettingsUpdate = Partial<Settings> & { apiKey?: string };

export interface SearchHit {
  title: string;
  url: string;
}

export type ViewBlock =
  | { kind: "text"; text: string }
  | { kind: "thinking"; text: string }
  | { kind: "search"; query: string }
  | { kind: "search-result"; hits: SearchHit[]; error?: string }
  | { kind: "image"; src: string }
  | { kind: "notice"; text: string };

export type StreamEvent =
  | { type: "block"; index: number; block: ViewBlock }
  | { type: "delta"; index: number; text: string }
  | { type: "search-query"; index: number; query: string }
  | { type: "done"; conversation: Conversation }
  | { type: "error"; message: string; conversation: Conversation };

export interface ChatApi {
  getSettings(): Promise<SettingsView>;
  saveSettings(update: SettingsUpdate): Promise<SettingsView>;
  listConversations(): Promise<ConversationSummary[]>;
  getConversation(id: string): Promise<Conversation | null>;
  deleteConversation(id: string): Promise<void>;
  send(conversationId: string | null, content: ChatMessage["content"]): Promise<string>;
  stop(conversationId: string): Promise<void>;
  onChatEvent(listener: (conversationId: string, event: StreamEvent) => void): void;
  onConversationsChanged(listener: () => void): void;
}
