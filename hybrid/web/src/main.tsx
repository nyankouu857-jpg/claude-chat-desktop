import "katex/dist/katex.min.css";
import "./styles.css";
import { createRoot } from "react-dom/client";
import { init, toast } from "./actions";
import { App } from "./components/App";
import { QuickEntry } from "./components/QuickEntry";

const quick = new URLSearchParams(location.search).has("quick");
createRoot(document.getElementById("root")!).render(quick ? <QuickEntry /> : <App />);
init().catch((e) => toast(`起動に失敗しました: ${e instanceof Error ? e.message : e}`, "error"));
