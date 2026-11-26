"""Split the generated Harbor Front scene into chunks the streamer can load and unload.

Reads data/world/harbor_streaming.json for the cell size. Sky, water and the walk collision
stay in HarborFront.tscn. Meshes and props move to scenes/world/chunks/chunk_X_Z.tscn.
A deck or road that crosses a cell is cut on the grid so a distant piece can leave memory
without taking the ground under the player with it. Run after tools/build_harbor_scene.py.
"""

import json
import math
import re
from collections import defaultdict
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SCENE = ROOT / 'game/scenes/world/HarborFront.tscn'
CHUNK_DIR = ROOT / 'game/scenes/world/chunks'
CONFIG = ROOT / 'data/world/harbor_streaming.json'
EPS = 1e-4

SHELL_TYPES = {
    'WorldEnvironment', 'DirectionalLight3D', 'Marker3D', 'StaticBody3D', 'CollisionShape3D',
}
SHELL_NAMES = {'HarborFront', 'Bay'}
HEADER_RE = {
    'name': re.compile(r'name="([^"]+)"'),
    'type': re.compile(r'type="([^"]+)"'),
    'parent': re.compile(r'parent="([^"]*)"'),
    'groups': re.compile(r'groups=(\[[^\]]*\])'),
    'instance': re.compile(r'instance=ExtResource\("([^"]+)"\)'),
}
VEC_RE = re.compile(r'Vector3\(([^)]*)\)')
SUB_RE = re.compile(r'SubResource\("([^"]+)"\)')
EXT_RE = re.compile(r'ExtResource\("([^"]+)"\)')


def load_size():
    data = json.loads(CONFIG.read_text(encoding='utf-8'))
    size = float(data['chunkSizeMeters'])
    if size <= 0:
        raise SystemExit('chunkSizeMeters must be positive')
    return size, data


def blocks(text):
    parts = re.split(r'\n(?=\[)', text.strip())
    return [part.strip('\n') for part in parts if part.strip()]


def parse_header(line):
    def grab(key):
        match = HEADER_RE[key].search(line)
        return match.group(1) if match else None

    return {
        'name': grab('name'),
        'type': grab('type'),
        'parent': grab('parent'),
        'groups': grab('groups'),
        'instance': grab('instance'),
    }


def format_header(info, parent):
    line = f'[node name="{info["name"]}"'
    if info['type']:
        line += f' type="{info["type"]}"'
    if parent is not None:
        line += f' parent="{parent}"'
    if info['groups']:
        line += f' groups={info["groups"]}'
    if info['instance']:
        line += f' instance=ExtResource("{info["instance"]}")'
    return line + ']'


def vec(text):
    found = VEC_RE.search(text)
    if not found:
        return None
    return tuple(float(part) for part in found.group(1).split(','))


def property_vec(body, key):
    match = re.search(rf'^{key} = Vector3\(([^)]*)\)', body, re.M)
    if not match:
        return None
    return tuple(float(part) for part in match.group(1).split(','))


def fmt(value):
    if abs(value) < 5e-7:
        return '0.0'
    text = f'{value:.6f}'.rstrip('0').rstrip('.')
    if '.' not in text:
        text += '.0'
    return text


def set_vec(body, key, value):
    line = f'{key} = Vector3({fmt(value[0])}, {fmt(value[1])}, {fmt(value[2])})'
    if re.search(rf'^{key} = ', body, re.M):
        return re.sub(rf'^{key} = .*$', line, body, count=1, flags=re.M)
    return body + ('\n' if body and not body.endswith('\n') else '') + line


def coord_of(x, z, size):
    return math.floor(x / size), math.floor(z / size)


def cells_covering(min_x, max_x, min_z, max_z, size):
    if max_x - min_x < EPS and max_z - min_z < EPS:
        return [coord_of((min_x + max_x) / 2, (min_z + max_z) / 2, size)]
    x0 = math.floor(min_x / size)
    x1 = math.floor((max_x - EPS) / size)
    z0 = math.floor(min_z / size)
    z1 = math.floor((max_z - EPS) / size)
    return [(x, z) for x in range(x0, x1 + 1) for z in range(z0, z1 + 1)]


def axis_aligned(vals):
    return abs(vals[1]) + abs(vals[2]) + abs(vals[4]) + abs(vals[6]) + abs(vals[8]) + abs(vals[9]) < 1e-3


