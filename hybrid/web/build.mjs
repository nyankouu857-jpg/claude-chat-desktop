import { context, build } from "esbuild";
import { cp, mkdir, rm } from "node:fs/promises";

const watch = process.argv.includes("--watch");
await rm("dist", { recursive: true, force: true });
await mkdir("dist", { recursive: true });
await cp("index.html", "dist/index.html");

const options = {
  entryPoints: ["src/main.tsx"],
  bundle: true,
  outdir: "dist",
  entryNames: "app",
  format: "iife",
  target: "chrome120",
  jsx: "automatic",
  minify: !watch,
  sourcemap: watch ? "inline" : false,
  // KaTeX のフォントも dist に出して、exe に埋め込めるようにする
  loader: { ".woff2": "file", ".woff": "file", ".ttf": "file" },
  assetNames: "fonts/[name]",
  logLevel: "info",
};

if (watch) {
  const ctx = await context(options);
  await ctx.watch();
} else {
  await build(options);
}
