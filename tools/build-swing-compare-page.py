#!/usr/bin/env python3
"""対決ビューのスイング比較ページを組む（issue #368）。

入力: screenshots/<prefix>-<条件>-<コマ番号>-t<秒>.jpg
      （DuelSwingStripShooter の出力。条件は "k1.4-miss-R" のような文字列）
出力: 1枚のHTML（JPEGは data URI で内包＝アーティファクトは外部取得できない）

「速い／遅い」「カクつく」はコマを並べただけでは判定しづらいので、
条件ごとに **実時間で再生できるビューア**（0.05秒刻みをそのままの速さで回す）を付ける。
下にコマ送りのストリップを敷いて、止めて見比べられるようにする。

対になる静止画（修正前/修正後・つまみA/B）を並べる欄も足せる。

使い方:
  python3 tools/build-swing-compare-page.py out.html i368 "タイトル" \
      ["見出し:左ラベル=左ファイル:右ラベル=右ファイル" ...]
"""
import base64
import os
import re
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
# 既定は screenshots/。data URI で内包するぶんページが重くなるので、
# 縮小版を別ディレクトリへ用意したときは SWING_SHOTS_DIR で差し替える
SRC = os.environ.get("SWING_SHOTS_DIR") or os.path.join(HERE, "screenshots")
PAT = re.compile(r"^(?P<prefix>.+?)-(?P<run>.+)-(?P<idx>\d{2})-t(?P<t>[\d.]+)\.jpg$")
STEP_MS = 50  # DuelSwingStripShooter.StepSec と一致（実時間再生）


def collect(prefix):
    runs = {}
    for name in sorted(os.listdir(SRC)):
        m = PAT.match(name)
        if not m or m.group("prefix") != prefix:
            continue
        runs.setdefault(m.group("run"), []).append(
            (int(m.group("idx")), float(m.group("t")), os.path.join(SRC, name)))
    for frames in runs.values():
        frames.sort()
    return runs


def uri(path):
    mime = "png" if path.lower().endswith(".png") else "jpeg"
    with open(path, "rb") as f:
        return "data:image/%s;base64,%s" % (mime, base64.b64encode(f.read()).decode("ascii"))


def main():
    out = sys.argv[1]
    prefix = sys.argv[2] if len(sys.argv) > 2 else "i368"
    title = sys.argv[3] if len(sys.argv) > 3 else "スイング比較"
    runs = collect(prefix)
    if not runs:
        sys.exit("該当する画像が無い: %s/%s-*.jpg" % (SRC, prefix))

    parts = ["<title>%s</title>" % title, STYLE, "<h1>%s</h1>" % title]

    # 対の静止画（修正前/修正後 等）を先に置く＝結論から見せる
    for spec in sys.argv[4:]:
        head, left, right = spec.split(":")
        la, lf = left.split("=")
        ra, rf = right.split("=")
        parts.append(
            '<section><h2>%s</h2><div class=pair>'
            '<figure><img src="%s"><figcaption>%s</figcaption></figure>'
            '<figure><img src="%s"><figcaption>%s</figcaption></figure>'
            '</div></section>' % (head, uri(lf), la, uri(rf), ra))

    parts.append("<p class=hint>▶ を押すと 0.05 秒刻みを<b>実時間で</b>再生する（対決ビューの見え方そのもの）。"
                 "下のストリップは同じコマを並べたもの。数字はリリースからの秒。</p>")
    for i, (run, frames) in enumerate(sorted(runs.items())):
        srcs = ",".join('"%s"' % uri(p) for _, _, p in frames)
        labels = ",".join('"%.2f"' % t for _, t, _ in frames)
        parts.append(
            '<section><h2>%s</h2>'
            '<div class=player>'
            '<img id="v%d" src="%s">'
            '<div class=ctrl><button onclick="play(%d)">▶ 再生</button>'
            '<span class=clock id="c%d">0.05s</span></div></div>'
            '<div class=strip>%s</div>'
            '<script>F%d=[%s];L%d=[%s];</script></section>'
            % (run, i, uri(frames[0][2]), i, i,
               "".join('<figure><img src="%s"><figcaption>%.2f</figcaption></figure>' % (uri(p), t)
                       for _, t, p in frames),
               i, srcs, i, labels))
    parts.append(SCRIPT % STEP_MS)
    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(parts))
    print("書いた: %s（条件 %d 件・%d コマ）"
          % (out, len(runs), sum(len(v) for v in runs.values())))


