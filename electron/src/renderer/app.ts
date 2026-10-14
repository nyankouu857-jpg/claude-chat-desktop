import {
  DEFAULT_TITLE,
  EFFORTS,
  MODELS,
  type ChatApi,
  type ChatMessage,
  type Conversation,
  type ConversationSummary,
  type Effort,
  type StreamEvent,
  type ViewBlock,
} from "../shared/types";
import { contentToViewBlocks, pushViewBlock } from "../shared/view";
import { renderMarkdown } from "./markdown";

declare global {
  interface Window {
    api: ChatApi;
  }
}

const api = window.api;
const byId = <T extends HTMLElement = HTMLElement>(id: string) => document.getElementById(id) as T;

const conversationList = byId<HTMLUListElement>("conversation-list");
const messagesPane = byId<HTMLDivElement>("messages");
const chatTitle = byId("chat-title");
const usageLabel = byId("usage");
const composer = byId<HTMLFormElement>("composer");
const promptInput = byId<HTMLTextAreaElement>("prompt");
const attachmentTray = byId("attachments");
const fileInput = byId<HTMLInputElement>("file-input");
const webSearchToggle = byId<HTMLInputElement>("web-search");
const modelSelect = byId<HTMLSelectElement>("model");
const effortSelect = byId<HTMLSelectElement>("effort");
const sendButton = byId<HTMLButtonElement>("send");
const stopButton = byId<HTMLButtonElement>("stop");
const settingsDialog = byId<HTMLDialogElement>("settings-dialog");
const apiKeyInput = byId<HTMLInputElement>("api-key");
const apiKeyState = byId("api-key-state");
const systemPromptInput = byId<HTMLTextAreaElement>("system-prompt");

type ImageMediaType = "image/png" | "image/jpeg" | "image/gif" | "image/webp";
const IMAGE_TYPES: string[] = ["image/png", "image/jpeg", "image/gif", "image/webp"];
// API 側の1枚あたり上限
const MAX_IMAGE_BYTES = 5 * 1024 * 1024;

interface PendingImage {
  mediaType: ImageMediaType;
  data: string;
}

interface LiveReply {
  blocks: ViewBlock[];
  indexMap: Map<number, number>;
}

let currentId: string | null = null;
let summaries: ConversationSummary[] = [];
let pendingImages: PendingImage[] = [];
// 別の会話を見ている間も途中経過を溜めておき、戻ったときに続きから描く
const liveReplies = new Map<string, LiveReply>();
const lastErrors = new Map<string, string>();
let liveView: AssistantView | null = null;

const isPinnedToBottom = () => messagesPane.scrollHeight - messagesPane.scrollTop - messagesPane.clientHeight < 80;
const scrollToBottom = () => (messagesPane.scrollTop = messagesPane.scrollHeight);

class AssistantView {
  readonly root = document.createElement("div");
  private readonly bubble = document.createElement("div");
  private readonly cursor = document.createElement("span");
  private readonly nodes: HTMLElement[] = [];
  private readonly dirty = new Set<number>();
  private frame = 0;

  constructor(private readonly blocks: ViewBlock[], streaming: boolean) {
    this.root.className = "message assistant";
    this.bubble.className = "bubble";
    this.root.append(this.bubble);
    if (streaming) {
      this.cursor.className = "cursor";
      this.bubble.append(this.cursor);
    }
    blocks.forEach((_, i) => this.renderBlock(i));
  }

  // 1トークンごとに Markdown を組み直すと重いのでフレーム単位にまとめる
  update(index: number) {
    this.dirty.add(index);
    this.frame ||= requestAnimationFrame(() => {
      this.frame = 0;
      const pinned = isPinnedToBottom();
      for (const i of this.dirty) this.renderBlock(i);
      this.dirty.clear();
      if (pinned) scrollToBottom();
    });
  }

  private renderBlock(index: number) {
    const block = this.blocks[index];
    if (!block) return;
    let node = this.nodes[index];
    if (!node) {
      node = createBlockNode(block);
      this.nodes[index] = node;
      this.bubble.insertBefore(node, this.cursor.isConnected ? this.cursor : null);
    }
    fillBlockNode(node, block);
  }
}

