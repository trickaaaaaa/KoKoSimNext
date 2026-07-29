# デシメート済み装備FBXの6面プレビュー（Blenderヘッドレス用）
#
# 使い方:
#   blender --background --python tools/aigear-preview.py -- <in.fbx> <out_prefix> [size_px]
#
# 出力: <out_prefix>-{pZ,pX,nZ,nY,pY,q}.png（正射影・単色マット・原点中心）。
# Unityを起動せず生成物の向きと形を確かめるための道具。装備の「装着正位」（正面+Z・上+Y）は
# 資産ごとに寝ていたり前後が逆だったりするので、実測してから正位へ焼く（DuelGearMeshes 参照）。
# Unity側の PreviewRenderUtility でも同じことはできるが、生メッシュ（50万tri級）を
# 取り込ませるとエディタが数分固まるため、デシメート直後の確認はこちらで回す。

import math
import sys

import bpy
from mathutils import Vector

# タグは「Blenderのどの面を見ているか」。Blenderは Z-up・-Y が正面なので front = -Y面。
# **Unityの軸名ではない**（FBX取り込みで軸が入れ替わる）。装着正位の判定は Unity 側の
# 6面レンダで行い、これは形と破綻の有無を見るための道具に留める。
VIEWS = [
    ("front", (0.0, 0.0, 0.0)),      # -Y面（Blenderの正面）・+Zが画面上
    ("side", (0.0, 0.0, -90.0)),     # 横
    ("back", (0.0, 0.0, 180.0)),     # +Y面
    ("side2", (0.0, 0.0, 90.0)),     # 反対側の横
    ("top", (-90.0, 0.0, 0.0)),      # 上から
    ("q", (-70.0, 0.0, -35.0)),      # 3/4
]


def main() -> None:
    argv = sys.argv[sys.argv.index("--") + 1:]
    src, prefix = argv[0], argv[1]
    px = int(argv[2]) if len(argv) > 2 else 420

    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=src)
    meshes = [o for o in bpy.context.scene.objects if o.type == "MESH"]
    if not meshes:
        raise SystemExit(f"no mesh in {src}")
    bpy.ops.object.select_all(action="DESELECT")
    for o in meshes:
        o.select_set(True)
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    obj = bpy.context.view_layer.objects.active
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    bpy.ops.object.origin_set(type="ORIGIN_GEOMETRY", center="BOUNDS")
    obj.location = (0.0, 0.0, 0.0)

    mat = bpy.data.materials.new("preview")
    mat.use_nodes = True
    bsdf = mat.node_tree.nodes["Principled BSDF"]
    bsdf.inputs["Base Color"].default_value = (0.70, 0.72, 0.76, 1.0)
    bsdf.inputs["Roughness"].default_value = 0.55
    obj.data.materials.clear()
    obj.data.materials.append(mat)

    radius = max(obj.dimensions) * 0.5
    cam_data = bpy.data.cameras.new("cam")
    cam_data.type = "ORTHO"
    cam_data.ortho_scale = radius * 2.3
    cam = bpy.data.objects.new("cam", cam_data)
    bpy.context.scene.collection.objects.link(cam)
    bpy.context.scene.camera = cam

    sun_data = bpy.data.lights.new("sun", type="SUN")
    sun_data.energy = 4.0
    sun = bpy.data.objects.new("sun", sun_data)
    sun.rotation_euler = (math.radians(55.0), 0.0, math.radians(35.0))
    bpy.context.scene.collection.objects.link(sun)

    scene = bpy.context.scene
    scene.render.engine = "BLENDER_EEVEE"   # 5.2 の列挙は EEVEE / WORKBENCH / CYCLES
    scene.render.resolution_x = px
    scene.render.resolution_y = px
    scene.render.film_transparent = False
    scene.world = bpy.data.worlds.new("w")
    scene.world.use_nodes = True
    scene.world.node_tree.nodes["Background"].inputs[0].default_value = (0.12, 0.13, 0.16, 1.0)

    dist = radius * 6.0 + 1.0
    for tag, rot in VIEWS:
        rx, ry, rz = (math.radians(a) for a in rot)
        # カメラは -Z を向くので、回転を掛けた「後退方向」へ置いて原点を見る
        cam.rotation_euler = (math.radians(90.0) + rx, ry, rz)
        # rotation_euler を書いても matrix_world は依存グラフ更新まで古いまま＝
        # 更新を挟まないとカメラが前の向きの位置へ飛んで空フレームになる
        bpy.context.view_layer.update()
        cam.location = cam.matrix_world.to_quaternion() @ Vector((0.0, 0.0, dist))
        scene.render.filepath = f"{prefix}-{tag}.png"
        bpy.ops.render.render(write_still=True)

    print(f"AIGEAR_PREVIEW_OK {src} dims={obj.dimensions.x:.3f},"
          f"{obj.dimensions.y:.3f},{obj.dimensions.z:.3f} -> {prefix}-*.png")


main()
