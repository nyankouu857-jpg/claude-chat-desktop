import Anthropic from "@anthropic-ai/sdk";
import { BrowserWindow, Menu, app, ipcMain, shell } from "electron";
import path from "node:path";
import { DEFAULT_TITLE, type ChatMessage, type SettingsUpdate, type SettingsView, type StreamEvent } from "../shared/types";
import { generateTitle, streamReply } from "./chat";
import * as store from "./store";

let window: BrowserWindow | null = null;
const runningChats = new Map<string, AbortController>();

function createWindow() {
  window = new BrowserWindow({
    width: 1200,
    height: 820,
    minWidth: 720,
    minHeight: 480,
    title: "ClaudeChat",
    backgroundColor: "#faf9f5",
    webPreferences: {
      preload: path.join(__dirname, "preload.js"),
      contextIsolation: true,
      sandbox: true,
    },
  });
  Menu.setApplicationMenu(null);
  window.loadFile(path.join(__dirname, "renderer", "index.html"));

  // 回答中のリンクでアプリ自体が遷移しないように
  window.webContents.setWindowOpenHandler(({ url }) => {
    if (url.startsWith("https://") || url.startsWith("http://")) shell.openExternal(url);
    return { action: "deny" };
  });
  window.webContents.on("will-navigate", (event) => event.preventDefault());

  // メニューを消したので開発者ツールの入口を残す
  window.webContents.on("before-input-event", (_event, input) => {
    if (input.type === "keyDown" && input.key === "F12") window?.webContents.toggleDevTools();
  });
}

async function settingsView(): Promise<SettingsView> {
  return { ...(await store.getSettings()), hasApiKey: Boolean(await store.getApiKey()) };
}

function sendToRenderer(channel: string, ...args: unknown[]) {
  if (window && !window.isDestroyed()) window.webContents.send(channel, ...args);
}

async function runChat(conversationId: string) {
  const conversation = await store.getConversation(conversationId);
  if (!conversation) return;
  const controller = new AbortController();
  runningChats.set(conversationId, controller);
  const emit = (event: StreamEvent) => sendToRenderer("chat:event", conversationId, event);

  try {
    const apiKey = await store.getApiKey();
    if (!apiKey) {
      emit({ type: "error", message: "APIキーが未設定です。左下の設定から登録してください", conversation });
      return;
    }
    const client = new Anthropic({ apiKey });
    const settings = await store.getSettings();
    const outcome = await streamReply(client, settings, conversation, controller.signal, emit);
    // 応答中に削除された会話を保存し直して復活させない
    if (!(await store.getConversation(conversationId))) return;
    await store.saveConversation(conversation);
    if (outcome !== "error") emit({ type: "done", conversation });
    sendToRenderer("conversations:changed");

    if (outcome === "done" && conversation.title === DEFAULT_TITLE) {
      // タイトルは後回しでいいので失敗しても会話は止めない
      const title = await generateTitle(client, conversation).catch(() => null);
      if (title) {
        conversation.title = title;
        await store.saveConversation(conversation);
        sendToRenderer("conversations:changed");
      }
    }
  } finally {
    runningChats.delete(conversationId);
  }
}

ipcMain.handle("settings:get", () => settingsView());
ipcMain.handle("settings:save", async (_e, update: SettingsUpdate) => {
  await store.updateSettings(update);
  return settingsView();
});
ipcMain.handle("conversations:list", () => store.listConversations());
ipcMain.handle("conversations:get", (_e, id: string) => store.getConversation(id));
ipcMain.handle("conversations:delete", async (_e, id: string) => {
  runningChats.get(id)?.abort();
  await store.deleteConversation(id);
  sendToRenderer("conversations:changed");
});
ipcMain.handle("chat:send", async (_e, conversationId: string | null, content: ChatMessage["content"]) => {
  const conversation = (conversationId && (await store.getConversation(conversationId))) || store.createConversation();
  if (runningChats.has(conversation.id)) throw new Error("この会話は応答中です");
  conversation.messages.push({ role: "user", content });
  await store.saveConversation(conversation);
  sendToRenderer("conversations:changed");
  void runChat(conversation.id);
  return conversation.id;
});
ipcMain.handle("chat:stop", (_e, conversationId: string) => {
  runningChats.get(conversationId)?.abort();
});

app.whenReady().then(createWindow);
app.on("window-all-closed", () => app.quit());
