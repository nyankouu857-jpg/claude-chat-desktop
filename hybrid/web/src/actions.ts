import { call, on } from "./bridge";
import { getState, setState, type AppState } from "./store";
import type {
  Approval,
  ChatEvent,
  Conversation,
  LiveReply,
  McpServerConfig,
  SecretsView,
  Settings,
  SettingsTab,
  View,
  WireBlock,
  WireMessage,
} from "./types";

// C# 側の FileToolProvider.Source と同じ
export const FILES_SOURCE = "files";
// 組み込みツールの持ち主。サーバー側の Source と同じ名前
export const BUILTIN_SOURCE_IDS = [FILES_SOURCE, "terminal", "programs", "browser"];

let toastId = 0;

export function toast(text: string, kind: "info" | "error" = "info") {
  const id = ++toastId;
  setState((s) => ({ ...s, toasts: [...s.toasts, { id, text, kind }] }));
  setTimeout(() => setState((s) => ({ ...s, toasts: s.toasts.filter((t) => t.id !== id) })), kind === "error" ? 6000 : 3000);
}

async function guard<T>(work: () => Promise<T>): Promise<T | undefined> {
  try {
    return await work();
  } catch (e) {
    toast(e instanceof Error ? e.message : String(e), "error");
    return undefined;
  }
}

function emptyLive(): LiveReply {
  return { blocks: [], toolResults: {}, runningTools: [], approvals: [], notices: [], startedAt: Date.now() };
}

function updateLive(id: string, update: (live: LiveReply) => LiveReply, deferred = false) {
  setState((s) => {
    const live = s.live[id] ?? emptyLive();
    return { ...s, live: { ...s.live, [id]: update(live) } };
  }, deferred);
}

function putConversation(s: AppState, conversation: Conversation): AppState {
  return { ...s, conversations: { ...s.conversations, [conversation.id]: conversation } };
}

export async function init() {
  const data = await call("app.init");
  setState((s) => ({
    ...s,
    ready: true,
    settings: data.settings,
    secrets: data.secrets,
    paths: data.paths,
    version: data.version,
    summaries: data.conversations,
    mcp: data.mcp,
    live: Object.fromEntries(
      (data.running as { conversationId: string; approvals: Approval[] }[]).map((r) => [r.conversationId, { ...emptyLive(), approvals: r.approvals }]),
    ),
    choice: {
      model: data.settings.chat.model,
      effort: data.settings.chat.effort,
      webSearch: data.settings.chat.webSearch,
      disabledSources: [],
    },
  }));
  if (!data.secrets.hasApiKey) navigate({ name: "settings", tab: "api" });

  on("chat.event", ({ conversationId, event }) => applyChatEvent(conversationId, event));
  on("conversations.changed", () => void refreshSummaries());
  on("mcp.changed", (mcp) => setState((s) => ({ ...s, mcp })));
}

async function refreshSummaries() {
  const summaries = await call("conversations.list");
  setState((s) => {
    // タイトルが変わったら開いている会話にも反映する
    const conversations = { ...s.conversations };
    for (const summary of summaries) {
      const loaded = conversations[summary.id];
      if (loaded && (loaded.title !== summary.title || loaded.starred !== summary.starred)) {
        conversations[summary.id] = { ...loaded, title: summary.title, starred: summary.starred };
      }
    }
    return { ...s, summaries, conversations };
  });
}

async function ensureConversation(id: string) {
  if (getState().conversations[id]) return;
  const conversation = await call<Conversation | null>("conversations.get", { id });
  if (conversation) setState((s) => (s.conversations[id] ? s : putConversation(s, conversation)));
}

