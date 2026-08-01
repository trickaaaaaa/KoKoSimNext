# 球場OBJの検分用レンダ（Blenderヘッドレス）
#
# 使い方:
#   blender --background --python out/stadium/preview.py -- <in.obj> <out_prefix> [size]
#
# エンジンと同じカメラ位置・同じ「縦に収めたい実寸[m]」で撮る（design-18 §3.5.1 の掟）。
# 画角[度]で指定すると欄の縦横比が変わった瞬間に構図が変わるので、必ず実寸で持つ。
#
#   全景     (0,24,-14) → 注視 (0,8,69)   縦155.2m   ＝ CameraPlanBuilder の ZoomMult=1
#   守備寄り (0,10,-14) → 注視 (0,1,45)   縦 28m     ＝ 決着の瞬間の実測レンジ(7〜28m)の広い側
#   投打     (0,1.6,78.44) → 注視 (0,1.2,0) 縦 4m    ＝ 対決ビュー（本塁の裏が背景に映る）
#   俯瞰     (95,70,-40) → 注視 (0,0,55)  縦200m     ＝ ボウルの立体を見るための斜め
#
# 人の高さ1.75mの箱を守備位置に置く（大きさの基準が無いと「チープかどうか」を判定できない）。

import math
import sys

import bpy

ARGS = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
SRC = ARGS[0] if ARGS else "out/stadium/stadium.obj"
OUT = ARGS[1] if len(ARGS) > 1 else "out/stadium/shot"
SIZE = int(ARGS[2]) if len(ARGS) > 2 else 1280

# (名前, カメラ位置, 注視点, 縦に収める実寸[m])
SHOTS = [
    ("wide", (0.0, 24.0, -14.0), (0.0, 8.0, 69.0), 155.2),
    ("field", (0.0, 10.0, -14.0), (0.0, 1.0, 45.0), 28.0),
    ("duel", (0.0, 1.6, 78.44), (0.0, 1.2, 0.0), 4.0),
    ("iso", (95.0, 70.0, -40.0), (0.0, 0.0, 55.0), 200.0),
]

# 守備9人の定位置（engine FieldGeometry.Positions。+x=一塁側 / +z=センター）
FIELDERS = [(0, 18.44), (0, -1.0), (17, 24), (9, 40), (-9, 40), (-17, 24),
            (-37.6, 70.6), (0, 82), (37.6, 70.6)]


def clear():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def add_people():
    """大きさの基準。1.75m の細い箱を守備位置へ。"""
    # ⚠ 目印は**球場に無い色**にする。灰色にしていたせいで、本塁の裏に立つ捕手の目印を
    #    「バックネットの支柱が真後ろに立っている」と誤読した（2026-08-01）。
    #    use_nodes を切らないと、後段の Kd→viewport 転写ループが既定の灰色で塗り潰す。
    mat = bpy.data.materials.new("person")
    mat.use_nodes = False
    mat.diffuse_color = (0.90, 0.20, 0.55, 1.0)
    # OBJ は (x, y, z) → Blender (x, -z, y) で入る。目印も同じ変換に揃えること（前後が逆になる）
    for i, (x, z) in enumerate(FIELDERS):
        bpy.ops.mesh.primitive_cube_add(size=1.0, location=(x, -z, 0.875))
        o = bpy.context.object
        o.name = "person_%d" % i
        o.scale = (0.45, 0.30, 1.75)
        o.data.materials.append(mat)


def setup_shot(name, cam_pos, aim, frame_h):
    aim_e = bpy.data.objects.new("aim", None)
    bpy.context.scene.collection.objects.link(aim_e)
    # OBJ は Y-up、Blender は Z-up。インポータが (x, y, z) → (x, -z, y) へ回すので座標も合わせる
    aim_e.location = (aim[0], -aim[2], aim[1])

    cam_d = bpy.data.cameras.new(name)
    cam_d.sensor_fit = "VERTICAL"          # 縦を実寸で固定する（掟）
    dist = math.dist((cam_pos[0], -cam_pos[2], cam_pos[1]), aim_e.location)
    cam_d.lens = cam_d.sensor_height * dist / max(frame_h, 1e-6)
    cam_d.clip_start = 0.1
    cam_d.clip_end = 2000.0

    cam = bpy.data.objects.new(name, cam_d)
    bpy.context.scene.collection.objects.link(cam)
    cam.location = (cam_pos[0], -cam_pos[2], cam_pos[1])
    c = cam.constraints.new("TRACK_TO")
    c.target = aim_e
    c.track_axis = "TRACK_NEGATIVE_Z"
    c.up_axis = "UP_Y"
    return cam, aim_e


def main():
    clear()
    bpy.ops.wm.obj_import(filepath=SRC, forward_axis="NEGATIVE_Z", up_axis="Y")
    add_people()

    sc = bpy.context.scene
    sc.render.engine = "BLENDER_WORKBENCH"
    sc.display.shading.light = "STUDIO"
    sc.display.shading.color_type = "MATERIAL"
    sc.display.shading.show_specular_highlight = False
    sc.display.shading.show_backface_culling = True     # 巻き順の裏返しを見つけるため
    sc.display.render_aa = "8"
    sc.world = bpy.data.worlds.new("w")
    sc.world.color = (0.60, 0.72, 0.82)                 # 空（実機の背景色ではない・検分用）
    sc.render.film_transparent = False
    sc.render.resolution_x = SIZE
    sc.render.resolution_y = SIZE * 9 // 16
    sc.render.image_settings.file_format = "PNG"

    # MTL の Kd を Workbench が使う viewport 色へ写す（インポータは base_color にしか入れない）
    for m in bpy.data.materials:
        if not m.use_nodes:
            continue
        for n in m.node_tree.nodes:
            if n.type == "BSDF_PRINCIPLED":
                c = n.inputs["Base Color"].default_value
                m.diffuse_color = (c[0], c[1], c[2], 1.0)

    for name, pos, aim, fh in SHOTS:
        cam, aim_e = setup_shot(name, pos, aim, fh)
        sc.camera = cam
        sc.render.filepath = "%s-%s.png" % (OUT, name)
        bpy.ops.render.render(write_still=True)
        print("wrote %s-%s.png" % (OUT, name))
        bpy.data.objects.remove(cam, do_unlink=True)
        bpy.data.objects.remove(aim_e, do_unlink=True)


main()
