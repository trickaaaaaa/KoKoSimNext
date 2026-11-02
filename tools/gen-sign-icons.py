#!/usr/bin/env python3
"""采配カード（TacticsCardDeck）の攻撃サイン絵10枚を SVG から PNG へ書き出す。

元絵は tools/sign-icons/*.svg（黒＝絵、白＝抜き、100×100 の viewBox）。
出力は Assets/UI/Images/signs/*.png ＝ **白い絵＋透明地**（256px）。
色は USS の -unity-background-image-tint-color で付ける（白地なので乗算でそのまま指定色になる）
＝選択中・選べない等の状態色を絵の差し替えなしで出せる。

白い「抜き」（手のひらの指の境目・ボールの中など）は透明にする。黒白の明度をそのまま
α に写すので、アンチエイリアスの縁も保たれる。

ラスタライズは Google Chrome のヘッドレス描画を使う（SVG の描画系を別途入れずに済む）。

使い方: python3 tools/gen-sign-icons.py
        （.meta は既存のものを使い回すので消さないこと＝GUID が変わると USS の参照が切れる）
"""
import pathlib
import subprocess
import sys
import tempfile

from PIL import Image

ROOT = pathlib.Path(__file__).resolve().parent.parent
SRC = ROOT / "tools" / "sign-icons"
OUT = ROOT / "Assets" / "UI" / "Images" / "signs"
SIZE = 256
CHROME = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome"


def render(svg: pathlib.Path, work: pathlib.Path) -> Image.Image:
    page = work / (svg.stem + ".html")
    page.write_text(
        '<!doctype html><html><body style="margin:0;background:#fff">'
        f'<img src="{svg.as_uri()}" width="{SIZE}" height="{SIZE}" style="display:block"></body></html>',
        encoding="utf-8",
    )
    shot = work / (svg.stem + ".png")
    subprocess.run(
        [CHROME, "--headless=new", "--disable-gpu", "--hide-scrollbars", "--force-device-scale-factor=1",
         "--allow-file-access-from-files", f"--window-size={SIZE},{SIZE}", f"--screenshot={shot}", page.as_uri()],
        check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
    )
    return Image.open(shot).convert("L").crop((0, 0, SIZE, SIZE))


def main() -> int:
    svgs = sorted(SRC.glob("*.svg"))
    if not svgs:
        print(f"SVG が見つからない: {SRC}", file=sys.stderr)
        return 1
    OUT.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory() as tmp:
        work = pathlib.Path(tmp)
        for svg in svgs:
            gray = render(svg, work)
            alpha = gray.point(lambda v: 255 - v)
            img = Image.new("RGBA", (SIZE, SIZE), (255, 255, 255, 0))
            img.putalpha(alpha)
            if img.getbbox() is None:
                print(f"空の絵になった: {svg.name}", file=sys.stderr)
                return 1
            img.save(OUT / (svg.stem + ".png"))
            print(f"{svg.stem}.png")
    return 0


if __name__ == "__main__":
    sys.exit(main())