function applyChatEvent(id: string, event: ChatEvent) {
  switch (event.type) {
    case "started":
      setState((s) => {
        const { [id]: _, ...errors } = s.errors;
        return { ...s, errors, live: { ...s.live, [id]: emptyLive() } };
      });
      void ensureConversation(id);
      break;
    case "message-start":
      updateLive(id, (live) => ({ ...live, blocks: [] }));
      break;
    case "block-start":
      updateLive(id, (live) => {
        const blocks = [...live.blocks];
        blocks[event.index] = { ...event.block, _startedAt: Date.now() };
        return { ...live, blocks };
      });
      break;
    case "delta":
      updateLive(id, (live) => {
        const blocks = [...live.blocks];
        const block = blocks[event.index];
        if (!block) return live;
        if (block.type === "thinking") blocks[event.index] = { ...block, thinking: (block.thinking ?? "") + event.text };
        else blocks[event.index] = { ...block, text: (block.text ?? "") + event.text };
        return { ...live, blocks };
      }, true);
      break;
    case "block-stop":
      updateLive(id, (live) => {
        const blocks = [...live.blocks];
        const block = blocks[event.index];
        if (block) blocks[event.index] = { ...block, input: event.input ?? block.input, _stoppedAt: Date.now() };
        return { ...live, blocks };
      });
      break;
    case "message":
      setState((s) => {
        const conversation = s.conversations[id];
        const live = s.live[id];
        let next = s;
        if (conversation) next = putConversation(next, { ...conversation, messages: [...conversation.messages, event.message] });
        if (live) next = { ...next, live: { ...next.live, [id]: { ...live, blocks: [] } } };
        return next;
      });
      break;
    case "approval":
      updateLive(id, (live) => ({ ...live, approvals: [...live.approvals, event] }));
      break;
    case "tool-running":
      updateLive(id, (live) => ({ ...live, runningTools: [...live.runningTools, event.toolUseId] }));
      break;
    case "tool-result":
      updateLive(id, (live) => ({
        ...live,
        runningTools: live.runningTools.filter((t) => t !== event.toolUseId),
        toolResults: { ...live.toolResults, [event.toolUseId]: event.result },
      }));
      break;
    case "notice":
      updateLive(id, (live) => ({ ...live, notices: [...live.notices, event.text] }));
      break;
    case "done":
      setState((s) => {
        const notices = [...(s.live[id]?.notices ?? []), ...(event.status === "aborted" ? ["応答を途中で止めました"] : [])];
        const { [id]: _, ...live } = s.live;
        const errors = { ...s.errors };
        if (event.status === "failed" && event.error) errors[id] = event.error;
        const conversation = { ...event.conversation, _notices: notices } as Conversation;
        return { ...putConversation(s, conversation), live, errors };
      });
      break;
  }
}

export function navigate(view: View) {
  setState((s) => ({ ...s, view }));
  if (view.name === "chat") void guard(() => ensureConversation(view.id));
}

export const openSettings = (tab: SettingsTab = "general") => navigate({ name: "settings", tab });

export function toggleSidebar() {
  setState((s) => ({ ...s, sidebarOpen: !s.sidebarOpen }));
}

export function setChoice(patch: Partial<AppState["choice"]>) {
  setState((s) => ({ ...s, choice: { ...s.choice, ...patch } }));
}

function chatOptions() {
  const { choice, mcp } = getState();
  return {
    model: choice.model,
    effort: choice.effort,
    webSearch: choice.webSearch,
    enabledSources: [...BUILTIN_SOURCE_IDS, ...mcp.map((m) => m.name)].filter((name) => !choice.disabledSources.includes(name)),
  };
}

export async function send(conversationId: string | null, content: WireMessage["content"]) {
  const result = await guard(() => call<{ conversationId: string }>("chat.send", { conversationId, content, ...chatOptions() }));
  if (!result) return false;
  if (!conversationId) navigate({ name: "chat", id: result.conversationId });
  else {
    // 送った発言をすぐ画面に出す。正確な内容は応答完了時に置き換わる
    setState((s) => {
      const conversation = s.conversations[conversationId];
      if (!conversation) return s;
      return putConversation(s, { ...conversation, messages: [...conversation.messages, { role: "user", content }] });
    });
  }
  return true;
}

