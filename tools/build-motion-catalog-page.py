#!/usr/bin/env python3
"""AAパックのモーション棚卸しページを組む。

入力: out/duel-stage/catalog/ の <clip>.png（横並びコマ列）と catalog_<役割>.tsv
出力: 1枚のHTML（PNGはdata URIで内包＝アーティファクトは外部取得できないため）

使い方:
  python3 tools/build-motion-catalog-page.py out.html [役割 ...]
"""
import base64
import json
import os
import sys

HERE = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
SRC = os.path.join(HERE, "out/duel-stage/catalog")
FRAME = 128  # 1コマの実寸px（Unity側 DuelMotionCatalog.FrameW/H と一致）

ROLES = [
    ("Pitcher", "投手", "投球・牽制・けん制後の立ち直り・感情"),
    ("Batter", "打者", "構え・スイング5種・バント・死球・三振・本塁打"),
    ("Catcher", "捕手", "捕球5方向・サイン・送球・捕逸"),
    ("Defender", "守備", "捕球・移動・ダイブ・中継・タッチ"),
    ("Offense", "走塁", "リード・スタート・スライディング・帰塁"),
    ("Umpire", "審判", "ストライク/ボール・カウント・アウト/セーフ・本塁打"),
]


def load(role):
    path = os.path.join(SRC, "catalog_%s.tsv" % role)
    if not os.path.exists(path):
        return []
    rows = []
    with open(path, encoding="utf-8") as f:
        next(f)
        for line in f:
            parts = line.rstrip("\n").split("\t")
            if len(parts) < 6:
                continue
            name, frames, clip_sec, play_sec = parts[0], int(parts[1]), float(parts[2]), float(parts[3])
            png = os.path.join(SRC, name + ".png")
            if not os.path.exists(png):
                continue
            with open(png, "rb") as g:
                b64 = base64.b64encode(g.read()).decode("ascii")
            rows.append({
                "n": name,
                "f": frames,
                "d": round(play_sec, 2),
                "fps": round(frames / play_sec, 1) if play_sec > 0 else 12,
                "b": b64,
            })
    return rows


