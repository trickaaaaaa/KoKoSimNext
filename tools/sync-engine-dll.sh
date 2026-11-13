#!/usr/bin/env bash
# 純エンジン(KokoSim.Engine)をビルドし、UnityのPluginsへ配置する。
# design-07 §1: エンジンはUnityへローカル参照（ここではnetstandard2.1のDLLとして共有）。
#
# 配置先（DLL と StreamingAssets の YAML 複製）は**生成物で Git 管理外**（issue #785）。
# 並行 PR がマージのたびにバイナリ DLL で衝突していたため、PR に含めず各環境で作る。
#   - ローカル: tools/install-git-hooks.sh で入るフックが checkout / pull / rebase 後に自動で回す
#   - Unity CI: unity-ci.yml がコンパイル前に回す
#   - tools/check-unity-compile.sh も検査前に回す
# engine/ を編集して Unity で確かめたいときは手で回す（Unity Editor を再フォーカスすると反映される）。
#
# 使い方:
#   bash tools/sync-engine-dll.sh            # このリポジトリへ配置
#   bash tools/sync-engine-dll.sh <dir>      # 別の作業ツリー（git worktree 等）へ配置
#
# 中身が変わったファイルだけを上書きする（同じなら触らない＝Unity の無駄な再インポートを起こさない）。
set -euo pipefail

DOTNET="${DOTNET:-/usr/local/share/dotnet/dotnet}"
ROOT="${1:-$(cd "$(dirname "$0")/.." && pwd)}"
ROOT="$(cd "$ROOT" && pwd)"

# 変化があるときだけ上書きする
place() { # $1=src $2=dst
  if cmp -s "$1" "$2"; then return; fi
  cp "$1" "$2"
  echo "配置: ${2#$ROOT/}"
}

"$DOTNET" build "$ROOT/engine/KokoSim.Engine/KokoSim.Engine.csproj" -c Release -v q --nologo

DST_DIR="$ROOT/Assets/Plugins/KokoSim"
mkdir -p "$DST_DIR"
place "$ROOT/engine/KokoSim.Engine/bin/Release/netstandard2.1/KokoSim.Engine.dll" "$DST_DIR/KokoSim.Engine.dll"

# data/ の YAML を StreamingAssets へ複製（Unity は data/ を直接読めない＝C-1）。data/ を単一ソースに保つ。
SA_DIR="$ROOT/Assets/StreamingAssets/KokoSim"
mkdir -p "$SA_DIR"
for y in school-names.yaml team-colors.yaml events.yaml skills.yaml preview-phrases.yaml scout-blurbs.yaml stadiums.yaml; do
  place "$ROOT/data/$y" "$SA_DIR/$y"
done

# 県フォーマット（新規ゲーム設定の県内地区・issue #585）。Unity は地区数だけを
# PrefFormatDistrictParser で読む（正典は data/pref-formats/ のまま）。
PF_DIR="$SA_DIR/pref-formats"
mkdir -p "$PF_DIR"
for y in "$ROOT"/data/pref-formats/*.yaml; do
  place "$y" "$PF_DIR/$(basename "$y")"
done

echo "同期完了（engine DLL＋StreamingAssets）: $ROOT"