function createBlockNode(block: ViewBlock): HTMLElement {
  switch (block.kind) {
    case "text": {
      const div = document.createElement("div");
      div.className = "markdown";
      return div;
    }
    case "thinking": {
      const details = document.createElement("details");
      details.className = "thinking";
      details.innerHTML = `<summary>思考プロセス</summary><div class="thinking-body"></div>`;
      return details;
    }
    case "search-result": {
      const details = document.createElement("details");
      details.className = "search-result";
      details.innerHTML = `<summary></summary><ul></ul>`;
      return details;
    }
    case "image":
      return document.createElement("img");
    default: {
      const div = document.createElement("div");
      div.className = block.kind;
      return div;
    }
  }
}

function fillBlockNode(node: HTMLElement, block: ViewBlock) {
  switch (block.kind) {
    case "text":
      renderMarkdown(node, block.text);
      break;
    case "thinking":
      node.querySelector(".thinking-body")!.textContent = block.text;
      break;
    case "search":
      node.textContent = block.query ? `Web を検索: ${block.query}` : "Web を検索しています";
      break;
    case "search-result": {
      const summary = node.querySelector("summary")!;
      const list = node.querySelector("ul")!;
      summary.textContent = block.error ? `検索に失敗しました: ${block.error}` : `${block.hits.length} 件の検索結果`;
      list.replaceChildren(
        ...block.hits.map((hit) => {
          const li = document.createElement("li");
          const a = document.createElement("a");
          a.href = hit.url;
          a.textContent = hit.title || hit.url;
          li.append(a);
          return li;
        }),
      );
      break;
    }
    case "image":
      (node as HTMLImageElement).src = block.src;
      break;
    case "notice":
      node.textContent = block.text;
      break;
  }
}

function renderUserMessage(content: ChatMessage["content"]): HTMLElement {
  const root = document.createElement("div");
  root.className = "message user";
  const bubble = document.createElement("div");
  bubble.className = "bubble";
  for (const block of contentToViewBlocks(content)) {
    if (block.kind === "image") {
      const img = document.createElement("img");
      img.src = block.src;
      bubble.append(img);
    } else if (block.kind === "text") {
      bubble.append(block.text);
    }
  }
  root.append(bubble);
  return root;
}

function renderEmptyState() {
  const hour = new Date().getHours();
  const greeting = hour < 5 ? "夜更かしですね" : hour < 11 ? "おはようございます" : hour < 18 ? "こんにちは" : "こんばんは";
  messagesPane.innerHTML = `<div class="empty-state"><h2>${greeting}</h2><p>何について話しましょうか?</p></div>`;
}

function formatTokens(n: number) {
  return n >= 1000 ? `${(n / 1000).toFixed(1)}k` : String(n);
}

function renderConversation(conversation: Conversation | null) {
  liveView = null;
  chatTitle.textContent = conversation?.title ?? DEFAULT_TITLE;
  usageLabel.textContent = conversation
    ? `入力 ${formatTokens(conversation.usage.input)} / 出力 ${formatTokens(conversation.usage.output)} / キャッシュ ${formatTokens(conversation.usage.cacheRead)} tokens`
    : "";

  if (!conversation || conversation.messages.length === 0) {
    renderEmptyState();
    return;
  }

  messagesPane.replaceChildren();
  let assistantBlocks: ViewBlock[] = [];
  const flushAssistant = () => {
    if (assistantBlocks.length) messagesPane.append(new AssistantView(assistantBlocks, false).root);
    assistantBlocks = [];
  };

  // pause_turn で assistant が連続することがあるので1つの吹き出しにまとめる
  for (const message of conversation.messages) {
    if (message.role === "assistant") {
      for (const block of contentToViewBlocks(message.content)) pushViewBlock(assistantBlocks, block);
    } else {
      flushAssistant();
      messagesPane.append(renderUserMessage(message.content));
    }
  }
  flushAssistant();

  const live = liveReplies.get(conversation.id);
  if (live) {
    liveView = new AssistantView(live.blocks, true);
    messagesPane.append(liveView.root);
  }
  const error = lastErrors.get(conversation.id);
  if (error) {
    const div = document.createElement("div");
    div.className = "error";
    div.textContent = error;
    messagesPane.append(div);
  }
  scrollToBottom();
}

