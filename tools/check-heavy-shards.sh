#!/usr/bin/env bash
# .github/workflows/ci.yml の heavy シャード分割が Category=Heavy を過不足なく覆うか検証する（issue #23）。
#
# 検査するのは2点:
#   1) 取りこぼしなし … 各シャードの --list-tests の和集合 == Category=Heavy 全件
#   2) 重複なし       … 同じテストが2つ以上のシャードで走らない（＝ランナー時間の無駄）
#
# 不変条件#5「バランス影響のある変更後は統計回帰を必ず実行」を毎PRで満たすには、
# Heavy が1件でもどのシャードにも入らない状態を作ってはならない。CI の fast ジョブから呼ぶ。
set -euo pipefail

DOTNET="${DOTNET:-dotnet}"
command -v "$DOTNET" >/dev/null 2>&1 || DOTNET=/usr/local/share/dotnet/dotnet

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SLN="$ROOT/engine/KokoSim.sln"
CI_YML="$ROOT/.github/workflows/ci.yml"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT

"$DOTNET" build "$SLN" -c Release >/dev/null

# フィルタ式ごとに、そのシャードで走るテストの完全修飾名を列挙する。
list_tests() {
  "$DOTNET" test "$SLN" -c Release --no-build --filter "$1" --list-tests 2>/dev/null \
    | sed -n 's/^    \(KokoSim\..*\)$/\1/p' | LC_ALL=C sort
}

list_tests "Category=Heavy" > "$WORK/all.txt"
echo "Category=Heavy: $(wc -l < "$WORK/all.txt" | tr -d ' ') 件"

# ci.yml の heavy マトリクスから filter 行を抜き出す（filter: は heavy シャード定義にしか無い）。
# bash 3.2（macOS 既定）でも動くよう mapfile は使わない。
sed -n 's/^ *filter: "\(.*\)"$/\1/p' "$CI_YML" > "$WORK/filters.txt"
if [ ! -s "$WORK/filters.txt" ]; then
  echo "ci.yml から heavy シャードの filter を抽出できなかった" >&2
  exit 1
fi

: > "$WORK/union.txt"
while IFS= read -r f; do
  list_tests "$f" > "$WORK/shard.txt"
  printf '  %3d 件  %s\n' "$(wc -l < "$WORK/shard.txt" | tr -d ' ')" "$f"
  cat "$WORK/shard.txt" >> "$WORK/union.txt"
done < "$WORK/filters.txt"

status=0

LC_ALL=C sort "$WORK/union.txt" | uniq -d > "$WORK/dup.txt"
if [ -s "$WORK/dup.txt" ]; then
  echo "NG: 複数シャードで重複実行されるテスト:" >&2
  cat "$WORK/dup.txt" >&2
  status=1
fi

LC_ALL=C sort -u "$WORK/union.txt" > "$WORK/union-sorted.txt"
if ! diff -u "$WORK/all.txt" "$WORK/union-sorted.txt" > "$WORK/diff.txt"; then
  echo "NG: シャードの和集合が Category=Heavy と一致しない（- が取りこぼし / + が余分）:" >&2
  cat "$WORK/diff.txt" >&2
  status=1
fi

if [ "$status" -eq 0 ]; then
  echo "OK: heavy シャードは Category=Heavy を過不足なく覆っている"
fi
exit "$status"
