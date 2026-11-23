#!/usr/bin/env python3
"""県選択画面（issue #1410）の日本地図データ data/japan-map.json を作る前処理。

出力: data/japan-map.yaml（ゲームは DataFiles 経由で読み、解釈は engine の JapanMapParser）。
入力（無ければ下の URL から --cache に落とす）:
  * Natural Earth 1:10m Admin 1（パブリックドメイン）  … 47都道府県の外形
  * 国土数値情報 行政区域データ N03-20240101（出典表記が要る）  … 北海道の振興局・東京の市区町村
        （拡大図で南北／東西に塗り分けるためだけに使う）
出力: 座標は平面投影（経度×cos36°, 緯度を上下反転）した値を、全国図は ×100・拡大図は長い辺が約1500になる倍率で
        整数にしたフラットな配列（丸めは shapely.set_precision＝丸めても自己交差・重なりが出ない）。
        各 view は bbox の左上が原点。feature は rings（穴を含む外周のリスト・偶奇規則で塗る/判定する）。

使い方:  python3 tools/gen-japan-map.py [--cache DIR] [--preview out.png]
依存: shapely>=2, pyshp（pillow はプレビュー時だけ）
"""
import argparse, io, json, math, os, sys, urllib.request, zipfile

import shapefile
import shapely
from shapely.geometry import Polygon, MultiPolygon, shape, box
from shapely.ops import unary_union
from shapely import affinity

NE_URL = "https://naturalearth.s3.amazonaws.com/10m_cultural/ne_10m_admin_1_states_provinces.zip"
N03_URL = "https://nlftp.mlit.go.jp/ksj/gml/data/N03/N03-2024/N03-20240101_%s_GML.zip"

COS = math.cos(math.radians(36.0))
SCALE = 100  # 全国図: 1 投影単位 = 100 整数
ZOOM_LONG_SIDE = 1500  # 拡大図: 長い辺がこの目盛り数になるよう view ごとに倍率を決める（issue #1499）

# PrefectureNames.Japanese と同じ（JIS順）。NE の name_ja と突き合わせて県Id の取り違えを防ぐ
PREF_JA = (
    "北海道 青森県 岩手県 宮城県 秋田県 山形県 福島県 茨城県 栃木県 群馬県 埼玉県 千葉県 東京都 神奈川県 "
    "新潟県 富山県 石川県 福井県 山梨県 長野県 岐阜県 静岡県 愛知県 三重県 滋賀県 京都府 大阪府 兵庫県 奈良県 和歌山県 "
    "鳥取県 島根県 岡山県 広島県 山口県 徳島県 香川県 愛媛県 高知県 福岡県 佐賀県 長崎県 熊本県 大分県 宮崎県 鹿児島県 沖縄県"
).split()

HOKKAIDO_NAMES = ["北北海道", "南北海道"]
# 振興局→南北（engine の SummerRegions.HokkaidoBranches と同じ区割り: 札幌=石狩・函館=渡島/檜山・室蘭=胆振/日高・小樽=後志 が南）
HOKKAIDO_SOUTH = ("石狩", "渡島", "檜山", "胆振", "日高", "後志")
TOKYO_NAMES = ["東東京", "西東京"]
TOKYO_WEST_WARDS = ("世田谷区", "杉並区", "練馬区")
TOKYO_ISLANDS = ("三宅村", "八丈町", "利島村", "大島町", "小笠原村", "御蔵島村", "新島村", "神津島村", "青ヶ島村")



def fetch(url, dest):
    if os.path.exists(dest):
        return dest
    os.makedirs(os.path.dirname(dest), exist_ok=True)
    print("download", url, file=sys.stderr)
    with urllib.request.urlopen(url, timeout=300) as r, open(dest, "wb") as f:
        f.write(r.read())
    return dest


def unzip(zpath, outdir):
    if not os.path.isdir(outdir):
        with zipfile.ZipFile(zpath) as z:
            z.extractall(outdir)
    return outdir


def project(geom):
    # 経度×cos36°, 緯度は上を北にするため符号反転（原点は後で bbox に合わせる）
    return affinity.affine_transform(geom, [COS, 0, 0, -1, 0, 0])


def polys(geom):
    if isinstance(geom, Polygon):
        return [geom]
    if isinstance(geom, MultiPolygon):
        return list(geom.geoms)
    return [g for g in getattr(geom, "geoms", []) if isinstance(g, Polygon)]


