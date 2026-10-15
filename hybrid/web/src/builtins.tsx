import { AppWindow, FolderOpen, MousePointerClick, SquareTerminal, type LucideIcon } from "lucide-react";
import type { Settings } from "./types";

// 組み込みツールの表示情報。ツール名の接頭辞が id
export interface BuiltinSource {
  id: string;
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
];

export const findBuiltin = (id: string) => BUILTIN_SOURCES.find((b) => b.id === id);
