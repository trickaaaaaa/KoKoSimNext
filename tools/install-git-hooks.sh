#!/usr/bin/env bash
# tools/git-hooks/ のフックを .git/hooks へ導入する（issue #349）。
# git はフックをリポジトリで共有できないので、各自が一度これを実行する必要がある。
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
