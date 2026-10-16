import { call, on } from "./bridge";
import { getState, setState, type AppState } from "./store";
import type {
  Approval,
  ChatEvent,
  Conversation,
  Folder,
  HookEntry,
  LiveReply,
  McpServerConfig,
  ArtifactListItem,
  KnowledgeFile,
  MemoryFile,
  PermissionMode,
  Schedule,
  SecretsView,
  Settings,
  SettingsTab,
  View,
  WireBlock,
  WireMessage,
  WorkflowRun,
} from "./types";

// C# 側の FileToolProvider.Source と同じ
export const FILES_SOURCE = "files";
// 組み込みツールの持ち主。サーバー側の Source と同じ名前
export const BUILTIN_SOURCE_IDS = [FILES_SOURCE, "terminal", "programs", "browser", "browse", "agents", "chats", "todo", "artifacts", "memory", "schedule", "skills", "ask"];

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
  return { blocks: [], toolResults: {}, runningTools: [], approvals: [], notices: [], startedAt: Date.now(), outputTokens: 0, streamingTokens: 0 };
}

// 本当の数は問い合わせが終わるまで届かないので、それまでは文字数から見積もる。英数字は 4 文字、日本語は 1 文字でおよそ 1 トークン
function estimateTokens(text: string) {
  let ascii = 0;
  let other = 0;
  for (const ch of text) {
    if (ch.charCodeAt(0) < 128) ascii++;
    else other++;
  }
  return ascii / 4 + other;
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
  // 初期化の応答を待つ間に届いた通知も取りこぼさないよう、先に受け口を作る
  on("chat.event", ({ conversationId, event }) => applyChatEvent(conversationId, event));
  on("conversations.changed", () => void refreshSummaries());
  on("mcp.changed", (mcp) => setState((s) => ({ ...s, mcp })));
  on("chats.status", ({ id, status }) => setState((s) => ({ ...s, statuses: { ...s.statuses, [id]: status } })));
  on("workflow.changed", (run: WorkflowRun) => setState((s) => ({ ...s, workflows: { ...s.workflows, [run.id]: run } })));
  on("folders.changed", () => void refreshFolders());
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
    folders: data.folders,
    statuses: data.statuses,
    workflows: Object.fromEntries((data.workflows as WorkflowRun[]).map((w) => [w.id, w])),
    extensions: data.extensions,
    live: {
      ...Object.fromEntries(
        (data.running as { conversationId: string; approvals: Approval[] }[]).map((r) => [r.conversationId, { ...emptyLive(), approvals: r.approvals }]),
      ),
      ...s.live,
    },
    choice: {
      model: data.settings.chat.model,
      effort: data.settings.chat.effort,
      webSearch: data.settings.chat.webSearch,
      webFetch: data.settings.chat.webFetch,
      codeExecution: data.settings.chat.codeExecution,
      research: false,
      style: data.settings.chat.outputStyle,
      permissionMode: "default",
      disabledSources: [],
    },
  }));
  if (!data.secrets.hasApiKey) navigate({ name: "settings", tab: "api" });
}

async function refreshFolders() {
  const folders = await call<Folder[]>("folders.list");
  setState((s) => ({ ...s, folders }));
}

export const getStyle = (id: string) => guard(() => call<{ id: string; name: string; text: string }>("styles.get", { id }));
export const generateStyle = (request: string) => guard(() => call<{ name: string; text: string }>("styles.generate", { request }));

export async function saveStyle(id: string | undefined, name: string, text: string) {
  const saved = await guard(() => call<string>("styles.save", { id, name, text }));
  if (saved) await refreshExtensions();
  return saved;
}

export async function deleteStyle(id: string) {
  await guard(() => call("styles.delete", { id }));
  await refreshExtensions();
  if (getState().choice.style === id) setChoice({ style: "default" });
}

export async function importSkill(file: File) {
  const data = await new Promise<string>((resolve, reject) => {
    const reader = new FileReader();
    reader.onload = () => resolve(String(reader.result).split(",")[1] ?? "");
    reader.onerror = () => reject(reader.error);
    reader.readAsDataURL(file);
  });
  const name = await guard(() => call<string>("skills.import", { data }));
  if (name) {
    // 取り込んだ Skill は止めた状態で入るので、設定の一覧も読み直す
    await updateSettings({});
    await refreshExtensions();
    toast(`Skill「${name}」を追加しました。中身を確かめてからオンにしてください`);
  }
}

