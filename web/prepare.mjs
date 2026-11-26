import { spawnSync } from 'node:child_process';
import { copyFile, mkdir, open, readFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';

const web = import.meta.dirname;
const root = resolve(web, '..');
const publicDir = join(web, 'public');
const appBundle = join(publicDir, 'runtime');
const project = join(web, 'runtime', 'Harbor.Browser.csproj');
const dotnet = process.env.DOTNET || 'dotnet';
const build = spawnSync(dotnet, ['build', project, '-c', 'Release', '--nologo',
  '-p:WasmAppDir=' + appBundle], { stdio: 'inherit' });
if (build.error) throw build.error;
if (build.status !== 0) process.exit(build.status ?? 1);

const dataFiles = ['harbor_front.json', 'harbor_visuals.json'];
await mkdir(join(publicDir, 'world'), { recursive: true });
for (const name of dataFiles) {
  await copyFile(join(root, 'data', 'world', name), join(publicDir, 'world', name));
}

const visuals = JSON.parse(await readFile(join(publicDir, 'world', 'harbor_visuals.json'), 'utf8'));
const assets = new Set([
  'assets/characters/chr_harbor_rook.glb',
  'assets/characters/chr_harbor_crew.glb',
  'assets/characters/chr_harbor_dockhand.glb',
  'assets/characters/chr_harbor_officer.glb',
  'assets/characters/chr_npc_civilian.glb',
  'assets/vehicles/veh_calloway_compact.glb',
  'assets/vehicles/veh_pier_runner.glb',
  'assets/vehicles/veh_breakwater_utility.glb',
  'assets/fonts/BarlowSemiCondensed-Bold.ttf',
  'assets/fonts/Barlow-OFL.txt',
  'assets/audio/wasp.ogg',
  'assets/audio/kestrel.ogg',
  'assets/audio/dock_ambience.ogg',
]);
for (const item of visuals.assets) assets.add(item.path.replace(/^res:\/\//, ''));

for (const asset of assets) {
  if (!/^assets\/(?:[\w-]+\/)+[\w.-]+$/.test(asset)) throw new Error('Invalid web asset path ' + asset);
  const source = join(root, 'game', asset);
  const target = join(publicDir, asset);
  const file = await open(source);
  try {
    const signature = Buffer.alloc(8);
    await file.read(signature, 0, 8, 0);
    if (signature.toString() === 'version ') throw new Error('Run git lfs pull to download ' + asset);
  } finally {
    await file.close();
  }
  await mkdir(dirname(target), { recursive: true });
  await copyFile(source, target);
}
