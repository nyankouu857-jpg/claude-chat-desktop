using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeChat.Core;

// アーティファクトを iframe に出すページを組み立てる。外から読めるのは CSP で cdnjs だけ
public static partial class ArtifactPages
{
    const string Cdn = "https://cdnjs.cloudflare.com/ajax/libs/";

    // claude.ai の React アーティファクトで使えるライブラリのうち cdnjs にあるもの。import されたものだけ読み込む
    static readonly (string Module, string Global, string[] Scripts)[] Libraries =
    [
        ("recharts", "Recharts", ["prop-types/15.8.1/prop-types.min.js", "recharts/2.12.7/Recharts.min.js"]),
        ("lodash", "_", ["lodash.js/4.17.21/lodash.min.js"]),
        ("papaparse", "Papa", ["PapaParse/5.4.1/papaparse.min.js"]),
        ("mathjs", "math", ["mathjs/11.11.0/math.min.js"]),
        ("d3", "d3", ["d3/7.8.5/d3.min.js"]),
        ("three", "THREE", ["three.js/r128/three.min.js"]),
        ("chart.js", "Chart", ["Chart.js/4.4.1/chart.umd.min.js"]),
    ];

    // 中で起きたエラーを画面へ知らせる。「修正を試す」に使う。
    // WebRTC は CSP の connect-src で止まらず、会話の中身を外へ送り出せるので、ページのスクリプトより先に取り上げる
    const string ErrorReporter = """
        <script>
        for (const name of ["RTCPeerConnection", "webkitRTCPeerConnection", "RTCDataChannel", "RTCSessionDescription", "RTCIceCandidate"]) {
          Object.defineProperty(window, name, { value: undefined, writable: false, configurable: false });
        }
        window.__reportError = (message) => parent.postMessage({ type: "artifact-error", message: String(message) }, "*");
        window.addEventListener("error", (e) => window.__reportError(e.error?.stack || e.message));
        window.addEventListener("unhandledrejection", (e) => window.__reportError(e.reason?.stack || e.reason));
        </script>
        """;

    public static string? Render(Artifact artifact, string content) => artifact.Type switch
    {
        "html" => WithErrorReporter(content),
        "react" => React(content),
        "svg" => $"<!doctype html><meta charset=\"utf-8\">{ErrorReporter}<style>body{{margin:0;display:flex;justify-content:center;background:#fff}}svg{{max-width:100%;height:auto}}</style>{content}",
        "mermaid" => $$"""
            <!doctype html><meta charset="utf-8">
            <style>body{margin:16px;background:#fff;font-family:sans-serif}</style>
            {{ErrorReporter}}
            <pre class="mermaid">{{WebUtility.HtmlEncode(content)}}</pre>
            <script crossorigin src="{{Cdn}}mermaid/10.9.1/mermaid.min.js"></script>
            <script>mermaid.initialize({ startOnLoad: true, securityLevel: "strict" })</script>
            """,
        _ => null,
    };

    // ページのどのスクリプトよりも先に動かす。doctype より前に置くと互換モードになるので、その直後に差し込む
    static string WithErrorReporter(string html)
    {
        var doctype = Doctype().Match(html);
        return doctype.Success && doctype.Index == html.Length - html.TrimStart().Length
            ? html.Insert(doctype.Index + doctype.Length, ErrorReporter)
            : ErrorReporter + html;
    }

    static string React(string code)
    {
        var imported = ImportFrom().Matches(code).Select(m => m.Groups[1].Value).ToHashSet();
        var scripts = new StringBuilder();
        var modules = new StringBuilder();
        foreach (var (module, global, files) in Libraries.Where(l => imported.Contains(l.Module)))
        {
            foreach (var file in files) scripts.Append($"<script crossorigin src=\"{Cdn}{file}\"></script>\n");
            modules.Append($"\"{module}\": window.{global},\n");
        }
        // コードは JSON 文字列として埋め込む。</script> が中にあってもタグが閉じないよう < を逃がす
        var source = System.Text.Json.JsonSerializer.Serialize(code).Replace("<", "\\u003c");
        // script の crossorigin が無いと、ライブラリの中で起きたエラーが「Script error.」としか届かない
        return $$"""
            <!doctype html>
            <html><head><meta charset="utf-8">
            {{ErrorReporter}}
            <link rel="stylesheet" href="{{Cdn}}tailwindcss/2.2.19/tailwind.min.css">
            <style>body{margin:0;background:#fff}#error{white-space:pre-wrap;color:#b53333;font:13px/1.5 monospace;padding:16px}</style>
            <script crossorigin src="{{Cdn}}react/18.2.0/umd/react.production.min.js"></script>
            <script crossorigin src="{{Cdn}}react-dom/18.2.0/umd/react-dom.production.min.js"></script>
            <script crossorigin src="{{Cdn}}babel-standalone/7.23.5/babel.min.js"></script>
            {{scripts}}
            </head><body><div id="root"></div>
            <script>
            (() => {
              const showError = (message) => {
                document.getElementById("root").innerHTML = "";
                const box = document.createElement("div");
                box.id = "error";
                box.textContent = message;
                document.body.appendChild(box);
                window.__reportError(message);
              };
              // React 18 は描画中のエラーを非同期で投げるので、try では拾えない
              window.addEventListener("error", (e) => showError(e.error?.stack || e.message));
              // lucide-react は cdnjs に無いので、名前だけ受け取る空のアイコンで代わりにする
              const icon = (name) => (props) => React.createElement("svg", { width: props.size ?? 24, height: props.size ?? 24, viewBox: "0 0 24 24", fill: "none", stroke: "currentColor", strokeWidth: 2, className: props.className, "aria-label": name }, React.createElement("rect", { x: 4, y: 4, width: 16, height: 16, rx: 3 }));
              const modules = {
                "react": React,
                "react-dom": ReactDOM,
                "react-dom/client": ReactDOM,
                "lucide-react": new Proxy({}, { get: (_, name) => (name === "__esModule" ? false : icon(String(name))) }),
                {{modules}}
              };
              window.__require = (name) => {
                if (name in modules && modules[name]) return modules[name];
                throw new Error(`${name} はこのアプリのアーティファクトでは使えません`);
              };
              window.__run = (factory) => {
                const module = { exports: {} };
                try {
                  factory(window.__require, module, module.exports);
                  const App = module.exports.default ?? Object.values(module.exports).find((v) => typeof v === "function");
                  if (!App) throw new Error("export default のコンポーネントがありません");
                  ReactDOM.createRoot(document.getElementById("root")).render(React.createElement(App));
                } catch (e) {
                  showError(e.stack || e.message);
                }
              };
              let compiled;
              try {
                compiled = Babel.transform({{source}}, { filename: "artifact.tsx", presets: ["react", "typescript"], plugins: ["transform-modules-commonjs"] }).code;
              } catch (e) {
                showError(e.message);
                return;
              }
              // eval を CSP で止めているので、変換した結果はインラインのスクリプトとして動かす
              const script = document.createElement("script");
              script.textContent = "window.__run((require, module, exports) => {\n" + compiled + "\n});";
              document.body.appendChild(script);
            })();
            </script>
            </body></html>
            """;
    }

    [GeneratedRegex(@"<!doctype[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex Doctype();

    [GeneratedRegex(@"from\s+[""']([^""']+)[""']")]
    private static partial Regex ImportFrom();
}
