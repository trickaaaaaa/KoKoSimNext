#!/usr/bin/env bash
# この変更に Heavy（統計回帰）が要るかを判定する。要るなら終了コード 0、要らないなら 1。
# 理由を1行で標準出力に出す。
#
# 使い方:
#   bash tools/needs-heavy.sh                     # origin/main...HEAD の差分で判定
#   bash tools/needs-heavy.sh <base> [<worktree>] [<head>]  # 基準・作業ツリー・比較先（既定 HEAD）を指定
#
# 判定の考え方（2026-11-09）:
#   試合の結果が1ビットでも変わる変更は、日常ループの決定論ゲート（代表3カード×50シードの
#   全結果の指紋 determinism-baseline.txt）が赤くなる＝指紋を作り直してコミットすることになる。
#   したがって「試合の解決（Match/）だけを触り、指紋もデータも変えていない」変更は帯を動かさない。
#   指紋で測っていない所（ロスター生成・全国・育成・係数の C#既定・Balance CLI）を触ったら Heavy。
#   日常ループが緑であることが前提（指紋が守られていると言えるのは日常ループが通ったときだけ）。
#
#   CI（engine-ci）は PR とマージのたびに Heavy を全部回す＝ここで省いても取りこぼしは CI が拾う。
set -uo pipefail
BASE="${1:-origin/main}"
WT="${2:-$(cd "$(dirname "$0")/.." && pwd)}"
HEADREF="${3:-HEAD}"

files="$(git -C "$WT" diff --name-only "$BASE...$HEADREF" 2>/dev/null)" || {
  echo "Heavy 要: 差分を取れない（$BASE）"; exit 0; }

need() { echo "Heavy 要: $1"; exit 0; }

while IFS= read -r f; do
  [ -z "$f" ] && continue
  case "$f" in
    data/*|global.json)                     need "データ／SDK を変更（$f）" ;;
    */determinism-baseline.txt)             need "試合結果の指紋が変わった（$f）" ;;
    engine/KokoSim.Engine/Match/*)          ;;   # 指紋で守られている
    engine/KokoSim.Engine.Tests/*)
      # Heavy のテスト自体を触ったら回す（消したファイルは中身を見られないので回す）
      if ! git -C "$WT" cat-file -e "$HEADREF:$f" 2>/dev/null \
         || git -C "$WT" show "$HEADREF:$f" | grep -q '"Category", *"Heavy"'; then
        need "Heavy のテストを変更（$f）"
      fi ;;
    engine/*)                               need "指紋で測っていない engine を変更（$f）" ;;
    *)                                      ;;   # Assets/・docs/・tools/ 等は帯に無関係
  esac
done <<< "$files"

echo "Heavy 不要: 試合の解決（Match/）とテストだけの変更で、試合結果の指紋もデータも不変"
exit 1
