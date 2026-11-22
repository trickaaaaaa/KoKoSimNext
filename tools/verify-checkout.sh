#!/usr/bin/env bash
# 検証専用チェックアウト（KoKoSimNext-verify）を指定のブランチ/コミットへ切り替える。
#
# 素の `git checkout --detach` は、前のブランチで Unity が自動生成した .meta や、前のブランチにだけ
# あったアセットが未追跡で残っていると「untracked working tree files would be overwritten」で止まる
# （2026-11-21 の夜間で #1346 #1349 #1352 の実機確認がこれで全滅。memory: verify の未追跡 .meta 罠）。
# verify は使い捨ての検証専用なので、切り替え前に Unity が触る場所（Assets/ ProjectSettings/ Packages/）の
# 未コミットの変更と未追跡ファイルを捨ててから切り替える。Git 管理外の生成物（engine DLL・
# Resources/KokoSimData の YAML 複製＝.gitignore 済み）は消さない（git clean に -x を付けない）。
#
# 使い方: bash tools/verify-checkout.sh <ブランチ名かコミット>
#   例:   bash tools/verify-checkout.sh issue/1234-agent
#         bash tools/verify-checkout.sh origin/main
set -euo pipefail

VERIFY_DIR="${VERIFY_DIR:-/Users/seiya.oda/Unity/KoKoSimNext-verify}"
ref="${1:-}"
if [ -z "$ref" ]; then
  echo "使い方: bash tools/verify-checkout.sh <ブランチ名かコミット>" >&2
  exit 2
fi
if [ ! -d "$VERIFY_DIR/.git" ] && [ ! -f "$VERIFY_DIR/.git" ]; then
  echo "検証用チェックアウトがありません: ${VERIFY_DIR}" >&2
  exit 1
fi
# 取り違え防止: 本人のメインのチェックアウトを掃除しない
case "$(cd "$VERIFY_DIR" && pwd -P)" in
  *-verify) ;;
  *) echo "VERIFY_DIR が検証用（*-verify）ではありません: ${VERIFY_DIR}" >&2; exit 1 ;;
esac

if ! git -C "$VERIFY_DIR" rev-parse --verify -q "${ref}^{commit}" >/dev/null; then
  git -C "$VERIFY_DIR" fetch -q origin 2>/dev/null || true
  if ! git -C "$VERIFY_DIR" rev-parse --verify -q "${ref}^{commit}" >/dev/null; then
    echo "見つからない参照: ${ref}" >&2
    exit 1
  fi
fi

dirs="Assets ProjectSettings Packages"
dirty="$(git -C "$VERIFY_DIR" status --porcelain -- $dirs | wc -l | tr -d ' ')"
if [ "$dirty" != "0" ]; then
  echo "verify の Unity 側の残り ${dirty} 件を捨てる:"
  git -C "$VERIFY_DIR" status --porcelain -- $dirs | head -20 | sed 's/^/  /'
  git -C "$VERIFY_DIR" checkout -q -- $dirs
  git -C "$VERIFY_DIR" clean -q -fd -- $dirs
fi

if ! out="$(git -C "$VERIFY_DIR" checkout -q --detach "$ref" 2>&1)"; then
  echo "切り替えに失敗:" >&2
  echo "$out" | head -30 >&2
  exit 1
fi
echo "verify を ${ref}（$(git -C "$VERIFY_DIR" rev-parse --short HEAD)）に切り替えた"
