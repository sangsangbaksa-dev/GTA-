import { spawnSync } from 'node:child_process';
import { copyFile, mkdir, readFile, stat } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';
import { exportGodot } from './prepare-godot.mjs';

const web = import.meta.dirname;
const root = resolve(web, '..');
const publicDir = join(web, 'public');
const appBundle = join(publicDir, 'runtime');
const project = join(web, 'runtime', 'Harbor.Browser.csproj');
const dotnet = process.env.DOTNET || 'dotnet';
const build = spawnSync(dotnet, ['build', project, '-c', 'Release', '--nologo',
  '-t:Build;WasmBuildApp', '-p:WasmAppDir=' + appBundle], { cwd: web, stdio: 'inherit' });
if (build.error) throw build.error;
if (build.status !== 0) process.exit(build.status ?? 1);

for (const file of ['dotnet.js', 'blazor.boot.json', 'dotnet.native.wasm', 'Harbor.Browser.wasm', 'Harbor.Core.wasm']) {
  const output = join(appBundle, '_framework', file);
  const info = await stat(output).catch(() => null);
  if (!info?.isFile() || info.size === 0)
    throw new Error('Missing WebAssembly build output: ' + output + '. Run dotnet workload install wasm-tools from web/.');
}

await mkdir(join(publicDir, 'world'), { recursive: true });
await copyFile(join(root, 'data/world/harbor_front.json'), join(publicDir, 'world/harbor_front.json'));

for (const asset of ['fonts/BarlowSemiCondensed-Bold.ttf', 'fonts/Barlow-OFL.txt',
  'audio/wasp.ogg', 'audio/kestrel.ogg', 'audio/dock_ambience.ogg']) {
  const source = join(root, 'game/assets', asset);
  const target = join(publicDir, 'assets', asset);
  const bytes = await readFile(source);
  if (bytes.subarray(0, 8).toString() === 'version ') throw new Error('Run git lfs pull to download ' + asset);
  await mkdir(dirname(target), { recursive: true });
  await copyFile(source, target);
}
await exportGodot();
