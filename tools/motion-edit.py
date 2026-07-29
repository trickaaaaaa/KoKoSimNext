"""design-18 §7-2 自作モーキャップの修正ツール（Blender単体・ヘッドレス）。

**2026-07-29 以降、対決ビューの打撃フォーム補正は実行時（`Assets/KokoSim/Match/DuelBatterPose.cs`＋
DuelCalibration のスライダ）へ移した。** 絵を見ながら追い込めること、Humanoidの可動域クランプを
回避できること、FBXを収録のまま保てることが理由。このツールは「クリップそのものを焼き直したい」
場合のために残す（例: 実行時コストを消したい・別プロジェクトへ持ち出す）。


収録モーション（Rokoko→Mixamoリグ）の「絵として気に食わない所」だけを、
ボーンのワールド姿勢を組み直して焼き直す。元クリップの緩急（キーの時間配置）は触らない
＝全フレームに既にキーが打たれている密ベイクなので、フレーム単位で姿勢を差し替えても
タイミングは1ミリも動かない（design-18 §4.5 の「内部緩急を改変しない」を守る）。

直せるもの（--fix で個別に有効化）:
  grip  … 両手が離れて握れていない／利き手でない側の手首が曲がっている
  head  … 頭が振れすぎる（スイングまで投手を見続けさせる）
  knee  … 踏み込み脚の膝が開きすぎる（体を残す）

使い方:
  blender -b --factory-startup --python tools/motion-edit.py -- \
      --in Assets/Motions/Duel/Batter_Batting_01.fbx --out /tmp/out.fbx \
      --fix grip --fix head --fix knee --probe

注意:
  - ポーズボーン行列（pose.bones[].matrix）は**オブジェクト空間**（このFBXは cm・スケール0.01）。
    距離パラメータはメートルで受けて内部でcmへ直す
  - 親を動かすと子のワールドが変わるので、1本触るごとに view_layer.update() を呼ぶ
  - 顔の向きは骨のローカル軸を決め打ちしない。レストポーズのつま先向き＝体の正面から実測する
    （素体・収録ごとに骨の軸が違う。design-18 §3.2.1 と同じ罠）
"""

import bpy
import sys
import math
import json
from mathutils import Vector, Quaternion, Matrix


# ---------------------------------------------------------------- 引数

def parse_args(argv):
    a = argv[argv.index("--") + 1:] if "--" in argv else []
    opts = {"fix": [], "probe": False, "params": {}}
    i = 0
    while i < len(a):
        k = a[i]
        if k == "--fix":
            opts["fix"].append(a[i + 1]); i += 2
        elif k == "--probe":
            opts["probe"] = True; i += 1
        elif k == "--params":
            opts["params"] = json.loads(a[i + 1]); i += 2
        elif k.startswith("--"):
            opts[k[2:]] = a[i + 1]; i += 2
        else:
            i += 1
    return opts


# 既定パラメータ（--params '{"grip_gap_m":0.10}' で個別に上書き）
DEFAULTS = {
    # grip: 両手をくっつける
    "grip_gap_m": 0.10,          # 手首間隔の目標[m]（実物の握り＝拳2つ分）
    "grip_reach_guard": 0.92,    # 肩→手首を腕長のこの割合までに抑える（1.0=伸び切り＝肘ロック）
    # スイング区間だけ許容を上げる。元データはこの辺りで手が0.49m離れる破綻があり、
    # 通常の許容だと握りを詰めきれずに片手打ちの絵になる。実際の打者も接触では腕が伸びる
    # （0.97＝肘角およそ151°・ロックはしない）
    "grip_reach_guard_swing": 0.97,
    "grip_swing_from_sec": 2.75,
    "grip_swing_to_sec": 3.20,
    "grip_flex_side": "Left",    # 手首が折れている側（収録の破綻を戻す）
    "grip_flex_deg": 150.0,      # その手首の目標の伸び[度]（180=まっすぐ・反対の手の実測は158°）
    "grip_flex_strength": 0.85,  # 戻す強さ
    "grip_end_sec": 3.25,        # ここから先は握りを触らない（走り出し＝バットを放す）
    "grip_fade_sec": 0.15,       # 上の手前でフェードアウトする秒数

    # head: 投手を見続けさせる
    "head_strength": 0.85,       # 補正の強さ（1=完全固定・0=素のまま）
    "head_until_sec": 2.95,      # ここまで見続ける（スイング後は素のまま）
    "head_fade_sec": 0.20,       # 上の手前でフェードアウトする秒数
    "head_elev_deg": -4.0,       # 視線の仰角[度]（投手の手元はやや低い＝わずかに下向き）
    "head_neck_share": 0.35,     # 補正のうち首が受け持つ割合（残りが頭）
    # 投手方向（足並び）からのヨー微調整[度]。probe の gazeYaw と同じ符号
    # （＝Unityの実測から逆算した値をそのまま入れられる）
    "head_yaw_offset_deg": 0.0,
    "head_max_vs_chest_deg": 85.0,  # 胸の正面に対する頭の振り向き上限[度]（首の可動域）

    # knee: 踏み込み脚の膝の開きを抑える
    "knee_deg": 18.0,            # 膝を内へ入れる角度[度]（股関節→足首軸まわり）
    "knee_from_sec": 2.60,       # 効き始め
    "knee_full_sec": 2.95,       # 全開
    "knee_to_sec": 3.30,         # ここで0へ戻す（走り出しは自由に）
    "knee_side": "Left",         # 踏み込み脚（右打者は左）
}