def clean(geom, tol, min_area, keep=None, close=0.0):
    g = geom.buffer(0)
    if close > 0:
        # 拡大図: 埋立地のあいだの運河など close 以下の隙間を埋める（拡大すると白い筋＝欠けに見える・issue #1499）
        g = g.buffer(close, join_style="mitre").buffer(-close, join_style="mitre")
    ps = [p for p in polys(g) if p.area >= min_area and (keep is None or keep(p))]
    if not ps:
        raise SystemExit("empty geometry after clean")
    # 穴（湾岸の埋立地の継ぎ目など）は埋める。県の飛び地を持たない図なので穴は継ぎ目の欠けでしかない
    ps = [Polygon(p.exterior) for p in ps]
    out = unary_union(ps).simplify(tol, preserve_topology=True)
    return [p for p in polys(out) if not p.is_empty and p.area > 0]


def rings_of(plist, ox, oy, scale=SCALE):
    """view の整数座標へ写して輪郭を取り出す。

    丸めは shapely.set_precision で行う＝丸めた後も有効な図形（自己交差・輪郭の重なり無し）に保たれる。
    頂点を1つずつ round するだけだと、粗い格子（旧: 東京の拡大図は 89×50）で輪郭が自分と交差したり
    埋立地の小さな輪郭が本体と重なったりし、偶奇規則の塗りで白く抜けていた（issue #1499）。
    """
    g = unary_union(plist)
    g = affinity.affine_transform(g, [scale, 0, 0, scale, -ox * scale, -oy * scale])
    g = shapely.set_precision(g, 1.0)
    rings = []
    for p in polys(g):
        if p.is_empty or p.area <= 0:
            continue
        for ring in [p.exterior] + list(p.interiors):
            pts = []
            for x, y in ring.coords[:-1]:
                ix, iy = int(round(x)), int(round(y))
                if not pts or pts[-1] != (ix, iy):
                    pts.append((ix, iy))
            if len(pts) >= 3:
                rings.append([v for pt in pts for v in pt])
    check_rings(rings)
    return rings


def check_rings(rings):
    """偶奇規則で塗って欠けが出ない形か（各輪郭が単純閉曲線・外周どうしが重ならない）を確かめる。"""
    shells = []
    for rg in rings:
        pts = [(rg[i], rg[i + 1]) for i in range(0, len(rg), 2)]
        lr = shapely.LinearRing(pts)
        if not lr.is_simple:
            raise SystemExit("自己交差する輪郭がある: %s..." % (pts[:4],))
        shells.append(Polygon(pts))
    for i in range(len(shells)):
        for j in range(i + 1, len(shells)):
            a, b = shells[i], shells[j]
            if a.intersects(b):
                inter = a.intersection(b)
                # 穴（外周の中に丸ごと入る輪郭）は偶奇規則の意図どおり。それ以外の面積を持つ重なりは欠けになる
                if inter.area > 0 and not (a.contains(b) or b.contains(a)):
                    raise SystemExit("輪郭どうしが重なっている（面積 %.1f）" % inter.area)


def zoom_scale(b, pad):
    """拡大図の倍率: 長い辺が ZOOM_LONG_SIDE 目盛りになる。"""
    return ZOOM_LONG_SIDE / max(b[2] - b[0] + pad * 2, b[3] - b[1] + pad * 2)


