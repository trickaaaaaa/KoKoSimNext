#!/usr/bin/env bash
# git フック（post-checkout / post-merge / post-rewrite）から呼ぶ。HEAD が動いた前後で
# engine/KokoSim.Engine か data/ が変わったとき、または DLL がまだ無いときだけ
# tools/sync-engine-dll.sh を回す（issue #785）。DLL は Git 管理外なので、ブランチを
# 切り替えても pull しても、ここで作り直さない限り Unity は古い DLL のまま動く。
#
# 使い方: bash tools/sync-engine-dll-if-changed.sh <旧ref> <新ref>
# フックの後処理なので、失敗しても git 操作は止めない（警告だけ出して 0 で終わる）。
set -u

root="$(git rev-parse --show-toplevel 2>/dev/null)" || exit 0
old="${1:-}"; new="${2:-HEAD}"
dll="$root/Assets/Plugins/KokoSim/KokoSim.Engine.dll"

need=0
if [ ! -f "$dll" ]; then
  need=1
elif [ -z "$old" ] || ! git -C "$root" rev-parse -q --verify "$old^{commit}" >/dev/null 2>&1; then
  need=1   # 旧 ref が無い（clone 直後など）＝比べられないので作り直す
elif ! git -C "$root" diff --quiet "$old" "$new" -- engine/KokoSim.Engine data 2>/dev/null; then
  need=1
fi
[ "$need" = 0 ] && exit 0

echo "[engine-dll] engine/ か data/ が変わったので DLL と StreamingAssets を作り直します…"
if ! bash "$root/tools/sync-engine-dll.sh" "$root"; then
  echo "[engine-dll] ⚠ 同期に失敗しました。手で bash tools/sync-engine-dll.sh を回してください。" >&2
fi
exit 0
