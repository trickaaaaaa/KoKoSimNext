#!/usr/bin/env bash
# 決定論ベースライン（engine/KokoSim.Engine.Tests/Match/Game/determinism-baseline.txt）を現行エンジンで作り直す。
# エンジンの挙動を**意図して**変えたときだけ使う（帯の再校正・確率モデルの変更）。RNG 消費が変わっていない
# はずの変更で差が出たら、それは回帰＝このスクリプトで上書きしてはいけない。
# 実行: bash tools/regen-determinism-baseline.sh
#
# 失敗は必ず理由を出して非0で終わる（issue #1232: 以前は Skip 文言が変わると sed が外れて grep が空振りし、
# set -e で無言終了＋.new が残っていた）。夜間エージェントの rebase 後の作り直しからも呼ばれる。
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="${DOTNET:-/usr/local/share/dotnet/dotnet}"
DUMP="$ROOT/engine/KokoSim.Engine.Tests/Match/Game/DeterminismBaselineDump.cs"
OUT="$ROOT/engine/KokoSim.Engine.Tests/Match/Game/determinism-baseline.txt"
CARDS="$ROOT/engine/KokoSim.Engine.Tests/Match/Game/GameResultDigest.cs"

# Skip を外す対象の行が無ければ（文言が変わった等）何もせず止める
SKIP_RE='\[Fact(Skip = "[^"]*")\]'
if ! grep -q "$SKIP_RE" "$DUMP"; then
  echo "DeterminismBaselineDump.cs に [Fact(Skip = \"...\")] が見つからない（文言・形が変わった？）" >&2
  exit 1
fi

cp "$DUMP" "$DUMP.bak"
trap 'mv "$DUMP.bak" "$DUMP"; rm -f "$OUT.new"' EXIT
# Skip を外して1回だけ走らせる。
sed -i '' "s/$SKIP_RE/[Fact]/" "$DUMP"

set +e
"$DOTNET" test "$ROOT/engine/KokoSim.Engine.Tests" -c Release \
  --filter "FullyQualifiedName~DeterminismBaselineDump" --logger "console;verbosity=detailed" > "$OUT.log" 2>&1
rc=$?
set -e
grep -E "^[[:space:]]*BL " "$OUT.log" | sed -E 's/^[[:space:]]*BL //' > "$OUT.new" || true
if [ "$rc" -ne 0 ]; then
  echo "ダンプのテストが失敗した（終了コード ${rc}）。ログ末尾:" >&2; tail -30 "$OUT.log" >&2; rm -f "$OUT.log"; exit 1
fi
rm -f "$OUT.log"

# 期待行数＝カード数×シード数（GameResultDigest.cs の定義から数える。EngineDeterminismGateTests と同じ根拠）
cards=$(grep -E 'CardNames *= *\{' "$CARDS" | grep -o '"[^"]*"' | wc -l | tr -d ' ')
seeds=$(sed -n 's/.*for (ulong s = 1; s <= \([0-9][0-9]*\);.*/\1/p' "$CARDS" | head -1)
if [ -z "$cards" ] || [ "$cards" -eq 0 ] || [ -z "$seeds" ]; then
  echo "GameResultDigest.cs からカード数・シード数を読めない（cards=${cards:-?} seeds=${seeds:-?}）" >&2; exit 1
fi
expected=$((cards * seeds))
lines=$(wc -l < "$OUT.new" | tr -d ' ')
if [ "$lines" -ne "$expected" ]; then echo "ベースラインの行数が ${expected}（${cards}カード×${seeds}シード）でない: $lines" >&2; exit 1; fi
bad=$(grep -cvE '^[a-z-]+ [0-9]+ [0-9a-f]{64}$' "$OUT.new" || true)
if [ "$bad" -ne 0 ]; then echo "形式の崩れた行が ${bad} 行ある" >&2; exit 1; fi
dup=$(awk '{print $1" "$2}' "$OUT.new" | sort | uniq -d | wc -l | tr -d ' ')
if [ "$dup" -ne 0 ]; then echo "同じカード×シードが重複している（${dup}件）" >&2; exit 1; fi
mv "$OUT.new" "$OUT"
echo "再生成: $OUT ($lines 行)"