FPS = 30.0


# ---------------------------------------------------------------- 下ごしらえ

def load(path):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.fbx(filepath=path, automatic_bone_orientation=False,
                             ignore_leaf_bones=False)
    arm = next(o for o in bpy.data.objects if o.type == "ARMATURE")
    return arm


def action_of(arm):
    return arm.animation_data.action


def frame_range(act):
    lo, hi = act.frame_range
    return int(round(lo)), int(round(hi))


def pb(arm, name):
    return arm.pose.bones["mixamorig:" + name]


def obj_pos(arm, name):
    return pb(arm, name).matrix.translation.copy()


def apply_world_rot(pose_bone, quat, pivot):
    """オブジェクト空間の回転をボーンへ足す（pivot まわり）。子はぶら下がって一緒に回る。"""
    m = pose_bone.matrix.copy()
    r = Matrix.Translation(pivot) @ quat.to_matrix().to_4x4() @ Matrix.Translation(-pivot)
    pose_bone.matrix = r @ m


def update():
    bpy.context.view_layer.update()


def two_bone_ik(arm, root_name, mid_name, tip_name, target):
    """2ボーンIK。tip（子ボーンの根元＝手首/足首）を target へ運ぶ。オブジェクト空間。"""
    root, mid, tip = pb(arm, root_name), pb(arm, mid_name), pb(arm, tip_name)
    s = root.matrix.translation.copy()
    e = mid.matrix.translation.copy()
    w = tip.matrix.translation.copy()
    l1 = (e - s).length
    l2 = (w - e).length
    if l1 < 1e-6 or l2 < 1e-6:
        return
    t = target.copy()
    d = (t - s).length
    dmax = (l1 + l2) * 0.999
    dmin = abs(l1 - l2) * 1.001 + 1e-4
    if d > dmax:
        t = s + (t - s).normalized() * dmax
        d = dmax
    elif d < dmin:
        t = s + (t - s).normalized() * dmin if d > 1e-6 else s + Vector((0, 0, dmin))
        d = dmin
    # 現在の肘（膝）の回転面。伸び切っていると法線が出ないので、その時は素の平面を使う
    axis = (e - s).cross(w - e)
    if axis.length < 1e-5:
        axis = (e - s).cross(Vector((0.0, 0.0, 1.0)))
        if axis.length < 1e-5:
            axis = (e - s).cross(Vector((1.0, 0.0, 0.0)))
    axis.normalize()
    cos_a1 = max(-1.0, min(1.0, (l1 * l1 + d * d - l2 * l2) / (2.0 * l1 * d)))
    a1 = math.acos(cos_a1)
    dir_st = (t - s).normalized()
    # 符号に注意: axis = cross(肩→肘, 肘→手首) に対し、肩→手首 を肘の側へ倒すのは **-a1**。
    # +a1 にすると肘が反対側へ回り、肘が180°裏返る（＝上腕が上を向いて脇が開く。2026-07-29 実害）
    e_new = s + (Quaternion(axis, -a1) @ dir_st) * l1
    # ① 上（大腿・上腕）を回して肘（膝）を新しい位置へ
    apply_world_rot(root, (e - s).rotation_difference(e_new - s), s)
    update()
    # ② 下（前腕・下腿）を回して手首（足首）を target へ
    e2 = mid.matrix.translation.copy()
    w2 = tip.matrix.translation.copy()
    apply_world_rot(mid, (w2 - e2).rotation_difference(t - e2), e2)
    update()


