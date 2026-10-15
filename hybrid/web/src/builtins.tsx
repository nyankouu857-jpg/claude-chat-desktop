import { AppWindow, Bot, Brain, CalendarClock, FolderOpen, ListChecks, MessagesSquare, MousePointerClick, NotebookPen, PanelsTopLeft, SquareTerminal, type LucideIcon } from "lucide-react";
import type { Settings } from "./types";

// 組み込みツールの表示情報。ツール名の接頭辞が id
export interface BuiltinSource {
  id: string;
  // pc は PC を操作するもの、claude は Claude 自身の機能
  group: "pc" | "claude";
  label: string;
  icon: LucideIcon;
  isAvailable: (settings: Settings) => boolean;
  hint: (settings: Settings) => string;
  titles: Record<string, string>;
  subject: (tool: string, input: Record<string, any>) => string;
}

const shorten = (s: unknown, max = 80) => {
  const text = String(s ?? "").replace(/\s+/g, " ");
  return text.length > max ? `${text.slice(0, max)}...` : text;
};

export const BUILTIN_SOURCES: BuiltinSource[] = [
  {
    id: "files",
    group: "pc",
    label: "フォルダ",
    icon: FolderOpen,
    isAvailable: (s) => s.files.enabled && s.files.folders.length > 0,
    hint: (s) => `${s.files.folders.length} 個のフォルダを操作`,
    titles: {
      list_allowed_folders: "許可フォルダを確認",
      list_directory: "フォルダの中身を見る",
      read_file: "ファイルを読む",
      search_files: "ファイルを探す",
      get_file_info: "ファイルの情報",
      write_file: "ファイルに書き込む",
      edit_file: "ファイルを直す",
      create_directory: "フォルダを作る",
      move_path: "移動",
      copy_file: "コピー",
      delete_path: "ごみ箱へ移す",
    },
    subject: (_, input) => input.path ?? input.source ?? "",
  },
  {
    id: "terminal",
    group: "pc",
    label: "ターミナル",
    icon: SquareTerminal,
    isAvailable: (s) => s.terminal.enabled,
    hint: () => "コマンドを実行",
    titles: {
      run_command: "コマンドを実行",
      start_background: "バックグラウンドで実行",
      read_background: "バックグラウンドの出力を見る",
      stop_background: "バックグラウンドを止める",
    },
    subject: (_, input) => (input.command ? shorten(input.command) : input.id !== undefined ? `id ${input.id}` : ""),
  },
  {
    id: "programs",
    group: "pc",
    label: "アプリの起動",
    icon: AppWindow,
    isAvailable: (s) => s.programs.enabled,
    hint: () => "exe やファイルを開く",
    titles: {
      find_program: "アプリを探す",
      launch_program: "アプリやファイルを開く",
      list_windows: "開いているウィンドウを見る",
      close_window: "ウィンドウを閉じる",
    },
    subject: (_, input) => input.path ?? input.name ?? (input.process_id !== undefined ? `id ${input.process_id}` : ""),
  },
  {
    id: "browser",
    group: "pc",
    label: "ブラウザ操作",
    icon: MousePointerClick,
    isAvailable: (s) => s.browser.enabled,
    hint: () => "Chrome や Edge を動かす",
    titles: {
      open: "ページを開く",
      snapshot: "ページを読む",
      screenshot: "スクリーンショット",
      click: "クリック",
      type: "文字を入力",
      press_key: "キーを押す",
      scroll: "スクロール",
      back: "戻る",
      tabs: "タブ",
      evaluate: "JavaScript を実行",
    },
    subject: (tool, input) =>
      input.url ?? (input.ref !== undefined ? `要素 ${input.ref}${tool === "type" ? ` に「${shorten(input.text, 30)}」` : ""}` : input.key ?? input.direction ?? (input.expression ? shorten(input.expression) : "")),
  },
  {
    id: "agents",
    group: "claude",
    label: "エージェント",
    icon: Bot,
    isAvailable: (s) => s.agents.enabled,
    hint: () => "サブエージェント、ワークフロー、チャット同士の通信",
    titles: {
      spawn_agent: "サブエージェントに任せる",
      run_workflow: "ワークフローを実行",
      list_tasks: "タスク一覧",
      stop_task: "タスクを止める",
      list_chats: "チャットの一覧と状態",
      send_message: "ほかのチャットに送る",
    },
    subject: (_, input) => input.title ?? input.to ?? input.id ?? "",
  },
  {
    id: "chats",
    group: "claude",
    label: "過去のチャット",
    icon: MessagesSquare,
    isAvailable: (s) => s.features.chatSearch,
    hint: () => "以前の会話を探して読む",
    titles: { search: "過去のチャットを探す", read: "過去のチャットを読む" },
    subject: (_, input) => input.query ?? input.id ?? "",
  },
  {
    id: "memory",
    group: "claude",
    label: "メモリ",
    icon: Brain,
    isAvailable: (s) => s.features.memory,
    hint: () => "会話をまたいで覚えておく",
    titles: { "": "メモリ" },
    subject: (_, input) => [input.command, input.path ?? input.old_path].filter(Boolean).join(" "),
  },
  {
    id: "artifacts",
    group: "claude",
    label: "アーティファクト",
    icon: PanelsTopLeft,
    isAvailable: (s) => s.features.artifacts,
    hint: () => "文書やアプリを横に表示",
    titles: { create: "アーティファクトを作る", update: "アーティファクトを直す" },
    subject: (_, input) => input.title ?? input.id ?? "",
  },
  {
    id: "todo",
    group: "claude",
    label: "タスクリスト",
    icon: ListChecks,
    isAvailable: () => true,
    hint: () => "長い作業の進み具合を見せる",
    titles: { write: "タスクリストを更新" },
    subject: (_, input) => (Array.isArray(input.todos) ? `${input.todos.length} 件` : ""),
  },
  {
    id: "schedule",
    group: "claude",
    label: "予定",
    icon: CalendarClock,
    isAvailable: () => true,
    hint: () => "定期的に動く予定を登録",
    titles: { create: "予定を登録", list: "予定の一覧", delete: "予定を消す" },
    subject: (_, input) => input.title ?? input.id ?? "",
  },
  {
    id: "skills",
    group: "claude",
    label: "Skills",
    icon: NotebookPen,
    isAvailable: () => true,
    hint: () => "config/skills の手順書",
    titles: { use: "Skill を読み込む", read_file: "Skill のファイルを読む" },
    subject: (_, input) => [input.name, input.path].filter(Boolean).join(" / "),
  },
];

// 計画モードのときだけ出る。メニューには並べない
export const PLAN_SOURCE: BuiltinSource = {
  id: "plan",
  group: "claude",
  label: "計画モード",
  icon: ListChecks,
  isAvailable: () => false,
  hint: () => "",
  titles: { exit_plan_mode: "計画の承認" },
  subject: () => "",
};

export const findBuiltin = (id: string) => (id === PLAN_SOURCE.id ? PLAN_SOURCE : BUILTIN_SOURCES.find((b) => b.id === id));