function renderSidebar() {
  conversationList.replaceChildren(
    ...summaries.map((summary) => {
      const li = document.createElement("li");
      li.classList.toggle("active", summary.id === currentId);
      const title = document.createElement("span");
      title.className = "title";
      title.textContent = summary.title;
      li.append(title);
      if (liveReplies.has(summary.id)) {
        const dot = document.createElement("span");
        dot.className = "streaming-dot";
        li.append(dot);
      }
      const deleteButton = document.createElement("button");
      deleteButton.className = "delete";
      deleteButton.textContent = "×";
      deleteButton.title = "削除";
      deleteButton.addEventListener("click", async (event) => {
        event.stopPropagation();
        if (!confirm(`${summary.title} を削除しますか?`)) return;
        await api.deleteConversation(summary.id);
        if (summary.id === currentId) startNewChat();
      });
      li.append(deleteButton);
      li.addEventListener("click", () => openConversation(summary.id));
      return li;
    }),
  );
}

function updateComposerState() {
  const streaming = currentId !== null && liveReplies.has(currentId);
  sendButton.hidden = streaming;
  stopButton.hidden = !streaming;
}

async function refreshSidebar() {
  summaries = await api.listConversations();
  renderSidebar();
  const current = summaries.find((s) => s.id === currentId);
  if (current) chatTitle.textContent = current.title;
}

async function openConversation(id: string) {
  currentId = id;
  renderConversation(await api.getConversation(id));
  renderSidebar();
  updateComposerState();
  promptInput.focus();
}

function startNewChat() {
  currentId = null;
  renderConversation(null);
  renderSidebar();
  updateComposerState();
  promptInput.focus();
}

function renderAttachments() {
  attachmentTray.replaceChildren(
    ...pendingImages.map((image, i) => {
      const wrapper = document.createElement("div");
      wrapper.className = "attachment";
      const img = document.createElement("img");
      img.src = `data:${image.mediaType};base64,${image.data}`;
      const remove = document.createElement("button");
      remove.type = "button";
      remove.textContent = "×";
      remove.addEventListener("click", () => {
        pendingImages.splice(i, 1);
        renderAttachments();
      });
      wrapper.append(img, remove);
      return wrapper;
    }),
  );
}

async function addImages(files: Iterable<File>) {
  for (const file of files) {
    if (!IMAGE_TYPES.includes(file.type)) continue;
    if (file.size > MAX_IMAGE_BYTES) {
      alert(`${file.name} は 5MB を超えているので添付できません`);
      continue;
    }
    const dataUrl = await new Promise<string>((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(reader.result as string);
      reader.onerror = () => reject(reader.error);
      reader.readAsDataURL(file);
    });
    pendingImages.push({ mediaType: file.type as ImageMediaType, data: dataUrl.slice(dataUrl.indexOf(",") + 1) });
  }
  renderAttachments();
}

function resizePrompt() {
  promptInput.style.height = "auto";
  promptInput.style.height = `${promptInput.scrollHeight}px`;
}

async function sendPrompt() {
  const text = promptInput.value.trim();
  if (!text && pendingImages.length === 0) return;
  if (currentId && liveReplies.has(currentId)) return;

  const content: ChatMessage["content"] =
    pendingImages.length === 0
      ? text
      : [
          ...pendingImages.map((image) => ({
            type: "image" as const,
            source: { type: "base64" as const, media_type: image.mediaType, data: image.data },
          })),
          ...(text ? [{ type: "text" as const, text }] : []),
        ];

  sendButton.disabled = true;
  try {
    const id = await api.send(currentId, content);
    if (currentId) lastErrors.delete(currentId);
    currentId = id;
    if (!liveReplies.has(id)) liveReplies.set(id, { blocks: [], indexMap: new Map() });
    promptInput.value = "";
    pendingImages = [];
    renderAttachments();
    resizePrompt();
    await openConversation(id);
  } catch (error) {
    alert(error instanceof Error ? error.message : String(error));
  } finally {
    sendButton.disabled = false;
  }
}

function ensureLive(conversationId: string): LiveReply {
  let live = liveReplies.get(conversationId);
  if (!live) {
    live = { blocks: [], indexMap: new Map() };
    liveReplies.set(conversationId, live);
  }
  return live;
}