def instance_bounds(vals):
    xs, zs = [], []
    for lx in (-0.5, 0.5):
        for ly in (-0.5, 0.5):
            for lz in (-0.5, 0.5):
                xs.append(vals[0] * lx + vals[1] * ly + vals[2] * lz + vals[3])
                zs.append(vals[8] * lx + vals[9] * ly + vals[10] * lz + vals[11])
    return min(xs), max(xs), min(zs), max(zs)


def clip_span(min_v, max_v, origin, size):
    start = max(min_v, origin)
    end = min(max_v, origin + size)
    if end - start < EPS:
        return None
    return start, end


def place_instance(tokens, size):
    """Return (cell, token list) pieces. Large axis-aligned instances are cut to the cell."""
    vals = [float(token) for token in tokens]
    min_x, max_x, min_z, max_z = instance_bounds(vals)
    cells = cells_covering(min_x, max_x, min_z, max_z, size)
    if len(cells) <= 1 or not axis_aligned(vals):
        return [(coord_of(vals[3], vals[11], size), list(tokens))]
    pieces = []
    for cell in cells:
        span_x = clip_span(min_x, max_x, cell[0] * size, size)
        span_z = clip_span(min_z, max_z, cell[1] * size, size)
        if span_x is None or span_z is None:
            continue
        cloned = list(vals)
        width = span_x[1] - span_x[0]
        depth = span_z[1] - span_z[0]
        cloned[0] = math.copysign(width, vals[0] or 1)
        cloned[3] = (span_x[0] + span_x[1]) / 2
        cloned[10] = math.copysign(depth, vals[10] or 1)
        cloned[11] = (span_z[0] + span_z[1]) / 2
        pieces.append((cell, [fmt(value) if isinstance(value, float) else value for value in cloned]))
    return pieces


def closure(seed_texts, subs):
    needed_sub, needed_ext = set(), set()
    pending = list(seed_texts)
    while pending:
        text = pending.pop()
        needed_ext.update(EXT_RE.findall(text))
        for sub_id in SUB_RE.findall(text):
            if sub_id in needed_sub:
                continue
            needed_sub.add(sub_id)
            pending.append(subs[sub_id])
    return needed_sub, needed_ext


def scene_text(ext_lines, sub_blocks, node_blocks):
    steps = len(ext_lines) + len(sub_blocks) + 1
    parts = [f'[gd_scene load_steps={steps} format=3]', '']
    for line in ext_lines:
        parts.append(line)
        parts.append('')
    for block in sub_blocks:
        parts.append(block)
        parts.append('')
    for block in node_blocks:
        parts.append(block)
        parts.append('')
    return '\n'.join(parts).rstrip() + '\n'