CSS = """
:root{
  --paper:#F7F5EF; --card:#FFFFFF; --ink:#131512; --muted:#5B5A50; --faint:#8A887B;
  --rule:#E4E0D2; --amber:#F5C64A; --stage:#000000; --shadow:rgba(19,21,18,.16);
  --serif:"Hiragino Mincho ProN","Yu Mincho",YuMincho,"Songti SC",Georgia,serif;
  --sans:"Hiragino Sans","Yu Gothic",system-ui,-apple-system,"Segoe UI",sans-serif;
  --mono:ui-monospace,"SF Mono",Menlo,Consolas,monospace;
  --tile:128px;
}
@media (prefers-color-scheme:dark){
  :root{ --paper:#141513; --card:#1C1D1A; --ink:#EFEBDD; --muted:#A6A297; --faint:#7C7970;
         --rule:#2A2C27; --shadow:rgba(0,0,0,.5); }
}
:root[data-theme="dark"]{ --paper:#141513; --card:#1C1D1A; --ink:#EFEBDD; --muted:#A6A297;
  --faint:#7C7970; --rule:#2A2C27; --shadow:rgba(0,0,0,.5); }
:root[data-theme="light"]{ --paper:#F7F5EF; --card:#FFFFFF; --ink:#131512; --muted:#5B5A50;
  --faint:#8A887B; --rule:#E4E0D2; --shadow:rgba(19,21,18,.16); }

*{box-sizing:border-box}
body{margin:0;background:var(--paper);color:var(--ink);font-family:var(--sans);
  font-size:14px;line-height:1.5;-webkit-font-smoothing:antialiased}
.wrap{max-width:1560px;margin:0 auto;padding:0 20px 80px}

header{padding:34px 0 18px;border-bottom:2px solid var(--ink)}
h1{font-family:var(--serif);font-weight:600;font-size:30px;line-height:1.25;margin:0;
  letter-spacing:.02em;text-wrap:balance}
.sub{margin:8px 0 0;color:var(--muted);max-width:62ch}
.sub code{font-family:var(--mono);font-size:12px;background:var(--card);
  border:1px solid var(--rule);padding:1px 5px}

.bar{position:sticky;top:0;z-index:20;display:flex;flex-wrap:wrap;align-items:center;gap:10px;
  padding:10px 0;background:var(--paper);border-bottom:1px solid var(--rule)}
.bar input[type=search]{font:inherit;font-family:var(--mono);font-size:12px;width:230px;
  padding:6px 9px;color:var(--ink);background:var(--card);border:1px solid var(--ink);border-radius:0}
.bar input[type=search]:focus{outline:2px solid var(--amber);outline-offset:1px}
button{font:inherit;font-size:12px;padding:6px 12px;color:var(--ink);background:var(--card);
  border:1px solid var(--ink);border-radius:0;cursor:pointer}
button:hover{background:var(--rule)}
button:focus-visible{outline:2px solid var(--amber);outline-offset:1px}
button[aria-pressed=true]{background:var(--ink);color:var(--paper)}
#play{background:var(--amber);border-color:var(--ink);color:#1A1608;font-weight:700;min-width:96px}
.seg{display:flex}
.seg button{border-left-width:0}
.seg button:first-child{border-left-width:1px}
.count{margin-left:auto;font-family:var(--mono);font-size:12px;color:var(--muted);
  font-variant-numeric:tabular-nums}

section{margin-top:34px}
section[hidden]{display:none}
.rhead{display:flex;align-items:baseline;gap:12px;padding-bottom:8px;border-bottom:1px solid var(--ink)}
.rhead h2{font-family:var(--serif);font-size:19px;font-weight:600;margin:0;letter-spacing:.03em}
.rhead .en{font-family:var(--mono);font-size:11px;color:var(--faint);letter-spacing:.08em;
  text-transform:uppercase}
.rhead .note{color:var(--muted);font-size:12px}
.rhead .num{margin-left:auto;font-family:var(--mono);font-size:12px;color:var(--muted);
  font-variant-numeric:tabular-nums}

.grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(var(--tile),max-content));
  gap:14px 12px;padding-top:14px;justify-content:start}
figure{margin:0;width:var(--tile)}
figure[hidden]{display:none}
.stage{width:var(--tile);height:var(--tile);background-color:var(--stage);
  background-repeat:no-repeat;image-rendering:pixelated;border:0;padding:0;display:block;
  cursor:zoom-in;box-shadow:0 1px 0 var(--shadow)}
.stage:focus-visible{outline:2px solid var(--amber);outline-offset:2px}
figcaption{padding-top:5px}
.cn{display:block;font-family:var(--mono);font-size:10.5px;line-height:1.35;color:var(--ink);
  word-break:break-all}
.cm{display:block;font-family:var(--mono);font-size:10px;color:var(--faint);
  font-variant-numeric:tabular-nums}

dialog{border:2px solid var(--ink);border-radius:0;background:var(--card);color:var(--ink);
  padding:0;max-width:min(96vw,700px)}
dialog::backdrop{background:rgba(19,21,18,.62)}
.mbody{padding:16px}
.mstage{width:512px;max-width:100%;aspect-ratio:1;background-color:var(--stage);
  background-repeat:no-repeat;image-rendering:pixelated;margin:0 auto;display:block}
.mtitle{font-family:var(--mono);font-size:13px;margin:0 0 10px;word-break:break-all}
.mctl{display:flex;align-items:center;gap:10px;margin-top:12px}
.mctl input[type=range]{flex:1;accent-color:var(--amber)}
.mmeta{font-family:var(--mono);font-size:11px;color:var(--muted);
  font-variant-numeric:tabular-nums;min-width:96px;text-align:right}
"""

