namespace ClaudeChat.Core;

// いつもの Chrome に入れてもらう拡張機能。アプリの中に持ち、設定画面のボタンで書き出す
public static class ChromeExtension
{
    // 拡張機能の ID を BrowserBridge.ExtensionId に固定するための公開鍵。秘密鍵は持たない (パッケージ化しない読み込みには要らない)
    const string PublicKey = "MIIBIjANBgkqhkiG9w0BAQEFAAOCAQ8AMIIBCgKCAQEAk9AmnF0uk+Cdz8p9WG7nrdKlFCoobJp9LHtmbdB86CRDNllAVy1+DxT6/9D1mqcmFIDijIAPeX/PFK3ZwwAMYXguF9ChyrlzNUxADUkpHRXAy0dsFTfKhOf9QlzKhKNOPegy2IZWAf9ufgkIuWFTePFNGNYRprPw6Aaoay+6u9JoTPo5S9tQiHliJsAM4gMUmWDGZtDLPbPQIwnWQD/lAJ76f5XAP97cjwLewaieRjZFQn6niAcZcr9OAvW0rCPMQ9MaKzSZin9r2DY4LaINrE3uMvPGec9iT3w05brN6X5nAqfkxgIX917GgVZ6Rpdqq/uUudwfRiGGHGFtsL/+EwIDAQAB";

    const string Manifest = """
        {
          "manifest_version": 3,
          "key": "__KEY__",
          "name": "ClaudeChat 連携",
          "version": "1.3.0",
          "description": "ClaudeChat のブラウザ操作で、このブラウザを使えるようにします。",
          "permissions": ["debugger", "tabs", "tabGroups", "storage", "alarms"],
          "background": { "service_worker": "background.js" },
          "action": { "default_popup": "popup.html", "default_title": "ClaudeChat 連携" }
        }
        """;

