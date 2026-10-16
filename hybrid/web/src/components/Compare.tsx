import { Check, LoaderCircle } from "lucide-react";
import { useEffect } from "react";
import { archiveConversation, navigate, stop } from "../actions";
import { call } from "../bridge";
import { modelLabel } from "../models";
import { getState, setState, useStore } from "../store";
import type { Conversation } from "../types";
import { MessageList } from "./Messages";
import { usd } from "./ui";

function CompareColumn({ id, others }: { id: string; others: string[] }) {
  const conversation = useStore((s) => s.conversations[id]);
  const live = useStore((s) => s.live[id]);
  return (
    <section className="compare-column">
      <header className="compare-header">
        <span className="compare-model">{modelLabel(conversation?.model)}</span>
        {live && <LoaderCircle size={14} className="spin" />}
        {!!conversation?.usage.costUsd && <span className="cost-chip">{usd(conversation.usage.costUsd)}</span>}
        <span className="spacer" />
        <button
          type="button"
          className="button small primary"
          disabled={!!live}
          onClick={async () => {
            // 選ばなかった方は消さずにアーカイブして、後から見返せるようにする
            for (const other of others) {
              await stop(other);
              await archiveConversation(other, true);
            }
            navigate({ name: "chat", id });
          }}
        >
          <Check size={14} /> これで続ける
        </button>
      </header>
      <div className="compare-scroll">
        <MessageList conversationId={id} />
      </div>
    </section>
  );
}

// 同じ発言を複数のモデルに送った結果を並べる。続けるチャットを 1 つ選ぶ
export function CompareView({ ids }: { ids: string[] }) {
  useEffect(() => {
    for (const id of ids) {
      void (async () => {
        if (getState().conversations[id]) return;
        try {
          const conversation = await call<Conversation | null>("conversations.get", { id });
          if (conversation) setState((s) => ({ ...s, conversations: { ...s.conversations, [id]: conversation } }));
        } catch {
          // 消された会話は列が空のまま残る。続けるには残った方を選べばよい
        }
      })();
    }
  }, [ids]);
  return (
    <div className="compare-view">
      <div className="compare-title">モデルを比べています。続けたい方の「これで続ける」を押してください</div>
      <div className="compare-columns" style={{ gridTemplateColumns: `repeat(${ids.length}, minmax(0, 1fr))` }}>
        {ids.map((id) => <CompareColumn key={id} id={id} others={ids.filter((o) => o !== id)} />)}
      </div>
    </div>
  );
}