export async function refreshExtensions() {
  const extensions = await guard(() => call("extensions.list"));
  if (extensions) setState((s) => ({ ...s, extensions }));
}

async function refreshSummaries() {
  const summaries = await call("conversations.list");
  setState((s) => {
    // タイトルが変わったら開いている会話にも反映する
    const conversations = { ...s.conversations };
    for (const summary of summaries) {
      const loaded = conversations[summary.id];
      if (loaded && (loaded.title !== summary.title || loaded.starred !== summary.starred || loaded.folderId !== summary.folderId)) {
        conversations[summary.id] = { ...loaded, title: summary.title, starred: summary.starred, folderId: summary.folderId };
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
      updateLive(id, (live) => ({ ...live, blocks: [], streamingTokens: 0 }));
      break;
    case "options":
      // Claude が切り替えたチャットを開いているなら、入力欄の選択も合わせる
      if (getState().view.name === "chat" && (getState().view as { id?: string }).id === id) {
        setChoice({ ...(event.model ? { model: event.model } : {}), ...(event.effort ? { effort: event.effort } : {}) });
      }
      break;
    case "usage":
      updateLive(id, (live) => ({ ...live, outputTokens: live.outputTokens + event.output, streamingTokens: 0 }));
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
        else if (block.type === "compaction") blocks[event.index] = { ...block, content: (block.content ?? "") + event.text };
        else blocks[event.index] = { ...block, text: (block.text ?? "") + event.text };
        return { ...live, blocks, streamingTokens: live.streamingTokens + estimateTokens(event.text) };
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
    case "inbound":
      // ほかのチャットや予定から届いた発言。開いていない会話は開いたときに読み込む
      setState((s) => {
        const conversation = s.conversations[id];
        return conversation ? putConversation(s, { ...conversation, messages: [...conversation.messages, event.message] }) : s;
      });
      break;
    case "state":
      setState((s) => {
        const conversation = s.conversations[id];
        if (!conversation) return s;
        // claude.ai と同じく、作ったり直したりしたアーティファクトはすぐ横に開く
        const before = new Map(conversation.artifacts.map((a) => [a.id, a.version]));
        const touched = event.state.artifacts.find((a) => before.get(a.id) !== a.version);
        const next = putConversation(s, { ...conversation, ...event.state });
        const viewing = s.view.name === "chat" && s.view.id === id;
        return touched && viewing ? { ...next, artifact: { conversationId: id, id: touched.id } } : next;
      });
      break;
    case "file":
      setState((s) => {
        const conversation = s.conversations[id];
        return conversation ? putConversation(s, { ...conversation, files: { ...conversation.files, [event.fileId]: event.path } }) : s;
      });
      break;
    case "discard":
      // 断られて捨てた応答の途中の文を消す
      updateLive(id, (live) => ({ ...live, blocks: [] }));
      break;
    case "done":
      setState((s) => {
        const notices = [...(s.live[id]?.notices ?? []), ...(event.status === "aborted" ? ["応答を途中で止めました"] : [])];
        const { [id]: _, ...live } = s.live;
        const errors = { ...s.errors };
        if (event.status === "failed" && event.error) errors[id] = event.error;
        const finished = s.live[id];
        // Claude Code の「✻ Cooked for 1m 6s」用に、最新の回答にかかった時間を覚えておく
        const lastTurn = finished ? { seconds: Math.round((Date.now() - finished.startedAt) / 1000), tokens: finished.outputTokens + finished.streamingTokens } : undefined;
        const conversation = { ...event.conversation, _notices: notices, _lastTurn: lastTurn } as Conversation;
        return { ...putConversation(s, conversation), live, errors };
      });
      break;
  }
}

export function navigate(view: View) {
  // 設定は画面を切り替えず、今の画面の上に開く
  if (view.name === "settings") return openSettings(view.tab);
  setState((s) => ({ ...s, view, settingsOverlay: null }));
  if (view.name === "chat") void guard(() => ensureConversation(view.id));
}

export function openSettings(tab: SettingsTab = "general") {
  setState((s) => ({ ...s, settingsOverlay: { tab, full: s.settingsOverlay?.full ?? false } }));
}

export const closeSettings = () => setState((s) => ({ ...s, settingsOverlay: null }));
export const setSettingsFull = (full: boolean) => setState((s) => (s.settingsOverlay ? { ...s, settingsOverlay: { ...s.settingsOverlay, full } } : s));

export function toggleInfoPanel() {
  setState((s) => {
    try {
      localStorage.setItem("infoPanelOpen", String(!s.infoPanelOpen));
    } catch {
      // 保存できなくても、開いている間は効く
    }
    return { ...s, infoPanelOpen: !s.infoPanelOpen };
  });
}

export const setArtifactFull = (artifactFull: boolean) => setState((s) => ({ ...s, artifactFull }));

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
    webFetch: choice.webFetch,
    codeExecution: choice.codeExecution,
    style: choice.style,
    enabledSources: [...BUILTIN_SOURCE_IDS, ...mcp.map((m) => m.name)].filter((name) => !choice.disabledSources.includes(name)),
  };
}

export async function send(conversationId: string | null, content: WireMessage["content"], extra: { research?: boolean } = {}) {
  const { choice, view } = getState();
  const research = extra.research ?? choice.research;
  const result = await guard(() => call<{ conversationId: string }>("chat.send", {
    conversationId,
    content,
    research,
    // 新しいチャットだけ。既存のチャットはヘッダーで切り替える
    permissionMode: conversationId ? undefined : choice.permissionMode,
    folderId: conversationId ? undefined : view.name === "new" ? view.folderId : view.name === "project" ? view.id : undefined,
    incognito: !conversationId && view.name === "new" && !view.folderId && view.incognito ? true : undefined,
    ...chatOptions(),
  }));
  if (result && research) setChoice({ research: false });
  // 権限モードは始めた会話に移したので、次の新しいチャットは毎回確認から始める
  if (result && !conversationId && choice.permissionMode !== "default") setChoice({ permissionMode: "default" });
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

// messageIndex はやり直す返事の元になった発言。省けば最後の発言
export async function retry(conversationId: string, opts: { messageIndex?: number; model?: string } = {}) {
  const options = { ...chatOptions(), ...(opts.model ? { model: opts.model } : {}) };
  if (opts.model) setChoice({ model: opts.model });
  const conversation = await guard(() => call<Conversation>("chat.retry", { conversationId, messageIndex: opts.messageIndex, ...options }));
  if (conversation) setState((s) => putConversation(s, conversation));
}

export async function switchBranch(conversationId: string, index: number, version: number) {
  const conversation = await guard(() => call<Conversation>("chat.switchBranch", { conversationId, index, version }));
  if (conversation) setState((s) => putConversation(s, conversation));
}

export async function edit(conversationId: string, messageIndex: number, content: WireMessage["content"]) {
  const conversation = await guard(() => call<Conversation>("chat.edit", { conversationId, messageIndex, content, ...chatOptions() }));
  if (conversation) setState((s) => putConversation(s, conversation));
}

export async function approve(conversationId: string, requestId: string, decision: "once" | "always" | "deny") {
  updateLive(conversationId, (live) => ({ ...live, approvals: live.approvals.filter((a) => a.requestId !== requestId) }));
  await guard(() => call("tools.approve", { requestId, decision }));
  // 中身側で権限が書き足されるので、取り消せるように設定を取り直す
  if (decision === "always") await updateSettings({});
}

export async function answer(conversationId: string, requestId: string, answers: { selected: string[]; other: string }[]) {
  updateLive(conversationId, (live) => ({ ...live, approvals: live.approvals.filter((a) => a.requestId !== requestId) }));
  await guard(() => call("tools.answer", { requestId, answers }));
}

export const searchConversations = (query: string) => guard(() => call<{ id: string; snippet: string }[]>("conversations.search", { query }));

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
  if (!settings) return settings;
  setState((s) => ({ ...s, settings }));
  // 既定を変えたら、入力欄の選択にもすぐ反映する
  const chat = patch.chat ?? {};
  const choice: Partial<AppState["choice"]> = {};
  for (const key of ["model", "effort", "webSearch", "webFetch", "codeExecution"] as const) {
    if (chat[key] !== undefined) (choice as Record<string, unknown>)[key] = chat[key];
  }
  if (chat.outputStyle !== undefined) choice.style = chat.outputStyle;
  if (Object.keys(choice).length > 0) setChoice(choice);
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

export const openPath = (target: "root" | "config" | "data" | "logs" | "settings" | "pricing" | "memory" | "skills" | "commands" | "outputStyles" | "exports") =>
  guard(() => call("app.open", { target }));

export const pickFolder = () => guard(() => call<string | null>("app.pickFolder"));
export const previewSystemPrompt = (conversationId?: string) => guard(() => call<string>("chat.systemPrompt", { conversationId }));

// Folders

export async function saveFolder(folder: Partial<Folder>) {
  const folders = await guard(() => call<Folder[]>("folders.save", { folder }));
  if (folders) setState((s) => ({ ...s, folders }));
  return folders;
}

export async function deleteFolder(id: string) {
  const folders = await guard(() => call<Folder[]>("folders.delete", { id }));
  if (folders) setState((s) => ({ ...s, folders }));
}

export const moveConversation = (id: string, folderId: string | null) => guard(() => call("conversations.move", { id, folderId }));

// Chat commands

export async function setMode(conversationId: string | null, mode: PermissionMode) {
  if (!conversationId) {
    setChoice({ permissionMode: mode });
    return;
  }
  const conversation = await guard(() => call<Conversation>("chat.setMode", { conversationId, mode }));
  if (conversation) setState((s) => putConversation(s, conversation));
}

export async function compact(conversationId: string, instructions?: string) {
  toast("会話を要約しています...");
  const conversation = await guard(() => call<Conversation>("chat.compact", { conversationId, instructions }));
  if (conversation) {
    setState((s) => putConversation(s, conversation));
    toast("要約しました。ここから先は要約から続けます");
  }
}

export const btw = (conversationId: string, question: string) => guard(() => call<{ answer: string }>("chat.btw", { conversationId, question }));

export async function exportConversation(conversationId: string) {
  const result = await guard(() => call<{ path: string }>("chat.export", { conversationId }));
  if (result) toast(`書き出しました: ${result.path}`);
}

export async function rewind(conversationId: string, messageIndex: number, restoreFiles: boolean) {
  const conversation = getState().conversations[conversationId];
  const text = conversation ? humanText(conversation.messages[messageIndex]?.content ?? "") : "";
  const result = await guard(() => call<{ conversation: Conversation; files: { restored: string[]; skipped: string[] } | null }>("chat.rewind", { conversationId, messageIndex, restoreFiles }));
  if (!result) return;
  setState((s) => ({ ...putConversation(s, result.conversation), prefill: { ...s.prefill, [conversationId]: { text, nonce: Date.now() } } }));
  if (result.files) {
    const { restored, skipped } = result.files;
    toast(`ファイルを ${restored.length} 件戻しました${skipped.length ? `。戻せなかったもの: ${skipped.join("、")}` : ""}`, skipped.length ? "error" : "info");
  }
}

export const rewindTurns = (conversationId: string) => guard(() => call<number[]>("chat.rewindInfo", { conversationId }));

// ブラウザの confirm の代わり。claude.ai と同じく、赤いボタンの確認ダイアログを出す
let confirmResolver: ((ok: boolean) => void) | null = null;

export function askConfirm(title: string, message: string, confirmLabel = "削除") {
  confirmResolver?.(false);
  setState((s) => ({ ...s, dialog: { kind: "confirm", title, message, confirmLabel } }));
  return new Promise<boolean>((resolve) => { confirmResolver = resolve; });
}

export function answerConfirm(ok: boolean) {
  confirmResolver?.(ok);
  confirmResolver = null;
  setState((s) => ({ ...s, dialog: null }));
}

export const setQuote = (conversationId: string, text: string | null) =>
  setState((s) => {
    const { [conversationId]: _, ...quotes } = s.quotes;
    return { ...s, quotes: text ? { ...quotes, [conversationId]: text } : quotes };
  });

export interface BrowserExtensionStatus {
  connected: boolean;
  version?: string | null;
  error?: string | null;
  token?: string | null;
}

export async function createShortcuts() {
  try {
    await call("app.createShortcuts");
    toast("デスクトップとスタートメニューにショートカットを作りました");
  } catch (e) {
    toast(e instanceof Error ? e.message : String(e), "error");
  }
}

export const browserExtensionStatus = () => call<BrowserExtensionStatus>("browser.extensionStatus");
export const writeBrowserExtension = () => guard(() => call<string>("browser.writeExtension"));

export const resetBrowserToken = () => guard(() => call<BrowserExtensionStatus>("browser.resetToken"));

export const extractDocumentText = (name: string, data: string) => call<string>("files.extractText", { name, data });

export const listArtifacts = () => guard(() => call<ArtifactListItem[]>("artifacts.list"));

export const openArtifact = (conversationId: string, id: string) => setState((s) => ({ ...s, artifact: { conversationId, id } }));
export const closeArtifact = () => setState((s) => ({ ...s, artifact: null, artifactFull: false }));

export async function exportArtifact(conversationId: string, id: string) {
  const result = await guard(() => call<{ path: string }>("artifacts.export", { conversationId, id }));
  if (result) toast(`保存しました: ${result.path}`);
}

export const openFile = (conversationId: string, fileId: string, reveal = false) => guard(() => call("files.open", { conversationId, fileId, reveal }));
export const stopWorkflow = (id: string) => guard(() => call("workflows.stop", { id }));
export const expandCommand = (name: string, args: string) => guard(() => call<string>("commands.expand", { name, args }));

// Schedules

export const listSchedules = () => guard(() => call<Schedule[]>("schedules.list"));
export const saveSchedule = (schedule: Partial<Schedule>, loop = false) => guard(() => call<Schedule[]>("schedules.save", { schedule, loop }));
export const deleteSchedule = (id: string) => guard(() => call<Schedule[]>("schedules.delete", { id }));
export const setScheduleEnabled = (id: string, enabled: boolean) => guard(() => call<Schedule[]>("schedules.setEnabled", { id, enabled }));

// Memory

// folderId を渡すとそのプロジェクトのメモリ
export const listMemory = (folderId?: string) => guard(() => call<MemoryFile[]>("memory.list", { folderId }));
export const readMemory = (path: string, folderId?: string) => guard(() => call<string>("memory.read", { path, folderId }));
export const writeMemory = (path: string, content: string, folderId?: string) => guard(() => call<MemoryFile[]>("memory.write", { path, content, folderId }));
export const deleteMemory = (path: string, folderId?: string) => guard(() => call<MemoryFile[]>("memory.delete", { path, folderId }));

export const listKnowledge = (id: string) => guard(() => call<KnowledgeFile[]>("folders.knowledge", { id }));
export const addKnowledge = (id: string, name: string, text: string) => guard(() => call<KnowledgeFile[]>("folders.addKnowledge", { id, name, text }));
export const readKnowledge = (id: string, name: string) => guard(() => call<string>("folders.readKnowledge", { id, name }));
export const deleteKnowledge = (id: string, name: string) => guard(() => call<KnowledgeFile[]>("folders.deleteKnowledge", { id, name }));

export async function saveHooks(hooks: Record<string, HookEntry[]>) {
  const settings = await guard(() => call<Settings>("settings.update", { hooks }));
  if (settings) setState((s) => ({ ...s, settings }));
  return settings;
}

export const localUsage = () => guard(() => call("usage.local"));
export const reloadPricing = () => guard(() => call("pricing.reload"));

export async function orgUsage(range: string) {
  try {
    return { data: await call("usage.org", { range }) };
  } catch (e) {
    return { error: e instanceof Error ? e.message : String(e) };
  }
}

// フックの補足など、アプリが添えた部分を除いた本人の文
const ADDED_TAG = /^<(hook-feedback)[\s>]/;

export function humanText(content: WireMessage["content"]): string {
  if (typeof content === "string") return content;
  const own = content.filter((b: WireBlock) => b.type === "text" && !ADDED_TAG.test(b.text ?? "")).map((b) => b.text).join("\n");
  return own || textOf(content);
}

export function clearPrefill(conversationId: string) {
  setState((s) => {
    const { [conversationId]: _, ...prefill } = s.prefill;
    return { ...s, prefill };
  });
}

export function textOf(content: WireMessage["content"]): string {
  if (typeof content === "string") return content;
  return content.filter((b: WireBlock) => b.type === "text").map((b) => b.text).join("\n");
}
