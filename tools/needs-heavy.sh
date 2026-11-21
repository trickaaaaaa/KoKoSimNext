#!/usr/bin/env bash
# この変更に Heavy（統計回帰）が要るかを判定する。要るなら終了コード 0、要らないなら 1。
# 理由を1行で標準出力に出す。
#
# 使い方:
#   bash tools/needs-heavy.sh                     # origin/main...HEAD の差分で判定
#   bash tools/needs-heavy.sh <base> [<worktree>] [<head>]  # 基準・作業ツリー・比較先（既定 HEAD）を指定
#   比較先が HEAD（既定）のときは、まだコミットしていない変更（ステージ済み・未ステージ・未追跡）も見る
#   （issue #1232: CLAUDE.md は「コミット前に判定」と言うのに、以前はコミット済みの差分しか見ず、
#   data/ を編集中に回すと「不要」と答えていた）。
#
# 判定の考え方（2026-11-09）:
#   試合の結果が1ビットでも変わる変更は、日常ループの決定論ゲート（代表6カード×50シードの
#   全結果の指紋 determinism-baseline.txt）が赤くなる＝指紋を作り直してコミットすることになる。
#   DH・天候・左右非対称の球場・球数上限は "dh-weather" カードが通す（issue #1232。以前はどのカードも
#   通らず、これらだけの変更が指紋をすり抜けていた）。
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
  echo "Heavy 要: 差分を取れない（${BASE}）"; exit 0; }
WORKTREE_FILES=""
if [ "$HEADREF" = "HEAD" ]; then
  WORKTREE_FILES="$( { git -C "$WT" diff --name-only HEAD 2>/dev/null; git -C "$WT" ls-files --others --exclude-standard 2>/dev/null; } | sort -u)"
  files="$(printf '%s\n%s\n' "$files" "$WORKTREE_FILES" | grep -v '^$' | sort -u)"
fi
in_worktree() { [ -n "$WORKTREE_FILES" ] && printf '%s\n' "$WORKTREE_FILES" | grep -qxF "$1"; }

need() { echo "Heavy 要: $1"; exit 0; }

while IFS= read -r f; do
  [ -z "$f" ] && continue
  case "$f" in
    data/*|global.json)                     need "データ／SDK を変更（${f}）" ;;
    */determinism-baseline.txt)             need "試合結果の指紋が変わった（${f}）" ;;
    engine/KokoSim.Engine/Match/*)          ;;   # 指紋で守られている
    engine/KokoSim.Engine.Tests/*)
      # Heavy のテスト自体を触ったら回す（消したファイルは中身を見られないので回す）。
      # 未コミットの変更はディスク上の中身を見る
      if in_worktree "$f"; then
        if [ ! -f "$WT/$f" ] || grep -q '"Category", *"Heavy"' "$WT/$f"; then
          need "Heavy のテストを変更（${f}）"
        fi
      elif ! git -C "$WT" cat-file -e "$HEADREF:$f" 2>/dev/null \
         || git -C "$WT" show "$HEADREF:$f" | grep -q '"Category", *"Heavy"'; then
        need "Heavy のテストを変更（${f}）"
      fi ;;
    engine/*)                               need "指紋で測っていない engine を変更（${f}）" ;;
    *)                                      ;;   # Assets/・docs/・tools/ 等は帯に無関係
  esac
done <<< "$files"

echo "Heavy 不要: 試合の解決（Match/）とテストだけの変更で、試合結果の指紋もデータも不変（未コミット分も含めて判定）"
exit 1