def bounds_of(plists):
    xs0, ys0, xs1, ys1 = [], [], [], []
    for pl in plists:
        for p in pl:
            b = p.bounds
            xs0.append(b[0]); ys0.append(b[1]); xs1.append(b[2]); ys1.append(b[3])
    return min(xs0), min(ys0), max(xs1), max(ys1)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--cache", default="out/japan-map-src")
    ap.add_argument("--preview")
    ap.add_argument("--out", default="data/japan-map.yaml")
    a = ap.parse_args()

    ne_dir = unzip(fetch(NE_URL, a.cache + "/ne.zip"), a.cache + "/ne")
    r = shapefile.Reader(ne_dir + "/ne_10m_admin_1_states_provinces.shp", encoding="utf-8")
    ne = {}
    for sr in r.shapeRecords():
        rec = sr.record.as_dict()
        if rec["iso_a2"] != "JP":
            continue
        code = int(rec["iso_3166_2"].split("-")[1])  # JIS 都道府県コード → 県Id（data/prefectures.yaml の JIS順添字）
        if rec["name_ja"] != PREF_JA[code - 1]:
            raise SystemExit("NE の県名が JIS順と合わない: %s / %s" % (rec["name_ja"], PREF_JA[code - 1]))
        ne[code - 1] = shape(sr.shape.__geo_interface__)
    if sorted(ne) != list(range(47)):
        raise SystemExit("NE の県が47そろわない")

    # ---- 全国図 ----
    nat = {}
    for pid in range(47):
        g = project(ne[pid])
        keep = None
        if pid == 12:  # 東京: 伊豆・小笠原は本土から遠く図が間延びするので本土部だけ（緯度 35.3° 以北）
            keep = lambda p: -p.centroid.y > 35.3
        nat[pid] = clean(g, 0.012, 0.0004 if pid not in (0, 46) else 0.002, keep)
    # 沖縄は縮小して左下の枠へ
    okinawa = nat[46]
    ob = bounds_of([okinawa])
    rest = bounds_of([v for k, v in nat.items() if k != 46])
    s = 0.7
    ow, oh = (ob[2] - ob[0]) * s, (ob[3] - ob[1]) * s
    pad = 0.25
    fx = rest[0]
    fy = rest[3] + 0.5  # 本土の南端の下
    nat[46] = [affinity.affine_transform(p, [s, 0, 0, s, fx + pad - ob[0] * s, fy + pad - ob[1] * s]) for p in okinawa]
    frame = (fx, fy, ow + pad * 2, oh + pad * 2)
    b = bounds_of(nat.values())
    ox, oy = b[0] - 0.3, b[1] - 0.3
    W, H = round((b[2] + 0.3 - ox) * SCALE), round((b[3] + 0.3 - oy) * SCALE)
    national = {
        "w": W, "h": H,
        "frame": [round((frame[0] - ox) * SCALE), round((frame[1] - oy) * SCALE), round(frame[2] * SCALE), round(frame[3] * SCALE)],
        "features": [{"id": pid, "rings": rings_of(nat[pid], ox, oy)} for pid in range(47)],
    }

    # ---- 拡大図 ----
    def n03(code):
        d = unzip(fetch(N03_URL % code, "%s/n03_%s.zip" % (a.cache, code)), "%s/n03_%s" % (a.cache, code))
        return d

    # 北海道: 振興局のサブプレフェクチャ shp を南北に溶かす
    d01 = n03("01")
    rs = shapefile.Reader(d01 + "/N03-20240101_01_subprefecture.shp", encoding="utf-8")
    parts = {0: [], 1: []}
    for sr in rs.shapeRecords():
        sub = sr.record[1]
        south = any(sub.startswith(x) for x in HOKKAIDO_SOUTH)
        parts[1 if south else 0].append(shape(sr.shape.__geo_interface__))
    # 北方領土（東経145.9°以東）は拡大図の幅を間延びさせるので含めない（全国図の NE も含めていない）
    far_east = lambda p: p.centroid.x / COS < 145.9
    hk = {k: clean(project(unary_union([g.buffer(0) for g in v])), 0.03, 0.004, far_east) for k, v in parts.items()}
    hb = bounds_of(hk.values())
    hox, hoy = hb[0] - 0.2, hb[1] - 0.2
    hs = zoom_scale(hb, 0.2)
    hokkaido = {
        "w": round((hb[2] + 0.2 - hox) * hs), "h": round((hb[3] + 0.2 - hoy) * hs),
        "features": [{"id": 0, "split": k, "name": HOKKAIDO_NAMES[k], "rings": rings_of(hk[k], hox, hoy, hs)} for k in (0, 1)],
    }

    # 東京: 市区町村を 東東京（20区＋所属未定地）/西東京（世田谷・杉並・練馬＋多摩）に
    d13 = n03("13")
    rt = shapefile.Reader(d13 + "/N03-20240101_13.shp", encoding="utf-8")
    tp = {0: [], 1: []}
    for sr in rt.shapeRecords():
        gun, city = sr.record[2], sr.record[3]
        if city in TOKYO_ISLANDS:
            continue
        west = city in TOKYO_WEST_WARDS or (not city.endswith("区") and city != "所属未定地")
        tp[1 if west else 0].append(shape(sr.shape.__geo_interface__))
    mainland = lambda p: -p.centroid.y > 35.3
    tk = {k: clean(project(unary_union([g.buffer(0) for g in v])), 0.003, 0.00005, mainland, close=0.004) for k, v in tp.items()}
    # 隙間埋め（close）で境目が隣の区画へ少し食い込むので、西から東を差し引く（当たり判定が二重にならない）
    tk[1] = [p for p in polys(unary_union(tk[1]).difference(unary_union(tk[0]))) if p.area > 0.00005]
    tb = bounds_of(tk.values())
    tox, toy = tb[0] - 0.05, tb[1] - 0.05
    ts = zoom_scale(tb, 0.05)
    tokyo = {
        "w": round((tb[2] + 0.05 - tox) * ts), "h": round((tb[3] + 0.05 - toy) * ts),
        "features": [{"id": 12, "split": k, "name": TOKYO_NAMES[k], "rings": rings_of(tk[k], tox, toy, ts)} for k in (0, 1)],
    }

    out = {
        "national": national,
        "zoom": {"hokkaido": hokkaido, "tokyo": tokyo},
    }
    os.makedirs(os.path.dirname(a.out) or ".", exist_ok=True)
    write_yaml(out, a.out)
    print("wrote", a.out, os.path.getsize(a.out), "bytes", file=sys.stderr)

    if a.preview:
        preview(out, a.preview)


