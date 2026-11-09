#!/usr/bin/env bash
# テストの所要時間をクラス別に集計する（日常ループが遅くなったときの犯人探し）。
#
# xUnit は同じクラスのテスト（Theory の各ケースも）を直列に回す＝「クラスの合計」が
# そのまま壁時間の下限になる。上位のクラスが壁時間を決めていないかを見る（issue #639）。
#
# 使い方:
#   bash tools/test-timing.sh                          # 日常ループ（Category!=Heavy）を回して集計
#   bash tools/test-timing.sh "Category=Heavy"         # Heavy を回して集計
#   TRX=path/to/x.trx bash tools/test-timing.sh        # 既存の trx を集計だけする
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
FILTER="${1:-Category!=Heavy}"
DOTNET="${DOTNET:-$(command -v dotnet || echo /usr/local/share/dotnet/dotnet)}"

if [ -z "${TRX:-}" ]; then
  OUT="$(mktemp -d)"
  "$DOTNET" test "$REPO/engine/" -c Release --filter "$FILTER" \
    --logger "trx;LogFileName=timing.trx" --results-directory "$OUT" >/dev/null || true
  TRX="$OUT/timing.trx"
fi

python3 -I - "$TRX" <<'EOF'
import sys, collections, xml.etree.ElementTree as ET
ns = {'t': 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'}
root = ET.parse(sys.argv[1]).getroot()
cls_of = {u.get('id'): u.find('t:TestMethod', ns).get('className') for u in root.iterfind('.//t:UnitTest', ns)}
def sec(d):
    h, m, s = d.split(':'); return int(h) * 3600 + int(m) * 60 + float(s)
total = collections.Counter(); count = collections.Counter(); single = []
for r in root.iterfind('.//t:UnitTestResult', ns):
    c = cls_of[r.get('testId')]; d = sec(r.get('duration', '0:0:0'))
    total[c] += d; count[c] += 1; single.append((d, r.get('testName')))
print(f"テスト時間の合計 {sum(total.values()):.0f}s（クラスは直列＝上位クラスの合計が壁時間の下限）")
print("--- クラス別（合計の大きい順）")
for c, d in total.most_common(15):
    print(f"{d:7.1f}s {count[c]:4d}件  {c.split('.')[-1]}")
print("--- 1本あたり（遅い順）")
for d, n in sorted(single, reverse=True)[:10]:
    print(f"{d:7.1f}s {n[:120]}")
EOF
