import {
  compact,
  expandCommand,
  exportConversation,
  navigate,
  openSettings,
  saveSchedule,
  send,
  setChoice,
  setMode,
  toast,
  updateSettings,
} from "./actions";
import { EFFORTS, MODELS } from "./models";
import { getState, setState, type DialogState } from "./store";
import type { PermissionMode } from "./types";

export interface SlashCommand {
  name: string;
  args?: string;
  description: string;
  // 会話が無いと意味がないもの
  needsChat?: boolean;
  kind: "builtin" | "skill" | "command";
}

// Claude Code と claude.ai の / コマンドのうち、このアプリで意味のあるもの
export const BUILTIN_COMMANDS: SlashCommand[] = [
  { name: "clear", description: "新しいチャットを始める", kind: "builtin" },
  { name: "compact", args: "[要約の指示]", description: "ここまでを要約して、続きを軽くする", needsChat: true, kind: "builtin" },
  { name: "btw", args: "<質問>", description: "会話に残さずに、ちょっとした質問をする", needsChat: true, kind: "builtin" },
  { name: "rewind", description: "前の発言まで巻き戻す (ファイルも戻せる)", needsChat: true, kind: "builtin" },
  { name: "export", description: "会話を Markdown に書き出す", needsChat: true, kind: "builtin" },
  { name: "research", args: "<調べたいこと>", description: "リサーチモードで、複数のエージェントに調べさせる", kind: "builtin" },
  { name: "plan", description: "計画モードを切り替える", kind: "builtin" },
  { name: "mode", args: "<default|acceptEdits|plan|bypass>", description: "権限モードを変える", kind: "builtin" },
  { name: "model", args: "<opus|fable|sonnet|haiku>", description: "モデルを変える", kind: "builtin" },
  { name: "effort", args: "<low|medium|high|xhigh|max>", description: "思考の深さを変える", kind: "builtin" },
  { name: "loop", args: "[間隔] <内容>", description: "一定の間隔で繰り返し頼む (7 日で終わる)", kind: "builtin" },
  { name: "tasks", description: "このチャットのサブエージェントとワークフロー", needsChat: true, kind: "builtin" },
  { name: "output-style", args: "[名前]", description: "回答のスタイルを変える", kind: "builtin" },
  { name: "memory", description: "メモリを見る、直す", kind: "builtin" },
  { name: "agents", description: "エージェントの設定", kind: "builtin" },
  { name: "schedule", description: "予定の一覧と登録", kind: "builtin" },
  { name: "skills", description: "Skills、自作コマンド、フック", kind: "builtin" },
  { name: "hooks", description: "フックの設定", kind: "builtin" },
  { name: "usage", description: "使用量と料金", kind: "builtin" },
  { name: "help", description: "コマンドの一覧", kind: "builtin" },
];

export function allCommands(): SlashCommand[] {
  const custom = getState().extensions.commands
    .filter((c) => !BUILTIN_COMMANDS.some((b) => b.name === c.name))
    .map((c): SlashCommand => ({ name: c.name, description: c.description, kind: c.kind, args: "[引数]" }));
  return [...BUILTIN_COMMANDS, ...custom];
}

export function parseCommand(text: string) {
  const match = /^\/([A-Za-z0-9_-]+)(?:\s+([\s\S]*))?$/.exec(text.trim());
  return match ? { name: match[1].toLowerCase(), args: (match[2] ?? "").trim() } : null;
}

const openDialog = (dialog: DialogState) => setState((s) => ({ ...s, dialog }));

const MODES: PermissionMode[] = ["default", "acceptEdits", "plan", "bypass"];

// 「5m」「2h」「30」(分) を分に直す
function parseInterval(token: string) {
  const match = /^(\d+)\s*(m|min|分|h|hr|時間|d|日)?$/i.exec(token);
  if (!match) return null;
  const value = Number(match[1]);
  const unit = (match[2] ?? "m").toLowerCase();
  if (unit.startsWith("h") || unit === "時間") return value * 60;
  if (unit === "d" || unit === "日") return value * 1440;
  return value;
}

// Claude Code の /loop は間隔を省くと 10 分ごと
const DEFAULT_LOOP_MINUTES = 10;
// 予定の側の上限と同じ
const MIN_LOOP_MINUTES = 5;
const MAX_LOOP_MINUTES = 7 * 24 * 60;

// null はコマンドではないので普通に送る。keep は失敗したので入力を残す
export type CommandOutcome = "done" | "keep" | null;

