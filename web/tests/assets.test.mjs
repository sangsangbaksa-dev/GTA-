import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';

const root = new URL('../../', import.meta.url);
for (const model of ['chr_harbor_rook', 'chr_harbor_officer', 'chr_harbor_crew', 'chr_harbor_dockhand', 'chr_npc_civilian']) {
  test(model + ' ships as a skinned animated GLB', async () => {
    const bytes = await readFile(new URL('game/assets/characters/' + model + '.glb', root));
    assert.equal(bytes.toString('ascii', 0, 4), 'glTF', 'run git lfs pull before using the models');
    const jsonLength = bytes.readUInt32LE(12);
    const data = JSON.parse(bytes.toString('utf8', 20, 20 + jsonLength));
    assert.ok(data.skins.length > 0);
    assert.ok(data.skins[0].joints.length >= 45);
    const clips = new Set(data.animations.map(clip => clip.name));
    for (const name of ['Idle', 'Walk', 'Jog', 'Run', 'CrouchIdle', 'CrouchWalk'])
      assert.ok(clips.has(name), name + ' is missing from ' + model);
  });
}

test('browser renderer and simulation reference the same world', async () => {
  const [layout, visuals] = await Promise.all(['harbor_front', 'harbor_visuals'].map(async file =>
    JSON.parse(await readFile(new URL('data/world/' + file + '.json', root), 'utf8'))));
  assert.equal(visuals.waterLevel, layout.waterLevel);
  assert.equal(layout.districts.length, 7);
  const materials = new Set(visuals.materials.map(material => material.id));
  for (const item of visuals.primitives) assert.ok(materials.has(item.material), item.material);
  const [min, max] = [layout.bounds.min, layout.bounds.max];
  assert.ok((max[0] - min[0]) * (max[1] - min[1]) > 1_000_000);
});
