# AI生成装備FBXのデシメート＋実寸正規化（Blenderヘッドレス用）
#
# 使い方:
#   blender --background --python tools/aigear-decimate.py -- \
#       <input.fbx> <output.fbx> <target_tris> <target_size_m>
#
# 処理: FBXインポート → メッシュ統合 → Decimate(Collapse)で目標三角形数へ →
#       最大辺が target_size_m になるよう一様スケール → 原点をバウンズ中心へ → FBX出力。
# Meshy等の生成メッシュ（数十万tri・スケール不定）を装備用の軽量実寸アセットに落とす。
# 手作業でUV・頂点を触らない原則（design-18 §3.5.2）に従い、全工程をスクリプトで再現可能にする。

import sys

import bpy


def main() -> None:
    argv = sys.argv[sys.argv.index("--") + 1:]
    src, dst, target_tris, target_size = argv[0], argv[1], int(argv[2]), float(argv[3])

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=src)

    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    if not meshes:
        raise SystemExit(f"no mesh in {src}")

    # 統合して1オブジェクトへ（Meshyは通常1メッシュだが念のため）
    bpy.ops.object.select_all(action="DESELECT")
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)

    tri_count = sum(len(p.vertices) - 2 for p in obj.data.polygons)
    if tri_count > target_tris:
        mod = obj.modifiers.new("decimate", "DECIMATE")
        mod.decimate_type = "COLLAPSE"
        mod.ratio = target_tris / tri_count
        bpy.ops.object.modifier_apply(modifier=mod.name)

    # 実寸正規化: 最大辺 = target_size_m、原点 = バウンズ中心
    dims = obj.dimensions
    max_dim = max(dims.x, dims.y, dims.z)
    if max_dim > 0:
        s = target_size / max_dim
        obj.scale = (s, s, s)
        bpy.ops.object.transform_apply(scale=True)
    bpy.ops.object.origin_set(type="ORIGIN_GEOMETRY", center="BOUNDS")
    obj.location = (0.0, 0.0, 0.0)

    result_tris = sum(len(p.vertices) - 2 for p in obj.data.polygons)
    bpy.ops.export_scene.fbx(
        filepath=dst,
        use_selection=False,
        add_leaf_bones=False,
        apply_scale_options="FBX_SCALE_UNITS",
    )
    print(f"AIGEAR_DECIMATE_OK {src} {tri_count}->{result_tris}tris "
          f"dims={obj.dimensions.x:.3f},{obj.dimensions.y:.3f},{obj.dimensions.z:.3f}")


main()
