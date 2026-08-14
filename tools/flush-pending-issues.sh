#!/usr/bin/env bash
# GitHub 停止中に溜めた issue 草稿（out/issues-pending/*.md）を一括で起票する。
#
# 背景: 2026-08-12 のアカウント停止で gh API が全部 403 になり、その場で起票できなくなった。
# 草稿は「完了条件まで書いた本物の issue 本文」としてローカルに置き、復帰後にこれで流す。
#
# 草稿の形式（先頭に YAML 風フロントマター、以降が本文）:
#   ---
#   title: <日本語タイトル>
#   labels: bug, area:engine, model:opus
#   created: 2026-08-13
#   url:                       # 起票後にこのスクリプトが埋める＝二重起票の目印
#   ---
#   ## 症状
#   ...
#
# 使い方:
#   bash tools/flush-pending-issues.sh --dry-run   # 何を作るか確認するだけ
#   bash tools/flush-pending-issues.sh             # 実際に起票して url: を書き戻す
set -euo pipefail

REPO="trickaaaaaa/KoKoSimNext"
DIR="$(cd "$(dirname "$0")/.." && pwd)/out/issues-pending"
DRY_RUN=0
[[ "${1:-}" == "--dry-run" ]] && DRY_RUN=1

if [[ ! -d "$DIR" ]]; then
  echo "草稿ディレクトリが無い: $DIR" >&2
  exit 1
fi

# 起票前に API が生きているか確かめる（停止中は 403 で全部失敗するので先に落とす）。
# --dry-run は停止中の確認用なので API を触らない＝ここは通す。
if [[ $DRY_RUN -eq 0 ]] && ! gh issue list --repo "$REPO" --limit 1 >/dev/null 2>&1; then
  echo "gh が $REPO に届かない（アカウント停止・未認証など）。復帰後に再実行する。" >&2
  exit 1
fi

# フロントマターの1項目を取り出す（値が空なら空文字）。
field() { sed -n "s/^$2:[[:space:]]*//p" "$1" | head -1; }

created=0
skipped=0
for f in "$DIR"/*.md; do
  [[ -e "$f" ]] || continue
  name="$(basename "$f")"

  if [[ -n "$(field "$f" url)" ]]; then
    echo "skip  $name（既に起票済み: $(field "$f" url)）"
    skipped=$((skipped + 1))
    continue
  fi

  title="$(field "$f" title)"
  if [[ -z "$title" ]]; then
    echo "skip  $name（title が無い）" >&2
    skipped=$((skipped + 1))
    continue
  fi

  # 本文＝2つ目の "---" より後ろ（フロントマターは起票内容に含めない）。
  body="$(awk 'BEGIN{n=0} /^---[[:space:]]*$/{n++; next} n>=2{print}' "$f")"

  # ラベル引数を組む。無いラベルは事前に作る（色・説明は .claude/skills/issue/SKILL.md の表と同じ）。
  label_args=()
  IFS=',' read -ra labels <<<"$(field "$f" labels)"
  for l in "${labels[@]}"; do
    l="$(echo "$l" | xargs)"   # 前後の空白を落とす
    [[ -z "$l" ]] && continue
    label_args+=(--label "$l")
  done

  if [[ $DRY_RUN -eq 1 ]]; then
    echo "would create  $name"
    echo "  title : $title"
    echo "  labels: ${labels[*]:-（なし）}"
    echo "  body  : $(echo "$body" | wc -l | xargs) 行"
    continue
  fi

  # 領域ラベルが未作成でも起票が落ちないよう、先に作る（既存なら何もしない）。
  for l in "${labels[@]}"; do
    l="$(echo "$l" | xargs)"
    case "$l" in
      area:engine)  gh label create "$l" --repo "$REPO" --color 1D3227 --description "engine/ 純C#エンジン" 2>/dev/null || true ;;
      area:unity)   gh label create "$l" --repo "$REPO" --color 264653 --description "unity/ UI・表示" 2>/dev/null || true ;;
      area:data)    gh label create "$l" --repo "$REPO" --color 5F6F52 --description "data/ YAML バランス" 2>/dev/null || true ;;
      area:docs)    gh label create "$l" --repo "$REPO" --color 0075CA --description "docs/design 設計書" 2>/dev/null || true ;;
      area:balance) gh label create "$l" --repo "$REPO" --color F5C64A --description "統計回帰・帯校正" 2>/dev/null || true ;;
      task)         gh label create "$l" --repo "$REPO" --color C5DEF5 --description "実装タスク" 2>/dev/null || true ;;
      model:opus)   gh label create "$l" --repo "$REPO" --color 8B5CF6 --description "夜間エージェント: この issue は Opus で実装する" 2>/dev/null || true ;;
    esac
  done

  tmp="$(mktemp)"
  printf '%s\n' "$body" >"$tmp"
  url="$(gh issue create --repo "$REPO" --title "$title" "${label_args[@]}" --body-file "$tmp")"
  rm -f "$tmp"

  # 二重起票を防ぐため、採番された URL をフロントマターへ書き戻す。
  # ⚠ macOS の sed は -i に引数が要る（GNU sed と非互換）ので明示する。
  sed -i '' "s|^url:.*|url: $url|" "$f"
  echo "created  $name → $url"
  created=$((created + 1))
done

echo
echo "起票 $created 件 / スキップ $skipped 件"
if [[ $created -gt 0 ]]; then
  echo "→ out/issue-backlog-restored.md の台帳へ追記し、gh issue list と突き合わせること。"
fi
