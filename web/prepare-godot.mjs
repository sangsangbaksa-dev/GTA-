import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import {
  copyFileSync,
  existsSync,
  mkdirSync,
  readdirSync,
  readFileSync,
  rmSync,
  statSync,
  writeFileSync,
} from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "..");
const gameDir = path.join(repoRoot, "game");
const engineDir = path.join(here, "engine");
const godotOutDir = path.join(here, "public", "godot");
const chunksOutDir = path.join(godotOutDir, "chunks");

function resolveGodotBinary() {
  const candidates = [
    process.env.GODOT,
    "/tmp/harbor-tools/godot-std/Godot_v4.4.1-stable_linux.x86_64",
    "godot4",
    "godot",
  ].filter(Boolean);

  for (const bin of candidates) {
    const check = spawnSync(bin, ["--version"], { encoding: "utf8" });
    if (check.status === 0 && !String(check.stdout).includes(".mono.")) {
      return bin;
    }
  }

  throw new Error(
    "Standard (non-.NET) Godot 4.4.1 binary is required for Web export. Set GODOT=/path/to/Godot_v4.4.1-stable_linux.x86_64."
  );
}

function copyTreeFiltered(srcDir, dstDir, exts) {
  if (!existsSync(srcDir)) return;
  mkdirSync(dstDir, { recursive: true });
  for (const entry of readdirSync(srcDir, { withFileTypes: true })) {
    const srcPath = path.join(srcDir, entry.name);
    const dstPath = path.join(dstDir, entry.name);
    if (entry.isDirectory()) {
      copyTreeFiltered(srcPath, dstPath, exts);
    } else if (exts.some((ext) => entry.name.toLowerCase().endsWith(ext))) {
      copyFileSync(srcPath, dstPath);
    }
  }
}

function stageHarborScenesAndAssets() {
  rmSync(path.join(engineDir, "assets"), { recursive: true, force: true });
  rmSync(path.join(engineDir, "scenes"), { recursive: true, force: true });

  const worldDst = path.join(engineDir, "scenes", "world");
  const chunksDst = path.join(worldDst, "chunks");
  mkdirSync(chunksDst, { recursive: true });

  for (const shader of ["HarborWater.gdshader", "HarborPaving.gdshader"]) {
    copyFileSync(
      path.join(gameDir, "scenes", "world", shader),
      path.join(worldDst, shader)
    );
  }

  const frontRaw = readFileSync(
    path.join(gameDir, "scenes", "world", "HarborFront.tscn"),
    "utf8"
  );
  const frontCleaned = frontRaw
    .replace(
      /^\[ext_resource type="Script" path="res:\/\/scripts\/Streaming\/ChunkStreamer\.cs" id="ChunkStreamer"\]\r?\n?/m,
      ""
    )
    .replace(
      /\[node name="ChunkStreamer" type="Node3D" parent="\."\]\r?\nscript = ExtResource\("ChunkStreamer"\)\r?\n?/m,
      ""
    );
  writeFileSync(path.join(worldDst, "HarborFront.tscn"), frontCleaned, "utf8");

  const srcChunks = path.join(gameDir, "scenes", "world", "chunks");
  for (const file of readdirSync(srcChunks)) {
    if (file.startsWith("chunk_") && file.endsWith(".tscn")) {
      copyFileSync(path.join(srcChunks, file), path.join(chunksDst, file));
    }
  }

  for (const folder of ["characters", "vehicles", "weapons", "props", "buildings"]) {
    copyTreeFiltered(
      path.join(gameDir, "assets", folder),
      path.join(engineDir, "assets", folder),
      [".glb"]
    );
  }
  copyTreeFiltered(
    path.join(gameDir, "assets", "audio"),
    path.join(engineDir, "assets", "audio"),
    [".ogg"]
  );
}

function runGodot(godotBin, args, extraEnv = {}) {
  const res = spawnSync(godotBin, args, {
    cwd: engineDir,
    env: { ...process.env, ...extraEnv },
    stdio: "inherit",
  });
  if (res.status !== 0) {
    throw new Error(`Godot command failed (${res.status}): ${godotBin} ${args.join(" ")}`);
  }
}

function sha12(filePath) {
  const buf = readFileSync(filePath);
  return createHash("sha256").update(buf).digest("hex").slice(0, 12);
}

