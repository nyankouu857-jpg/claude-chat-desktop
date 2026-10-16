import type { Effort } from "./types";

export interface ModelInfo {
  id: string;
  label: string;
  description?: string;
  defaultEffort: Effort;
  // xhigh は Opus 4.7 から。それより前のモデルでは選べない
  efforts?: Effort[];
}

const WITHOUT_XHIGH: Effort[] = ["low", "medium", "high", "max"];

// defaultEffort は API の既定値。claude.ai でもモデルごとに「既定」が付く
export const MODELS: ModelInfo[] = [
  { id: "claude-opus-5-5", label: "Opus 5.5", description: "複雑な作業に強い標準モデル", defaultEffort: "medium" },
  { id: "claude-fable-5-1", label: "Fable 5.1", description: "最も賢いモデル。料金は高め", defaultEffort: "high" },
  { id: "claude-sonnet-5-5", label: "Sonnet 5.5", description: "速くて賢い。普段使い向け", defaultEffort: "high" },
  { id: "claude-haiku-5-5", label: "Haiku 5.5", description: "最速で最安。簡単な質問向け", defaultEffort: "medium" },
];

// claude.ai の「他のモデル」と同じ並び。今も API で使える前の世代
export const OTHER_MODELS: ModelInfo[] = [
  // effort の代わりに、思考に回すトークン数を effort に合わせて決める
  { id: "claude-haiku-4-5", label: "Haiku 4.5", defaultEffort: "medium", efforts: WITHOUT_XHIGH },
  { id: "claude-sonnet-5", label: "Sonnet 5", defaultEffort: "high" },
  { id: "claude-opus-5", label: "Opus 5", defaultEffort: "high" },
  { id: "claude-fable-5", label: "Fable 5", defaultEffort: "high" },
  { id: "claude-opus-4-8", label: "Opus 4.8", defaultEffort: "high" },
  { id: "claude-opus-4-7", label: "Opus 4.7", defaultEffort: "high" },
  { id: "claude-opus-4-6", label: "Opus 4.6", defaultEffort: "high", efforts: WITHOUT_XHIGH },
  { id: "claude-sonnet-4-6", label: "Sonnet 4.6", defaultEffort: "high", efforts: WITHOUT_XHIGH },
];

export const ALL_MODELS = [...MODELS, ...OTHER_MODELS];

export const defaultEffortOf = (model: string): Effort => ALL_MODELS.find((m) => m.id === model)?.defaultEffort ?? "medium";

export const effortsOf = (model: string) => ALL_MODELS.find((m) => m.id === model)?.efforts;

export const EFFORTS: { id: Effort; label: string; description: string }[] = [
  { id: "low", label: "低", description: "すばやく答える" },
  { id: "medium", label: "中", description: "普段使いの標準" },
  { id: "high", label: "高", description: "じっくり考える" },
  { id: "xhigh", label: "より高", description: "難しいコードや設計向け" },
  { id: "max", label: "最大", description: "時間と料金をかけても正確さ優先" },
];

export function modelLabel(id: string | undefined) {
  return ALL_MODELS.find((m) => m.id === id)?.label ?? id ?? "";
}
