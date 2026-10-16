import type { Effort } from "./types";

// defaultEffort は API の既定値。claude.ai でもモデルごとに「既定」が付く
export const MODELS: { id: string; label: string; description: string; defaultEffort: Effort }[] = [
  { id: "claude-opus-5-5", label: "Opus 5.5", description: "複雑な作業に強い標準モデル", defaultEffort: "medium" },
  { id: "claude-fable-5-1", label: "Fable 5.1", description: "最も賢いモデル。料金は高め", defaultEffort: "high" },
  { id: "claude-sonnet-5-5", label: "Sonnet 5.5", description: "速くて賢い。普段使い向け", defaultEffort: "high" },
  { id: "claude-haiku-5-5", label: "Haiku 5.5", description: "最速で最安。簡単な質問向け", defaultEffort: "medium" },
];

export const defaultEffortOf = (model: string): Effort => MODELS.find((m) => m.id === model)?.defaultEffort ?? "medium";

export const EFFORTS: { id: Effort; label: string; description: string }[] = [
  { id: "low", label: "低", description: "すばやく答える" },
  { id: "medium", label: "中", description: "普段使いの標準" },
  { id: "high", label: "高", description: "じっくり考える" },
  { id: "xhigh", label: "より高", description: "難しいコードや設計向け" },
  { id: "max", label: "最大", description: "時間と料金をかけても正確さ優先" },
];

export function modelLabel(id: string | undefined) {
  return MODELS.find((m) => m.id === id)?.label ?? id ?? "";
}
