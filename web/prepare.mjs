import { spawnSync } from "node:child_process";
import { copyFileSync, cpSync, existsSync, mkdirSync, rmSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { prepareGodot } from "./prepare-godot.mjs";

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "..");
const runtimeCsproj = path.join(here, "runtime", "Harbor.Browser.csproj");
const publicDir = path.join(here, "public");
const frameworkOut = path.join(publicDir, "runtime", "_framework");
const fontsOut = path.join(publicDir, "fonts");

function resolveDotnet() {
  const candidates = [
    process.env.DOTNET,
    "/tmp/harbor-tools/dotnet/dotnet",
    "dotnet",
  ].filter(Boolean);

  for (const bin of candidates) {
    const check = spawnSync(bin, ["--version"], { encoding: "utf8" });
    if (check.status === 0) return bin;
  }
  throw new Error("Could not find .NET 8 SDK (`dotnet`) with `wasm-tools` workload.");
}

function buildDotnetWasm() {
  const dotnet = resolveDotnet();
  const dotnetRoot = path.dirname(dotnet);
  const env = { ...process.env };
  if (existsSync(path.join(dotnetRoot, "sdk"))) {
    env.DOTNET_ROOT = dotnetRoot;
  }

  console.log(`[harbor-web] Publishing Harbor.Core to browser-wasm using ${dotnet}...`);
  const res = spawnSync(dotnet, ["publish", runtimeCsproj, "-c", "Release", "--nologo"], {
    cwd: repoRoot,
    env,
    stdio: "inherit",
  });
  if (res.status !== 0) {
    throw new Error(`dotnet publish failed with exit code ${res.status}`);
  }

  const bundleFramework = path.join(
    here,
    "runtime",
    "bin",
    "Release",
    "net8.0",
    "browser-wasm",
    "AppBundle",
    "_framework"
  );
  if (!existsSync(bundleFramework)) {
    throw new Error(`Expected AppBundle/_framework at ${bundleFramework}`);
  }

  rmSync(frameworkOut, { recursive: true, force: true });
  cpSync(bundleFramework, frameworkOut, { recursive: true });
  console.log("[harbor-web] Copied .NET 8 browser-wasm _framework bundle (including supportFiles).");
}

function copyFonts() {
  mkdirSync(fontsOut, { recursive: true });
  for (const font of ["BarlowSemiCondensed-Bold.ttf", "IBMPlexMono-Regular.ttf"]) {
    const src = path.join(repoRoot, "game", "assets", "fonts", font);
    if (existsSync(src)) {
      copyFileSync(src, path.join(fontsOut, font));
    }
  }
}

buildDotnetWasm();
copyFonts();
prepareGodot();
