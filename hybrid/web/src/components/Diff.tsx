// 書き換え前後を行で比べる。長すぎると表を作るのに時間がかかるので、その分だけ前後をまとめて出す
const MAX_DIFF_CELLS = 4_000_000;
// 変わっていない行は前後この行数だけ残す
const CONTEXT_LINES = 3;

type Line = { kind: "same" | "added" | "removed"; text: string };

function diffLines(before: string[], after: string[]): Line[] {
  if (before.length * after.length > MAX_DIFF_CELLS) {
    return [...before.map((text) => ({ kind: "removed" as const, text })), ...after.map((text) => ({ kind: "added" as const, text }))];
  }
  // 最長共通部分列の長さを後ろから埋める
  const lcs = Array.from({ length: before.length + 1 }, () => new Uint32Array(after.length + 1));
  for (let i = before.length - 1; i >= 0; i--) {
    for (let j = after.length - 1; j >= 0; j--) {
      lcs[i][j] = before[i] === after[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1]);
    }
  }
  const lines: Line[] = [];
  let i = 0;
  let j = 0;
  while (i < before.length && j < after.length) {
    if (before[i] === after[j]) {
      lines.push({ kind: "same", text: before[i] });
      i++;
      j++;
    } else if (lcs[i + 1][j] >= lcs[i][j + 1]) {
      lines.push({ kind: "removed", text: before[i++] });
    } else {
      lines.push({ kind: "added", text: after[j++] });
    }
  }
  while (i < before.length) lines.push({ kind: "removed", text: before[i++] });
  while (j < after.length) lines.push({ kind: "added", text: after[j++] });
  return lines;
}

// 変わった行の近くだけ残し、離れた変わらない行は「…」にまとめる
function trimContext(lines: Line[]): (Line | null)[] {
  const near = lines.map((_, index) => lines.slice(Math.max(0, index - CONTEXT_LINES), index + CONTEXT_LINES + 1).some((l) => l.kind !== "same"));
  const result: (Line | null)[] = [];
  lines.forEach((line, index) => {
    if (near[index]) result.push(line);
    else if (result.at(-1) !== null) result.push(null);
  });
  return result;
}

export function Diff({ before, after }: { before: string; after: string }) {
  const lines = trimContext(diffLines(before.split("\n"), after.split("\n")));
  return (
    <pre className="diff">
      {lines.map((line, index) => line === null
        ? <div key={index} className="diff-gap">…</div>
        : <div key={index} className={`diff-line ${line.kind}`}><span className="diff-mark">{line.kind === "added" ? "+" : line.kind === "removed" ? "-" : " "}</span>{line.text}</div>)}
    </pre>
  );
}

// ファイル操作のうち、中身を書き換えるものだけ差分で見せる
export function FileChangePreview({ tool, input }: { tool: string; input: Record<string, any> }) {
  if (tool === "edit_file" && typeof input.old_text === "string" && typeof input.new_text === "string") {
    return (
      <>
        <div className="diff-path">{input.path}{input.replace_all ? " (一致したところすべて)" : ""}</div>
        <Diff before={input.old_text} after={input.new_text} />
      </>
    );
  }
  if (tool === "write_file" && typeof input.content === "string") {
    return (
      <>
        <div className="diff-path">{input.path} (中身を丸ごと書き込みます)</div>
        <Diff before="" after={input.content} />
      </>
    );
  }
  return <pre>{JSON.stringify(input, null, 2)}</pre>;
}
