#!/usr/bin/env python3
"""Generate the shared, multi-pipeline overlay subgraph without changing its interface."""
from pathlib import Path
import json
import uuid

ROOT = Path(__file__).resolve().parents[1]
NAMESPACE = uuid.UUID('3177291e-01bb-47f0-b82a-13e1d1ba9f56')

def guid(name):
    return uuid.uuid5(NAMESPACE, name).hex

def ref(value):
    return {'m_Id': value}

def slot(name, number, kind, output=False, identifier=None):
    value = 0.0 if kind == 'Vector1' else dict.fromkeys('xyzw'[:int(kind[-1])] if kind.startswith('Vector') else 'xyzw', 0.0)
    result = {'m_SGVersion': 0, 'm_Type': 'UnityEditor.ShaderGraph.' + kind + 'MaterialSlot',
              'm_ObjectId': identifier or guid('slot-' + name), 'm_Id': number, 'm_DisplayName': name,
              'm_SlotType': 1 if output else 0, 'm_Hidden': False, 'm_ShaderOutputName': name,
              'm_StageCapability': 2, 'm_Value': value, 'm_DefaultValue': value, 'm_Labels': []}
    if kind.startswith('Texture'):
        result.pop('m_Value'); result.pop('m_DefaultValue'); result.pop('m_Labels')
        result['m_BareResource'] = False
        if not output:
            result['m_Texture'] = {'m_SerializedTexture': '{"texture":{"instanceID":0}}', 'm_Guid': ''}
            result['m_DefaultType'] = 0
    return result

def node(name, kind, slots, identifier=None, version=0):
    return {'m_SGVersion': version, 'm_Type': 'UnityEditor.ShaderGraph.' + kind,
            'm_ObjectId': identifier or guid('node-' + name), 'm_Group': ref(''), 'm_Name': name,
            'm_DrawState': {'m_Expanded': True, 'm_Position': {'serializedVersion': '2', 'x': 0.0, 'y': 0.0, 'width': 200.0, 'height': 120.0}},
            'm_Slots': [ref(s['m_ObjectId']) for s in slots], 'synonyms': [], 'm_Precision': 1,
            'm_PreviewExpanded': False, 'm_PreviewMode': 0, 'm_CustomColors': {'m_SerializableColors': []}}

