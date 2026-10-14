import { ChevronDown, Pencil, Star, Trash } from "lucide-react";
import { useEffect, useLayoutEffect, useRef, useState } from "react";
import { deleteConversation, starConversation } from "../actions";
import { useStore } from "../store";
import { Composer } from "./Composer";
import { MessageList } from "./Messages";
import { RenameDialog } from "./Sidebar";
import { Dropdown, MenuItem, tokens, usd } from "./ui";

export function ChatView({ id }: { id: string }) {
  const conversation = useStore((s) => s.conversations[id]);
  const live = useStore((s) => s.live[id]);
  const [renaming, setRenaming] = useState(false);
  const scroller = useRef<HTMLDivElement>(null);
  const pinned = useRef(true);

  useEffect(() => {
    pinned.current = true;
  }, [id]);

  // 読み返しているときに勝手に下へ飛ばないよう、最下部付近にいるときだけ追従する
  useLayoutEffect(() => {
    const el = scroller.current;
    if (el && pinned.current) el.scrollTop = el.scrollHeight;
  });

  const title = conversation?.title ?? "";
  return (
    <div className="chat-view">
      <header className="chat-header">
        {conversation && (
          <Dropdown
            trigger={(open, toggle) => (
              <button type="button" className={`title-button${open ? " open" : ""}`} onClick={toggle}>
                {conversation.starred && <Star size={14} className="star-filled" />}
                <span className="title-text">{title}</span>
                <ChevronDown size={14} />
              </button>
            )}
          >
            {(close) => (
              <>
                <MenuItem icon={<Star size={16} />} onClick={() => { close(); void starConversation(id, !conversation.starred); }}>
                  {conversation.starred ? "スターを外す" : "スターを付ける"}
                </MenuItem>
                <MenuItem icon={<Pencil size={16} />} onClick={() => { close(); setRenaming(true); }}>名前を変更</MenuItem>
                <MenuItem icon={<Trash size={16} />} danger onClick={() => {
                  close();
                  if (confirm(`${title} を削除しますか?`)) void deleteConversation(id);
                }}>削除</MenuItem>
              </>
            )}
          </Dropdown>
        )}
        <span className="spacer" />
        {conversation && conversation.usage.costUsd > 0 && (
          <span
            className="cost-chip"
            title={`入力 ${tokens(conversation.usage.input)} / 出力 ${tokens(conversation.usage.output)} / キャッシュ読み込み ${tokens(conversation.usage.cacheRead)} トークン`}
          >
            {usd(conversation.usage.costUsd)}
          </span>
        )}
      </header>
      <div
        className="chat-scroll"
        ref={scroller}
        onScroll={(e) => {
          const el = e.currentTarget;
          pinned.current = el.scrollHeight - el.scrollTop - el.clientHeight < 120;
        }}
      >
        <MessageList conversationId={id} />
      </div>
      <div className="composer-dock">
        <Composer conversationId={id} autoFocus={!live} />
      </div>
      {renaming && conversation && <RenameDialog summary={conversation} onClose={() => setRenaming(false)} />}
    </div>
  );
}