JS = r"""
const TILE = __FRAME__;
const DATA = __DATA__;
const state = {playing: !window.matchMedia('(prefers-reduced-motion: reduce)').matches, speed: 1, t: 0};
const tiles = [];

const byName = {};
DATA.forEach(sec => sec.items.forEach(it => byName[it.n] = it));

function src(it){ return 'url("data:image/png;base64,' + it.b + '")'; }

const io = new IntersectionObserver(es => {
  for (const e of es){
    const el = e.target;
    if (e.isIntersecting){
      if (!el.dataset.on){ el.style.backgroundImage = src(byName[el.dataset.n]); el.dataset.on = '1'; }
    } else if (el.dataset.on){
      el.style.backgroundImage = 'none'; delete el.dataset.on;   // 見えない分は画像を手放す
    }
  }
}, {rootMargin: '400px 0px'});

function build(){
  const wrap = document.getElementById('sections');
  for (const sec of DATA){
    const s = document.createElement('section');
    s.dataset.role = sec.role;
    s.innerHTML = '<div class="rhead"><h2>' + sec.ja + '</h2><span class="en">' + sec.role +
      '</span><span class="note">' + sec.note + '</span><span class="num">' +
      sec.items.length + '本</span></div>';
    const g = document.createElement('div');
    g.className = 'grid';
    for (const it of sec.items){
      const fig = document.createElement('figure');
      const btn = document.createElement('button');
      btn.className = 'stage';
      btn.dataset.n = it.n;
      btn.style.backgroundSize = (TILE * it.f) + 'px auto';
      btn.setAttribute('aria-label', it.n + ' を拡大');
      btn.addEventListener('click', () => openClip(it));
      const cap = document.createElement('figcaption');
      cap.innerHTML = '<span class="cn">' + it.n.slice(sec.role.length + 1) + '</span>' +
        '<span class="cm">' + it.d.toFixed(2) + 's / ' + it.f + 'f' +
        (it.fps < 11.5 ? ' / ' + it.fps + 'fps' : '') + '</span>';
      fig.appendChild(btn); fig.appendChild(cap);
      g.appendChild(fig);
      tiles.push({el: btn, fig: fig, it: it});
      io.observe(btn);
    }
    s.appendChild(g);
    wrap.appendChild(s);
  }
}

let last = 0;
function loop(now){
  if (state.playing){
    const dt = last ? Math.min(0.1, (now - last) / 1000) : 0;
    state.t += dt * state.speed;
  }
  last = now;
  for (const t of tiles){
    if (!t.el.dataset.on) continue;
    const f = Math.floor((state.t / t.it.d) * t.it.f) % t.it.f;
    t.el.style.backgroundPositionX = (-f * TILE) + 'px';
  }
  if (modal.open && cur){
    if (state.playing){
      const f = Math.floor((state.t / cur.d) * cur.f) % cur.f;
      setModalFrame(f);
    }
  }
  requestAnimationFrame(loop);
}

const modal = document.getElementById('modal');
const mstage = document.getElementById('mstage');
const mrange = document.getElementById('mrange');
const mmeta = document.getElementById('mmeta');
let cur = null;

function setModalFrame(f){
  const w = mstage.clientWidth;
  mstage.style.backgroundSize = (w * cur.f) + 'px auto';
  mstage.style.backgroundPositionX = (-f * w) + 'px';
  mrange.value = f;
  mmeta.textContent = (f + 1) + ' / ' + cur.f + ' コマ';
}

function openClip(it){
  cur = it;
  document.getElementById('mtitle').textContent = it.n;
  mstage.style.backgroundImage = src(it);
  mrange.max = it.f - 1;
  document.getElementById('msub').textContent =
    it.d.toFixed(2) + '秒 / ' + it.f + 'コマ / ' + it.fps + 'fps';
  modal.showModal();          // 先に開かないと clientWidth が0でコマ幅を計算できない
  setModalFrame(0);
}

mrange.addEventListener('input', () => { state.playing = false; syncPlay(); setModalFrame(+mrange.value); });
modal.addEventListener('close', () => { cur = null; mstage.style.backgroundImage = 'none'; });

const playBtn = document.getElementById('play');
function syncPlay(){
  playBtn.textContent = state.playing ? '❙❙ 停止' : '▶ 再生';
  playBtn.setAttribute('aria-pressed', String(state.playing));
}
playBtn.addEventListener('click', () => { state.playing = !state.playing; syncPlay(); });

document.querySelectorAll('[data-speed]').forEach(b => b.addEventListener('click', () => {
  state.speed = parseFloat(b.dataset.speed);
  document.querySelectorAll('[data-speed]').forEach(o =>
    o.setAttribute('aria-pressed', String(o === b)));
}));
document.querySelectorAll('[data-tile]').forEach(b => b.addEventListener('click', () => {
  document.documentElement.style.setProperty('--tile', b.dataset.tile + 'px');
  tiles.forEach(t => t.el.style.backgroundSize = (parseInt(b.dataset.tile, 10) * t.it.f) + 'px auto');
  document.querySelectorAll('[data-tile]').forEach(o =>
    o.setAttribute('aria-pressed', String(o === b)));
}));

const q = document.getElementById('q');
const count = document.getElementById('count');
function filter(){
  const v = q.value.trim().toLowerCase();
  let shown = 0;
  for (const t of tiles){
    const hit = !v || t.it.n.toLowerCase().includes(v);
    t.fig.hidden = !hit;
    if (hit) shown++;
  }
  document.querySelectorAll('#sections section').forEach(s => {
    s.hidden = !s.querySelector('figure:not([hidden])');
  });
  count.textContent = shown + ' / ' + tiles.length + ' 本';
}
q.addEventListener('input', filter);

build();
filter();
syncPlay();
requestAnimationFrame(loop);
"""


