// WebView2 の中なら postMessage、ブラウザで開発しているときは開発ホストの WebSocket を使う
type Listener = (data: any) => void;

interface WebViewHost {
  postMessage(message: string): void;
  addEventListener(type: "message", listener: (e: { data: string }) => void): void;
}

const webview: WebViewHost | undefined = (window as any).chrome?.webview;
const pending = new Map<string, { resolve: (v: any) => void; reject: (e: Error) => void }>();
const listeners = new Map<string, Set<Listener>>();
// 返事はすべての窓に届くので、窓ごとに違う ID にして取り違えないようにする
const windowKey = crypto.randomUUID();
let nextId = 1;
let socket: WebSocket | null = null;
let ready: Promise<void>;

function receive(raw: string) {
  const message = JSON.parse(raw);
  if (message.event) {
    for (const listener of listeners.get(message.event) ?? []) listener(message.data);
    return;
  }
  const waiter = pending.get(message.id);
  if (!waiter) return;
  pending.delete(message.id);
  if (message.error) waiter.reject(new Error(message.error));
  else waiter.resolve(message.result);
}

if (webview) {
  webview.addEventListener("message", (e) => receive(e.data));
  ready = Promise.resolve();
} else {
  socket = new WebSocket(`ws://${location.host}/ws`);
  socket.addEventListener("message", (e) => receive(e.data));
  ready = new Promise((resolve) => socket!.addEventListener("open", () => resolve(), { once: true }));
}

export async function call<T = any>(method: string, params: Record<string, unknown> = {}): Promise<T> {
  await ready;
  const id = `${windowKey}-${nextId++}`;
  const payload = JSON.stringify({ id, method, params });
  return new Promise<T>((resolve, reject) => {
    pending.set(id, { resolve, reject });
    if (webview) webview.postMessage(payload);
    else socket!.send(payload);
  });
}

export function on(event: string, listener: Listener) {
  if (!listeners.has(event)) listeners.set(event, new Set());
  listeners.get(event)!.add(listener);
  return () => listeners.get(event)!.delete(listener);
}
