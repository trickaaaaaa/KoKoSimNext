#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
外部で作った球場モデル（OBJ＋MTL）を、試合の3D球場として焼ける形へ変換する（issue #904）。

    python3 -I tools/stadium-import.py <src.obj> <out_base> --wing 97.5 --center 120 [--std data/stadiums/standard]
    例: python3 -I tools/stadium-import.py ~/Downloads/jingu-stadium.obj data/stadiums/jingu --wing 97.5 --center 120

出力: <out_base>.obj / .mtl / .seats.tsv（tools/stadium-gen.py と同じ書式＝DuelStadiumBaker がそのまま焼ける）

やること（手作業で直さない。元のOBJを入れ直せば同じ結果になる）:
  1. 本塁（home_plate の中心）を原点へ平行移動する
  2. 地面の層（芝・土・ライン等）の高さを詰める。
     2b. フェンスの曲線を engine の弧に寄せる（フェンスから外を半径方向に。内野は動かさない）
外部モデルは層を数cmずつ積んでいるが、
     ゲームは選手の足を y=0 に置くので、そのままだと内野で足が埋まる。重なり順は保つ
  3. 判定と照合する部品（マウンド・投手板・塁・本塁）を standard の検証済み部品に差し替える
     （外部モデルの一塁ベースは中心が塁線上＝半分ファウル側にはみ出す置き方で、検証に落ちる）
  4. 透過の部位（防球ネット等）を捨てる（球場のシェーダは不透明の単色で塗るので壁になる）
  5. ゲームが名前で探す部位の名前を standard に揃える（fence_outfield / stand_* / backscreen / scoreboard）
     スタンドは方位で ネット裏・一塁側・三塁側・外野左右 に分ける（standard と同じ境目）
  6. 色を線形→sRGB に直す（three.js の書き出しは線形。standard の MTL は sRGB）
  7. 座席の列（.seats.tsv）を、本塁からの光線と座面の交差で作る（観客を座らせる場所）
  8. 頂点・法線を共有して書き出す（面ごとの法線＝フラットシェード）

