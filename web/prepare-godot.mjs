import { spawnSync } from 'node:child_process';
import { copyFile, mkdir, readFile, rm, stat, unlink, writeFile } from 'node:fs/promises';
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
  for (const name of ['rook_courier', 'calloway_amara', 'calloway_mateo', 'calloway_linnea', 'calloway_arthur',
    'harbor_crew', 'harbor_officer'])
    files.add('assets/characters/chr_' + name + '.glb');
  for (const name of ['calloway_compact', 'pier_runner', 'breakwater_utility'])
    files.add('assets/vehicles/veh_' + name + '.glb');
  for (const name of ['smg_wasp', 'pistol_kestrel'])
    files.add('assets/weapons/wpn_' + name + '.glb');
  await Promise.all([...files].map(stage));

  await mkdir(output, { recursive: true });
  const worldDir = join(project, dirname(world));
  const generatedChunks = join(worldDir, 'chunks');
  await Promise.all([generatedChunks, join(worldDir, 'chunk_index.json'), join(worldDir, 'HarborBackdrop.scn')]
    .map(path => rm(path, { recursive: true, force: true })));
  await mkdir(generatedChunks);
  run(['--headless', '--path', project, '--import']);
  run(['--headless', '--path', project, '--script', 'res://build/partition_world.gd']);
  const index = JSON.parse(await readFile(join(worldDir, 'chunk_index.json'), 'utf8'));
  if (!index.length) throw new Error('Godot generated no world chunks');
  run(['--headless', '--path', project, '--import']);
  run(['--headless', '--path', project, '--export-release', 'Web', join(output, 'harbor.html')]);
  const publishedChunks = join(output, 'chunks');
  await rm(publishedChunks, { recursive: true, force: true });
  await mkdir(publishedChunks);
  await Promise.all(index.map(([, , file]) => copyFile(join(generatedChunks, file), join(publishedChunks, file))));
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
