import { contextBridge, ipcRenderer } from "electron";
import type { ChatApi } from "./shared/types";

const api: ChatApi = {
  getSettings: () => ipcRenderer.invoke("settings:get"),
  saveSettings: (update) => ipcRenderer.invoke("settings:save", update),
  listConversations: () => ipcRenderer.invoke("conversations:list"),
  getConversation: (id) => ipcRenderer.invoke("conversations:get", id),
  deleteConversation: (id) => ipcRenderer.invoke("conversations:delete", id),
  send: (conversationId, content) => ipcRenderer.invoke("chat:send", conversationId, content),
  stop: (conversationId) => ipcRenderer.invoke("chat:stop", conversationId),
  onChatEvent: (listener) => {
    ipcRenderer.on("chat:event", (_e, conversationId, event) => listener(conversationId, event));
  },
  onConversationsChanged: (listener) => {
    ipcRenderer.on("conversations:changed", () => listener());
  },
};

contextBridge.exposeInMainWorld("api", api);