焼いた寸法（判定と同じ両翼・中堅と、モデルのフェンス高）は OBJ 先頭の `# kokosim:` 行に書く。
Unity 側はこれを基準に、試合の球場寸法へ外野を変形する。
"""

import argparse
import collections
import math
import os
import sys

# ── 部位の扱い ─────────────────────────────────────────────

# 地面の層。元の高さ → 詰めた高さ（2mm刻みで重なり順を保つ）
GROUND = {
    "ground_apron", "turf_base", "turf_stripes", "warning_zone", "infield_brown", "infield_grass",
    "base_cutout_1b", "base_cutout_2b", "base_cutout_3b", "mound_circle", "home_circle",
    "field_lines", "bullpens", "bullpen_mounds",
}
GROUND_STEP = 0.002

# standard の検証済み部品に差し替える（外部モデル側は捨てる）
REPLACE_DROP = {"pitcher_mound", "pitcher_plate", "home_plate", "first_base", "second_base", "third_base"}
REPLACE_TAKE = ["mound", "pitcher_rubber", "base_first", "base_second", "base_third", "home_plate"]

# 名前を揃える（複数を1つにまとめるものもある）
RENAME = {
    "outfield_fence": "fence_outfield",
    "outfield_fence_top_line": "fence_outfield",   # フェンスと一緒に高さを変形させる
    "infield_wall": "fence_foul",
    "backstop_frame": "backstop",
    "batters_eye": "backscreen",
    "batters_eye_roof": "backscreen",
    "scoreboard_body": "scoreboard",
    "scoreboard_screen": "scoreboard",
    "scoreboard_video": "scoreboard",
    "scoreboard_display": "scoreboard",
    "scoreboard_structure": "scoreboard",
    "ad_board_l": "scoreboard",
    "ad_board_c": "scoreboard",
    "ad_board_r": "scoreboard",
}

# 方位で分けるスタンド（面の重心の方位で決める）
INFIELD_STANDS = {"infield_seats", "infield_steps"}
OUTFIELD_STANDS = {"outfield_seats", "outfield_steps"}
SEAT_GROUPS = {"infield_seats", "outfield_seats"}
BACKSTOP_DEG = 110.0   # standard の座席TSVと同じ境目（|θ|≥110°＝ネット裏）

# 座席の列の作り方
SEAT_SAMPLE_DEG = 0.25     # 光線の刻み
SEAT_SEGMENT_SAMPLES = 8   # 1行にまとめる光線の数（2°ごと）
SEAT_ABOVE_TREAD = 0.40    # 座面は踏面の0.40m上（DuelCrowdBaker の前提）
SEAT_BACK_IN = 0.22        # standard と同じ


def srgb(c):
    c = max(0.0, min(1.0, c))
    return 12.92 * c if c <= 0.0031308 else 1.055 * c ** (1 / 2.4) - 0.055


def parse_obj(path):
    V, groups, order = [], collections.OrderedDict(), []
    grp = mat = None
    mtllib = None
    with open(path) as fh:
        for line in fh:
            if line.startswith("v "):
                V.append(tuple(float(x) for x in line.split()[1:4]))
            elif line.startswith("o "):
                grp = line[2:].strip()
                if grp not in groups:
                    groups[grp] = []
            elif line.startswith("usemtl"):
                mat = line.split()[1]
            elif line.startswith("mtllib"):
                mtllib = line.split()[1]
            elif line.startswith("f "):
                idx = [int(t.split("/")[0]) - 1 for t in line.split()[1:]]
                for k in range(1, len(idx) - 1):   # 多角形は扇に割る
                    groups[grp].append((mat, (idx[0], idx[k], idx[k + 1])))
    return V, groups, mtllib


def parse_mtl(path):
    out, cur = collections.OrderedDict(), None
    if not path or not os.path.exists(path):
        return out
    with open(path) as fh:
        for line in fh:
            t = line.split()
            if not t:
                continue
            if t[0] == "newmtl":
                cur = t[1]
                out[cur] = {"Kd": (1.0, 0.0, 1.0), "d": 1.0}
            elif t[0] == "Kd" and cur:
                out[cur]["Kd"] = tuple(float(x) for x in t[1:4])
            elif t[0] == "d" and cur:
                out[cur]["d"] = float(t[1])
    return out


def centroid(pts):
    n = len(pts)
    return (sum(p[0] for p in pts) / n, sum(p[1] for p in pts) / n, sum(p[2] for p in pts) / n)


def theta_deg(x, z):
    return math.degrees(math.atan2(x, z))


def stand_name(base, x, z):
    th = theta_deg(x, z)
    if base in OUTFIELD_STANDS:
        return "stand_outfield_left" if th < 0 else "stand_outfield_right"
    if abs(th) >= BACKSTOP_DEG:
        return "stand_backstop"
    return "stand_first" if th > 0 else "stand_third"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("src")
    ap.add_argument("out")
    ap.add_argument("--wing", type=float, required=True, help="判定の両翼[m]（data/stadiums.yaml と同じ）")
    ap.add_argument("--center", type=float, required=True, help="判定の中堅[m]")
    ap.add_argument("--std", default=os.path.join(os.path.dirname(__file__), "..", "data", "stadiums", "standard"))
    a = ap.parse_args()

    V, groups, mtllib = parse_obj(a.src)
    mtl = parse_mtl(os.path.join(os.path.dirname(a.src), mtllib) if mtllib else os.path.splitext(a.src)[0] + ".mtl")

    # 1. 本塁を原点へ
    hp = {i for _, f in groups.get("home_plate", []) for i in f}
    if not hp:
        sys.exit("home_plate が無いので本塁の位置が分からない")
    hx = sum(V[i][0] for i in hp) / len(hp)
    hz = sum(V[i][2] for i in hp) / len(hp)
    V = [(x - hx, y, z - hz) for (x, y, z) in V]

    # 2. 地面の層を詰める（元の高さの順位で2mm刻み。ライン等の厚みは1/10に）
    levels = sorted({round(min(V[i][1] for i in f), 3)
                     for g in GROUND if g in groups for _, f in groups[g]})
    rank = {y: k * GROUND_STEP for k, y in enumerate(levels)}
    ground_vs = {i for g in GROUND if g in groups for _, f in groups[g] for i in f}

    def squash(y):
        base = max(l for l in levels if l <= round(y, 3) + 1e-9) if levels else 0.0
        return rank.get(base, 0.0) + (y - base) * 0.1

    V = [(x, squash(y), z) if i in ground_vs else (x, y, z) for i, (x, y, z) in enumerate(V)]

    # 2b. フェンスの曲線を判定（engine の弧）に合わせる。外部モデルは両翼・中堅だけ合っていて
    #     間が丸く膨らんでいることがある（神宮は 22.5° で +2.2m）。絵と判定がずれると
    #     「フェンス手前に落ちたのに本塁打」が起きるので、フェンスから外を半径方向に寄せる
    V, fence_fix = warp_fence_to_engine(V, groups, a.wing, a.center)

    # 出力の部位: name -> list of (mat, (p0,p1,p2) 座標)
    out = collections.OrderedDict()
    used_mats = collections.OrderedDict()

    def emit(name, mat, tri):
        out.setdefault(name, []).append((mat, tri))
        used_mats[mat] = True

    dropped = []
    for g, faces in groups.items():
        if g in REPLACE_DROP:
            dropped.append(g)
            continue
        for m, f in faces:
            if mtl.get(m, {}).get("d", 1.0) < 1.0:
                if g not in dropped:
                    dropped.append(g)
                continue   # 4. 透過は捨てる
            tri = tuple(V[i] for i in f)
            if g in INFIELD_STANDS or g in OUTFIELD_STANDS:
                c = centroid(tri)
                name = stand_name(g, c[0], c[2])
            else:
                name = RENAME.get(g, g)
            emit(name, m, tri)

    # 3. standard の検証済み部品を差し込む（材質名は std_ を付けて衝突させない）
    sV, sgroups, smtllib = parse_obj(a.std + ".obj")
    smtl = parse_mtl(a.std + ".mtl")
    for g in REPLACE_TAKE:
        for m, f in sgroups.get(g, []):
            emit(g, "std_" + m, tuple(sV[i] for i in f))

    # 7. 座席の列
    seat_rows = build_seat_rows(groups, V)

    # 8. 書き出し
    write_obj(a.out, out, a.wing, a.center, fence_height(out))
    write_mtl(a.out, used_mats, mtl, smtl)
    write_seats(a.out, seat_rows)

    tris = sum(len(v) for v in out.values())
    print(f"部位 {len(out)} / 三角形 {tris} / 座席の列 {len(seat_rows)} / 捨てた部位 {dropped}")
    print(f"本塁の移動 ({-hx:.3f}, {-hz:.3f}) / 地面の層 {len(levels)} 段 / フェンス高 {fence_height(out):.3f} m")
    print("フェンスの寄せ（方位: 元→判定）: " + " ".join(f"{d:g}°:{m:.2f}→{e:.2f}" for d, m, e in fence_fix))


# ── フェンスの曲線合わせ ─────────────────────────────────────

WARP_INNER_M = 50.0          # ここより内（内野）は動かさない（Unity の DuelStageRig.WarpToField と同じ）
WARP_FULL_BEFORE_FENCE_M = 6.0
WARP_FOUL_FADE_DEG = 70.0


def engine_fence(th_deg, wing, center):
    """engine の外野フェンス（FieldDiagramGeometry.FenceRadius）。|θ|>45° はポールの値。"""
    t = max(-45.0, min(45.0, th_deg))
    return wing + (center - wing) * math.cos(math.radians(2 * t))


def smoothstep(e0, e1, x):
    if e1 == e0:
        return 1.0 if x >= e1 else 0.0
    t = max(0.0, min(1.0, (x - e0) / (e1 - e0)))
    return t * t * (3 - 2 * t)


def warp_fence_to_engine(V, groups, wing, center):
    # モデルのフェンス内面＝地面の高さのフェンスの辺（両端が y≈0 の辺）を、本塁からの光線で測った最小距離。
    # 頂点の半径を方位で補間すると、フェンスの内面と外面（厚み約0.3m）の頂点が混ざって曲線がジグザグになる
    segs = []
    for _, f in groups.get("outfield_fence", []):
        for k in range(3):
            a, b = V[f[k]], V[f[(k + 1) % 3]]
            if a[1] <= 0.01 and b[1] <= 0.01:
                segs.append(((a[0], a[2]), (b[0], b[2])))
    if not segs:
        return V, []

    def model_fence(th):
        th = max(-45.0, min(45.0, th))
        dx, dz = math.sin(math.radians(th)), math.cos(math.radians(th))
        best = None
        for (ax, az), (bx, bz) in segs:
            ex, ez = bx - ax, bz - az
            den = dx * ez - dz * ex
            if abs(den) < 1e-12:
                continue
            t = (ax * ez - az * ex) / den
            u = (ax * dz - az * dx) / den
            if -1e-9 <= u <= 1 + 1e-9 and t > 0 and (best is None or t < best):
                best = t
        return best if best is not None else engine_fence(th, wing, center)

    cache = {}

    def model_fence_cached(th):
        key = round(th, 4)
        if key not in cache:
            cache[key] = model_fence(th)
        return cache[key]

    out = []
    for (x, y, z) in V:
        r = math.hypot(x, z)
        if r < WARP_INNER_M:
            out.append((x, y, z))
            continue
        th = theta_deg(x, z)
        thc = max(-45.0, min(45.0, th))
        rm = model_fence_cached(thc)
        delta = engine_fence(thc, wing, center) - rm
        wa = 1 - smoothstep(45.0, WARP_FOUL_FADE_DEG, abs(th))
        wr = smoothstep(WARP_INNER_M, rm - WARP_FULL_BEFORE_FENCE_M, r)
        k = (r + delta * wa * wr) / r
        out.append((x * k, y, z * k))
    report = [(d, model_fence(d), engine_fence(d, wing, center)) for d in (0, 11.25, 22.5, 33.75, 45)]
    return out, report


def fence_height(out):
    """判定に効くフェンス本体（上端のラインを除く）の高さ。上端ラインの材質は fenceYellow。"""
    ys = [p[1] for m, tri in out.get("fence_outfield", []) if "Yellow" not in m for p in tri]
    return max(ys) if ys else 0.0


# ── 座席 ─────────────────────────────────────────────────

def build_seat_rows(groups, V):
    """座面（上向きの面）を高さで段に分け、本塁からの光線で各方位の前縁・後縁を測る。"""
    levels = collections.defaultdict(list)   # (base, y) -> 2D 三角形
    for g in SEAT_GROUPS:
        for _, f in groups.get(g, []):
            p = [V[i] for i in f]
            ux, uy, uz = (p[1][k] - p[0][k] for k in range(3))
            vx, vy, vz = (p[2][k] - p[0][k] for k in range(3))
            nx, ny, nz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
            ln = math.sqrt(nx * nx + ny * ny + nz * nz) or 1.0
            if ny / ln < 0.99:
                continue
            levels[(g, round(p[0][1], 2))].append([(q[0], q[2]) for q in p])

    rows = []
    for g in sorted(SEAT_GROUPS):
        ys = sorted(y for (gg, y) in levels if gg == g)
        for k, y in enumerate(ys):
            tris = levels[(g, y)]
            buckets = collections.defaultdict(list)
            for t in tris:
                ths = [theta_deg(x, z) for x, z in t]
                lo, hi = min(ths), max(ths)
                if hi - lo > 180:   # ±180° をまたぐ
                    ths = [a + 360 if a < 0 else a for a in ths]
                    lo, hi = min(ths), max(ths)
                for d in range(int(math.floor(lo)) - 1, int(math.ceil(hi)) + 2):
                    buckets[((d + 180) % 360) - 180].append(t)
            samples = []
            n = int(round(360 / SEAT_SAMPLE_DEG))
            for s in range(n):
                th = -180 + (s + 0.5) * SEAT_SAMPLE_DEG
                hit = ray_span(th, buckets.get(int(math.floor(th)), []))
                samples.append((th, hit))
            rows += runs_to_rows(g, k, y, samples)
    return rows


def ray_span(th_deg, tris):
    """方位 th の光線が三角形の和集合に入る最小距離と出る最大距離。当たらなければ None。"""
    dx, dz = math.sin(math.radians(th_deg)), math.cos(math.radians(th_deg))
    ts = []
    for t in tris:
        for i in range(3):
            (ax, az), (bx, bz) = t[i], t[(i + 1) % 3]
            ex, ez = bx - ax, bz - az
            den = dx * ez - dz * ex
            if abs(den) < 1e-12:
                continue
            tt = (ax * ez - az * ex) / den
            ss = (ax * dz - az * dx) / den
            if 0.0 <= ss <= 1.0 and tt > 0:
                ts.append(tt)
    if len(ts) < 2:
        return None
    return min(ts), max(ts)


def runs_to_rows(g, k, y, samples):
    """当たった光線の連続を、SEAT_SEGMENT_SAMPLES ごとの行にする（通路の切れ目で必ず切る）。"""
    rows, run = [], []

    def flush():
        if not run:
            return
        th0, (a0, b0) = run[0]
        th1, (a1, b1) = run[-1]
        half = SEAT_SAMPLE_DEG / 2
        depth = sum(b - a for _, (a, b) in run) / len(run)
        name = stand_name("outfield_seats" if g == "outfield_seats" else "infield_seats",
                          math.sin(math.radians((th0 + th1) / 2)), math.cos(math.radians((th0 + th1) / 2)))
        rows.append((name, math.radians(th0 - half), math.radians(th1 + half), a0, a1,
                     y - SEAT_ABOVE_TREAD, depth + SEAT_BACK_IN, k))
        run.clear()

    for th, hit in samples:
        if hit is None:
            flush()
            continue
        run.append((th, hit))
        if len(run) >= SEAT_SEGMENT_SAMPLES:
            flush()
    flush()
    return rows


# ── 書き出し ───────────────────────────────────────────────

def write_obj(base, out, wing, center, fence_h):
    vidx, nidx = {}, {}
    V, N = [], []
    body = []

    def vi(p):
        key = (round(p[0], 4), round(p[1], 4), round(p[2], 4))
        if key not in vidx:
            vidx[key] = len(V) + 1
            V.append(key)
        return vidx[key]

    def ni(n):
        key = (round(n[0], 4), round(n[1], 4), round(n[2], 4))
        if key not in nidx:
            nidx[key] = len(N) + 1
            N.append(key)
        return nidx[key]

    for g, faces in out.items():
        body.append("o " + g)
        bymat = collections.OrderedDict()
        for m, tri in faces:
            bymat.setdefault(m, []).append(tri)
        for m, tris in bymat.items():
            body.append("usemtl " + m)
            for p in tris:
                ux, uy, uz = (p[1][k] - p[0][k] for k in range(3))
                vx, vy, vz = (p[2][k] - p[0][k] for k in range(3))
                nx, ny, nz = uy * vz - uz * vy, uz * vx - ux * vz, ux * vy - uy * vx
                ln = math.sqrt(nx * nx + ny * ny + nz * nz)
                if ln < 1e-12:
                    continue   # 潰れた三角形
                n = ni((nx / ln, ny / ln, nz / ln))
                a, b, c = vi(p[0]), vi(p[1]), vi(p[2])
                if a == b or b == c or a == c:
                    continue
                body.append("f %d//%d %d//%d %d//%d" % (a, n, b, n, c, n))

    head = ["# generated by tools/stadium-import.py (issue #904)",
            "# kokosim: wing=%g center=%g fence_h=%g" % (wing, center, fence_h),
            "mtllib %s.mtl" % os.path.basename(base)]
    with open(base + ".obj", "w") as fh:
        fh.write("\n".join(head) + "\n")
        fh.write("\n".join("v %g %g %g" % v for v in V) + "\n")
        fh.write("\n".join("vn %g %g %g" % n for n in N) + "\n")
        fh.write("\n".join(body) + "\n")


def write_mtl(base, used, mtl, smtl):
    out = ["# stadium materials (Kd only, sRGB)"]
    for m in used:
        if m.startswith("std_"):
            kd = smtl.get(m[4:], {}).get("Kd", (1, 0, 1))            # standard は sRGB のまま
        else:
            kd = tuple(srgb(c) for c in mtl.get(m, {}).get("Kd", (1, 0, 1)))   # 線形 → sRGB
        out.append("newmtl %s\nKd %.4f %.4f %.4f" % (m, kd[0], kd[1], kd[2]))
    with open(base + ".mtl", "w") as fh:
        fh.write("\n".join(out) + "\n")


def write_seats(base, rows):
    out = ["# group\tt0_rad\tt1_rad\tr0_front_m\tr1_front_m\ty_tread_m\ttread_m\trow\tback_in_m"]
    for g, t0, t1, a0, a1, y, tr, k in rows:
        out.append("%s\t%.6f\t%.6f\t%.4f\t%.4f\t%.4f\t%.4f\t%d\t%.4f" % (g, t0, t1, a0, a1, y, tr, k, SEAT_BACK_IN))
    with open(base + ".seats.tsv", "w") as fh:
        fh.write("\n".join(out) + "\n")


if __name__ == "__main__":
    main()