def main():
    out = sys.argv[1]
    want = sys.argv[2:] or [r[0] for r in ROLES]
    data = []
    total = 0
    for role, ja, note in ROLES:
        if role not in want:
            continue
        items = load(role)
        if not items:
            continue
        total += len(items)
        data.append({"role": role, "ja": ja, "note": note, "items": items})

    payload = json.dumps(data, ensure_ascii=False, separators=(",", ":"))
    html = []
    html.append('<title>AAパック モーション棚卸し</title>')
    html.append("<style>%s</style>" % CSS)
    html.append('<div class="wrap">')
    html.append('<header><h1>AA Basic Baseball ― モーション棚卸し</h1>'
                '<p class="sub">購入パックの全%d本を素体（UBC Superhero Male）へリターゲットし、'
                '12fpsで焼いたコマ列。対決ビューの構図ではなく、動きを見比べるための'
                '固定カメラ（3/4正面・全身）。下端の細い線が接地面。'
                'クリップ名は <code>AA_Baseball_</code> を外した実キー。</p></header>' % total)
    html.append('<div class="bar">'
                '<button id="play" aria-pressed="true">❙❙ 停止</button>'
                '<div class="seg" role="group" aria-label="再生速度">'
                '<button data-speed="0.25" aria-pressed="false">0.25×</button>'
                '<button data-speed="0.5" aria-pressed="false">0.5×</button>'
                '<button data-speed="1" aria-pressed="true">等速</button></div>'
                '<div class="seg" role="group" aria-label="表示倍率">'
                '<button data-tile="128" aria-pressed="true">×1</button>'
                '<button data-tile="256" aria-pressed="false">×2</button></div>'
                '<input type="search" id="q" placeholder="クリップ名で絞り込み" '
                'aria-label="クリップ名で絞り込み">'
                '<span class="count" id="count"></span></div>')
    html.append('<div id="sections"></div>')
    html.append('</div>')
    html.append('<dialog id="modal"><div class="mbody">'
                '<p class="mtitle" id="mtitle"></p>'
                '<div class="mstage" id="mstage"></div>'
                '<div class="mctl">'
                '<button onclick="document.getElementById(\'modal\').close()">閉じる</button>'
                '<input type="range" id="mrange" min="0" value="0" aria-label="コマ送り">'
                '<span class="mmeta" id="mmeta"></span></div>'
                '<p class="cm" id="msub" style="margin:8px 0 0"></p>'
                '</div></dialog>')
    js = JS.replace("__FRAME__", str(FRAME)).replace("__DATA__", payload)
    html.append("<script>%s</script>" % js)

    with open(out, "w", encoding="utf-8") as f:
        f.write("\n".join(html))
    print("%s  clips=%d  %.1fMB" % (out, total, os.path.getsize(out) / 1e6))


if __name__ == "__main__":
    main()
