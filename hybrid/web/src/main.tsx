import "katex/dist/katex.min.css";
import "./styles.css";
import { createRoot } from "react-dom/client";
import { QUICK_ENTRY, init, toast } from "./actions";
import { App } from "./components/App";
import { QuickEntry } from "./components/QuickEntry";

createRoot(document.getElementById("root")!).render(QUICK_ENTRY ? <QuickEntry /> : <App />);
init().catch((e) => toast(`起動に失敗しました: ${e instanceof Error ? e.message : e}`, "error"));