HEADER = """# 県選択画面（issue #1410）の日本地図データ。tools/gen-japan-map.py が作る生成物＝手で編集しない。
#
# 座標: 平面投影（x=経度×cos36°, y=-緯度）の整数。全国図は100倍、拡大図は長い辺が約1500になる倍率。view ごとに bbox の左上が原点。
#   national … 全国図（沖縄は縮小して frame の枠へ寄せてある）／hokkaido・tokyo … 拡大図（南北・東西に塗り分け）
#   feature の rings は外周と穴の頂点列（"x y x y ..."）。塗りも点内判定も偶奇規則。
#   split は拡大図の区画（0=北北海道/東東京, 1=南北海道/西東京）。SummerRegions の Split と同じ並び。
#
# 出典（ゲーム内クレジットと CREDITS.md に同じ表記を載せる）:
#   * Natural Earth 1:10m Admin 1 – States, Provinces（パブリックドメイン）… 47都道府県の外形
#   * 「国土数値情報（行政区域データ N03-20240101）」（国土交通省）https://nlftp.mlit.go.jp/ksj/ を加工して作成
#       … 北海道の振興局（北北海道/南北海道）・東京の市区町村（東東京/西東京）の区割り
#     北海道の南北は SummerRegions.HokkaidoBranches の支部に沿う（石狩・渡島・檜山・胆振・日高・後志＝南）。
#     東京の東西は全国高等学校野球選手権東東京大会の区割り（世田谷・杉並・練馬＋多摩＝西、他の20区＝東）を市区町村単位で近似。
"""


def write_yaml(out, path):
    L = [HEADER.rstrip("\n"), "views:"]

    def view(vid, v):
        L.append("  - id: " + vid)
        L.append("    size: [%d, %d]" % (v["w"], v["h"]))
        if "frame" in v:
            L.append("    frame: [%d, %d, %d, %d]" % tuple(v["frame"]))
        L.append("    features:")
        for ft in v["features"]:
            L.append("      - id: %d" % ft["id"])
            if "split" in ft:
                L.append("        split: %d" % ft["split"])
                L.append("        name: " + ft["name"])
            L.append("        rings:")
            for rg in ft["rings"]:
                L.append('          - "' + " ".join(str(n) for n in rg) + '"')

    view("national", out["national"])
    view("hokkaido", out["zoom"]["hokkaido"])
    view("tokyo", out["zoom"]["tokyo"])
    with open(path, "w", encoding="utf-8") as f:
        f.write("\n".join(L) + "\n")


def preview(out, path):
    from PIL import Image, ImageDraw
    views = [("national", out["national"]), ("hokkaido", out["zoom"]["hokkaido"]), ("tokyo", out["zoom"]["tokyo"])]
    cols = ["#e9b872", "#9fc5a0", "#b8c7e0", "#e8a0a0"]
    imgs = []
    for _, v in views:
        sc = 900 / v["h"] if v["h"] > v["w"] else 900 / v["w"]
        im = Image.new("RGB", (int(v["w"] * sc) + 1, int(v["h"] * sc) + 1), "#f3ecd8")
        dr = ImageDraw.Draw(im)
        for i, ft in enumerate(v["features"]):
            c = cols[ft.get("split", ft["id"]) % 4]
            for rg in ft["rings"]:
                pts = [(rg[j] * sc, rg[j + 1] * sc) for j in range(0, len(rg), 2)]
                dr.polygon(pts, fill=c, outline="#222")
        if "frame" in v:
            fr = v["frame"]
            dr.rectangle([fr[0] * sc, fr[1] * sc, (fr[0] + fr[2]) * sc, (fr[1] + fr[3]) * sc], outline="#222")
        imgs.append(im)
    W = sum(i.width for i in imgs) + 20 * len(imgs)
    H = max(i.height for i in imgs)
    sheet = Image.new("RGB", (W, H), "white")
    x = 0
    for i in imgs:
        sheet.paste(i, (x, 0)); x += i.width + 20
    sheet.save(path)


if __name__ == "__main__":
    main()
