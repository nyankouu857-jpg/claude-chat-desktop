import { build } from "esbuild";
import { cp, mkdir } from "node:fs/promises";

await mkdir("dist/renderer", { recursive: true });

// SDK も含めて全部束ねる。node_modules を exe に同梱しないため
const nodeSide = { bundle: true, platform: "node", format: "cjs", target: "node22", external: ["electron"] };

await Promise.all([
  build({ ...nodeSide, entryPoints: ["src/main/main.ts"], outfile: "dist/main.js" }),
  build({ ...nodeSide, entryPoints: ["src/preload.ts"], outfile: "dist/preload.js" }),
  build({
    entryPoints: ["src/renderer/app.ts"],
    bundle: true,
    platform: "browser",
    format: "iife",
    target: "chrome130",
    outfile: "dist/renderer/app.js",
  }),
]);

await Promise.all([
  cp("src/renderer/index.html", "dist/renderer/index.html"),
  cp("src/renderer/style.css", "dist/renderer/style.css"),
  cp("node_modules/highlight.js/styles/github.min.css", "dist/renderer/hljs-light.css"),
  cp("node_modules/highlight.js/styles/github-dark.min.css", "dist/renderer/hljs-dark.css"),
]);
