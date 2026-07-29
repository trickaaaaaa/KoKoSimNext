# 板もの防具（胸プロテクター・レガース）の手続きモデリング（Blenderヘッドレス用）
#
# 使い方:
#   blender --background --python tools/aigear-plates.py -- <output_dir>
#
# AI生成（Meshyテキスト→3D）は着用文脈に引きずられ板状防具が服・脚として出るため、
# 幾何的に単純なこの2点はスクリプトで直接作る（design-18 §3.3 プリミティブ自作の発展形）。
# 出力: ChestProtectorP.fbx（実寸0.45m級）/ LegGuardP.fbx（実寸0.40m級）。Z-up実寸で構築。

import math
import sys

import bpy


def reset_scene() -> None:
    bpy.ops.wm.read_factory_settings(use_empty=True)


def add_rounded_box(name, size, location, bevel=0.012):
    # size=1 の立方体は辺長1なので、寸法をそのまま scale に入れる（/2すると隙間だらけになる）
    bpy.ops.mesh.primitive_cube_add(size=1.0, location=location)
    obj = bpy.context.active_object
    obj.name = name
    obj.scale = (size[0], size[1], size[2])
    bpy.ops.object.transform_apply(scale=True)
    mod = obj.modifiers.new("bevel", "BEVEL")
    mod.width = bevel
    mod.segments = 2
    bpy.ops.object.modifier_apply(modifier=mod.name)
    return obj


def add_shell_arc(name, radius, depth, thickness, location, arc_deg=150.0, scale_y=0.7):
    """正面向きの半筒シェル（レガースの脛あて）。円筒の後方を落として solidify で厚みを付ける。"""
    bpy.ops.mesh.primitive_cylinder_add(vertices=20, radius=radius, depth=depth, location=location)
    obj = bpy.context.active_object
    obj.name = name
    bpy.ops.object.mode_set(mode="EDIT")
    import bmesh
    bm = bmesh.from_edit_mesh(obj.data)
    keep = math.cos(math.radians(arc_deg / 2.0))
    # 前方=-Y を残す: y が -radius*cos(arc/2) より大きい（背面側）頂点を削除
    doomed = [v for v in bm.verts if v.co.y > -radius * keep]
    bmesh.ops.delete(bm, geom=doomed, context="VERTS")
    bmesh.update_edit_mesh(obj.data)
    bpy.ops.object.mode_set(mode="OBJECT")
    obj.scale = (1.0, scale_y, 1.0)
    bpy.ops.object.transform_apply(scale=True)
    mod = obj.modifiers.new("solid", "SOLIDIFY")
    mod.thickness = thickness
    bpy.ops.object.modifier_apply(modifier=mod.name)
    return obj


def join_and_export(objs, name, out_dir):
    bpy.ops.object.select_all(action="DESELECT")
    for o in objs:
        o.select_set(True)
    bpy.context.view_layer.objects.active = objs[0]
    if len(objs) > 1:
        bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active
    obj.name = name
    bpy.ops.object.origin_set(type="ORIGIN_GEOMETRY", center="BOUNDS")
    obj.location = (0.0, 0.0, 0.0)
    path = f"{out_dir}/{name}.fbx"
    bpy.ops.export_scene.fbx(
        filepath=path,
        use_selection=False,
        add_leaf_bones=False,
        apply_scale_options="FBX_SCALE_UNITS",
    )
    tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
    print(f"AIGEAR_PLATES_OK {name} tris={tris} "
          f"dims={obj.dimensions.x:.3f},{obj.dimensions.y:.3f},{obj.dimensions.z:.3f}")


def build_chest_protector(out_dir) -> None:
    """横分節パッドの積層＋肩口の丸み。正面=-Y、上=+Z。"""
    reset_scene()
    parts = []
    # 首下の上端パッド。肩当ては装着側で肩ボーン実測のパーツを載せる（2026-07-28監督指摘:
    # 「肩の上も通っている」＝メッシュ内蔵の小さな肩箱では届かないため役割分担）
    parts.append(add_rounded_box("top", (0.30, 0.055, 0.085), (0.0, 0.0, 0.185)))
    # 胴の横分節 5段（下へ僅かに幅を絞る）
    for i in range(5):
        w = 0.325 - i * 0.012
        z = 0.115 - i * 0.068
        parts.append(add_rounded_box(f"seg{i}", (w, 0.055, 0.060), (0.0, 0.0, z)))
    join_and_export(parts, "AIGear_ChestProtectorP", out_dir)


def build_leg_guard(out_dir) -> None:
    """膝皿＋脛シェル3段＋足首あて。正面=-Y、上=+Z。"""
    reset_scene()
    parts = []
    # 膝皿（前面へ膨らむ扁平球）
    bpy.ops.mesh.primitive_uv_sphere_add(segments=16, ring_count=10, radius=0.055,
                                         location=(0.0, -0.015, 0.165))
    knee = bpy.context.active_object
    knee.name = "knee"
    knee.scale = (1.0, 0.62, 0.85)
    bpy.ops.object.transform_apply(scale=True)
    parts.append(knee)
    # 脛シェル3段
    for i in range(3):
        z = 0.075 - i * 0.095
        parts.append(add_shell_arc(f"shin{i}", 0.062, 0.10, 0.018, (0.0, 0.0, z)))
    # 足首あて（小さめのシェル）
    parts.append(add_shell_arc("ankle", 0.050, 0.055, 0.016, (0.0, 0.0, -0.23), arc_deg=120.0))
    join_and_export(parts, "AIGear_LegGuardP", out_dir)


def main() -> None:
    out_dir = sys.argv[sys.argv.index("--") + 1]
    build_chest_protector(out_dir)
    build_leg_guard(out_dir)


main()