def split(scene_path=SCENE):
    size, _config = load_size()
    source = Path(scene_path).read_text(encoding='utf-8')
    parsed = blocks(source)
    ext_lines, subs, nodes = [], {}, []
    for block in parsed:
        header, _, body = block.partition('\n')
        if header.startswith('[ext_resource'):
            ext_lines.append(header)
        elif header.startswith('[sub_resource'):
            sub_id = re.search(r'id="([^"]+)"', header).group(1)
            subs[sub_id] = block
        elif header.startswith('[node'):
            info = parse_header(header)
            info['raw'] = block
            info['body'] = body
            nodes.append(info)
        elif header.startswith('[gd_scene'):
            continue
        else:
            raise SystemExit(f'unrecognized scene block: {header[:80]}')

    ext_by_id = {}
    for line in ext_lines:
        ext_by_id[re.search(r'id="([^"]+)"', line).group(1)] = line

    shell_nodes = []
    chunk_nodes = defaultdict(list)
    chunk_batches = defaultdict(list)
    visual_names = set()
    batch_serial = defaultdict(int)
    clipped = 0
    kept = 0

    def remember(cell, text):
        chunk_nodes[cell].append(text)

    for node in nodes:
        kind = node['type']
        if kind in SHELL_TYPES or node['name'] in SHELL_NAMES:
            shell_nodes.append(node['raw'])
            continue
        if kind == 'Node3D' and not node['instance']:
            continue
        visual_names.add(node['name'])
        if kind == 'MultiMeshInstance3D':
            batch_id = SUB_RE.search(node['body']).group(1)
            batch = subs[batch_id]
            mesh_id = re.search(r'^mesh = SubResource\("([^"]+)"\)', batch, re.M).group(1)
            inner = batch.split('PackedFloat32Array(', 1)[1].rsplit(')', 1)[0]
            tokens = [token.strip() for token in inner.split(',') if token.strip()]
            if len(tokens) % 12 != 0:
                raise SystemExit(f'{batch_id} buffer is not a list of 3x4 transforms')
            buckets = defaultdict(list)
            for index in range(0, len(tokens), 12):
                for cell, piece in place_instance(tokens[index:index + 12], size):
                    buckets[cell].append(piece)
                    if piece != tokens[index:index + 12]:
                        clipped += 1
                    else:
                        kept += 1
            material = re.search(r'^material_override = .*$', node['body'], re.M)
            shadow = re.search(r'^cast_shadow = .*$', node['body'], re.M)
            for cell, pieces in buckets.items():
                local = f'Batch{batch_serial[cell]:03}'
                batch_serial[cell] += 1
                flat = ', '.join(token for piece in pieces for token in piece)
                chunk_batches[cell].append(
                    f'[sub_resource type="MultiMesh" id="{local}"]\n'
                    f'transform_format = 1\n'
                    f'mesh = SubResource("{mesh_id}")\n'
                    f'instance_count = {len(pieces)}\n'
                    f'buffer = PackedFloat32Array({flat})'
                )
                body = f'multimesh = SubResource("{local}")'
                if material:
                    body += '\n' + material.group(0)
                if shadow:
                    body += '\n' + shadow.group(0)
                remember(cell, f'[node name="{node["name"]}" type="MultiMeshInstance3D" parent="."]\n{body}')
            continue

        position = property_vec(node['body'], 'position') or (0.0, 0.0, 0.0)
        scale = property_vec(node['body'], 'scale')
        rotation = property_vec(node['body'], 'rotation_degrees') or (0.0, 0.0, 0.0)
        unit_box = 'SubResource("UnitBox")' in node['body']
        rotated = any(abs(axis) > 0.01 for axis in rotation)
        if unit_box and scale and not rotated:
            min_x, max_x = position[0] - abs(scale[0]) / 2, position[0] + abs(scale[0]) / 2
            min_z, max_z = position[2] - abs(scale[2]) / 2, position[2] + abs(scale[2]) / 2
            cells = cells_covering(min_x, max_x, min_z, max_z, size)
            if len(cells) > 1:
                for cell in cells:
                    span_x = clip_span(min_x, max_x, cell[0] * size, size)
                    span_z = clip_span(min_z, max_z, cell[1] * size, size)
                    if span_x is None or span_z is None:
                        continue
                    body = set_vec(node['body'], 'position', (
                        (span_x[0] + span_x[1]) / 2, position[1], (span_z[0] + span_z[1]) / 2))
                    body = set_vec(body, 'scale', (
                        math.copysign(span_x[1] - span_x[0], scale[0] or 1),
                        scale[1],
                        math.copysign(span_z[1] - span_z[0], scale[2] or 1),
                    ))
                    remember(cell, format_header(node, '.') + '\n' + body)
                    clipped += 1
                continue
        cell = coord_of(position[0], position[2], size)
        remember(cell, format_header(node, '.') + '\n' + node['body'])
        kept += 1

    emitted = set()
    for texts in chunk_nodes.values():
        for text in texts:
            emitted.add(HEADER_RE['name'].search(text).group(1))
    missing = visual_names - emitted
    if missing:
        raise SystemExit(f'visual nodes were not assigned to a chunk: {sorted(missing)[:8]}')

    shell_sub, shell_ext = closure(shell_nodes, subs)
    shell_ext_lines = [line for line in ext_lines if re.search(r'id="([^"]+)"', line).group(1) in shell_ext]
    shell_ext_lines.append('[ext_resource type="Script" path="res://scripts/Streaming/ChunkStreamer.cs" id="ChunkStreamer"]')
    shell_subs = [subs[sub_id] for sub_id in subs if sub_id in shell_sub]
    streamer = '[node name="ChunkStreamer" type="Node3D" parent="."]\nscript = ExtResource("ChunkStreamer")'
    shell = scene_text(shell_ext_lines, shell_subs, shell_nodes + [streamer])
    if 'MultiMeshInstance3D' in shell or 'instance=ExtResource' in shell:
        raise SystemExit('shell scene still contains streamed visuals')

    written = {}
    for cell, texts in chunk_nodes.items():
        batch_blocks = chunk_batches[cell]
        overlay = dict(subs)
        synthesized = set()
        for block in batch_blocks:
            sub_id = re.search(r'id="([^"]+)"', block).group(1)
            overlay[sub_id] = block
            synthesized.add(sub_id)
        used_sub, used_ext = closure(texts, overlay)
        file_exts = [line for line in ext_lines if re.search(r'id="([^"]+)"', line).group(1) in used_ext]
        file_subs = [subs[sub_id] for sub_id in subs if sub_id in used_sub and sub_id not in synthesized]
        file_subs.extend(batch_blocks)
        root = f'[node name="Chunk_{cell[0]}_{cell[1]}" type="Node3D"]'
        written[cell] = scene_text(file_exts, file_subs, [root, *texts])

    verify_coverage(nodes, subs, written, size)

    CHUNK_DIR.mkdir(parents=True, exist_ok=True)
    for old in CHUNK_DIR.glob('chunk_*.tscn'):
        old.unlink()
    for cell, text in sorted(written.items()):
        (CHUNK_DIR / f'chunk_{cell[0]}_{cell[1]}.tscn').write_text(text, encoding='utf-8')
    Path(scene_path).write_text(shell, encoding='utf-8')
    spawn = coord_of(0, 5, size)
    beacon = coord_of(139.4, -176.5, size)
    radius = int(json.loads(CONFIG.read_text(encoding='utf-8'))['loadRadiusChunks'])
    spawn_files = sum(
        1 for cell in written
        if max(abs(cell[0] - spawn[0]), abs(cell[1] - spawn[1])) <= radius
    )
    if beacon not in written or spawn not in written:
        raise SystemExit('spawn or beacon chunk has no scene')
    if max(abs(beacon[0] - spawn[0]), abs(beacon[1] - spawn[1])) <= radius:
        raise SystemExit('beacon chunk is inside the spawn load radius; the harbor would stay fully resident')
    print(f'harbor chunks: {len(written)} files, spawn window {spawn_files}, '
          f'clipped pieces {clipped}, single-cell pieces {kept}, shell {len(shell_nodes)} nodes')
    return written


