#!/usr/bin/env bash
# tools/git-hooks/ のフックを .git/hooks へ導入する（issue #349 / #785）。
# git はフックをリポジトリで共有できないので、各自が一度これを実行する必要がある。
#   pre-push                            … Assets/ の C# を触った push を Unity コンパイル検査で止める
#   post-checkout / post-merge / post-rewrite … engine DLL・StreamingAssets を作り直す（Git 管理外の生成物）
# clone 直後はこれ1つで DLL まで揃う（最後に一度 sync-engine-dll.sh を回す）。
set -eu
repo="$(cd "$(dirname "$0")/.." && pwd)"
hooks="$(git -C "$repo" rev-parse --git-path hooks)"
[ "${hooks#/}" = "$hooks" ] && hooks="$repo/$hooks"
mkdir -p "$hooks"
for f in "$repo"/tools/git-hooks/*; do
  n="$(basename "$f")"
  cp "$f" "$hooks/$n"
  chmod +x "$hooks/$n"
  echo "導入: $hooks/$n"
done
echo "完了。外すときは $hooks の該当ファイルを消してください。"
echo ""
echo "engine DLL と StreamingAssets を作ります（Git 管理外の生成物・issue #785）…"
bash "$repo/tools/sync-engine-dll.sh" "$repo"
