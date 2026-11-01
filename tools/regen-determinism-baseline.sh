#!/usr/bin/env bash
# 決定論ベースライン（engine/KokoSim.Engine.Tests/Match/Game/determinism-baseline.txt）を現行エンジンで作り直す。
# エンジンの挙動を**意図して**変えたときだけ使う（帯の再校正・確率モデルの変更）。RNG 消費が変わっていない
# はずの変更で差が出たら、それは回帰＝このスクリプトで上書きしてはいけない。
# 実行: bash tools/regen-determinism-baseline.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="${DOTNET:-/usr/local/share/dotnet/dotnet}"
DUMP="$ROOT/engine/KokoSim.Engine.Tests/Match/Game/DeterminismBaselineDump.cs"
OUT="$ROOT/engine/KokoSim.Engine.Tests/Match/Game/determinism-baseline.txt"
cp "$DUMP" "$DUMP.bak"
trap 'mv "$DUMP.bak" "$DUMP"' EXIT
# Skip を外して1回だけ走らせる。
sed -i '' 's/\[Fact(Skip = "ベースライン再生成用。determinism-baseline.txt を作り直すときだけ Skip を外す。")\]/[Fact]/' "$DUMP"
"$DOTNET" test "$ROOT/engine/KokoSim.Engine.Tests" -c Release \
  --filter "FullyQualifiedName~DeterminismBaselineDump" --logger "console;verbosity=detailed" 2>&1 \
  | grep -E "^[[:space:]]*BL " | sed -E 's/^[[:space:]]*BL //' > "$OUT.new"
lines=$(wc -l < "$OUT.new" | tr -d ' ')
if [[ "$lines" -ne 250 ]]; then echo "ベースラインの行数が 250 でない: $lines" >&2; rm -f "$OUT.new"; exit 1; fi
mv "$OUT.new" "$OUT"
echo "再生成: $OUT ($lines 行)"