def body_forward(arm):
    """レストポーズの体の正面（オブジェクト空間・水平）。つま先の向きから実測する。"""
    b = arm.data.bones["mixamorig:LeftToeBase"]
    v = (b.tail_local - b.head_local)
    v.z = 0.0
    return v.normalized()


def face_local(arm, bone="Head"):
    """そのボーンのローカル空間での「正面」ベクトル。レストで体の正面と一致する向き。"""
    rest = arm.data.bones["mixamorig:" + bone].matrix_local.to_3x3()
    return rest.inverted() @ body_forward(arm)


def yaw_of(v):
    return math.degrees(math.atan2(v.x, v.y))


def wrap180(a):
    return (a + 180.0) % 360.0 - 180.0


def stance_dir(arm, frame):
    """打者から見た投手方向（オブジェクト空間・水平）。両足並び＝捕手側の足→踏み込み足。"""
    scn = bpy.context.scene
    scn.frame_set(frame)
    update()
    back = obj_pos(arm, "RightFoot")
    front = obj_pos(arm, "LeftFoot")
    v = front - back
    v.z = 0.0
    return v.normalized()


def ramp(t, t0, t1):
    """t0→t1 で 0→1。"""
    if t1 <= t0:
        return 1.0 if t >= t1 else 0.0
    return max(0.0, min(1.0, (t - t0) / (t1 - t0)))


# ---------------------------------------------------------------- 各修正

def arm_length(arm, side):
    s = obj_pos(arm, side + "Arm")
    e = obj_pos(arm, side + "ForeArm")
    w = obj_pos(arm, side + "Hand")
    return (e - s).length + (w - e).length


def clamp_reach(arm, side, origin, target, guard):
    """肩から届く範囲まで target を origin 側へ引き戻す（肘をロックさせない）。"""
    limit = arm_length(arm, side) * guard
    shoulder = obj_pos(arm, side + "Arm")
    if (target - shoulder).length <= limit:
        return target
    lo, hi = 0.0, 1.0
    for _ in range(14):                      # origin→target を二分探索
        m = (lo + hi) * 0.5
        if (origin.lerp(target, m) - shoulder).length <= limit:
            lo = m
        else:
            hi = m
    return origin.lerp(target, lo)


def fix_wrist_flex(arm, side, target_deg, strength):
    """手首の折れを戻す（曲げ面の中だけで回す＝**ロール（ねじれ）は触らない**）。

    収録の左手首は全編95°（180=まっすぐ）で、人体の可動限界まで折れたまま入っている。
    ここで手ごと向きをコピーすると前腕がねじれて「一回転」した絵になる（2026-07-29 実害）。
    """
    el = obj_pos(arm, side + "ForeArm")
    wr = obj_pos(arm, side + "Hand")
    mid = obj_pos(arm, side + "HandMiddle1")
    u = wr - el
    h = mid - wr
    if u.length < 1e-5 or h.length < 1e-5:
        return
    u.normalize()
    h.normalize()
    cur = 180.0 - math.degrees(math.acos(max(-1.0, min(1.0, u.dot(h)))))
    delta = (target_deg - cur) * strength
    if abs(delta) < 0.5:
        return
    axis = h.cross(u)                        # この軸まわり＝曲げ面。ロールは含まない
    if axis.length < 1e-5:
        return
    hand = pb(arm, side + "Hand")
    apply_world_rot(hand, Quaternion(axis.normalized(), math.radians(delta)),
                    hand.matrix.translation.copy())
    update()


