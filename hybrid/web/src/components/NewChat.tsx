import { BookOpen, Code, Coffee, PenLine, Sparkles } from "lucide-react";
import { useState } from "react";
import { openSettings, updateSettings } from "../actions";
import { LANGUAGES } from "../languages";
import { useStore } from "../store";
import { Composer } from "./Composer";
import { Spark } from "./ui";

const SUGGESTIONS = [
  { icon: <PenLine size={15} />, label: "書く", text: "次の文章を、読みやすく書き直してください。\n\n" },
  { icon: <BookOpen size={15} />, label: "学ぶ", text: "初心者にも分かるように説明してください: " },
  { icon: <Code size={15} />, label: "コード", text: "次のコードをレビューして、改善点を教えてください。\n\n```\n\n```" },
  { icon: <Coffee size={15} />, label: "生活のこと", text: "今週の献立を考えるのを手伝ってください。条件は " },
  { icon: <Sparkles size={15} />, label: "Claude におまかせ", text: "今日の私に役立ちそうな、ちょっと面白い話を聞かせてください。" },
];

function greeting() {
  const hour = new Date().getHours();
  if (hour < 5) return "夜更かしですね";
  if (hour < 11) return "おはようございます";
  if (hour < 18) return "こんにちは";
  return "こんばんは";
}

// 先頭 5 つと「合わせる」だけ並べ、残りは設定で選ぶ
const FIRST_RUN_LANGUAGES = [...LANGUAGES.slice(0, 5), LANGUAGES[LANGUAGES.length - 1]];

function LanguagePicker() {
  return (
    <div className="language-picker">
      <div className="language-picker-title">Claude が答える言語を選んでください</div>
      <div className="language-picker-options">
        {FIRST_RUN_LANGUAGES.map((l) => (
          <button key={l.value} type="button" className="suggestion" onClick={() => void updateSettings({ profile: { language: l.value } })}>{l.label}</button>
        ))}
      </div>
      <button type="button" className="link-button" onClick={() => openSettings("general")}>ほかの言語は設定から選べます</button>
    </div>
  );
}

export function NewChat() {
  const profile = useStore((s) => s.settings?.profile);
  const name = profile?.nickname || profile?.fullName;
  const [prefill, setPrefill] = useState<{ text: string; nonce: number }>();
  return (
    <div className="new-chat-view">
      <div className="new-chat-center">
        <h1 className="greeting">
          <span className="greeting-spark"><Spark size={36} /></span>
          {greeting()}{name ? `、${name}さん` : ""}
        </h1>
        {profile && profile.language == null && <LanguagePicker />}
        <Composer conversationId={null} autoFocus prefill={prefill} />
        <div className="suggestions">
          {SUGGESTIONS.map((s) => (
            <button key={s.label} type="button" className="suggestion" onClick={() => setPrefill({ text: s.text, nonce: Date.now() })}>
              {s.icon}
              {s.label}
            </button>
          ))}
        </div>
      </div>
    </div>
  );
}