STYLE = """<style>
/* 色はゲーム本体の tokens.uss と同じ盤面色（面 #1D3227 / 地 #14231B / CTA #F5C64A）に揃える */
:root{--bg:#14231B;--panel:#1D3227;--ink:#EAF2EC;--sub:#9FB3A6;--line:#2C4A39;--cta:#F5C64A;--ctaInk:#14231B}
@media (prefers-color-scheme: light){
  :root{--bg:#F1F4F0;--panel:#FFF;--ink:#17211B;--sub:#57685C;--line:#D3DCD5;--cta:#9A6B00;--ctaInk:#FFF}}
:root[data-theme=dark]{--bg:#14231B;--panel:#1D3227;--ink:#EAF2EC;--sub:#9FB3A6;--line:#2C4A39;
  --cta:#F5C64A;--ctaInk:#14231B}
:root[data-theme=light]{--bg:#F1F4F0;--panel:#FFF;--ink:#17211B;--sub:#57685C;--line:#D3DCD5;
  --cta:#9A6B00;--ctaInk:#FFF}
body{background:var(--bg);color:var(--ink);font-family:'Hiragino Sans','Noto Sans JP',system-ui,sans-serif;
     margin:0;padding:24px;line-height:1.6;font-variant-numeric:tabular-nums}
h1{font-size:20px;margin:0 0 4px;text-wrap:balance}
h2{font-size:15px;margin:0 0 10px;text-wrap:balance}
.hint{color:var(--sub);font-size:13px;margin:0 0 20px;max-width:64ch}
section{background:var(--panel);border:1px solid var(--line);border-radius:8px;padding:14px;margin-bottom:18px}
.player{display:flex;align-items:flex-start;gap:12px;flex-wrap:wrap}
.player>img{width:min(560px,100%);border-radius:4px;display:block}
.ctrl{display:flex;flex-direction:column;gap:8px}
button{font:inherit;padding:8px 18px;border-radius:6px;border:0;
       background:var(--cta);color:var(--ctaInk);cursor:pointer;font-weight:700}
button:focus-visible{outline:2px solid var(--ink);outline-offset:2px}
.clock{color:var(--sub);font-size:13px;font-variant-numeric:tabular-nums}
.pair{display:flex;gap:12px;flex-wrap:wrap}
.pair figure{margin:0;flex:1 1 360px}
.pair img{width:100%;border-radius:4px;display:block}
.strip{display:flex;gap:4px;overflow-x:auto;margin-top:12px;padding-bottom:6px}
.strip figure{margin:0;flex:0 0 auto}
.strip img{width:150px;display:block;border-radius:3px}
figcaption{color:var(--sub);font-size:11px;text-align:center;font-variant-numeric:tabular-nums}
</style>"""

SCRIPT = """<script>
var STEP=%d, timers={};
function play(i){
  var img=document.getElementById('v'+i), lab=document.getElementById('c'+i);
  var F=window['F'+i], L=window['L'+i], n=0;
  if(timers[i]){clearInterval(timers[i]);}
  timers[i]=setInterval(function(){
    img.src=F[n]; lab.textContent=L[n]+'s';
    if(++n>=F.length){clearInterval(timers[i]);timers[i]=null;}
  }, STEP);
}
</script>"""


if __name__ == "__main__":
    main()
