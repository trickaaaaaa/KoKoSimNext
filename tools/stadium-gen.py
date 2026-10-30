#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Regional Japanese ballpark generator -> OBJ + MTL

Coordinate system (strict):
    meters, Y-up right-handed, ground at y = 0
    home plate at the origin (0, 0, 0)
    +Z = center field, +X = first-base side
    fair territory = +-45 deg around +Z

Everything is driven by the constants in PARAMS below.
Only the standard library is used.

    python3 generate_stadium.py            # -> stadium.obj / stadium.mtl
    python3 generate_stadium.py park       # -> park.obj / park.mtl
"""

import math
import os
import sys

# --------------------------------------------------------------------------
# PARAMS - change these to generate a different park
# --------------------------------------------------------------------------
PARAMS = dict(
    # ---- gameplay-critical (must match the engine) ----
    R_CORNER=95.0,        # fence distance down the lines  (theta = +-45 deg)
    R_CENTER=118.0,       # fence distance to center field (theta = 0)
    #   r(theta) = R_CORNER + (R_CENTER - R_CORNER) * cos(2 theta)
    FENCE_H=3.2,          # outfield fence height
    BASE_XY=19.4,         # 1B (BASE_XY,0,BASE_XY), 2B (0,0,2*BASE_XY), 3B (-BASE_XY,0,BASE_XY)
    BASE_SZ=0.56,        # 実寸は15インチ=0.381m。伏角14.5°の守備カメラで読めないので誇張（監督決定 2026-08-01）
    BASE_TH=0.08,
    MOUND_Z=18.44,        # mound center / rubber z
    MOUND_H=0.254,        # rubber + mound apex height
    MOUND_FLAT=0.9,       # 投手板まわりの平坦部（実物の table は 1.5m x 0.9m 程度）
    MOUND_BASE=2.74,      # 18 ft 直径 = 半径 2.74 m。ここで y = 0 に接する

    # ---- surface dressing ----
    GRASS_A=18.4,         # infield grass line d(t) = A cos t + sqrt(R^2 - (A sin t)^2)
    GRASS_R=29.0,
    HOME_CIRCLE=4.3,
    WT=3.5,               # warning track width (inside the fence)
    FENCE_TOP=0.2,        # yellow line at the top of the fence
    FENCE_CAP=0.3,        # fence top cap depth
    PLATE=0.43,
    MARK_Y=0.02,          # top face height of flat markings
    LINE_W=0.12,
    LINE_R0=8.0,
    LINE_R1=95.0,
    BOX_W=1.2,
    BOX_L=1.8,
    BOX_OFF=0.9,
    POLE_H=12.0,

    # ---- stands (one continuous bowl) ----
    STAND_GAP=3.0,        # bowl inner edge, just outside the playing surface
    ROWS=28,              # rows in the foul-side / backstop bowl  -> 11.8 m
    ROWS_OF=24,           # outfield seat rows (from OF_BASE_Y)    -> 13.6 m total
    RISE=0.42,
    TREAD=0.80,
    RAIL_H=1.0,           # parapet above the last row
    CONCOURSE=4.0,        # walkway behind the parapet
    # 外野は柵のすぐ後ろから座席（issue #704）。旧「柵の奥に20mの芝の土手（高さ0→7m）」では
    # 本塁打の99%が土手に落ち、余裕の差（前列/中段/上段）が絵に出ず全部「ぎりぎり」に見えた。
    OF_GAP=2.0,           # outfield: fence -> front wall of the seats (walkway)
    OF_BASE_Y=3.5,        # outfield: height of the first row (just above the fence top)
    SCREEN_H=14.0,        # batter's eye (dead centre)
    SCREEN_DEG=14.0,
    CORNER_DEG=62.0,      # bowl blends to the outfield radius between FOUL_DEG and this
    ROOF_Y=15.5,          # roof slab over the seats behind the plate
    ROOF_DEG=115.0,       # |theta| beyond which the roof exists
    AISLE=9,              # every Nth column is a stairway instead of seats
    # 座席（issue #705）。踏面の奥に背もたれの帯を立てる＝1段まるごと色面だった頃は「段々の斜面」にしか見えなかった。
    # 色は背もたれだけに塗り、踏面はコンクリートに戻す（色の帯とコンクリの帯が交互に並んで初めて座席の列に見える）
    SEAT_BACK_H=0.38,     # 背もたれの高さ（踏面から）
    SEAT_BACK_D=0.10,     # 背もたれの厚み
    SEAT_BACK_IN=0.12,    # 踏面の奥の縁から手前へ何m入れるか（蹴上げと面一にしない）

    # ---- foul territory / enclosure ----
    FOUL_DEG=45.0,
    FOUL_W=18.0,          # foul-line-to-fence offset
    BACK_R=20.0,          # backstop radius（本塁〜バックネット。NPB最低18m・地方球場は16〜20m）
    FOUL_FENCE_H=1.2,
    WALL_R=150.0,         # outer wall: circle of radius WALL_R centered at (0, 0, WALL_CZ)
    WALL_CZ=55.0,
    WALL_H=4.0,

    # ---- tessellation ----
    MOW_N=24,             # 刈り分けの放射ウェッジ本数（SEG_FAIR の約数にすること）

    SEG_FAIR=96,          # columns across the fair sector (also the fence arc)
    SEG_FOUL=30,          # columns per side across foul territory
)
# 案を並べるとき用の上書き（例: PARK_MOW_N=16 python3 generate_stadium.py mow16）
for _k, _v0 in list(PARAMS.items()):
    _v = os.environ.get("PARK_" + _k)
    if _v is not None:
        PARAMS[_k] = int(_v) if isinstance(_v0, int) else float(_v)

P = type("P", (), PARAMS)

D2R = math.pi / 180.0
R2D = 180.0 / math.pi

# ⚠ **色はリニア変換＋ポスタライズ6段を通った「出力」で選ぶ。素の見た目で選ばない。**
#   実機は自前ランバート（Ambient 0.45〜1.0）＋段 38/84/128/171/214 のポスト。
#   光は (-0.35,-0.85,-0.4) から来るので、**本塁を向く面（フェンスの内側・バックスクリーン）は
#   必ず最小陰影 0.45**。そこで段84に乗るにはチャンネルが 0x5D 以上要る。
#   旧値 fence=#24422E は全陰影で (38,38,38) ＝ **一様な黒**に潰れていた（2026-08-01 実機で確認）。
#   検算は tools/stadium-gen.py の隣に置かず、Unity で焼いて撮るのが早い。
MATERIALS = {
    "turf_dark":  (0x4D, 0x80, 0x3D), "turf_light": (0x5C, 0xA3, 0x4D),
    "dirt":       (0xA1, 0x78, 0x54), "fence":      (0x50, 0x78, 0x50),
    "fence_dark": (0x62, 0x8E, 0x62), "conc_light": (0xD6, 0xD6, 0xD6),
    "conc_mid":   (0xAB, 0xAB, 0xAB), "conc_dark":  (0x80, 0x80, 0x80),
    "board":      (0x26, 0x26, 0x26), "white":      (0xD6, 0xD6, 0xD6),
    "seat_red":   (0xAB, 0x54, 0x26), "seat_blue":  (0x26, 0x54, 0x80),
    "yellow":     (0xD6, 0xAB, 0x26), "wood":       (0xA1, 0x78, 0x54),
    "leaf_dark":  (0x4A, 0x72, 0x4A), "leaf_light": (0x5C, 0xA3, 0x4D),
    "screen":     (0x48, 0x6E, 0x48), "steel":      (0x5C, 0x5C, 0x5C),
    "roof":       (0x9A, 0x9A, 0x9A), "berm":       (0x44, 0x77, 0x37),
}


# --------------------------------------------------------------------------
# profile functions
# --------------------------------------------------------------------------
def r_fence(t):
    """outfield fence distance, |t| <= 45 deg"""
    return P.R_CORNER + (P.R_CENTER - P.R_CORNER) * math.cos(2.0 * t)


def r_grass(t):
    a = P.GRASS_A
    return a * math.cos(t) + math.sqrt(P.GRASS_R ** 2 - (a * math.sin(t)) ** 2)


def r_edge(t):
    """boundary of the playing surface for any theta (fence, then foul fence, then backstop)

    ⚠ 塁線から FOUL_W 外側の「直線の壁」までの距離は FOUL_W/sin(phi) だが、この式は
    **phi > 90°（本塁の真横より後ろ）で再び増える**。そのまま使うと境界が 160°で20m →
    180°で25.5m と広がり、**本塁の真後ろに V 字の角**ができる（2026-08-01 監督指摘
    「対決ビューで角ついてる」）。BACK_R が 30m だった頃は円弧が勝っていて出ていなかった。
    直線の壁は本塁の横までで、真後ろは半径 BACK_R の円弧＝phi が 90° を越えたら円弧へ切り替える。
    """
    at = abs(t)
    if at <= P.FOUL_DEG * D2R:
        return r_fence(t)
    phi = at - P.FOUL_DEG * D2R
    if phi >= math.pi / 2.0:
        return P.BACK_R
    return max(P.BACK_R, min(r_fence(P.FOUL_DEG * D2R), P.FOUL_W / math.sin(phi)))


def r_bowl(t):
    """内野〜本塁裏のボウルの内縁。ファウルポール際では外野スタンドの半径へ滑らかに寄せる。
    段差のまま突き合わせると高さ12mの擁壁になり、絵がダムになる（2026-08-01 実測）。"""
    d = abs(t) * R2D
    b = r_edge(t) + P.STAND_GAP
    if d >= P.CORNER_DEG:
        return b
    a = r_fence(P.FOUL_DEG * D2R) + P.OF_GAP
    u = max(0.0, min(1.0, (d - P.FOUL_DEG) / (P.CORNER_DEG - P.FOUL_DEG)))
    u = u * u * (3.0 - 2.0 * u)
    return a + (b - a) * u


def r_wall(t):
    c = P.WALL_CZ
    return c * math.cos(t) + math.sqrt(P.WALL_R ** 2 - (c * math.sin(t)) ** 2)


def pol(r, t, y=0.0):
    return (r * math.sin(t), y, r * math.cos(t))


# --------------------------------------------------------------------------
# mesh accumulator (flat shaded, one-sided, welded vertices)
# --------------------------------------------------------------------------
class Mesh(object):
    def __init__(self):
        self.V, self.N, self.F = [], [], []
        self._vm, self._nm = {}, {}
        self.g, self.m = "none", "dirt"

    def grp(self, g, m):
        self.g, self.m = g, m

    def _vi(self, p):
        k = (round(p[0], 4), round(p[1], 4), round(p[2], 4))
        i = self._vm.get(k)
        if i is None:
            self.V.append(k)
            i = len(self.V)
            self._vm[k] = i
        return i

    def _ni(self, n):
        k = (round(n[0], 3), round(n[1], 3), round(n[2], 3))
        i = self._nm.get(k)
        if i is None:
            self.N.append(k)
            i = len(self.N)
            self._nm[k] = i
        return i

    def tri(self, a, b, c, ref=None):
        """ref = the direction the front face must look at; winding is flipped to match."""
        ux, uy, uz = b[0] - a[0], b[1] - a[1], b[2] - a[2]
        vx, vy, vz = c[0] - a[0], c[1] - a[1], c[2] - a[2]
        n = (uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx)
        L = math.sqrt(n[0] ** 2 + n[1] ** 2 + n[2] ** 2)
        if L < 1e-9:
            return
        n = (n[0] / L, n[1] / L, n[2] / L)
        if ref is not None and (n[0] * ref[0] + n[1] * ref[1] + n[2] * ref[2]) < 0.0:
            b, c = c, b
            n = (-n[0], -n[1], -n[2])
        self.F.append((self.g, self.m, self._ni(n),
                       (self._vi(a), self._vi(b), self._vi(c))))

    def quad(self, a, b, c, d, ref=None):
        self.tri(a, b, c, ref)
        self.tri(a, c, d, ref)

    def box(self, cx, cy, cz, sx, sy, sz, yaw=0.0):
        cw, sw = math.cos(yaw), math.sin(yaw)
        hx, hz = sx / 2.0, sz / 2.0

        def corner(i, j):
            x, z = i * hx, j * hz
            return (cx + x * cw + z * sw, 0.0, cz - x * sw + z * cw)

        c = [corner(-1, -1), corner(1, -1), corner(1, 1), corner(-1, 1)]
        lo, hi = cy, cy + sy
        p = lambda q, y: (q[0], y, q[2])
        self.quad(p(c[0], hi), p(c[1], hi), p(c[2], hi), p(c[3], hi), (0, 1, 0))
        self.quad(p(c[0], lo), p(c[1], lo), p(c[2], lo), p(c[3], lo), (0, -1, 0))
        for k in range(4):
            a, b = c[k], c[(k + 1) % 4]
            mx, mz = (a[0] + b[0]) / 2.0 - cx, (a[2] + b[2]) / 2.0 - cz
            L = math.hypot(mx, mz) or 1.0
            self.quad(p(a, lo), p(b, lo), p(b, hi), p(a, hi), (mx / L, 0.0, mz / L))


def nrm(v):
    L = math.sqrt(v[0] ** 2 + v[1] ** 2 + v[2] ** 2) or 1.0
    return (v[0] / L, v[1] / L, v[2] / L)


def mid_angle(t0, t1):
    """2つの方位[rad]の中心。**端点の平均で採ってはいけない。**

    ボウルは 45°→315° を一本で回すので、角度を (-180,180] へ畳むと 180° をまたぐ列だけ
    端点が +180 と -180 に割れ、平均が**真裏（≒0°）**に飛ぶ。参照法線が逆を向いて、
    その列だけ巻き順と陰影が裏返る＝本塁の真後ろ 2.5° に「角」が立つ
    （2026-08-01 監督指摘。対決カメラがちょうどそこを見ている）。最短差で採れば畳み方に依存しない。
    """
    d = math.atan2(math.sin(t1 - t0), math.cos(t1 - t0))
    return t0 + d / 2.0


def inward(t0, t1):
    tm = mid_angle(t0, t1)
    return (-math.sin(tm), 0.0, -math.cos(tm))


# --------------------------------------------------------------------------
# ground: one partitioned polar mesh - no two coplanar faces ever overlap
# --------------------------------------------------------------------------
def build_ground(M):
    def band(t0, t1, a0, a1, b0, b1, n):
        for k in range(n):
            f0, f1 = k / float(n), (k + 1) / float(n)
            A = pol(a0 + (a1 - a0) * f0, t0)
            B = pol(b0 + (b1 - b0) * f0, t1)
            C = pol(b0 + (b1 - b0) * f1, t1)
            D = pol(a0 + (a1 - a0) * f1, t0)
            M.quad(A, B, C, D, (0, 1, 0))

    # fair sector
    for i in range(P.SEG_FAIR):
        t0 = (-45.0 + i * 90.0 / P.SEG_FAIR) * D2R
        t1 = (-45.0 + (i + 1) * 90.0 / P.SEG_FAIR) * D2R
        mid = (t0 + t1) / 2.0
        A = [0.0, P.HOME_CIRCLE, r_grass(t0), r_fence(t0) - P.WT, r_fence(t0), r_wall(t0)]
        B = [0.0, P.HOME_CIRCLE, r_grass(t1), r_fence(t1) - P.WT, r_fence(t1), r_wall(t1)]
        # 刈り分けは本塁からの放射。MOW_N は SEG_FAIR の約数にすること
        # （割り切れないとウェッジの境目が列の途中に来て、色が1列ぶんずれる）
        wedge = int(math.floor((mid * R2D + 45.0) / (90.0 / P.MOW_N)))
        even = (wedge % 2 == 0)
        defs = [("home_circle", "dirt", 1),
                ("infield_dirt", "dirt", 4),
                ("turf_fair" if even else "turf_stripes",
                 "turf_dark" if even else "turf_light", 6),
                ("warning_track", "dirt", 1),
                ("ground_outer", "turf_dark", 4)]
        for j, (g, m, n) in enumerate(defs):
            M.grp(g, m)
            band(t0, t1, A[j], A[j + 1], B[j], B[j + 1], n)

    # foul territory + everything outside it, both sides
    cols = []
    for i in range(P.SEG_FOUL):
        s = 45.0 + i * 135.0 / P.SEG_FOUL
        e = 45.0 + (i + 1) * 135.0 / P.SEG_FOUL
        cols.append((s * D2R, e * D2R))
        cols.append((-e * D2R, -s * D2R))
    for t0, t1 in cols:
        A = [0.0, P.HOME_CIRCLE, r_edge(t0), r_wall(t0)]
        B = [0.0, P.HOME_CIRCLE, r_edge(t1), r_wall(t1)]
        defs = [("home_circle", "dirt", 1),
                ("turf_foul", "turf_light", 5),
                ("ground_outer", "turf_dark", 5)]
        for j, (g, m, n) in enumerate(defs):
            M.grp(g, m)
            band(t0, t1, A[j], A[j + 1], B[j], B[j + 1], n)


# --------------------------------------------------------------------------
# infield furniture
# --------------------------------------------------------------------------
def build_infield(M):
    # mound: flat table of radius MOUND_FLAT, falling to y=0 at MOUND_BASE
    # 断面は「平坦部→smoothstep で地面へ」。直線の円錐にすると平坦部の縁が20°の崖になり、
    # 25cm しかない起伏が「もっこりした皿」に見える（2026-08-01 監督指摘）。
    #   y(r) = MOUND_H * (1 - smoothstep((r - MOUND_FLAT) / (MOUND_BASE - MOUND_FLAT)))
    # ⚠ Unity 側の接地高 DuelStageRig.Field.cs の FieldGroundY は**この式と同じ形**にすること。
    #   絵と接地が別の断面を持つと、投手がマウンドの斜面に浮く／めり込む。
    def mound_y(r):
        if r <= P.MOUND_FLAT:
            return P.MOUND_H
        if r >= P.MOUND_BASE:
            return 0.0
        u = (r - P.MOUND_FLAT) / (P.MOUND_BASE - P.MOUND_FLAT)
        return P.MOUND_H * (1.0 - u * u * (3.0 - 2.0 * u))

    M.grp("mound", "dirt")
    seg, rings = 48, 10
    rr = [P.MOUND_FLAT + (P.MOUND_BASE - P.MOUND_FLAT) * k / float(rings) for k in range(rings + 1)]
    for i in range(seg):
        a = i / float(seg) * 2 * math.pi
        b = (i + 1) / float(seg) * 2 * math.pi
        p = lambda r, ang: (r * math.sin(ang), mound_y(r), P.MOUND_Z + r * math.cos(ang))
        M.tri((0.0, P.MOUND_H, P.MOUND_Z), p(rr[0], a), p(rr[0], b), (0, 1, 0))
        for k in range(rings):
            M.quad(p(rr[k + 1], a), p(rr[k + 1], b), p(rr[k], b), p(rr[k], a),
                   nrm((math.sin((a + b) / 2) * 0.4, 1.0, math.cos((a + b) / 2) * 0.4)))

    M.grp("pitcher_rubber", "white")
    M.box(0.0, P.MOUND_H, P.MOUND_Z, 0.61, 0.06, 0.30)

    # 一塁・三塁は**塁の全部がフェア地域**に入る（塁線は塁の外側の辺に沿って走る）。
    # engine の塁の座標 (±19.4, 19.4) は塁線と塁間線の**交点＝塁の外側の辺**であって中心ではない
    # （実物も90フィートは塁の外角までを測る）。中心を交点へ置くと塁の半分がファウル地域へ
    # はみ出す（2026-08-01 監督指摘）。塁線方向 u=(sx,1)/√2 に対しフェア側の法線 n=(-sx,1)/√2 へ
    # 半辺ぶん寄せると、外側の辺がちょうど塁線に乗る。
    h = P.BASE_SZ / 2.0
    q = math.sqrt(2.0) / 2.0
    for g, sx in (("base_first", 1.0), ("base_third", -1.0)):
        M.grp(g, "white")
        M.box(sx * P.BASE_XY - sx * q * h, 0.0, P.BASE_XY + q * h,
              P.BASE_SZ, P.BASE_TH, P.BASE_SZ, 45.0 * D2R)
    # 二塁は塁線に絡まないので交点そのものが中心（実物も二塁だけ塁の中心が交点）
    M.grp("base_second", "white")
    M.box(0.0, 0.0, 2 * P.BASE_XY, P.BASE_SZ, P.BASE_TH, P.BASE_SZ, 45.0 * D2R)

    y, h = P.MARK_Y, P.PLATE / 2.0
    M.grp("home_plate", "white")
    M.quad((-h, y, -h), (h, y, -h), (h, y, h), (-h, y, h), (0, 1, 0))

    M.grp("batter_boxes", "white")
    w = P.LINE_W
    for sgn in (-1, 1):
        cx = sgn * P.BOX_OFF
        x0, x1 = cx - P.BOX_W / 2, cx + P.BOX_W / 2
        z0, z1 = -P.BOX_L / 2, P.BOX_L / 2
        for a, b, c, d in ((x0, z0, x1, z0 + w), (x0, z1 - w, x1, z1),
                           (x0, z0 + w, x0 + w, z1 - w), (x1 - w, z0 + w, x1, z1 - w)):
            M.quad((a, y, b), (c, y, b), (c, y, d), (a, y, d), (0, 1, 0))

    M.grp("foul_lines", "white")
    hw = P.LINE_W / 2.0
    for sgn in (-1, 1):
        t = sgn * 45.0 * D2R
        u = (math.sin(t), 0.0, math.cos(t))
        n = (math.cos(t), 0.0, -math.sin(t))
        pt = lambda r, o: (u[0] * r + n[0] * o, y, u[2] * r + n[2] * o)
        M.quad(pt(P.LINE_R0, -hw), pt(P.LINE_R1, -hw),
               pt(P.LINE_R1, hw), pt(P.LINE_R0, hw), (0, 1, 0))


# --------------------------------------------------------------------------
# fences
# --------------------------------------------------------------------------
def build_fences(M):
    seg = P.SEG_FAIR
    h_top = P.FENCE_H - P.FENCE_TOP
    for i in range(seg):
        t0 = (-45.0 + i * 90.0 / seg) * D2R
        t1 = (-45.0 + (i + 1) * 90.0 / seg) * D2R
        r0, r1 = r_fence(t0), r_fence(t1)
        inw = inward(t0, t1)
        out = (-inw[0], 0.0, -inw[2])
        M.grp("fence_outfield", "fence")
        M.quad(pol(r0, t0, 0), pol(r1, t1, 0), pol(r1, t1, h_top), pol(r0, t0, h_top), inw)
        M.grp("fence_outfield", "yellow")
        M.quad(pol(r0, t0, h_top), pol(r1, t1, h_top),
               pol(r1, t1, P.FENCE_H), pol(r0, t0, P.FENCE_H), inw)
        M.grp("fence_outfield", "fence_dark")
        M.quad(pol(r0, t0, P.FENCE_H), pol(r1, t1, P.FENCE_H),
               pol(r1 + P.FENCE_CAP, t1, P.FENCE_H), pol(r0 + P.FENCE_CAP, t0, P.FENCE_H), (0, 1, 0))
        M.quad(pol(r0 + P.FENCE_CAP, t0, 0), pol(r1 + P.FENCE_CAP, t1, 0),
               pol(r1 + P.FENCE_CAP, t1, P.FENCE_H), pol(r0 + P.FENCE_CAP, t0, P.FENCE_H), out)

    M.grp("foul_poles", "yellow")
    for sgn in (-1, 1):
        t = sgn * 45.0 * D2R
        r = r_fence(t)
        M.box(r * math.sin(t), 0.0, r * math.cos(t), 0.4, P.POLE_H, 0.4, 45.0 * D2R)

    # low fence around foul territory
    seg = P.SEG_FOUL
    h = P.FOUL_FENCE_H
    for d in (1, -1):
        for i in range(seg):
            if d > 0:
                a, b = 45.0 + i * 135.0 / seg, 45.0 + (i + 1) * 135.0 / seg
            else:
                a, b = -45.0 - (i + 1) * 135.0 / seg, -45.0 - i * 135.0 / seg
            t0, t1 = a * D2R, b * D2R
            r0, r1 = r_edge(t0), r_edge(t1)
            if abs(r0 - r1) > 40.0:      # skip the pole-corner jump
                continue
            inw = inward(t0, t1)
            M.grp("fence_foul", "fence")
            M.quad(pol(r0, t0, 0), pol(r1, t1, 0), pol(r1, t1, h), pol(r0, t0, h), inw)
            M.grp("fence_foul", "fence_dark")
            M.quad(pol(r0, t0, h), pol(r1, t1, h),
                   pol(r1 + 0.3, t1, h), pol(r0 + 0.3, t0, h), (0, 1, 0))

    # backstop: posts + rails only, no netting
    M.grp("backstop", "conc_dark")
    r = P.BACK_R + 1.0
    # ⚠ 支柱の割り付けは**奇数本にしない**。118〜242° を8等分すると真ん中(180°)に1本来て、
    #   対決ビュー（本塁の真後ろが背景）で打者の背中に柱が刺さる（2026-08-01 実測）。
    #   9等分＝10本にして 180° を柱の間に落とす。
    # ⚠ 本塁の**真後ろは空けておく**。対決カメラ (0,1.6,78.44) は本塁の裏を背景に映すので、
    #   支柱が入ると打者の両脇に太い柱が立つ（BACK_R を 30→20m に詰めた 2026-08-01 に実害）。
    #   枠は r=21 のところで幅 9.0m ＝ ±12.3°。余裕を見て中央 ±22° を柱なしの一枚にする。
    for lo, hi, n in ((118.0, 158.0, 3), (202.0, 242.0, 3)):
        for i in range(n + 1):
            t = (lo + i * (hi - lo) / n) * D2R
            M.box(r * math.sin(t), 0.0, r * math.cos(t), 0.5, 9.0, 0.5, -t)
    for y in (4.2, 8.5):
        seg = 16
        for i in range(seg):
            t0 = (118.0 + i * 124.0 / seg) * D2R
            t1 = (118.0 + (i + 1) * 124.0 / seg) * D2R
            inw = inward(t0, t1)
            M.quad(pol(r, t0, y), pol(r, t1, y), pol(r, t1, y + 0.4), pol(r, t0, y + 0.4), inw)
            M.quad(pol(r, t0, y + 0.4), pol(r, t1, y + 0.4),
                   pol(r + 0.4, t1, y + 0.4), pol(r + 0.4, t0, y + 0.4), (0, 1, 0))


# --------------------------------------------------------------------------
# stands - ONE continuous bowl
#
# 前版は「本塁裏の弧」「一塁側の直線」「三塁側の直線」の3つを別々に置いていたので、
# 間に芝がむき出しの隙間が空き、どれも地面に浮いた独立した箱に見えていた。
# 球場が球場に見えるのは連続した「すり鉢」の体積なので、45°→315° を一本で回す。
# --------------------------------------------------------------------------
def wrap(d):
    """degrees -> (-180, 180]"""
    return ((d + 180.0) % 360.0) - 180.0


# 座席の列（issue #705）。観客は Unity の焼き込み（DuelStadiumBaker）がこの一覧から撒く
#   (group, t0, t1, r0, r1, y_tread, tread, row)
SEAT_ROWS = []


def steps(M, cols, rows):
    """cols = [(t0, t1, r0, r1, y_base, group, seat_material)] の段組み。

    1列ぶんで「蹴上げ→踏面」を rows 回、最後に手すり帯・通路・外側の壁まで出す。
    列ごとに閉じているので、隣の列と半径が違っても隙間が空かない。
    """
    rise, tread = P.RISE, P.TREAD
    for i, (t0, t1, r0, r1, yb, g, seat) in enumerate(cols):
        inw = inward(t0, t1)
        out = (-inw[0], 0.0, -inw[2])
        aisle = (i % P.AISLE == 0)          # 階段。近景の密度はここで出る
        for k in range(rows):
            a0, a1 = r0 + k * tread, r1 + k * tread
            y0, y1 = yb + k * rise, yb + (k + 1) * rise
            M.grp(g, "conc_dark")
            M.quad(pol(a0, t0, y0), pol(a1, t1, y0), pol(a1, t1, y1), pol(a0, t0, y1), inw)
            M.grp(g, "conc_light" if aisle else "conc_mid")
            M.quad(pol(a0, t0, y1), pol(a1, t1, y1),
                   pol(a1 + tread, t1, y1), pol(a0 + tread, t0, y1), (0, 1, 0))
            if not aisle:
                # 背もたれの帯（前面＋天面）。前面は球場の内側を向く
                b0f, b1f = a0 + tread - P.SEAT_BACK_IN - P.SEAT_BACK_D, a1 + tread - P.SEAT_BACK_IN - P.SEAT_BACK_D
                b0b, b1b = b0f + P.SEAT_BACK_D, b1f + P.SEAT_BACK_D
                yt_ = y1 + P.SEAT_BACK_H
                M.grp(g, seat)
                M.quad(pol(b0f, t0, y1), pol(b1f, t1, y1), pol(b1f, t1, yt_), pol(b0f, t0, yt_), inw)
                M.quad(pol(b0f, t0, yt_), pol(b1f, t1, yt_), pol(b1b, t1, yt_), pol(b0b, t0, yt_), (0, 1, 0))
                SEAT_ROWS.append((g, t0, t1, a0, a1, y1, tread, k))
        rb0, rb1 = r0 + rows * tread, r1 + rows * tread
        yt = yb + rows * rise
        M.grp(g, "conc_light")
        M.quad(pol(rb0, t0, yt), pol(rb1, t1, yt),
               pol(rb1, t1, yt + P.RAIL_H), pol(rb0, t0, yt + P.RAIL_H), inw)
        M.quad(pol(rb0, t0, yt + P.RAIL_H), pol(rb1, t1, yt + P.RAIL_H),
               pol(rb1 + P.CONCOURSE, t1, yt + P.RAIL_H),
               pol(rb0 + P.CONCOURSE, t0, yt + P.RAIL_H), (0, 1, 0))
        # 外側の壁。一枚の面で立ち上げると高さ17mののっぺりした灰色になるので、
        # 腰の段と付け柱で縦横のリズムを入れる（近景でも遠景でも効く）
        rw0, rw1 = rb0 + P.CONCOURSE, rb1 + P.CONCOURSE
        yw = yt + P.RAIL_H
        M.grp(g, "conc_dark")
        M.quad(pol(rw0, t0, 0), pol(rw1, t1, 0), pol(rw1, t1, 3.0), pol(rw0, t0, 3.0), out)
        M.grp(g, "conc_mid")
        M.quad(pol(rw0, t0, 3.0), pol(rw1, t1, 3.0), pol(rw1, t1, yw), pol(rw0, t0, yw), out)
        if i % 6 == 0:
            tm = mid_angle(t0, t1)
            M.grp(g, "conc_light")
            M.box((rw0 + 0.25) * math.sin(tm), 0.0, (rw0 + 0.25) * math.cos(tm),
                  1.2, yw, 0.6, -tm)


def build_stands(M):
    # ---- 一塁側 → 本塁裏 → 三塁側（45°から315°まで一本） ----
    segs = 108
    cols = []
    for i in range(segs):
        d0 = 45.0 + i * 270.0 / segs
        d1 = 45.0 + (i + 1) * 270.0 / segs
        dm = wrap((d0 + d1) / 2.0)
        t0, t1 = wrap(d0) * D2R, wrap(d1) * D2R
        g = ("stand_backstop" if abs(dm) > 110.0 else
             "stand_first" if dm > 0 else "stand_third")
        seat = "seat_blue" if abs(dm) > 110.0 else "seat_red"
        cols.append((t0, t1, r_bowl(t0), r_bowl(t1), 0.0, g, seat))
    steps(M, cols, P.ROWS)

    # ---- 本塁裏の屋根（片持ち。上端に影の帯が出るだけで一気に球場になる） ----
    seg_r = 40
    for i in range(seg_r):
        d0 = P.ROOF_DEG + i * (360.0 - 2 * P.ROOF_DEG) / seg_r
        d1 = P.ROOF_DEG + (i + 1) * (360.0 - 2 * P.ROOF_DEG) / seg_r
        t0, t1 = wrap(d0) * D2R, wrap(d1) * D2R
        ri0 = r_bowl(t0) + P.ROWS * P.TREAD * 0.45
        ri1 = r_bowl(t1) + P.ROWS * P.TREAD * 0.45
        ro0 = r_bowl(t0) + P.ROWS * P.TREAD + P.CONCOURSE
        ro1 = r_bowl(t1) + P.ROWS * P.TREAD + P.CONCOURSE
        inw = inward(t0, t1)
        M.grp("roof_home", "roof")
        M.quad(pol(ri0, t0, P.ROOF_Y), pol(ri1, t1, P.ROOF_Y),
               pol(ro1, t1, P.ROOF_Y), pol(ro0, t0, P.ROOF_Y), (0, 1, 0))
        M.grp("roof_home", "conc_dark")
        M.quad(pol(ri0, t0, P.ROOF_Y - 0.8), pol(ri1, t1, P.ROOF_Y - 0.8),
               pol(ro1, t1, P.ROOF_Y - 0.8), pol(ro0, t0, P.ROOF_Y - 0.8), (0, -1, 0))
        M.grp("roof_home", "conc_light")
        M.quad(pol(ri0, t0, P.ROOF_Y - 0.8), pol(ri1, t1, P.ROOF_Y - 0.8),
               pol(ri1, t1, P.ROOF_Y), pol(ri0, t0, P.ROOF_Y), inw)
        if i % 5 == 0:                                   # 屋根を支える柱
            M.grp("roof_home", "steel")
            M.box(ro0 * math.sin(t0), P.ROWS * P.RISE + P.RAIL_H,
                  ro0 * math.cos(t0), 0.6, P.ROOF_Y - P.ROWS * P.RISE - P.RAIL_H, 0.6, -t0)

    # ---- 外野: 柵のすぐ後ろから座席の段（issue #704） ----
    # 本塁打の着弾が前列・中段・上段に散るのは、座席が柵の直後から始まるときだけ。
    # バックスクリーンの扇（±SCREEN_DEG）は座席を置かず、下の「スクリーン裏の壁」で塞ぐ。
    of = []
    segs_side = 14
    for lo, hi in ((-45.0, -P.SCREEN_DEG), (P.SCREEN_DEG, 45.0)):
        for i in range(segs_side):
            d0 = lo + i * (hi - lo) / segs_side
            d1 = lo + (i + 1) * (hi - lo) / segs_side
            t0, t1 = d0 * D2R, d1 * D2R
            f0, f1 = r_fence(t0) + P.OF_GAP, r_fence(t1) + P.OF_GAP
            g = "stand_outfield_right" if (d0 + d1) / 2.0 >= 0 else "stand_outfield_left"
            # 最前列の前壁（通路の床 y=0 から1段目まで）
            M.grp(g, "conc_mid")
            M.quad(pol(f0, t0, 0), pol(f1, t1, 0), pol(f1, t1, P.OF_BASE_Y), pol(f0, t0, P.OF_BASE_Y),
                   inward(t0, t1))
            of.append((t0, t1, f0, f1, P.OF_BASE_Y, g, "seat_blue"))
    steps(M, of, P.ROWS_OF)

    # ---- スクリーン裏の壁（中堅の扇。座席の代わりにバックスクリーンと同じ高さの塊で塞ぐ） ----
    depth = P.OF_GAP + P.ROWS_OF * P.TREAD + P.CONCOURSE
    seg_c = 14
    for i in range(seg_c):
        d0 = -P.SCREEN_DEG + i * 2 * P.SCREEN_DEG / seg_c
        d1 = -P.SCREEN_DEG + (i + 1) * 2 * P.SCREEN_DEG / seg_c
        t0, t1 = d0 * D2R, d1 * D2R
        a0, a1 = r_fence(t0) + 5.6, r_fence(t1) + 5.6       # バックスクリーン上面（+2.6〜+5.6）の奥から
        b0, b1 = r_fence(t0) + depth, r_fence(t1) + depth
        M.grp("backscreen", "screen")
        M.quad(pol(a0, t0, P.SCREEN_H), pol(a1, t1, P.SCREEN_H),
               pol(b1, t1, P.SCREEN_H), pol(b0, t0, P.SCREEN_H), (0, 1, 0))
        out = inward(t0, t1)
        M.grp("backscreen", "conc_dark")
        M.quad(pol(b0, t0, 0), pol(b1, t1, 0), pol(b1, t1, P.SCREEN_H), pol(b0, t0, P.SCREEN_H),
               (-out[0], 0.0, -out[2]))
    # 扇の両脇の側壁（座席の段から見える面）
    for sgn in (-1, 1):
        t = sgn * P.SCREEN_DEG * D2R
        a, b = r_fence(t) + 2.6, r_fence(t) + depth
        n = (math.cos(t) * sgn, 0.0, -math.sin(t) * sgn)
        M.grp("backscreen", "conc_dark")
        M.quad(pol(a, t, 0), pol(b, t, 0), pol(b, t, P.SCREEN_H), pol(a, t, P.SCREEN_H), n)

    # ---- バックスクリーン（打者の目線を切る暗い壁。無いと球場に見えない） ----
    seg_s = 14
    for i in range(seg_s):
        d0 = -P.SCREEN_DEG + i * 2 * P.SCREEN_DEG / seg_s
        d1 = -P.SCREEN_DEG + (i + 1) * 2 * P.SCREEN_DEG / seg_s
        t0, t1 = d0 * D2R, d1 * D2R
        f0, f1 = r_fence(t0) + 2.6, r_fence(t1) + 2.6
        inw = inward(t0, t1)
        M.grp("backscreen", "screen")
        M.quad(pol(f0, t0, 0), pol(f1, t1, 0),
               pol(f1, t1, P.SCREEN_H), pol(f0, t0, P.SCREEN_H), inw)
        M.grp("backscreen", "conc_dark")
        M.quad(pol(f0, t0, P.SCREEN_H), pol(f1, t1, P.SCREEN_H),
               pol(f1 + 3.0, t1, P.SCREEN_H), pol(f0 + 3.0, t0, P.SCREEN_H), (0, 1, 0))


# --------------------------------------------------------------------------
# scoreboard / lights / wall / trees / dugouts
# --------------------------------------------------------------------------
def build_props(M):
    # ---- スコアボード（バックスクリーンの上。手動式の黒板） ----
    z0 = r_fence(0.0) + 9.0
    w, h, y0 = 44.0, 13.0, P.SCREEN_H + 1.0
    M.grp("scoreboard", "conc_light")
    M.box(0.0, y0 - 1.0, z0 + 0.7, w + 2.4, h + 2.0, 1.0)         # 白枠
    M.grp("scoreboard", "board")
    M.box(0.0, y0, z0, w, h, 0.5)                                  # 黒板
    M.grp("scoreboard", "conc_light")
    for k in range(1, 4):                                          # 横桟
        M.box(0.0, y0 + h * k / 4.0 - 0.15, z0 - 0.25, w, 0.3, 0.3)
    for k in range(1, 5):                                          # 縦桟
        M.box(-w / 2.0 + w * k / 5.0 - 0.15, y0, z0 - 0.25, 0.3, h, 0.3)
    M.grp("scoreboard", "steel")
    for x in (-17.0, -6.0, 6.0, 17.0):                             # 脚
        M.box(x, 0.0, z0 + 0.9, 1.3, y0, 1.3)
    for k in range(1, 3):                                          # 脚の横帯
        M.box(0.0, y0 * k / 3.0, z0 + 0.9, 36.0, 0.4, 1.0)

    # ---- 照明塔（トラス脚＋灯体の格子。棒に箱だと街灯に見える） ----
    towers = [(26.0, 134.0, 34.0), (-26.0, 134.0, 34.0),
              (60.0, 104.0, 32.0), (-60.0, 104.0, 32.0),
              (112.0, 66.0, 30.0), (-112.0, 66.0, 30.0)]
    for i, (deg, r, h) in enumerate(towers):
        t = deg * D2R
        x, z = r * math.sin(t), r * math.cos(t)
        u = (math.sin(t), math.cos(t))
        n = (math.cos(t), -math.sin(t))
        g = "light_tower_%d" % (i + 1)
        hs = h - 5.0
        M.grp(g, "steel")
        for a in (-1.7, 1.7):
            for b in (-1.3, 1.3):
                M.box(x + n[0] * a + u[0] * b, 0.0, z + n[1] * a + u[1] * b,
                      0.45, hs, 0.45, -t)
        for k in range(1, 5):
            M.box(x, hs * k / 5.0, z, 3.9, 0.35, 3.1, -t)
        M.grp(g, "conc_light")
        for cx in (-4.6, 0.0, 4.6):
            for cy in (0.0, 2.3):
                M.box(x + n[0] * cx, hs + cy, z + n[1] * cx, 4.2, 1.9, 0.9, -t)

    # ---- 外周壁 ----
    seg = 72
    for i in range(seg):
        t0 = (-180.0 + i * 360.0 / seg) * D2R
        t1 = (-180.0 + (i + 1) * 360.0 / seg) * D2R
        r0, r1 = r_wall(t0), r_wall(t1)
        inw = inward(t0, t1)
        M.grp("outer_wall", "conc_dark")
        M.quad(pol(r0, t0, 0), pol(r1, t1, 0), pol(r1, t1, P.WALL_H), pol(r0, t0, P.WALL_H), inw)
        M.grp("outer_wall", "conc_mid")
        M.quad(pol(r0, t0, P.WALL_H), pol(r1, t1, P.WALL_H),
               pol(r1 + 0.4, t1, P.WALL_H), pol(r0 + 0.4, t0, P.WALL_H), (0, 1, 0))

    # ---- 木立（等間隔・同寸法だとクリップアートに見える。8面・寸法と間隔をばらす） ----
    seed = [12345]

    def rnd():
        seed[0] = (seed[0] * 1103515245 + 12345) % 2147483648
        return seed[0] / 2147483648.0

    n_trees = 38
    for i in range(n_trees):
        t = (-180.0 + (i + 0.5) * 360.0 / n_trees + (rnd() - 0.5) * 8.0) * D2R
        r = r_wall(t) - 7.0 - rnd() * 12.0
        x, z = r * math.sin(t), r * math.cos(t)
        h = 11.0 + rnd() ** 2 * 13.0
        rad = 2.6 + rnd() * 2.6
        M.grp("trees", "wood")
        M.box(x, 0.0, z, 0.6, h * 0.4, 0.6)
        M.grp("trees", "leaf_light" if i % 3 == 0 else "leaf_dark")
        yb, ym, yt = h * 0.32, h * 0.55, h
        for k in range(8):
            a = k / 8.0 * 2 * math.pi
            b = (k + 1) / 8.0 * 2 * math.pi
            p = lambda ang, rr, y: (x + rr * math.sin(ang), y, z + rr * math.cos(ang))
            d = nrm((math.sin((a + b) / 2), 0.5, math.cos((a + b) / 2)))
            M.tri(p(a, rad, ym), p(b, rad, ym), (x, yt, z), d)
            M.quad(p(a, rad * 0.45, yb), p(b, rad * 0.45, yb),
                   p(b, rad, ym), p(a, rad, ym),
                   nrm((math.sin((a + b) / 2), 0.2, math.cos((a + b) / 2))))

    # ---- ダグアウト（屋根＋背壁＋妻壁。閉じた箱だと便所に見える） ----
    for g, sgn in (("dugout_first", 1), ("dugout_third", -1)):
        t = sgn * 45.0 * D2R
        u = (math.sin(t), 0.0, math.cos(t))
        n = (math.cos(t) * sgn, 0.0, -math.sin(t) * sgn)
        s, o = 34.0, 16.0          # ファウル柵(18m)の際。r = 28 m の立入禁止円の外
        cx, cz = u[0] * s + n[0] * o, u[2] * s + n[2] * o
        M.grp(g, "conc_dark")
        M.box(cx + n[0] * 1.4, 0.0, cz + n[2] * 1.4, 12.0, 2.3, 0.4, -t)      # 背壁
        for e in (-5.8, 5.8):
            M.box(cx + u[0] * e, 0.0, cz + u[2] * e, 0.4, 2.3, 3.0, -t)       # 妻壁
        M.grp(g, "conc_mid")
        M.box(cx, 2.3, cz, 12.6, 0.4, 3.6, -t)                                # 屋根
        M.grp(g, "wood")
        M.box(cx + n[0] * 0.9, 0.45, cz + n[2] * 0.9, 11.0, 0.12, 0.5, -t)    # ベンチ


# --------------------------------------------------------------------------
# writers + report
# --------------------------------------------------------------------------
def write_obj(M, base):
    order, seen = [], set()
    for f in M.F:
        if f[0] not in seen:
            seen.add(f[0])
            order.append(f[0])
    out = ["# Regional ballpark (Japan, 5,000-15,000 seats) - procedurally generated",
           "# meters, Y-up right-handed, home plate at (0,0,0), +Z = center field, +X = first base",
           "mtllib %s.mtl" % os.path.basename(base)]
    for v in M.V:
        out.append("v %g %g %g" % v)
    for n in M.N:
        out.append("vn %g %g %g" % n)
    counts = {}
    for g in order:
        out.append("o " + g)
        cur = None
        for gg, m, ni, vs in M.F:
            if gg != g:
                continue
            if m != cur:
                out.append("usemtl " + m)
                cur = m
            out.append("f %d//%d %d//%d %d//%d" % (vs[0], ni, vs[1], ni, vs[2], ni))
            counts[g] = counts.get(g, 0) + 1
    with open(base + ".obj", "w") as fh:
        fh.write("\n".join(out) + "\n")
    return order, counts


def write_seats(base):
    """座席の列を TSV で書く（issue #705）。観客の形・密度・服の色は Unity 側の焼き込みが決める。
    ここが持つのは「どこに座れるか」だけ＝形の単一ソースはこのスクリプトのまま。"""
    out = ["# group\tt0_rad\tt1_rad\tr0_front_m\tr1_front_m\ty_tread_m\ttread_m\trow\tback_in_m"]
    for g, t0, t1, a0, a1, y, tr, k in SEAT_ROWS:
        out.append("%s\t%.6f\t%.6f\t%.4f\t%.4f\t%.4f\t%.4f\t%d\t%.4f"
                   % (g, t0, t1, a0, a1, y, tr, k, P.SEAT_BACK_IN + P.SEAT_BACK_D))
    with open(base + ".seats.tsv", "w") as fh:
        fh.write("\n".join(out) + "\n")
    return len(SEAT_ROWS)


def write_mtl(base):
    out = ["# stadium materials (Kd only)"]
    for k, c in MATERIALS.items():
        out.append("newmtl %s\nKd %.4f %.4f %.4f" % (k, c[0] / 255.0, c[1] / 255.0, c[2] / 255.0))
    with open(base + ".mtl", "w") as fh:
        fh.write("\n".join(out) + "\n")


GROUND_GROUPS = {"turf_fair", "turf_stripes", "turf_foul", "infield_dirt", "warning_track",
                 "home_circle", "ground_outer", "foul_lines", "home_plate", "batter_boxes",
                 "mound", "pitcher_rubber", "base_first", "base_second", "base_third"}


def report(M, order, counts):
    print("triangles total: %d   vertices: %d   normals: %d" % (len(M.F), len(M.V), len(M.N)))
    for g in order:
        print("   %-22s %5d" % (g, counts[g]))
    mn = [min(v[i] for v in M.V) for i in range(3)]
    mx = [max(v[i] for v in M.V) for i in range(3)]
    print("bbox  x %.2f .. %.2f   y %.2f .. %.2f   z %.2f .. %.2f"
          % (mn[0], mx[0], mn[1], mx[1], mn[2], mx[2]))
    print("fence samples:", ", ".join(
        "%.2f deg -> %.3f m" % (d, r_fence(d * D2R))
        for d in (0, 11.25, 22.5, 33.75, 45, -11.25, -22.5, -33.75, -45)))

    gv = {}
    for g, m, ni, vs in M.F:
        gv.setdefault(g, set()).update(vs)
    z1 = z2 = z3 = 0
    # 境界そのもの（フェンス・ネット・ポール）とダグアウトは、プレー面の内側に有るのが正しい
    edge = {"fence_outfield", "foul_poles", "fence_foul", "backstop",
            "dugout_first", "dugout_third"}
    stands = set(order) - GROUND_GROUPS - edge
    back = 1e9
    for g, idx in gv.items():
        for i in idx:
            x, y, z = M.V[i - 1]
            if g not in GROUND_GROUPS and math.hypot(x, z) < 12.0:
                z1 += 1                                   # 本塁まわり（打者・捕手・球審）
            # 守備カメラは (0,10,-14)/(0,24,-14) から +Z を向く。**背後（z < -14）は写らない**ので
            # 距離だけで弾くとバックネットが毎回引っかかる（実測17件）。前方だけを見る。
            if g not in GROUND_GROUPS and z > -14.0:
                for cy in (10.0, 24.0):
                    if math.sqrt(x * x + (y - cy) ** 2 + (z + 14.0) ** 2) < 8.0:
                        z2 += 1
                        break
            if g in stands and math.hypot(x, z) < r_edge(math.atan2(x, z)):
                z3 += 1                                   # プレー面の内側へ食い込んでいる
            if g == "backstop":
                back = min(back, math.hypot(x, z))
    print("backstop distance: %.1f m from home" % back)
    print("keep-out violations:  zone1(home r<12m)=%d  zone2(camera)=%d  zone3(inside play surface)=%d"
          % (z1, z2, z3))


def main():
    base = sys.argv[1] if len(sys.argv) > 1 else "stadium"
    M = Mesh()
    build_ground(M)
    build_infield(M)
    build_fences(M)
    build_stands(M)
    build_props(M)
    order, counts = write_obj(M, base)
    write_mtl(base)
    n_rows = write_seats(base)
    report(M, order, counts)
    print("seat rows: %d" % n_rows)


if __name__ == "__main__":
    main()
