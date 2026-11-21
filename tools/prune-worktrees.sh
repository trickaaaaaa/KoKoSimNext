#!/bin/bash
# 使い終わった git worktree を片付ける。既定は一覧を出すだけ（何も消さない）。
#
#   bash tools/prune-worktrees.sh           # 一覧だけ（消す候補／残す理由）
#   bash tools/prune-worktrees.sh --apply   # 消す候補を実際に消す
#
# 消す条件（すべて満たすものだけ）:
#   - メインの作業ツリーではない
#   - ブランチの PR が MERGED（gh で照合。PR が無い・未マージは残す）
#   - ブランチの先端が PR の head と同じ（または main に含まれる）＝マージ後に足した未 push の
#     コミットが無い（issue #1255: 以前は見ておらず、マージ後に同じブランチで続けた作業を消していた）
#   - 未コミットの変更も未追跡ファイルも無い
#   - 中で動いているプロセスが無い（Unity エディタ・claude・シェル等の cwd）
# 消すのは worktree とローカルのブランチだけ。リモートのブランチには触らない。
# 夜間エージェント（tools/overnight-agent.sh）の最後でも --apply で呼ぶ。
set -u
apply=0
[ "${1:-}" = "--apply" ] && apply=1

main_wt=$(git rev-parse --path-format=absolute --git-common-dir | sed 's#/\.git$##')
cd "$main_wt" || exit 1

# 動いているプロセスの作業ディレクトリ（1回だけ取る）
cwds=$(lsof -d cwd -Fn 2>/dev/null | sed -n 's/^n//p' | sort -u)
# Unity は -projectpath で開く（cwd が別のことがある）
unity_projects=$(ps -axo command= | sed -n 's/.*-projectpath \([^ ]*\).*/\1/p; s/.*-projectPath \([^ ]*\).*/\1/p' | sort -u)

# ブランチの先端（$2＝コミット）が、マージされた PR の head か main に含まれるか。
# 含まれなければ「マージ後に足したコミット」がある＝消すと失う。
merged_tip_is_safe() { # $1=branch $2=tip sha
  local head
  head=$(gh pr list --state merged --head "$1" --json headRefOid -q '.[0].headRefOid' 2>/dev/null)
  [ -n "$head" ] && [ "$2" = "$head" ] && return 0
  [ -n "$head" ] && git merge-base --is-ancestor "$2" "$head" 2>/dev/null && return 0
  git merge-base --is-ancestor "$2" origin/main 2>/dev/null && return 0
  return 1
}

in_use() {
  local w="$1"
  printf '%s\n%s\n' "$cwds" "$unity_projects" | grep -qE "^${w}(/|$)"
}

removed=0; kept=0
while IFS=$'\t' read -r w b locked; do
  [ "$w" = "$main_wt" ] && continue
  name=$(basename "$w")
  if [ ! -d "$w" ] || [ ! -e "$w/.git" ]; then
    # 抜け殻（フォルダごと消えた／.git だけ消えた）。git worktree remove は失敗するので、
    # 記録は最後の git worktree prune に任せ、PR がマージ済みならローカルブランチだけ消す。
    state=""
    [ "$b" != "DETACHED" ] && state=$(gh pr list --state all --head "$b" --json state -q '.[0].state' 2>/dev/null)
    if [ "$state" = "MERGED" ] && ! merged_tip_is_safe "$b" "$(git rev-parse -q --verify "refs/heads/$b" 2>/dev/null)"; then
      echo "prune   $name [$b]（抜け殻・記録だけ消す。ブランチはマージ後のコミットがあるので残す）"
    elif [ "$state" = "MERGED" ] && [ $apply -eq 1 ]; then
      git branch -D "$b" >/dev/null 2>&1
      echo "prune   $name [$b]（抜け殻・記録とブランチを消した。フォルダの残りは手で消す: ${w}）"
    else
      echo "prune   $name [$b]（抜け殻・記録だけ消す。PR が ${state:-無い}）"
    fi
    continue
  fi
  reason=""
  if [ "$b" = "DETACHED" ]; then reason="detached HEAD（どの PR か判らない）"
  elif [ -n "$(git -C "$w" status --porcelain 2>/dev/null)" ]; then reason="未コミットの変更・未追跡ファイルがある"
  elif in_use "$w"; then reason="中で動いているプロセスがある"
  else
    state=$(gh pr list --state all --head "$b" --json state -q '.[0].state' 2>/dev/null)
    if [ "$state" != "MERGED" ]; then reason="PR が ${state:-無い}"
    elif ! merged_tip_is_safe "$b" "$(git -C "$w" rev-parse HEAD 2>/dev/null)"; then
      reason="PR のマージ後に足したコミットがある（未 push の作業を消さない）"
    fi
  fi
  if [ -n "$reason" ]; then
    echo "keep    $name [$b] — $reason"; kept=$((kept+1)); continue
  fi
  if [ $apply -eq 1 ]; then
    [ "$locked" = "1" ] && git worktree unlock "$w" 2>/dev/null
    if git worktree remove "$w"; then
      git branch -D "$b" >/dev/null 2>&1
      echo "removed $name [$b]"; removed=$((removed+1))
    else
      echo "FAILED  $name [$b]"; kept=$((kept+1))
    fi
  else
    echo "remove? $name [$b]$([ "$locked" = "1" ] && echo '（locked）')"; removed=$((removed+1))
  fi
done < <(git worktree list --porcelain | awk '
  /^worktree /{if(w!="")print w"\t"b"\t"l; w=substr($0,10); b="DETACHED"; l=0}
  /^branch /{b=$2; sub("refs/heads/","",b)}
  /^locked/{l=1}
  END{if(w!="")print w"\t"b"\t"l}')

if [ $apply -eq 1 ]; then git worktree prune; else git worktree prune -n; fi
if [ $apply -eq 1 ]; then
  echo "消した: $removed / 残した: $kept"
else
  echo "消す候補: $removed / 残す: ${kept}（消すには --apply）"
fi
