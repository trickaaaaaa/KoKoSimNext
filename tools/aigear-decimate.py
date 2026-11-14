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
#
# 頭打ちより先に**形が壊れる**入力がある（2026-07-29 捕手ミット）。紐通し穴が縁を1周し、
# 穴の輪郭に三角形を食われて平らな面が細長い破片に潰れるため、下限(5,261)の1.5倍の8,000tri でも
# 見られる形にならなかった。判断の順序は「まず目標値どおり落ちたか」ではなく**必ず絵で確かめる**こと。
# ミットの当たりは remesh 0.0015 → 6,000tri（3,000だと面が折れ始める）。
#
# 目安: 穴が数個（通気孔）→ remesh 0.003 で 700tri まで落ちる。
#       穴が数十個（紐通し）→ remesh 0.0015 で 6,000tri が下限。
#       隙間が造形そのもの（ケージ）→ remesh 禁止・Collapse のみで 2,000tri。

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


def drop_thin_islands(obj, thin_max: float, diag_min: float) -> None:
    """細長い島（面積/対角² < thin_max かつ 対角 > diag_min[m]）を消す。"""
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    bm.verts.ensure_lookup_table()
    seen, doomed, dropped = set(), [], 0
    for v in bm.verts:
        if v.index in seen:
            continue
        stack, verts, faces = [v], [], set()
        seen.add(v.index)
        while stack:
            x = stack.pop()
            verts.append(x)
            faces.update(x.link_faces)
            for e in x.link_edges:
                y = e.other_vert(x)
                if y.index not in seen:
                    seen.add(y.index)
                    stack.append(y)
        lo = [min(u.co[k] for u in verts) for k in range(3)]
        hi = [max(u.co[k] for u in verts) for k in range(3)]
        diag = sum((hi[k] - lo[k]) ** 2 for k in range(3)) ** 0.5
        area = sum(f.calc_area() for f in faces)
        if diag > diag_min and area / (diag * diag) < thin_max:
            doomed.extend(verts)
            dropped += 1
    bmesh.ops.delete(bm, geom=doomed, context="VERTS")
    bm.to_mesh(obj.data)
    bm.free()
    obj.data.update()
    print(f"AIGEAR_DROP_THIN dropped={dropped}islands")


def add_core(obj, core) -> None:
    """外形バウンズの比率で楕円体の芯を作り、obj へ結合する（融合は後段の voxel remesh が行う）。"""
    from mathutils import Vector
    rx, ry, rz, cx, cy, cz = core
    vs = [obj.matrix_world @ v.co for v in obj.data.vertices]
    lo = Vector((min(v.x for v in vs), min(v.y for v in vs), min(v.z for v in vs)))
    hi = Vector((max(v.x for v in vs), max(v.y for v in vs), max(v.z for v in vs)))
    size, mid = hi - lo, (hi + lo) / 2.0
    loc = (mid.x + cx * size.x, mid.y + cy * size.y, mid.z + cz * size.z)
    bpy.ops.mesh.primitive_uv_sphere_add(segments=48, ring_count=24, radius=1.0, location=loc)
    sph = bpy.context.active_object
    sph.scale = (rx * size.x, ry * size.y, rz * size.z)
    bpy.ops.object.transform_apply(scale=True)
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    sph.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.join()


def main() -> None:
    argv = sys.argv[sys.argv.index("--") + 1:]
    src, dst, target_tris, target_size = argv[0], argv[1], int(argv[2]), float(argv[3])
    # 溶接・remesh はどちらも既定オフ（0）。上のコメントの理由で、入力を選ぶ工程なので明示指定させる
    weld_m = float(argv[4]) if len(argv) > 4 else 0.0
    remesh_m = float(argv[5]) if len(argv) > 5 else 0.0
    rot = [float(a) for a in argv[6].split(",")] if len(argv) > 6 else [0.0, 0.0, 0.0]
    core = [float(a) for a in argv[7].split(",")] if len(argv) > 7 and argv[7] != "-" else None
    exclude = set(argv[8].split(",")) - {"-"} if len(argv) > 8 else set()
    drop_thin = [float(a) for a in argv[9].split(",")] if len(argv) > 9 else None

    bpy.ops.wm.read_factory_settings(use_empty=True)
    # Meshy は FBX、Tripo（2026-11〜）は GLB で書き出す
    if src.lower().endswith((".glb", ".gltf")):
        bpy.ops.import_scene.gltf(filepath=src)
    else:
        bpy.ops.import_scene.fbx(filepath=src)

    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    for o in [o for o in meshes if o.name in exclude]:
        bpy.data.objects.remove(o, do_unlink=True)
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    if not meshes:
        raise SystemExit(f"no mesh in {src}")

    # 3ds Max 由来の購入 FBX は部品が空オブジェクト（親）の下にぶら下がる（2026-11-14 CGTrader のグラブ＝22部品）。
    # 親の変換を焼き込んで外さないと、結合後の transform_apply が親の分を取りこぼし、書き出しの向き・寸法が狂う
    bpy.ops.object.select_all(action="DESELECT")
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    bpy.ops.object.parent_clear(type="CLEAR_KEEP_TRANSFORM")
    for o in [o for o in bpy.context.scene.objects if o.type == "EMPTY"]:
        bpy.data.objects.remove(o, do_unlink=True)

    # 統合して1オブジェクトへ（Meshyは通常1メッシュだが念のため）
    bpy.ops.object.select_all(action="DESELECT")
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    if any(rot):
        import math
        # glTF 取り込みは rotation_mode が QUATERNION ＝ rotation_euler を書いても無視される（実害 2026-11-14）
        obj.rotation_mode = "XYZ"
        obj.rotation_euler = tuple(math.radians(a) for a in rot)
        bpy.ops.object.transform_apply(rotation=True)

    # 実寸正規化を先に（remesh のボクセル径・溶接しきい値を実寸(m)で扱えるようにするため）
    normalize_scale(obj, target_size)
    merged = weld(obj, weld_m) if weld_m > 0.0 else 0
    if drop_thin is not None:
        drop_thin_islands(obj, drop_thin[0], drop_thin[1])
    if core is not None:
        add_core(obj, core)
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