def verify_coverage(nodes, subs, written, size):
    """Every multimesh instance's area is still present, cut on cell borders and not duplicated."""
    for node in nodes:
        if node['type'] != 'MultiMeshInstance3D':
            continue
        batch_id = SUB_RE.search(node['body']).group(1)
        inner = subs[batch_id].split('PackedFloat32Array(', 1)[1].rsplit(')', 1)[0]
        tokens = [token.strip() for token in inner.split(',') if token.strip()]
        for index in range(0, len(tokens), 12):
            group = tokens[index:index + 12]
            vals = [float(token) for token in group]
            min_x, max_x, min_z, max_z = instance_bounds(vals)
            original_area = max(0.0, max_x - min_x) * max(0.0, max_z - min_z)
            pieces = place_instance(group, size)
            area = 0.0
            centers = []
            for cell, piece in pieces:
                piece_vals = [float(token) for token in piece]
                px0, px1, pz0, pz1 = instance_bounds(piece_vals)
                if px0 < min_x - 1e-3 or px1 > max_x + 1e-3 or pz0 < min_z - 1e-3 or pz1 > max_z + 1e-3:
                    raise SystemExit(f'{node["name"]} piece leaves its source bounds')
                area += max(0.0, px1 - px0) * max(0.0, pz1 - pz0)
                centers.append((piece_vals[3], piece_vals[11], cell))
                if cell not in written or node['name'] not in written[cell]:
                    raise SystemExit(f'{node["name"]} missing from chunk {cell}')
            if abs(area - original_area) > max(0.05, original_area * 1e-4):
                raise SystemExit(f'{node["name"]} area {original_area} became {area}')
            if len(pieces) == 1 and pieces[0][1] != group and axis_aligned(vals) and len(cells_covering(min_x, max_x, min_z, max_z, size)) <= 1:
                raise SystemExit(f'{node["name"]} was rewritten without crossing a border')


if __name__ == '__main__':
    split()