export async function runCommand(text: string, conversationId: string | null): Promise<CommandOutcome> {
  const parsed = parseCommand(text);
  if (!parsed) return null;
  const { name, args } = parsed;
  // 自作コマンドの名前は大文字も使える
  const command = allCommands().find((c) => c.name.toLowerCase() === name);
  if (!command) return null;
  if (command.needsChat && !conversationId) {
    toast(`/${name} はチャットの中で使ってください`, "error");
    return "keep";
  }
  const conversation = conversationId ? getState().conversations[conversationId] : null;

  switch (name) {
    case "clear":
      navigate({ name: "new", folderId: conversation?.folderId ?? undefined });
      return "done";
    case "compact":
      await compact(conversationId!, args || undefined);
      return "done";
    case "btw":
      if (!args) {
        toast("/btw の後に質問を書いてください", "error");
        return "keep";
      }
      openDialog({ kind: "btw", conversationId: conversationId!, question: args });
      return "done";
    case "rewind":
      openDialog({ kind: "rewind", conversationId: conversationId! });
      return "done";
    case "export":
      await exportConversation(conversationId!);
      return "done";
    case "research":
      if (!args) {
        setChoice({ research: true });
        toast("リサーチモードにしました。調べたいことを送ってください");
        return "done";
      }
      return (await send(conversationId, args, { research: true })) ? "done" : "keep";
    case "plan": {
      const current = conversation?.permissionMode ?? getState().choice.permissionMode;
      await setMode(conversationId, current === "plan" ? "default" : "plan");
      toast(current === "plan" ? "計画モードを終えました" : "計画モードにしました。Claude は調べて計画を出すだけで、変更はしません");
      return "done";
    }
    case "mode": {
      const mode = MODES.find((m) => m.toLowerCase() === args.toLowerCase());
      if (!mode) {
        toast(`モードは ${MODES.join(" / ")} のどれかです`, "error");
        return "keep";
      }
      await setMode(conversationId, mode);
      return "done";
    }
    case "model": {
      const model = MODELS.find((m) => m.id === args || m.label.toLowerCase().startsWith(args.toLowerCase()) || m.id.includes(args.toLowerCase()));
      if (!args || !model) {
        toast(`モデルは ${MODELS.map((m) => m.label).join(" / ")} から選んでください`, "error");
        return "keep";
      }
      setChoice({ model: model.id });
      toast(`${model.label} にしました`);
      return "done";
    }
    case "effort": {
      const effort = EFFORTS.find((e) => e.id === args.toLowerCase());
      if (!effort) {
        toast(`思考の深さは ${EFFORTS.map((e) => e.id).join(" / ")} のどれかです`, "error");
        return "keep";
      }
      setChoice({ effort: effort.id });
      toast(`思考の深さを「${effort.label}」にしました`);
      return "done";
    }
    case "loop": {
      const [first, ...rest] = args.split(/\s+/);
      const interval = first ? parseInterval(first) : null;
      let prompt = (interval === null ? args : rest.join(" ")).trim();
      if (!prompt) {
        toast("/loop 10m <繰り返し頼むこと> の形で書いてください", "error");
        return "keep";
      }
      const minutes = interval ?? DEFAULT_LOOP_MINUTES;
      // 1 回目だけ送られて予定の登録で断られる、とならないよう先に確かめる
      if (minutes < MIN_LOOP_MINUTES || minutes > MAX_LOOP_MINUTES) {
        toast(`間隔は ${MIN_LOOP_MINUTES} 分から 7 日の間にしてください`, "error");
        return "keep";
      }
      // /loop 10m /自作コマンド のように、繰り返す中身がコマンドでもよい
      const typed = prompt;
      const inner = parseCommand(prompt);
      if (inner && !BUILTIN_COMMANDS.some((b) => b.name === inner.name)) {
        const expanded = await expandCommand(inner.name, inner.args);
        if (expanded === undefined) return "keep";
        prompt = expanded;
      }
      if (!(await send(conversationId, prompt))) return "keep";
      const view = getState().view;
      const targetId = conversationId ?? (view.name === "chat" ? view.id : null);
      if (!targetId) return "done";
      const schedules = await saveSchedule({ title: typed.slice(0, 30), prompt, intervalMinutes: minutes, conversationId: targetId }, true);
      if (schedules) toast(`${minutes} 分ごとに繰り返します。7 日たつか、設定の予定から消すと止まります`);
      return "done";
    }
    case "tasks":
      openDialog({ kind: "tasks", conversationId: conversationId! });
      return "done";
    case "output-style": {
      const styles = getState().extensions.outputStyles;
      const style = styles.find((s) => s.id === args || s.label === args);
      if (!style) {
        toast(`スタイルは ${styles.map((s) => s.id).join(" / ")} から選んでください`, args ? "error" : "info");
        return "done";
      }
      if (await updateSettings({ chat: { outputStyle: style.id } })) toast(`回答のスタイルを「${style.label}」にしました`);
      return "done";
    }
    case "memory":
      openSettings("features");
      return "done";
    case "agents":
      openSettings("agents");
      return "done";
    case "schedule":
      openSettings("schedules");
      return "done";
    case "skills":
    case "hooks":
      openSettings("extensions");
      return "done";
    case "usage":
      openSettings("usage");
      return "done";
    case "help":
      openDialog({ kind: "help" });
      return "done";
  }

  // 自作コマンドと Skills は本文に展開して送る
  const expanded = await expandCommand(command.name, args);
  if (expanded === undefined) return "keep";
  return (await send(conversationId, expanded)) ? "done" : "keep";
}