function applyStreamEvent(conversationId: string, event: StreamEvent) {
  const isCurrent = conversationId === currentId;

  if (event.type === "done" || event.type === "error") {
    liveReplies.delete(conversationId);
    if (event.type === "error") lastErrors.set(conversationId, event.message);
    if (isCurrent) {
      renderConversation(event.conversation);
      updateComposerState();
    }
    renderSidebar();
    return;
  }

  const live = ensureLive(conversationId);
  let viewIndex: number | undefined;
  if (event.type === "block") {
    viewIndex = pushViewBlock(live.blocks, { ...event.block });
    live.indexMap.set(event.index, viewIndex);
  } else {
    viewIndex = live.indexMap.get(event.index);
    const block = viewIndex === undefined ? undefined : live.blocks[viewIndex];
    if (event.type === "delta" && (block?.kind === "text" || block?.kind === "thinking")) block.text += event.text;
    if (event.type === "search-query" && block?.kind === "search") block.query = event.query;
  }
  if (isCurrent && liveView && viewIndex !== undefined) liveView.update(viewIndex);
}

async function loadSettings() {
  modelSelect.replaceChildren(...MODELS.map((m) => new Option(m.label, m.id)));
  effortSelect.replaceChildren(...EFFORTS.map((e) => new Option(`思考: ${e}`, e)));
  const settings = await api.getSettings();
  modelSelect.value = settings.model;
  effortSelect.value = settings.effort;
  webSearchToggle.checked = settings.webSearch;
  if (!settings.hasApiKey) openSettings();
}

async function openSettings() {
  const settings = await api.getSettings();
  apiKeyInput.value = "";
  apiKeyState.textContent = settings.hasApiKey
    ? "登録済みです。変更するときだけ入力してください"
    : "未登録です。console.anthropic.com で発行したキーを入力してください";
  systemPromptInput.value = settings.systemPrompt;
  settingsDialog.showModal();
}

// Events

composer.addEventListener("submit", (event) => {
  event.preventDefault();
  void sendPrompt();
});

promptInput.addEventListener("keydown", (event) => {
  // IME 変換確定の Enter で送信しない
  if (event.key === "Enter" && !event.shiftKey && !event.isComposing) {
    event.preventDefault();
    void sendPrompt();
  }
});
promptInput.addEventListener("input", resizePrompt);
promptInput.addEventListener("paste", (event) => {
  const files = [...(event.clipboardData?.files ?? [])];
  if (files.length) void addImages(files);
});

composer.addEventListener("dragover", (event) => {
  event.preventDefault();
  composer.classList.add("dragging");
});
composer.addEventListener("dragleave", () => composer.classList.remove("dragging"));
composer.addEventListener("drop", (event) => {
  event.preventDefault();
  composer.classList.remove("dragging");
  void addImages(event.dataTransfer?.files ?? []);
});

byId("attach").addEventListener("click", () => fileInput.click());
fileInput.addEventListener("change", () => {
  void addImages(fileInput.files ?? []);
  fileInput.value = "";
});

stopButton.addEventListener("click", () => {
  if (currentId) void api.stop(currentId);
});

modelSelect.addEventListener("change", () => api.saveSettings({ model: modelSelect.value }));
effortSelect.addEventListener("change", () => api.saveSettings({ effort: effortSelect.value as Effort }));
webSearchToggle.addEventListener("change", () => api.saveSettings({ webSearch: webSearchToggle.checked }));

byId("new-chat").addEventListener("click", startNewChat);
byId("open-settings").addEventListener("click", () => void openSettings());

settingsDialog.addEventListener("close", async () => {
  if (settingsDialog.returnValue !== "save") return;
  const apiKey = apiKeyInput.value.trim();
  await api.saveSettings({ systemPrompt: systemPromptInput.value, ...(apiKey ? { apiKey } : {}) });
});

// main 側で外部ブラウザに回す
messagesPane.addEventListener("click", (event) => {
  const link = (event.target as HTMLElement).closest("a");
  if (!link) return;
  event.preventDefault();
  if (/^https?:\/\//.test(link.href)) window.open(link.href);
});

api.onChatEvent(applyStreamEvent);
api.onConversationsChanged(() => void refreshSidebar());

startNewChat();
void refreshSidebar();
void loadSettings();
