#!/usr/bin/env python3
"""Assets/KokoSim/Match/MotionReelShooter.cs が吐いた連番JPEGをアニメーションWebPへ束ねる。

    python3 tools/motion-reel-encode.py out/motion-reel out/motion-reel-webp
    BUDGET=620000 FPS_OUT=12 python3 tools/motion-reel-encode.py …   # 1本あたりの目標バイト／再生fps

アーティファクトはページ全体で16MB上限、data: URI は base64 で約1.33倍に膨らむので
生バイトの予算は約11.5MB。1本ずつ「画質→縮尺」の順に落として予算へ収める。
"""
import io
import json
import os
import sys
from PIL import Image

SRC = sys.argv[1] if len(sys.argv) > 1 else "out/motion-reel"
DST = sys.argv[2] if len(sys.argv) > 2 else "reel-webp"
FPS_OUT = int(os.environ.get("FPS_OUT", "12"))   # ポーズ量子化と同じ12fps（元は24fps収録）
FPS_SRC = 24
BUDGET = int(os.environ.get("BUDGET", "760000"))  # 1本あたりの目標バイト

os.makedirs(DST, exist_ok=True)


# 1280x720 の全画面から中央の盤面だけを切る（左右の打順パネルはモーションの確認に使わない）。
# 実測: 左右カラムが各260px、盤面の下端は采配パッド（tactics-host, y=436）の上。
CROP = (264, 0, 996, 436)


def encode(frames, quality, width):
    h = round(width * frames[0].height / frames[0].width / 2) * 2
    imgs = [f.resize((width, h), Image.LANCZOS) for f in frames]
    buf = io.BytesIO()
    imgs[0].save(buf, format="WEBP", save_all=True, append_images=imgs[1:],
                 duration=round(1000 / FPS_OUT), loop=0, quality=quality,
                 method=4, minimize_size=True)
    return buf.getvalue()


def build(slug):
    d = os.path.join(SRC, slug)
    names = sorted(n for n in os.listdir(d) if n.endswith(".jpg"))
    step = max(1, FPS_SRC // FPS_OUT)
    picked = names[::step]
    frames = [Image.open(os.path.join(d, n)).convert("RGB").crop(CROP) for n in picked]
    best = None
    for width, quality in [(680, 76), (680, 68), (680, 60), (620, 54), (560, 48), (500, 42)]:
        data = encode(frames, quality, width)
        best = (data, width, quality)
        if len(data) <= BUDGET:
            break
    data, width, quality = best
    with open(os.path.join(DST, slug + ".webp"), "wb") as f:
        f.write(data)
    return {"slug": slug, "frames": len(frames), "fps": FPS_OUT,
            "seconds": round(len(frames) / FPS_OUT, 2), "bytes": len(data),
            "width": width, "quality": quality}


slugs = sorted(n for n in os.listdir(SRC)
               if os.path.isdir(os.path.join(SRC, n))
               and any(x.endswith(".jpg") for x in os.listdir(os.path.join(SRC, n))))
out = [build(s) for s in slugs]
total = sum(o["bytes"] for o in out)
for o in out:
    print(f"{o['slug']:<14} {o['frames']:>4}コマ {o['seconds']:>5.1f}s "
          f"{o['bytes']/1024:>7.0f}KB  w={o['width']} q={o['quality']}")
print(f"{'合計':<14} {total/1024/1024:.2f}MB  → base64 {total*4/3/1024/1024:.2f}MB")
with open(os.path.join(DST, "clips.json"), "w") as f:
    json.dump(out, f, ensure_ascii=False, indent=1)
