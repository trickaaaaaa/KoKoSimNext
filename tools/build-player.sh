#!/usr/bin/env bash
# Windows の製品ビルドを batchmode で作る（issue #1310・docs/release/steam-roadmap.md 段階1）。
# 中身は Assets/Editor/Build/PlayerBuilder.cs。Editor を開いているときは
# メニュー「KokoSim/ビルド/…」から作る（同じプロジェクトを batchmode で二重に開けないため）。
#
# 使い方:
#   bash tools/build-player.sh            # 製品版   → out/build/windows-release/
#   bash tools/build-player.sh demo       # 体験版   → out/build/windows-demo/（KOKOSIM_DEMO）
#   bash tools/build-player.sh dev        # 開発ビルド → out/build/windows-dev/（DEVELOPMENT_BUILD）
#
# 前提: Unity Hub でこの版に「Windows Build Support (Mono)」を入れてあること。
# ログ: out/build/<flavor>.log
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
FLAVOR="${1:-release}"
case "$FLAVOR" in release|demo|dev) ;; *) echo "使い方: $0 [release|demo|dev]" >&2; exit 2;; esac

VERSION="$(sed -n 's/^m_EditorVersion: //p' "$ROOT/ProjectSettings/ProjectVersion.txt")"
UNITY="${UNITY:-/Applications/Unity/Hub/Editor/$VERSION/Unity.app/Contents/MacOS/Unity}"
[ -x "$UNITY" ] || { echo "Unity $VERSION が見つかりません: $UNITY（UNITY= で指定できます）" >&2; exit 2; }

if [ -f "$ROOT/Temp/UnityLockfile" ] && pgrep -if "Unity.app/Contents/MacOS/Unity .*-projectpath $ROOT( |$)" >/dev/null 2>&1; then
  echo "Editor でこのプロジェクトが開いています。メニュー「KokoSim/ビルド/…」から作るか、Editor を閉じてください。" >&2
  exit 2
fi

# engine DLL と Resources/KokoSimData の YAML は Git 管理外の生成物（issue #785）。最新を作ってから焼く。
bash "$ROOT/tools/sync-engine-dll.sh" "$ROOT"

mkdir -p "$ROOT/out/build"
LOG="$ROOT/out/build/$FLAVOR.log"
echo "ビルド中（$FLAVOR）… ログ: ${LOG#$ROOT/}"

set +e
"$UNITY" -batchmode -nographics -quit \
  -projectPath "$ROOT" \
  -buildTarget Win64 \
  -executeMethod KokoSim.EditorTools.PlayerBuilder.BuildFromCommandLine \
  -kokosimFlavor "$FLAVOR" \
  -logFile "$LOG"
rc=$?
set -e

grep -E '^\[PlayerBuilder\]|^(- |注意: )' "$LOG" || true
if [ $rc -ne 0 ]; then
  echo "🔴 ビルド失敗（終了コード $rc）。詳細は $LOG" >&2
  exit 1
fi
echo "🟢 完了: out/build/windows-$FLAVOR/"
