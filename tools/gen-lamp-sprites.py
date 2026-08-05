#!/usr/bin/env python3
"""BSOランプ（issue #479）のスプライト2枚を生成する。

USS には box-shadow も inset shadow も無いので、点灯のグローと消灯の凹みは
テクスチャで持つ。寸法は参照モック docs/design/mock-pitch-view.html の実測値:

    ランプ本体   13px の真円
    点灯グロー   box-shadow: 0 0 9px <同色>
    消灯の凹み   inset 0 0 3px rgba(0,0,0,.8)

⚠ グローの断面は「**本体と同じ円板をガウスぼかししたもの**」でなければならない。
box-shadow のぼかしは元の形をぼかすので、縁のαは 1.0 ではなく 0.35 程度から始まる。
「縁で1.0から外へ減衰」で作ると全半径で約3倍明るくなり、13px のランプに対して
halo が勝って滲んで見える（2026-08-05 実測・監督指摘で作り直し）。

使い方: python3 tools/gen-lamp-sprites.py
        （.meta は既存のものを使い回すので消さないこと＝GUID が変わると USS の参照が切れる）
"""
import math
import pathlib

from PIL import Image, ImageFilter

OUT = pathlib.Path(__file__).resolve().parent.parent / "Assets/UI/Images/board"

LAMP_D = 13.0        # ランプ径[px]
GLOW_D = 33.0        # グロー要素の一辺[px]（本体の約2.5倍）
GLOW_BLUR = 9.0      # box-shadow のぼかし半径[px]
INSET_BLUR = 3.0     # inset shadow のぼかし半径[px]
INSET_ALPHA = 0.8    # inset shadow の濃さ


def make_glow(n=128):
    """点灯グロー: 本体と同じ円板をガウスぼかしした断面（白。使用時に点灯色でティントする）。"""
    half = n / 2
    scale = n / GLOW_D            # テクセル/画面px
    disc_r = LAMP_D / 2 * scale
    sigma = GLOW_BLUR / 2 * scale  # CSS: 標準偏差 = ぼかし半径の半分

    disc = Image.new("L", (n, n), 0)
    p = disc.load()
    for y in range(n):
        for x in range(n):
            r = math.hypot(x + 0.5 - half, y + 0.5 - half)
            # 円板をアンチエイリアス付きで塗る（1テクセルぶんの傾斜）
            p[x, y] = int(round(255 * max(0.0, min(1.0, disc_r + 0.5 - r))))

    blur = disc.filter(ImageFilter.GaussianBlur(radius=sigma)).load()
    img = Image.new("RGBA", (n, n))
    ip = img.load()
    for y in range(n):
        for x in range(n):
            r = math.hypot(x + 0.5 - half, y + 0.5 - half)
            # 内側は本体が覆うので焼かない（焼くと本体の上で二重に明るくなる）
            a = 0 if r < disc_r else blur[x, y]
            ip[x, y] = (255, 255, 255, a)
    return img


def make_inset(n=64):
    """消灯の凹み: 縁が最も暗く内へ抜ける黒。円の外は border-radius が切るので0。"""
    half = n / 2
    scale = n / LAMP_D
    inner = half - INSET_BLUR * scale
    img = Image.new("RGBA", (n, n))
    p = img.load()
    for y in range(n):
        for x in range(n):
            r = math.hypot(x + 0.5 - half, y + 0.5 - half)
            if r >= half:
                a = 0.0
            else:
                t = max(0.0, min(1.0, (r - inner) / (half - inner)))
                a = INSET_ALPHA * (t * t * (3 - 2 * t))   # smoothstep
            p[x, y] = (0, 0, 0, int(round(a * 255)))
    return img


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    make_glow().save(OUT / "lamp-glow.png")
    make_inset().save(OUT / "lamp-inset.png")

    # 断面を出しておく（滲みを疑ったときはまずこの数値を見る）
    g = Image.open(OUT / "lamp-glow.png").split()[3].load()
    n = 128
    scale = n / GLOW_D
    print("半径[px]  α      （本体の縁 = %.1fpx）" % (LAMP_D / 2))
    for rp in (6.5, 8, 10, 12, 14, 16):
        off = int(rp * scale)
        a = g[n // 2 + off, n // 2] / 255 if n // 2 + off < n else 0.0
        print("  %4.1f    %.3f" % (rp, a))


if __name__ == "__main__":
    main()
