# AI生成装備FBXのデシメート＋実寸正規化（Blenderヘッドレス用）
#
# 使い方:
#   blender --background --python tools/aigear-decimate.py -- \
#       <input.fbx> <output.fbx> <target_tris> <target_size_m> [weld_m]
#
# 処理: FBXインポート → メッシュ統合 → 実寸正規化 → 距離マージ（溶接） →
#       Decimate(Collapse)で目標三角形数へ → 原点をバウンズ中心へ → FBX出力。
# Meshy等の生成メッシュ（数十万tri・スケール不定）を装備用の軽量実寸アセットに落とす。
# 手作業でUV・頂点を触らない原則（design-18 §3.5.2）に従い、全工程をスクリプトで再現可能にする。
#
# 正規化を先に回す理由: remesh のボクセル径と溶接しきい値を実寸(m)で指定できるようにするため。
# 当初は「巨大スケール（Meshy出力は10,000単位級）が Collapse の精度を壊している」と見て前へ出したが、
# **これは誤りだった**（2026-07-29 検証: 実寸0.26mへ正規化してから潰しても 6,482tri で頭打ちのまま）。
# 頭打ちの真因は下の genus の話。順序自体は上の理由で維持する。
#
# 距離マージ（weld_m）は既定オフ。生成メッシュは**そのままだと完全に多様体**で、
# 溶接するとかえって境界・非多様体エッジを作って壊す（同ヘルメット実測: 溶接前は境界0/非多様体0、
# 溶接後は境界1,729/非多様体3,056）。重複頂点が実際にある入力にだけ明示的に指定すること。
#
# voxel remesh（remesh_m）も既定オフ。Collapse は**穴（genus）を保存する**ので、通気孔グリルの
# ように穴が多い入力は目標値に関係なく頭打ちになる（同ヘルメット: 比率0.005でも0.001でも6,474tri、
# 0.1の反復でも6,452止まり）。remesh で位相ごと作り直すと目標値まで落とせる。
# **細バーの造形（捕手マスクのケージ等）は溶けて消えるので使わないこと。**

import sys

import bmesh
import bpy


def normalize_scale(obj, target_size: float) -> None:
    """最大辺が target_size になるよう一様スケールして確定させる。"""
    dims = obj.dimensions
    max_dim = max(dims.x, dims.y, dims.z)
    if max_dim > 0:
        s = target_size / max_dim
        obj.scale = (s, s, s)
        bpy.ops.object.transform_apply(scale=True)


def weld(obj, dist: float) -> int:
    """距離マージ。戻り値は減った頂点数。"""
    before = len(obj.data.vertices)
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=dist)
    bm.to_mesh(obj.data)
    bm.free()
    obj.data.update()
    return before - len(obj.data.vertices)


def main() -> None:
    argv = sys.argv[sys.argv.index("--") + 1:]
    src, dst, target_tris, target_size = argv[0], argv[1], int(argv[2]), float(argv[3])
    # 溶接・remesh はどちらも既定オフ（0）。上のコメントの理由で、入力を選ぶ工程なので明示指定させる
    weld_m = float(argv[4]) if len(argv) > 4 else 0.0
    remesh_m = float(argv[5]) if len(argv) > 5 else 0.0

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

    # 実寸正規化を先に（remesh のボクセル径・溶接しきい値を実寸(m)で扱えるようにするため）
    normalize_scale(obj, target_size)
    merged = weld(obj, weld_m) if weld_m > 0.0 else 0
    if remesh_m > 0.0:
        obj.data.remesh_voxel_size = remesh_m
        obj.data.remesh_voxel_adaptivity = 0.0
        bpy.ops.object.voxel_remesh()

    tri_count = sum(len(p.vertices) - 2 for p in obj.data.polygons)
    if tri_count > target_tris:
        mod = obj.modifiers.new("decimate", "DECIMATE")
        mod.decimate_type = "COLLAPSE"
        mod.ratio = target_tris / tri_count
        bpy.ops.object.modifier_apply(modifier=mod.name)

    # デシメートで実寸はほぼ変わらないが、外形が痩せる分を最後にもう一度合わせる
    normalize_scale(obj, target_size)
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
          f"weld={weld_m:.5f}m merged={merged}verts remesh={remesh_m:.5f}m "
          f"dims={obj.dimensions.x:.3f},{obj.dimensions.y:.3f},{obj.dimensions.z:.3f}")


main()