def fix_grip(arm, t, p):
    """両手をくっつける。

    寄せ方は**中点を保って両手を半分ずつ**。片手だけ寄せると、その腕が反対の手まで届かず
    伸び切って肘がロックする（2026-07-29 実測: 左手だけ寄せた版は t=3.0 で肘角176°＝棒）。
    手の向き（ロール）は収録のまま残す＝左右の手の相対角100°が自然な握り。
    """
    w = 1.0 - ramp(t, p["grip_end_sec"] - p["grip_fade_sec"], p["grip_end_sec"])
    if w <= 0.0:
        return []
    lh, rh = pb(arm, "LeftHand"), pb(arm, "RightHand")
    a = lh.matrix.translation.copy()
    b = rh.matrix.translation.copy()
    d = a - b
    if d.length < 1e-4:
        return []
    half = p["grip_gap_m"] * 100.0 * 0.5
    u = d.normalized()
    mid = (a + b) * 0.5
    # スイング区間は許容を上げる（山型: 入りで上げ、抜けで戻す）
    sw = min(ramp(t, p["grip_swing_from_sec"] - 0.10, p["grip_swing_from_sec"]),
             1.0 - ramp(t, p["grip_swing_to_sec"], p["grip_swing_to_sec"] + 0.10))
    guard = p["grip_reach_guard"] + (p["grip_reach_guard_swing"] - p["grip_reach_guard"]) * sw
    tl = clamp_reach(arm, "Left", a, a.lerp(mid + u * half, w), guard)
    tr = clamp_reach(arm, "Right", b, b.lerp(mid - u * half, w), guard)
    two_bone_ik(arm, "LeftArm", "LeftForeArm", "LeftHand", tl)
    two_bone_ik(arm, "RightArm", "RightForeArm", "RightHand", tr)
    fix_wrist_flex(arm, p["grip_flex_side"], p["grip_flex_deg"], p["grip_flex_strength"] * w)
    return ["LeftArm", "LeftForeArm", "LeftHand", "RightArm", "RightForeArm", "RightHand"]


def fix_head(arm, t, p, aim_dir, fl, cl):
    """スイングまで投手を見続けさせる。

    目標ヨーは**胸の正面からの相対角で頭打ち**にする。構えの胸は投手から107°向こうを向いており、
    頭だけで投手を向かせると首がねじ切れた絵になる（実物の打者も顎を前肩に寄せる所までしか回らない）。
    """
    w = p["head_strength"] * (1.0 - ramp(t, p["head_until_sec"] - p["head_fade_sec"],
                                         p["head_until_sec"]))
    if w <= 0.0:
        return []
    neck, head = pb(arm, "Neck"), pb(arm, "Head")
    chest_fwd = (pb(arm, "Spine2").matrix.to_3x3() @ cl).normalized()
    yaw_c = yaw_of(chest_fwd)
    lim = p["head_max_vs_chest_deg"]
    yaw_t = yaw_c + max(-lim, min(lim, wrap180(yaw_of(aim_dir) - yaw_c)))
    el = math.radians(p["head_elev_deg"])
    ty = math.radians(yaw_t)
    target = Vector((math.sin(ty) * math.cos(el), math.cos(ty) * math.cos(el), math.sin(el)))

    cur = (head.matrix.to_3x3() @ fl).normalized()
    full = cur.rotation_difference(target)
    final_dir = Quaternion().slerp(full, w) @ cur      # 素の視線と目標の間（strengthぶんだけ寄せる）
    share = p["head_neck_share"]
    if share > 0.0:
        apply_world_rot(neck, Quaternion().slerp(full, w * share), neck.matrix.translation.copy())
        update()
    cur2 = (head.matrix.to_3x3() @ fl).normalized()
    apply_world_rot(head, cur2.rotation_difference(final_dir), head.matrix.translation.copy())
    update()
    return ["Neck", "Head"]


def fix_knee(arm, t, p):
    """踏み込み脚の膝を内へ入れる（股関節→足首軸まわりに脚全体を回す＝足の位置は動かない）。"""
    w = ramp(t, p["knee_from_sec"], p["knee_full_sec"]) * (
        1.0 - ramp(t, p["knee_full_sec"], p["knee_to_sec"]))
    if w <= 0.001:
        return []
    side = p["knee_side"]
    hip = obj_pos(arm, side + "UpLeg")
    ankle = obj_pos(arm, side + "Foot")
    axis = ankle - hip
    if axis.length < 1e-4:
        return []
    foot = pb(arm, side + "Foot")
    foot_rot = foot.matrix.to_3x3().copy()
    sign = 1.0 if side == "Left" else -1.0
    q = Quaternion(axis.normalized(), sign * math.radians(p["knee_deg"]) * w)
    apply_world_rot(pb(arm, side + "UpLeg"), q, hip)
    update()
    # 足の向きは元のまま（脚だけ回して接地を崩さない）
    loc = foot.matrix.translation.copy()
    foot.matrix = Matrix.Translation(loc) @ foot_rot.to_4x4()
    update()
    return [side + "UpLeg", side + "Leg", side + "Foot"]


