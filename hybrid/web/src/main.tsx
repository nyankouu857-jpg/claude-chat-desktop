import "katex/dist/katex.min.css";
import "./styles.css";
import { createRoot } from "react-dom/client";
import { init, toast } from "./actions";
import { App } from "./components/App";

createRoot(document.getElementById("root")!).render(<App />);
init().catch((e) => toast(`起動に失敗しました: ${e instanceof Error ? e.message : e}`, "error"));