export const stop = (conversationId: string) => guard(() => call("chat.stop", { conversationId }));

export async function retry(conversationId: string) {
  const conversation = await guard(() => call<Conversation>("chat.retry", { conversationId, ...chatOptions() }));
  if (conversation) setState((s) => putConversation(s, conversation));
}

export async function edit(conversationId: string, messageIndex: number, content: WireMessage["content"]) {
  const conversation = await guard(() => call<Conversation>("chat.edit", { conversationId, messageIndex, content, ...chatOptions() }));
  if (conversation) setState((s) => putConversation(s, conversation));
}

export function approve(conversationId: string, requestId: string, decision: "once" | "always" | "deny") {
  updateLive(conversationId, (live) => ({ ...live, approvals: live.approvals.filter((a) => a.requestId !== requestId) }));
  void guard(() => call("tools.approve", { requestId, decision }));
}

export const renameConversation = (id: string, title: string) => guard(() => call("conversations.rename", { id, title }));
export const starConversation = (id: string, starred: boolean) => guard(() => call("conversations.star", { id, starred }));

export async function deleteConversation(id: string) {
  await guard(() => call("conversations.delete", { id }));
  setState((s) => {
    const { [id]: _, ...conversations } = s.conversations;
    const view: View = s.view.name === "chat" && s.view.id === id ? { name: "new" } : s.view;
    return { ...s, conversations, view };
  });
}

export async function updateSettings(patch: Partial<{ [K in keyof Settings]: Partial<Settings[K]> }>) {
  const settings = await guard(() => call<Settings>("settings.update", patch));
  if (settings) setState((s) => ({ ...s, settings }));
  return settings;
}

export async function reloadSettings() {
  const data = await guard(() => call("settings.reload"));
  if (!data) return;
  setState((s) => ({ ...s, settings: data.settings, secrets: data.secrets, mcp: data.mcp }));
  toast("設定ファイルを読み直しました");
}

export async function setSecret(name: "apiKey" | "adminKey", value: string) {
  const secrets = await guard(() => call<SecretsView>("secrets.set", { name, value }));
  if (secrets) setState((s) => ({ ...s, secrets }));
  return secrets;
}

export const testApiKey = () => guard(() => call<{ ok: boolean; message: string }>("api.test"));

export async function saveMcpServers(servers: Record<string, McpServerConfig>) {
  const mcp = await guard(() => call("mcp.save", { servers }));
  if (!mcp) return false;
  setState((s) => ({ ...s, mcp, settings: s.settings && { ...s.settings, mcpServers: servers } }));
  return true;
}

export const restartMcp = (name: string) => guard(() => call("mcp.restart", { name }));
export const openMcpLog = (name: string) => guard(() => call("mcp.openLog", { name }));

export async function revokePermission(apiName: string) {
  const settings = await guard(() => call<Settings>("permissions.revoke", { apiName }));
  if (settings) setState((s) => ({ ...s, settings }));
}

export const openPath = (target: "root" | "config" | "data" | "logs" | "settings" | "pricing") =>
  guard(() => call("app.open", { target }));

export const pickFolder = () => guard(() => call<string | null>("app.pickFolder"));
export const previewSystemPrompt = () => guard(() => call<string>("chat.systemPrompt"));

export const localUsage = () => guard(() => call("usage.local"));
export const reloadPricing = () => guard(() => call("pricing.reload"));

export async function orgUsage(range: string) {
  try {
    return { data: await call("usage.org", { range }) };
  } catch (e) {
    return { error: e instanceof Error ? e.message : String(e) };
  }
}

export function textOf(content: WireMessage["content"]): string {
  if (typeof content === "string") return content;
  return content.filter((b: WireBlock) => b.type === "text").map((b) => b.text).join("\n");
}