    const string Background = """
        const PORT = __PORT__;
        let socket = null;
        let connecting = false;
        // Claude がクリックやキー入力を送った時刻。この直後に開いたタブだけを Claude のものにする
        let lastInputAt = 0;

        // ClaudeChat が開いたタブだけを動かす。ユーザーが自分で開いているタブ (メールや銀行など) には触れない
        const ownedTabs = async () => (await chrome.storage.session.get("tabs")).tabs ?? [];
        const setOwnedTabs = (tabs) => chrome.storage.session.set({ tabs });

        async function proof(token, text) {
          const key = await crypto.subtle.importKey("raw", new TextEncoder().encode(token), { name: "HMAC", hash: "SHA-256" }, false, ["sign"]);
          const signature = await crypto.subtle.sign("HMAC", key, new TextEncoder().encode(text));
          return [...new Uint8Array(signature)].map((b) => b.toString(16).padStart(2, "0")).join("");
        }

        async function setStatus(status) {
          await chrome.storage.session.set({ status });
          await chrome.action.setBadgeText({ text: status === "connected" ? "" : "!" });
        }

        const send = (message) => socket?.readyState === WebSocket.OPEN && socket.send(JSON.stringify(message));

        async function connect() {
          if (connecting || socket?.readyState === WebSocket.OPEN) return;
          const { token } = await chrome.storage.local.get("token");
          if (!token) return setStatus("no-token");
          connecting = true;
          const ws = new WebSocket(`ws://127.0.0.1:${PORT}/`);
          const nonce = crypto.randomUUID();
          let verified = false;
          ws.onopen = () => ws.send(JSON.stringify({ type: "hello", nonce, version: chrome.runtime.getManifest().version }));
          ws.onmessage = async (e) => {
            const message = JSON.parse(e.data);
            if (!verified) {
              // 接続コードを知らない相手には従わない。ほかのプログラムがこの番号で待ち受けていても乗っ取られないように
              const bound = `${nonce}:${message.nonce}`;
              if (message.type !== "welcome" || typeof message.nonce !== "string" || message.proof !== await proof(token, "app:" + bound)) {
                await setStatus("bad-token");
                ws.close();
                return;
              }
              verified = true;
              socket = ws;
              connecting = false;
              ws.send(JSON.stringify({ type: "ready", proof: await proof(token, "ext:" + bound) }));
              await setStatus("connected");
              return;
            }
            if (message.type === "ping") return send({ type: "pong" });
            handle(message);
          };
          ws.onclose = async () => {
            connecting = false;
            if (socket === ws) socket = null;
            if (verified) await setStatus("disconnected");
            else if ((await chrome.storage.session.get("status")).status !== "bad-token") await setStatus("waiting");
          };
        }

        // アプリのツールが使う命令だけを通す。万一つないだ相手が偽物でも、全サイトの Cookie を抜くような命令は通さない
        const ALLOWED_METHODS = new Set([
          "Page.enable", "Page.navigate", "Page.captureScreenshot", "Page.handleJavaScriptDialog",
          "Runtime.evaluate", "Input.dispatchMouseEvent", "Input.dispatchKeyEvent", "Input.insertText",
        ]);

        async function requireOwned(tabId) {
          if (!(await ownedTabs()).includes(tabId)) throw new Error("ClaudeChat が開いたタブではないので操作できません");
        }

        async function addToGroup(tabId) {
          const { groupId } = await chrome.storage.session.get("groupId");
          try {
            // groupId を省くと題の無いグループができてしまう
            if (groupId === undefined) throw new Error("no group");
            await chrome.tabs.group({ tabIds: tabId, groupId });
          } catch {
            const created = await chrome.tabs.group({ tabIds: tabId });
            await chrome.tabGroups.update(created, { title: "ClaudeChat", color: "orange" });
            await chrome.storage.session.set({ groupId: created });
          }
        }

        const commands = {
          async listTabs() {
            const tabs = (await Promise.all((await ownedTabs()).map((id) => chrome.tabs.get(id).catch(() => null)))).filter(Boolean);
            await setOwnedTabs(tabs.map((t) => t.id));
            return tabs.map((t) => ({ id: String(t.id), title: t.title ?? "", url: t.url ?? "" }));
          },
          async newTab() {
            const tab = await chrome.tabs.create({ url: "about:blank", active: true });
            await setOwnedTabs([...(await ownedTabs()), tab.id]);
            await addToGroup(tab.id).catch(() => {});
            return { id: String(tab.id) };
          },
          async attach({ tabId }) {
            const id = Number(tabId);
            await requireOwned(id);
            const tab = await chrome.tabs.update(id, { active: true });
            await chrome.windows.update(tab.windowId, { focused: true });
            // 自分が付けたままなら、そのまま使う。ほかの拡張機能や DevTools が付けているタブは横取りしない
            const targets = await chrome.debugger.getTargets();
            if (!targets.some((t) => t.tabId === id && t.attached && t.extensionId === chrome.runtime.id)) await chrome.debugger.attach({ tabId: id }, "1.3");
            return {};
          },
          async detach({ tabId }) {
            await chrome.debugger.detach({ tabId: Number(tabId) }).catch(() => {});
            return {};
          },
          async send({ tabId, method, params }) {
            const id = Number(tabId);
            await requireOwned(id);
            if (!ALLOWED_METHODS.has(method)) throw new Error(`${method} は使えない命令です`);
            if (method.startsWith("Input.") || method === "Runtime.evaluate") lastInputAt = Date.now();
            // chrome:// や file:// を開かせない
            if (method === "Page.navigate" && !/^https?:\/\//i.test(params?.url ?? "") && params?.url !== "about:blank") throw new Error("開けるのは http と https のページだけです");
            return (await chrome.debugger.sendCommand({ tabId: id }, method, params ?? {})) ?? {};
          },
        };

        async function handle(message) {
          try {
            const command = commands[message.cmd];
            if (!command) throw new Error(`${message.cmd} は知らない命令です`);
            send({ id: message.id, result: await command(message) });
          } catch (e) {
            send({ id: message.id, error: String(e?.message ?? e) });
          }
        }

        chrome.debugger.onEvent.addListener((source, method, params) => {
          if (method === "Page.javascriptDialogOpening") send({ event: "dialog", tabId: String(source.tabId), params });
        });
        chrome.debugger.onDetach.addListener(async (source, reason) => {
          // デバッグの帯で「キャンセル」を押されたら、そのタブには二度と付けない
          if (reason === "canceled_by_user") await setOwnedTabs((await ownedTabs()).filter((id) => id !== source.tabId));
          send({ event: "detached", tabId: String(source.tabId) });
        });
        chrome.tabs.onRemoved.addListener(async (tabId) => setOwnedTabs((await ownedTabs()).filter((id) => id !== tabId)));
        // Claude のクリックで開いた新しいタブも Claude のものにする。
        // ユーザーが Claude のタブから Ctrl+クリックなどで開いたタブも opener が付くので、Claude の操作の直後に限る
        chrome.tabs.onCreated.addListener(async (tab) => {
          const owned = await ownedTabs();
          if (tab.openerTabId === undefined || !owned.includes(tab.openerTabId) || owned.includes(tab.id)) return;
          // クリックからタブが開くまでの猶予
          if (Date.now() - lastInputAt > 3000) return;
          await setOwnedTabs([...owned, tab.id]);
          await addToGroup(tab.id).catch(() => {});
        });
        chrome.storage.onChanged.addListener((changes, area) => {
          if (area !== "local" || !changes.token) return;
          socket?.close();
          connect();
        });
        // アプリが後から起動しても、しばらくするとつながるように
        chrome.alarms.create("reconnect", { periodInMinutes: 0.5 });
        chrome.alarms.onAlarm.addListener(connect);
        chrome.runtime.onStartup.addListener(connect);
        chrome.runtime.onInstalled.addListener(connect);
        connect();
        """;