export function prepareGodot() {
  const godotBin = resolveGodotBinary();
  console.log(`[harbor-web] Using Godot binary: ${godotBin}`);

  stageHarborScenesAndAssets();

  rmSync(godotOutDir, { recursive: true, force: true });
  mkdirSync(chunksOutDir, { recursive: true });
  const rawChunksTmp = path.join(engineDir, ".tmp_chunks");
  rmSync(rawChunksTmp, { recursive: true, force: true });
  mkdirSync(rawChunksTmp, { recursive: true });

  console.log("[harbor-web] Importing staged Harbor 3D assets...");
  runGodot(godotBin, ["--headless", "--path", engineDir, "--import"]);

  console.log("[harbor-web] Packing HarborBackdrop.scn and 64m binary chunk scenes...");
  runGodot(
    godotBin,
    ["--headless", "--path", engineDir, "--script", "res://build/prepare_world.gd"],
    { HARBOR_CHUNKS_OUT: rawChunksTmp }
  );

  const streamingCfg = JSON.parse(
    readFileSync(path.join(repoRoot, "data", "world", "harbor_streaming.json"), "utf8")
  );

  const chunkEntries = [];
  for (const file of readdirSync(rawChunksTmp).sort()) {
    const match = /^chunk_(-?\d+)_(-?\d+)\.scn$/.exec(file);
    if (!match) continue;
    const src = path.join(rawChunksTmp, file);
    const hash = sha12(src);
    const hashedName = `chunk_${match[1]}_${match[2]}.${hash}.scn`;
    const dst = path.join(chunksOutDir, hashedName);
    copyFileSync(src, dst);
    chunkEntries.push({
      x: Number(match[1]),
      z: Number(match[2]),
      file: hashedName,
      bytes: statSync(dst).size,
    });
  }
  rmSync(rawChunksTmp, { recursive: true, force: true });

  writeFileSync(
    path.join(chunksOutDir, "chunk_index.json"),
    JSON.stringify(
      {
        chunkSizeMeters: streamingCfg.chunkSizeMeters,
        loadRadiusChunks: streamingCfg.loadRadiusChunks,
        unloadPaddingChunks: streamingCfg.unloadPaddingChunks,
        lookaheadSeconds: streamingCfg.lookaheadSeconds,
        chunks: chunkEntries,
      },
      null,
      2
    ),
    "utf8"
  );

  console.log("[harbor-web] Re-indexing HarborBackdrop.scn and exporting WebGL2 release...");
  runGodot(godotBin, ["--headless", "--path", engineDir, "--import"]);
  const exportHtml = path.join(godotOutDir, "harbor.html");
  runGodot(godotBin, ["--headless", "--path", engineDir, "--export-release", "Web", exportHtml]);

  const rawJs = path.join(godotOutDir, "harbor.js");
  const rawWasm = path.join(godotOutDir, "harbor.wasm");
  const rawPck = path.join(godotOutDir, "harbor.pck");
  for (const req of [rawJs, rawWasm, rawPck]) {
    if (!existsSync(req)) {
      throw new Error(`Expected Godot Web export artifact missing: ${req}`);
    }
  }

  const wasmHash = sha12(rawWasm);
  const jsName = `harbor.${sha12(rawJs)}.js`;
  const wasmName = `harbor.${wasmHash}.wasm`;
  const pckName = `harbor.${sha12(rawPck)}.pck`;
  const audioWorkletName = `harbor.${wasmHash}.audio.worklet.js`;
  const audioPosWorkletName = `harbor.${wasmHash}.audio.position.worklet.js`;

  copyFileSync(rawJs, path.join(godotOutDir, jsName));
  copyFileSync(rawWasm, path.join(godotOutDir, wasmName));
  copyFileSync(rawPck, path.join(godotOutDir, pckName));

  const rawAudioWorklet = path.join(godotOutDir, "harbor.audio.worklet.js");
  const rawAudioPosWorklet = path.join(godotOutDir, "harbor.audio.position.worklet.js");
  if (existsSync(rawAudioWorklet)) {
    copyFileSync(rawAudioWorklet, path.join(godotOutDir, audioWorkletName));
  }
  if (existsSync(rawAudioPosWorklet)) {
    copyFileSync(rawAudioPosWorklet, path.join(godotOutDir, audioPosWorkletName));
  }

  const keepFiles = new Set([
    "chunks",
    jsName,
    wasmName,
    pckName,
    audioWorkletName,
    audioPosWorkletName,
    "manifest.json",
  ]);
  for (const entry of readdirSync(godotOutDir)) {
    if (keepFiles.has(entry)) continue;
    rmSync(path.join(godotOutDir, entry), { recursive: true, force: true });
  }

  const manifest = {
    script: `godot/${jsName}`,
    executable: `godot/${wasmName.replace(/\.wasm$/, "")}`,
    wasm: `godot/${wasmName}`,
    mainPack: `godot/${pckName}`,
    wasmBytes: statSync(path.join(godotOutDir, wasmName)).size,
    mainPackBytes: statSync(path.join(godotOutDir, pckName)).size,
    chunkCount: chunkEntries.length,
  };
  writeFileSync(path.join(godotOutDir, "manifest.json"), JSON.stringify(manifest, null, 2), "utf8");
  console.log(
    `[harbor-web] Godot WebGL2 export ready: ${pckName} (${(manifest.mainPackBytes / 1048576).toFixed(2)} MB) + ${chunkEntries.length} streamed chunks.`
  );
}

if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  prepareGodot();
}
