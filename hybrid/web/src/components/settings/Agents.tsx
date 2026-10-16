import { Square } from "lucide-react";
import { navigate, stopWorkflow, updateSettings } from "../../actions";
import { useStore } from "../../store";
import type { Settings } from "../../types";
import { NumberField, Toggle, relativeTime } from "../ui";
import { Section } from "./Settings";

const WORKFLOW_STATUS = { running: "実行中", completed: "完了", failed: "失敗", stopped: "停止" };

export function Agents({ settings }: { settings: Settings }) {
  const agents = settings.agents;
  const workflows = Object.values(useStore((s) => s.workflows)).sort((a, b) => b.startedAt - a.startedAt);
  const summaries = useStore((s) => s.summaries);
  return (
    <>
      <Section title="エージェント" description="Claude Code のサブエージェントやワークフローと同じく、Claude が別の Claude に作業を任せたり、ほかのチャットとやり取りしたりできます。任された子は、動いている間は親のチャットの下に入れ子で並びます。">
        <div className="switch-row">
          <div>
            <div>エージェントを使う</div>
            <div className="field-hint">サブエージェント、ワークフロー、リサーチ、チャット同士の通信をまとめて切り替えます</div>
          </div>
          <Toggle checked={agents.enabled} onChange={(v) => void updateSettings({ agents: { enabled: v } })} label="エージェントを使う" />
        </div>
        <div className="switch-row">
          <div>
            <div>同時に動かせるエージェントの数</div>
            <div className="field-hint">料金がかさみすぎないよう、これを超える分は空くまで待たせます</div>
          </div>
          <NumberField value={agents.maxConcurrent} min={1} max={20} onCommit={(v) => void updateSettings({ agents: { maxConcurrent: v } })} />
        </div>
        <div className="switch-row">
          <div>
            <div>1 つのワークフローで使えるエージェントの数</div>
          </div>
          <NumberField value={agents.maxWorkflowAgents} min={1} max={100} onCommit={(v) => void updateSettings({ agents: { maxWorkflowAgents: v } })} />
        </div>
        <div className="switch-row">
          <div>
            <div>チャット同士のやり取りの上限</div>
            <div className="field-hint">Claude 同士で返事をし合って止まらなくなるのを防ぎます</div>
          </div>
          <NumberField value={agents.maxMessageHops} min={1} max={100} onCommit={(v) => void updateSettings({ agents: { maxMessageHops: v } })} />
        </div>
        <div className="switch-row">
          <div>
            <div>サブエージェントは確認なしで動かす</div>
            <div className="field-hint">親と話している間に子の確認待ちで止まらないよう、サブエージェントを始めるときに 1 回だけ確認し、そのあと子のツールは承認なしで動きます。子からユーザーへの質問もしません。「自動で判断」の会話の子は、作業中も確認役が見て、止めたものは人に聞かずに断ります。計画モードの制限とフックは子にも効きます</div>
          </div>
          <Toggle checked={agents.autoApproveChildren} onChange={(v) => void updateSettings({ agents: { autoApproveChildren: v } })} label="サブエージェントは確認なしで動かす" />
        </div>
        <div className="switch-row">
          <div>
            <div>終わったサブエージェントのチャットを消す</div>
            <div className="field-hint">結果は親に渡し終えているので、うまく終わった子のチャットは消します。ファイルの書き換えやコマンドなど、読むだけでないツールを使った子は、何をしたか確かめて巻き戻せるよう、消さずにアーカイブします。失敗や停止で終わった子は残します</div>
          </div>
          <Toggle checked={agents.deleteFinishedChildren} onChange={(v) => void updateSettings({ agents: { deleteFinishedChildren: v } })} label="終わったサブエージェントのチャットを消す" />
        </div>
      </Section>

      <Section title="チャット同士の通信" description="通信できる範囲はフォルダごとに決めます。サイドバーのフォルダのメニューから「指示と通信の設定」を開いてください。フォルダに入っていないチャットは、自分が始めたサブエージェントとだけやり取りできます。">
        {null}
      </Section>

      <Section title="ワークフロー" description="アプリを起動してから動かしたワークフローです。">
        {workflows.length === 0 ? <div className="empty-note">まだありません</div> : (
          <div className="list-card">
            {workflows.map((w) => {
              const parent = summaries.find((s) => s.id === w.parentId);
              return (
                <div key={w.id} className="list-row">
                  <span>
                    {w.title}
                    <span className="field-hint"> {relativeTime(w.startedAt)} ・ エージェント {w.agents.length} 個{parent ? ` ・ ${parent.title}` : ""}</span>
                  </span>
                  <span className={`tool-badge${w.status === "failed" ? " error" : ""}`}>{WORKFLOW_STATUS[w.status]}</span>
                  {parent && <button type="button" className="button small" onClick={() => navigate({ name: "chat", id: parent.id })}>チャット</button>}
                  {w.status === "running" && <button type="button" className="button small" onClick={() => void stopWorkflow(w.id)}><Square size={12} /> 止める</button>}
                </div>
              );
            })}
          </div>
        )}
      </Section>
    </>
  );
}