def generate():
    objects, nodes, properties, edges = [], [], [], []
    def add_node(n, slots):
        nodes.append(ref(n['m_ObjectId'])); objects.append(n); objects.extend(slots)
        return n['m_ObjectId']
    def edge(a, a_slot, b, b_slot):
        edges.append({'m_OutputSlot': {'m_Node': ref(a), 'm_SlotId': a_slot},
                      'm_InputSlot': {'m_Node': ref(b), 'm_SlotId': b_slot}})
    specs = [
        ('baseColor', 'Color', 'Vector4', '26647bc5e7fd404898d346925fc7f8be', '83e390c8-0352-4898-86e2-9172c1b9394b'),
        ('textureCoordinateIndex', 'Vector1', 'Vector1', '74f0216fff2947ae83695928c24d9708', '6fcbe01a-3c9c-48dc-99a1-39447c83ffda'),
        ('texture', 'Texture2D', 'Texture2D', '0520d21fcee0473c8d5620f2b90bf897', 'fab66303-04f0-4ac1-8673-91dec11133b2'),
        ('translationAndScale', 'Vector4', 'Vector4', 'b5a504aa2fa446edb5a5620aaa4019e3', 'e055c41d-2685-42d1-a890-5f8a0de03f45')]
    property_nodes = []
    for name, kind, slot_kind, identifier, property_guid in specs:
        p = {'m_SGVersion': 3 if kind == 'Color' else (0 if kind == 'Texture2D' else 1),
             'm_Type': 'UnityEditor.ShaderGraph.Internal.' + kind + 'ShaderProperty',
             'm_ObjectId': identifier, 'm_Guid': {'m_GuidSerialized': property_guid},
             'm_Name': name, 'm_DefaultRefNameVersion': 1, 'm_RefNameGeneratedByDisplayName': name,
             'm_DefaultReferenceName': '_' + name, 'm_OverrideReferenceName': '', 'm_GeneratePropertyBlock': True,
             'm_UseCustomSlotLabel': False, 'm_CustomSlotLabel': '', 'm_Precision': 1,
             'overrideHLSLDeclaration': False, 'hlslDeclarationOverride': 0, 'm_Hidden': False}
        if kind == 'Texture2D':
            p.update(m_Value={'m_SerializedTexture': '{"texture":{"instanceID":0}}', 'm_Guid': ''},
                     m_DefaultType=0, m_Modifiable=True, isMainTexture=False, useTilingAndOffset=False)
        elif kind == 'Color':
            p.update(m_Value=dict.fromkeys('rgba', 0.0), isMainColor=False, m_ColorMode=0)
        elif kind == 'Vector1':
            p.update(m_Value=0.0, m_FloatType=0, m_RangeValues={'x': 0.0, 'y': 1.0})
        else:
            p['m_Value'] = dict.fromkeys('xyzw', 0.0)
        properties.append(ref(identifier)); objects.append(p)
        slots = [slot(name + 'Value', 0, slot_kind, True)]
        n = node(name, 'PropertyNode', slots); n['m_Property'] = ref(identifier)
        property_nodes.append(add_node(n, slots))
    slots = [slot('textureCoordinateIndex', 1953941338, 'Vector1'), slot('Out_TextureCoordinates', 1, 'Vector2', True)]
    n = node('CesiumSelectTexCoords', 'SubGraphNode', slots)
    n.update(m_SerializedSubGraph=json.dumps({'subGraph': {'fileID': -5475051401550479605,
                 'guid': '45c8c2a0ab2df934c8e0f63147f35d0e', 'type': 3}}),
             m_PropertyGuids=['846bdd1a-ae9c-4b2c-b510-8cdf69a4bd64'], m_PropertyIds=[1953941338],
             m_Dropdowns=[], m_DropdownSelectedEntries=[])
    uv = add_node(n, slots)
    edge(property_nodes[1], 0, uv, 1953941338)
    slots = [slot('AbsoluteWorld', 0, 'Vector3', True)]
    n = node('Absolute World Position', 'PositionNode', slots); n.update(m_Space=4, m_PositionSource=0)
    position = add_node(n, slots)
    slots = [slot('BaseColor', 0, 'Vector4'), slot('CoordinateIndex', 1, 'Vector1'),
             slot('Raster', 2, 'Texture2DInput'), slot('TranslationScale', 3, 'Vector4'),
             slot('AbsoluteWorld', 4, 'Vector3'), slot('LegacyUV', 5, 'Vector2'), slot('Color', 6, 'Vector4', True)]
    # Slot identifiers are per serialized object, not merely per display name.
    for s in slots: s['m_ObjectId'] = guid('function-' + s['m_DisplayName'])
    n = node('CesiumRasterOverlay', 'CustomFunctionNode', slots, version=1)
    n.update(m_SourceType=0, m_FunctionName='CesiumRasterOverlay',
             m_FunctionSource=guid('Source/Runtime/Resources/CesiumInstancedRaster.hlsl'),
             m_FunctionBody='', m_FunctionSourceUsePragmas=False)
    function = add_node(n, slots)
    for i, p in enumerate(property_nodes): edge(p, 0, function, i)
    edge(position, 0, function, 4); edge(uv, 1, function, 5)
    slots = [slot('Color', 1, 'Vector4', identifier='d18d5f6954fc4250ba94dee7e36de376')]
    n = node('Output', 'SubGraphOutputNode', slots, identifier='7904a41547714fd29861c04eb81da7a2')
    n['IsFirstSlotValid'] = True
    output = add_node(n, slots); edge(function, 6, output, 1)
    graph = {'m_SGVersion': 3, 'm_Type': 'UnityEditor.ShaderGraph.GraphData',
             'm_ObjectId': 'd2e75912f39249a58df1535da7d09a9a', 'm_Properties': properties,
             'm_Keywords': [], 'm_Dropdowns': [], 'm_CategoryData': [], 'm_Nodes': nodes,
             'm_GroupDatas': [], 'm_StickyNoteDatas': [], 'm_Edges': edges,
             'm_VertexContext': {'m_Position': {'x': 0.0, 'y': 0.0}, 'm_Blocks': []},
             'm_FragmentContext': {'m_Position': {'x': 0.0, 'y': 0.0}, 'm_Blocks': []},
             'm_PreviewData': {'serializedMesh': {'m_SerializedMesh': '{"mesh":{"instanceID":0}}', 'm_Guid': ''}, 'preventRotation': False},
             'm_Path': 'Sub Graphs', 'm_GraphPrecision': 1, 'm_PreviewMode': 2,
             'm_OutputNode': ref(output), 'm_ActiveTargets': []}
    return '\n\n'.join(json.dumps(o, separators=(',', ':')) for o in [graph] + objects) + '\n'

if __name__ == '__main__':
    target = ROOT / 'Source/Runtime/Resources/CesiumRasterOverlay.shadersubgraph'
    target.write_text(generate(), encoding='utf-8')
    print(target)
