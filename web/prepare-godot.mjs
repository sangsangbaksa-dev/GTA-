import { spawnSync } from 'node:child_process';
import { mkdir, readFile, stat, unlink, writeFile } from 'node:fs/promises';
import { dirname, join, resolve } from 'node:path';

const web = import.meta.dirname;
const project = join(web, 'engine');
const native = resolve(web, '../game');
const output = join(web, 'public/godot');
const godot = process.env.GODOT || 'godot';

function run(args) {
  const result = spawnSync(godot, args, { stdio: 'inherit' });
  if (result.error) throw result.error;
  if (result.status !== 0) throw new Error('Godot exited with status ' + result.status);
}

async function stage(relative) {
  const target = join(project, relative);
  const source = await readFile(join(native, relative));
  if (source.subarray(0, 8).toString() === 'version ') throw new Error('Run git lfs pull to download ' + relative);
  const previous = await readFile(target).catch(error => {
    if (error.code !== 'ENOENT') throw error;
  });
  if (previous?.equals(source)) return;
  await mkdir(dirname(target), { recursive: true });
  await writeFile(target, source);
}

export async function exportGodot() {
  const version = spawnSync(godot, ['--version'], { encoding: 'utf8' });
  if (version.error) throw version.error;
  if (!version.stdout.trim().startsWith('4.4.1.stable.official.')) {
    throw new Error('Set GODOT to the standard Godot 4.4.1 executable. Found ' + version.stdout.trim());
  }

  const world = 'scenes/world/HarborFront.tscn';
  const scene = await readFile(join(native, world), 'utf8');
  const files = new Set([world, 'assets/props/prop_crate_wood.glb']);
  for (const match of scene.matchAll(/^\[ext_resource [^\n]*?path="res:\/\/([\w./-]+)"/gm)) files.add(match[1]);
  for (const name of ['harbor_rook', 'harbor_dockhand', 'harbor_crew', 'harbor_officer', 'npc_civilian'])
    files.add('assets/characters/chr_' + name + '.glb');
  for (const name of ['calloway_compact', 'pier_runner', 'breakwater_utility'])
    files.add('assets/vehicles/veh_' + name + '.glb');
  for (const name of ['smg_wasp', 'pistol_kestrel'])
    files.add('assets/weapons/wpn_' + name + '.glb');
  await Promise.all([...files].map(stage));

  await mkdir(output, { recursive: true });
  run(['--headless', '--path', project, '--import']);
  run(['--headless', '--path', project, '--export-release', 'Web', join(output, 'harbor.html')]);
  for (const suffix of ['js', 'wasm', 'pck', 'audio.worklet.js', 'audio.position.worklet.js']) {
    const file = join(output, 'harbor.' + suffix);
    if (!(await stat(file)).size) throw new Error('Empty Godot Web export file ' + file);
  }
  // The Vite shell owns the page, controls and loading screen.
  for (const suffix of ['html', 'png', 'icon.png', 'apple-touch-icon.png'])
    await unlink(join(output, 'harbor.' + suffix)).catch(error => {
      if (error.code !== 'ENOENT') throw error;
    });
}
