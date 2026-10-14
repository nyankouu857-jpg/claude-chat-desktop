import { app, safeStorage } from "electron";
import { randomUUID } from "node:crypto";
import { existsSync } from "node:fs";
import { mkdir, readFile, rename, rm, writeFile } from "node:fs/promises";
import path from "node:path";
import {
  DEFAULT_TITLE,
  type Conversation,
  type ConversationSummary,
  type Settings,
  type SettingsUpdate,
} from "../shared/types";

interface StoredSettings extends Settings {
  apiKey?: string;
  apiKeyEncrypted?: boolean;
}

const DEFAULT_SETTINGS: Settings = {
  model: "claude-opus-5-5",
  effort: "medium",
  systemPrompt: "",
  webSearch: false,
};

const dataDir = () => app.getPath("userData");
const conversationDir = () => path.join(dataDir(), "conversations");
const indexPath = () => path.join(conversationDir(), "index.json");
const settingsPath = () => path.join(dataDir(), "settings.json");

// 書き込み中に落ちても履歴が壊れないように
async function writeJsonAtomic(file: string, value: unknown) {
  await mkdir(path.dirname(file), { recursive: true });
  const tmp = `${file}.tmp`;
  await writeFile(tmp, JSON.stringify(value), "utf8");
  await rename(tmp, file);
}

async function readJson<T>(file: string): Promise<T | null> {
  if (!existsSync(file)) return null;
  return JSON.parse(await readFile(file, "utf8")) as T;
}

let storedSettings: StoredSettings | null = null;

async function loadStoredSettings(): Promise<StoredSettings> {
  storedSettings ??= { ...DEFAULT_SETTINGS, ...(await readJson<StoredSettings>(settingsPath())) };
  return storedSettings;
}

export async function getSettings(): Promise<Settings> {
  const { apiKey, apiKeyEncrypted, ...settings } = await loadStoredSettings();
  return settings;
}

export async function getApiKey(): Promise<string | undefined> {
  const s = await loadStoredSettings();
  if (s.apiKey) {
    return s.apiKeyEncrypted ? safeStorage.decryptString(Buffer.from(s.apiKey, "base64")) : s.apiKey;
  }
  return process.env.ANTHROPIC_API_KEY;
}

export async function updateSettings(update: SettingsUpdate) {
  const { apiKey, ...rest } = update;
  const s = await loadStoredSettings();
  Object.assign(s, rest);
  if (apiKey !== undefined) {
    // Linux でキーリングが無い環境だと暗号化できないので平文に落とす
    const canEncrypt = safeStorage.isEncryptionAvailable();
    s.apiKey = apiKey === "" ? undefined : canEncrypt ? safeStorage.encryptString(apiKey).toString("base64") : apiKey;
    s.apiKeyEncrypted = canEncrypt;
  }
  await writeJsonAtomic(settingsPath(), s);
}

// 履歴本体は画像の base64 で重くなるので、一覧は index.json だけ読む
let summaries: ConversationSummary[] | null = null;
const conversations = new Map<string, Conversation>();

export async function listConversations(): Promise<ConversationSummary[]> {
  summaries ??= (await readJson<ConversationSummary[]>(indexPath())) ?? [];
  return [...summaries].sort((a, b) => b.updatedAt - a.updatedAt);
}

export async function getConversation(id: string): Promise<Conversation | null> {
  const cached = conversations.get(id);
  if (cached) return cached;
  const loaded = await readJson<Conversation>(path.join(conversationDir(), `${id}.json`));
  if (loaded) conversations.set(id, loaded);
  return loaded;
}

export function createConversation(): Conversation {
  const now = Date.now();
  const conversation: Conversation = {
    id: randomUUID(),
    title: DEFAULT_TITLE,
    createdAt: now,
    updatedAt: now,
    messages: [],
    usage: { input: 0, output: 0, cacheRead: 0 },
  };
  conversations.set(conversation.id, conversation);
  return conversation;
}

export async function saveConversation(conversation: Conversation) {
  conversation.updatedAt = Date.now();
  await writeJsonAtomic(path.join(conversationDir(), `${conversation.id}.json`), conversation);
  const list = await listConversations();
  const summary = { id: conversation.id, title: conversation.title, updatedAt: conversation.updatedAt };
  summaries = [summary, ...list.filter((s) => s.id !== conversation.id)];
  await writeJsonAtomic(indexPath(), summaries);
}

export async function deleteConversation(id: string) {
  conversations.delete(id);
  summaries = (await listConversations()).filter((s) => s.id !== id);
  await writeJsonAtomic(indexPath(), summaries);
  await rm(path.join(conversationDir(), `${id}.json`), { force: true });
}
