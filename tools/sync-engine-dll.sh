#!/usr/bin/env bash
# 純エンジン(KokoSim.Engine)をビルドし、UnityのPluginsへ配置する。
# エンジンを変更したら実行し、Unity Editorを再フォーカスすると反映される。
# design-07 §1: エンジンはUnityへローカル参照（ここではnetstandard2.1のDLLとして共有）。
set -euo pipefail

DOTNET="${DOTNET:-/usr/local/share/dotnet/dotnet}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"

"$DOTNET" build "$ROOT/engine/KokoSim.Engine/KokoSim.Engine.csproj" -c Release

SRC="$ROOT/engine/KokoSim.Engine/bin/Release/netstandard2.1/KokoSim.Engine.dll"
DST_DIR="$ROOT/Assets/Plugins/KokoSim"
mkdir -p "$DST_DIR"
cp "$SRC" "$DST_DIR/KokoSim.Engine.dll"
echo "配置完了: $DST_DIR/KokoSim.Engine.dll"

# 校名語彙 YAML を StreamingAssets へ複製（Unity は data/ を直接読めない＝C-1）。data/ を単一ソースに保つ。
SA_DIR="$ROOT/Assets/StreamingAssets/KokoSim"
mkdir -p "$SA_DIR"
cp "$ROOT/data/school-names.yaml" "$SA_DIR/school-names.yaml"
echo "配置完了: $SA_DIR/school-names.yaml"