# ---------------------------------------------------------------- 本体

def main():
    opts = parse_args(sys.argv)
    p = dict(DEFAULTS)
    p.update(opts.get("params", {}))
    src, dst = opts["in"], opts.get("out")
    fixes = set(opts["fix"])

    arm = load(src)
    act = action_of(arm)
    f0, f1 = frame_range(act)
    scn = bpy.context.scene
    scn.frame_start, scn.frame_end = f0, f1

    fl = face_local(arm)
    cl = face_local(arm, "Spine2")
    aim = stance_dir(arm, f0)
    if abs(p["head_yaw_offset_deg"]) > 1e-6:
        # yaw = atan2(x, y) は +Z まわりの回転に対して符号が逆（時計回り基準）
        aim = Quaternion(Vector((0.0, 0.0, 1.0)), -math.radians(p["head_yaw_offset_deg"])) @ aim
    print("[motion-edit] fixes=%s frames=%d..%d faceLocal=%s aim=%s"
          % (sorted(fixes), f0, f1, tuple(round(v, 3) for v in fl),
             tuple(round(v, 3) for v in aim)))

    touched = set()
    for f in range(f0, f1 + 1):
        scn.frame_set(f)
        update()
        t = (f - f0) / FPS
        names = []
        # 親から順に（頭→腕→脚は互いに独立なので順序は問わないが、必ず update を挟む）
        if "head" in fixes:
            names += fix_head(arm, t, p, aim, fl, cl)
        if "grip" in fixes:
            names += fix_grip(arm, t, p)
        if "knee" in fixes:
            names += fix_knee(arm, t, p)
        for n in names:
            pb(arm, n).keyframe_insert("rotation_quaternion", frame=f)
        touched.update(names)

    print("[motion-edit] touched bones: %s" % sorted(touched))

    if opts.get("probe"):
        probe(arm, f0, f1, fl)

    if dst:
        # 取り込み時と同じ軸・単位で書き戻す。leaf ボーンを足すと Unity 側の
        # Humanoid マッピングに余計な骨が増えるので add_leaf_bones=False
        bpy.ops.object.select_all(action="SELECT")
        bpy.ops.export_scene.fbx(
            filepath=dst, use_selection=False, apply_unit_scale=True,
            add_leaf_bones=False, bake_anim=True, bake_anim_use_all_bones=True,
            bake_anim_use_nla_strips=False, bake_anim_use_all_actions=False,
            bake_anim_force_startend_keying=True, bake_anim_step=1.0,
            bake_anim_simplify_factor=0.0, axis_forward="-Z", axis_up="Y",
            path_mode="COPY", embed_textures=False)
        print("[motion-edit] wrote %s" % dst)


def probe(arm, f0, f1, fl):
    scn = bpy.context.scene
    print("PROBE\tf\tt\tgapWrist\tgazeYaw\tgazeElev\tkneeYaw\tLfootZ")
    aim = stance_dir(arm, f0)
    aim_yaw = math.degrees(math.atan2(aim.x, aim.y))
    for f in range(f0, f1 + 1, 3):
        scn.frame_set(f)
        update()
        lw, rw = obj_pos(arm, "LeftHand"), obj_pos(arm, "RightHand")
        g = (lw - rw).length / 100.0
        fw = (pb(arm, "Head").matrix.to_3x3() @ fl).normalized()
        yaw = math.degrees(math.atan2(fw.x, fw.y)) - aim_yaw
        yaw = (yaw + 180.0) % 360.0 - 180.0
        elev = math.degrees(math.asin(max(-1.0, min(1.0, fw.z))))
        th = obj_pos(arm, "LeftLeg") - obj_pos(arm, "LeftUpLeg")
        kyaw = math.degrees(math.atan2(th.x, th.y))
        print("PROBE\t%d\t%.2f\t%.3f\t%.1f\t%.1f\t%.1f\t%.3f"
              % (f, (f - f0) / FPS, g, yaw, elev, kyaw, obj_pos(arm, "LeftFoot").z / 100.0))


main()