    const string Popup = """
        <!doctype html>
        <meta charset="utf-8">
        <style>
          body { width: 280px; margin: 0; padding: 14px; font: 13px/1.5 "Segoe UI", "Yu Gothic UI", sans-serif; color: #141413; }
          h1 { font-size: 15px; margin: 0 0 8px; }
          #status { margin: 0 0 12px; }
          #status.connected { color: #2f7d4f; }
          #status.bad { color: #b53333; }
          label { display: block; margin-bottom: 4px; color: #73726c; }
          input { box-sizing: border-box; width: 100%; padding: 6px 8px; border: 1px solid #ccc; border-radius: 6px; font-family: Consolas, monospace; }
          button { margin-top: 8px; width: 100%; padding: 7px; border: 0; border-radius: 6px; background: #c96442; color: #fff; cursor: pointer; }
          p.hint { color: #73726c; font-size: 12px; margin: 10px 0 0; }
        </style>
        <h1>ClaudeChat 連携</h1>
        <p id="status"></p>
        <label for="token">接続コード</label>
        <input id="token" type="password" autocomplete="off" spellcheck="false">
        <button id="save" type="button">保存してつなぐ</button>
        <p class="hint">接続コードは ClaudeChat の設定の「PC 操作」にあります。操作するのは ClaudeChat が開いた「ClaudeChat」グループのタブだけです。</p>
        <script src="popup.js"></script>
        """;

    const string PopupScript = """
        const LABELS = {
          connected: ["ClaudeChat とつながっています", "connected"],
          disconnected: ["ClaudeChat との接続が切れました。アプリが起動していれば、30 秒ほどでつなぎ直します", ""],
          waiting: ["ClaudeChat を待っています。アプリを起動して、設定でいつもの Chrome を選んでください", ""],
          "bad-token": ["接続コードが違います。アプリの設定からコピーし直してください", "bad"],
          "no-token": ["接続コードを入れてください", ""],
        };
        const status = document.getElementById("status");
        async function render() {
          const value = (await chrome.storage.session.get("status")).status ?? "waiting";
          const [text, kind] = LABELS[value] ?? LABELS.waiting;
          status.textContent = text;
          status.className = kind;
        }
        document.getElementById("save").addEventListener("click", async () => {
          const token = document.getElementById("token").value.trim();
          if (!token) return;
          await chrome.storage.session.set({ status: "waiting" });
          await chrome.storage.local.set({ token });
          document.getElementById("token").value = "";
          render();
        });
        chrome.storage.onChanged.addListener(render);
        render();
        """;

    public static void Write(string dir)
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "manifest.json"), Manifest.Replace("__KEY__", PublicKey));
        File.WriteAllText(Path.Combine(dir, "background.js"), Background.Replace("__PORT__", BrowserBridge.Port.ToString()));
        File.WriteAllText(Path.Combine(dir, "popup.html"), Popup);
        File.WriteAllText(Path.Combine(dir, "popup.js"), PopupScript);
    }
}
