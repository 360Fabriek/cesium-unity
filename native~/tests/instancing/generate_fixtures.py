#!/usr/bin/env python3
"""Deterministic, self-authored 3D Tiles fixtures. No Rotterdam data is copied."""
from __future__ import annotations
import argparse
import json
import math
from pathlib import Path
import struct



def padded(data: bytes, alignment: int, pad: bytes = b'\0', offset: int = 0) -> bytes:
    return data + pad * (-(len(data) + offset) % alignment)


def jbytes(value: object) -> bytes:
    return json.dumps(value, separators=(',', ':'), allow_nan=False).encode()


def glb(instanced: bool = False, multiple_primitives: bool = False,
        invalid_counts: bool = False) -> bytes:
    binary = bytearray()
    accessors, views = [], []

    def add(values, fmt, components, kind, component_type, minimum=None, maximum=None):
        binary.extend(b'\0' * (-len(binary) % 4))
        start = len(binary)
        binary.extend(struct.pack('<' + fmt * len(values), *values))
        view = len(views)
        views.append({'buffer': 0, 'byteOffset': start, 'byteLength': len(binary)-start})
        accessor = {'bufferView': view, 'componentType': component_type,
                    'count': len(values)//components, 'type': kind}
        if minimum is not None:
            accessor.update(min=minimum, max=maximum)
        accessors.append(accessor)
        return len(accessors) - 1

    positions = [0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 2, 0]
    pos = add(positions, 'f', 3, 'VEC3', 5126, [0, 0, 0], [1, 2, 1])
    idx = add([0, 2, 1, 0, 1, 3, 1, 2, 3, 2, 0, 3], 'H', 1, 'SCALAR', 5123)
    primitives = [{'attributes': {'POSITION': pos}, 'indices': idx, 'material': 0}]
    if multiple_primitives:
        shifted = [v + (2 if i % 3 == 0 else 0) for i, v in enumerate(positions)]
        pos2 = add(shifted, 'f', 3, 'VEC3', 5126, [2, 0, 0], [3, 2, 1])
        primitives.append({'attributes': {'POSITION': pos2}, 'indices': idx, 'material': 0})
    node = {'mesh': 0}
    model = {'asset': {'version': '2.0', 'generator': 'Cesium Unity instancing regression fixtures'},
             'scene': 0, 'scenes': [{'nodes': [0]}], 'nodes': [node],
             'meshes': [{'primitives': primitives}],
             'materials': [{'doubleSided': True, 'pbrMetallicRoughness': {
                 'baseColorFactor': [0.2, 0.7, 0.3, 1], 'metallicFactor': 0,
                 'roughnessFactor': 1}}]}
    if instanced:
        count = 2 if multiple_primitives else 3
        offsets = [-20, 0, 0, 0, 0, 0, 20, 0, 0][:count*3]
        tr = add(offsets, 'f', 3, 'VEC3', 5126)
        q = math.sqrt(0.5)
        rotations = [0, 0, 0, 1, 0, q, 0, q, 0, 0, 1, 0][:count*4]
        rt = add(rotations, 'f', 4, 'VEC4', 5126)
        scales = [5, 5, 5, 2, 4, 6, -3, 3, 3][:count*3]
        sc = add(scales, 'f', 3, 'VEC3', 5126)
        if invalid_counts:
            accessors[sc]['count'] = count - 1
        node['extensions'] = {'EXT_mesh_gpu_instancing': {
            'attributes': {'TRANSLATION': tr, 'ROTATION': rt, 'SCALE': sc}}}
        # Non-identity ancestor transform: it must precede instance TRS.
        model['nodes'] = [{'translation': [0, 2, 0], 'children': [1]}, node]
        model['extensionsUsed'] = ['EXT_mesh_gpu_instancing']
        model['extensionsRequired'] = ['EXT_mesh_gpu_instancing']
    model.update(buffers=[{'byteLength': len(binary)}], bufferViews=views, accessors=accessors)
    js = padded(jbytes(model), 4, b' ')
    binary_chunk = padded(bytes(binary), 4)
    total = 12 + 8 + len(js) + 8 + len(binary_chunk)
    if total % 8:
        js += b' ' * 4  # Keep the whole GLB eight-byte aligned inside I3DM.
        total += 4
    return (struct.pack('<4sII', b'glTF', 2, total)
            + struct.pack('<I4s', len(js), b'JSON') + js
            + struct.pack('<I4s', len(binary_chunk), b'BIN\0') + binary_chunk)


def i3dm(positions, external=False):
    feature_binary = struct.pack('<'+'f'*(len(positions)*3), *sum(positions, []))
    scale_offset = len(feature_binary)
    feature_binary += struct.pack('<'+'f'*len(positions), *([5]*len(positions)))
    feature_binary = padded(feature_binary, 8)
    feature_json = padded(jbytes({'INSTANCES_LENGTH': len(positions),
                                 'POSITION': {'byteOffset': 0},
                                 'SCALE': {'byteOffset': scale_offset},
                                 'RTC_CENTER': [1, 2, 3]}), 8, b' ', 32)
    content = padded(b'models/model.glb', 8, b' ') if external else glb()
    length = 32 + len(feature_json) + len(feature_binary) + len(content)
    assert length % 8 == 0
    return (struct.pack('<4s7I', b'i3dm', 1, length, len(feature_json),
                        len(feature_binary), 0, 0, 0 if external else 1)
            + feature_json + feature_binary + content)


def b3dm():
    feature_json = padded(jbytes({'BATCH_LENGTH': 0}), 8, b' ', 28)
    content = glb()
    length = 28 + len(feature_json) + len(content)
    assert length % 8 == 0
    return struct.pack('<4s6I', b'b3dm', 1, length, len(feature_json), 0, 0, 0) + feature_json + content


def cmpt(*children):
    return struct.pack('<4s3I', b'cmpt', 1, 16 + sum(map(len, children)), len(children)) + b''.join(children)


def tileset(uri):
    # Local ENU frame at longitude 0, latitude 0. Bounding volume is tile-local.
    return {'asset': {'version': '1.0'}, 'geometricError': 0,
            'root': {'boundingVolume': {'sphere': [0, 0, 0, 200]},
                     'transform': [0, 1, 0, 0, 0, 0, 1, 0,
                                   1, 0, 0, 0, 6378137, 0, 0, 1],
                     'geometricError': 0, 'refine': 'REPLACE',
                     'content': {'uri': uri}}}


def main(root: Path):
    root.mkdir(parents=True, exist_ok=True)
    (root/'models').mkdir(exist_ok=True)
    (root/'models/model.glb').write_bytes(glb())
    embedded = i3dm([[-30, 0, 0], [-15, 0, 0]])
    external = i3dm([[0, 0, 0], [15, 0, 0], [30, 0, 0]], True)
    files = {'embedded.i3dm': embedded, 'external.i3dm': external,
             'nested.cmpt': cmpt(embedded, cmpt(external, b3dm())),
             'direct.glb': glb(instanced=True),
             'multi-primitive.glb': glb(instanced=True, multiple_primitives=True),
             'invalid-counts.glb': glb(instanced=True, invalid_counts=True)}
    expected = {'embedded': [2, 1, 1], 'external': [3, 1, 1], 'nested': [6, 3, 3],
                'direct': [3, 1, 1], 'multi-primitive': [4, 2, 2],
                'invalid-counts': [0, 0, 0]}
    for name, data in files.items():
        (root/name).write_bytes(data)
        stem = Path(name).stem
        (root/(stem+'-tileset.json')).write_text(json.dumps(tileset(name), indent=2)+'\n')
    (root/'expected.json').write_text(json.dumps({
        name: {'renderers': counts[0], 'referencedUniqueMeshes': counts[1],
               'referencedUniqueMaterials': counts[2]} for name, counts in expected.items()}, indent=2)+'\n')
    print(f'Generated {len(files)} tile contents and their tilesets in {root}')

if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True,
                        help='Directory in which to generate the fixture files.')
    main(parser.parse_args().output)
