#!/bin/bash
# Play 中の通し確認（*FlowCheck 等）の結果を Editor.log で待つ。止まったら待たずに失敗で抜ける。
#
#   bash tools/wait-unity-harness.sh <結果の行の正規表現> [上限秒=900] [無音の上限秒=0（無効）]
#   例: bash tools/wait-unity-harness.sh '\[DISTRICTFLOW\] \(PASS\|FAIL\)'
#
# 起動した時点のログ行から先だけを見る（前の回の PASS を拾わない）。
# 抜ける条件:
#   - 結果の行が出た → 0（その後の40行＝明細を表示）
#   - ドメインリロード（Play 中の再コンパイル）→ 2。コルーチンが死に、状態も巻き戻っている
#   - Play が止まった → 3
#   - 例外が出た → 4（最初の例外を表示）
#   - 上限秒を過ぎた → 5
#   - ログが無音の上限秒だけ伸びない → 6（固まった疑い。確認が途中で何も出さない作りなら 0＝無効のまま）
set -u
PATTERN="$1"
LIMIT="${2:-900}"
SILENT_LIMIT="${3:-0}"
LOG="$HOME/Library/Logs/Unity/Editor.log"
START=$(($(wc -l < "$LOG") + 1))
BEGIN=$(date +%s)
LAST_SIZE=$(wc -c < "$LOG")
LAST_GROW=$BEGIN

fresh() { tail -n +"$START" "$LOG"; }

while true; do
  NOW=$(date +%s)
  if fresh | grep -q "$PATTERN"; then
    fresh | grep -B3 -A40 "$PATTERN" | grep -v '^UnityEngine\|^KokoSim\|^(File\|^System\|^$' | head -60
    exit 0
  fi
  if fresh | grep -q 'Reloading assemblies'; then
    echo "STALL: Play 中にドメインリロードが走った（確認のコルーチンは死んでいる）。Play を止めてやり直す"
    exit 2
  fi
  if fresh | grep -q 'Exiting playmode\|ExitingPlayMode'; then
    echo "STALL: Play が止まった"
    exit 3
  fi
  if fresh | grep -q 'Exception:'; then
    echo "STALL: 例外が出た"
    fresh | grep -m1 -A8 'Exception:'
    exit 4
  fi
  if [ $((NOW - BEGIN)) -ge "$LIMIT" ]; then
    echo "STALL: ${LIMIT}秒たっても結果が出ない"
    exit 5
  fi
  SIZE=$(wc -c < "$LOG")
  if [ "$SIZE" != "$LAST_SIZE" ]; then LAST_SIZE=$SIZE; LAST_GROW=$NOW; fi
  if [ "$SILENT_LIMIT" -gt 0 ] && [ $((NOW - LAST_GROW)) -ge "$SILENT_LIMIT" ]; then
    echo "STALL: ログが${SILENT_LIMIT}秒伸びていない（固まった疑い）"
    exit 6
  fi
  sleep 5
done
