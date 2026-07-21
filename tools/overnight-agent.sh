#!/usr/bin/env bash
# 夜間 Issue 自動消化エージェント
#
# open な issue を1件ずつ、専用 worktree 上で Claude (Opus) に解決させる。
# 1 issue = 1 プロセス（毎回まっさらなコンテキスト）。PR 作成→CI待ち→squash マージまで自動。
# ユーザー判断が必要な issue は needs-human ラベルを付けてスキップする。
# リストを全消化したら再取得し、実行中に増えた issue があれば続けて処理する（新着なしで終了）。
# 同じ晩に一度着手した issue（失敗・スキップ含む）には再挑戦しない。
#
# 使い方:
#   bash tools/overnight-agent.sh                 # open issue を消化し尽くすまで処理
#   MAX_ISSUES=3 bash tools/overnight-agent.sh    # 一晩の総件数上限3件
#   DRY_RUN=1 bash tools/overnight-agent.sh       # 現時点の処理対象一覧だけ表示
#   ISSUE_TIMEOUT=5400 bash tools/overnight-agent.sh   # 1件あたりの制限秒数（既定 3600）
#   FOREGROUND=1 bash tools/overnight-agent.sh    # 従来どおり前面で実行（開発デバッグ用）
#
# 既定ではターミナルから切り離して起動する（nohup）。ターミナルや VSCode を閉じても止まらない。
# 進捗は tail -f out/overnight/current.log で見る。止めるには: kill $(cat out/overnight/current.pid)
# 結果は out/overnight/<日時>/SUMMARY.md に集約される（朝これを読む）。
set -u

export PATH="$HOME/.local/bin:/usr/local/share/dotnet:$PATH"

SELF_DIR="$(cd "$(dirname "$0")/.." && pwd)"

# ターミナルから切り離して再実行（閉じても SIGHUP で死なないように）
if [ -z "${OVERNIGHT_DETACHED:-}" ] && [ -z "${DRY_RUN:-}" ] && [ -z "${FOREGROUND:-}" ]; then
  mkdir -p "$SELF_DIR/out/overnight"
  RUNLOG="$SELF_DIR/out/overnight/current.log"
  : > "$RUNLOG"
  nohup env OVERNIGHT_DETACHED=1 bash "$0" "$@" < /dev/null > "$RUNLOG" 2>&1 &
  echo "$!" > "$SELF_DIR/out/overnight/current.pid"
  echo "夜間エージェントをバックグラウンドで起動しました (PID $!)。"
  echo "このターミナルは閉じて大丈夫です。Mac は電源接続・蓋は開けたままで。"
  echo ""
  echo "  進捗を見る:  tail -f out/overnight/current.log"
  echo "  止める:      kill \$(cat out/overnight/current.pid)"
  echo "  朝の結果:    out/overnight/<日時>/SUMMARY.md"
  exit 0
fi

# Mac をスリープさせない（caffeinate 配下で自動再実行。ディスプレイは消えてよい）
if [ -z "${OVERNIGHT_CAFFEINATED:-}" ] && [ -z "${DRY_RUN:-}" ] && command -v caffeinate >/dev/null; then
  exec caffeinate -is env OVERNIGHT_CAFFEINATED=1 "$0" "$@"
fi

REPO_DIR="$(cd "$(dirname "$0")/.." && pwd)"
WT_ROOT="${REPO_DIR}-worktrees"
STAMP="$(date +%Y%m%d-%H%M%S)"
RUN_START="$(date +%s)"
LOG_DIR="$REPO_DIR/out/overnight/$STAMP"
PROMPT_TEMPLATE="$REPO_DIR/tools/overnight-prompt.template.md"

CLAUDE_BIN="${CLAUDE_BIN:-$HOME/.local/bin/claude}"
MODEL="${OVERNIGHT_MODEL:-claude-sonnet-5}"          # 既定は Sonnet（2026-08末まで導入価格でOpusの約4割）。Fable は使わない
OPUS_MODEL="claude-opus-4-8"                         # issue に model:opus ラベルが付いていれば昇格
FALLBACK_MODEL="${OVERNIGHT_FALLBACK:-claude-sonnet-4-6}"
ISSUE_TIMEOUT="${ISSUE_TIMEOUT:-5400}"   # Unity実機確認込みで90分
MAX_ISSUES="${MAX_ISSUES:-0}"                        # 0 = 無制限

command -v gh >/dev/null || { echo "gh が見つかりません" >&2; exit 1; }
[ -x "$CLAUDE_BIN" ] || { echo "claude CLI が見つかりません: $CLAUDE_BIN" >&2; exit 1; }
[ -f "$PROMPT_TEMPLATE" ] || { echo "プロンプトテンプレートがありません: $PROMPT_TEMPLATE" >&2; exit 1; }

# 処理対象: needs-human / agent-failed を除く open issue。bug を先に、あとは番号昇順。
fetch_issues() {
  gh issue list --state open --limit 100 --json number,title,labels --jq '
    [ .[] | select( ( [.labels[].name] | (index("needs-human") or index("agent-failed")) ) | not ) ]
    | sort_by( (if ([.labels[].name] | index("bug")) then 0 else 1 end), .number )
    | .[] | "\(.number)\t\(.title)"'
}

issues="$(fetch_issues)"

if [ -z "$issues" ]; then
  echo "処理対象の issue がありません。"
  exit 0
fi

if [ -n "${DRY_RUN:-}" ]; then
  echo "現時点の処理予定（この順で1件ずつ。全消化後に再取得し、新着があれば続行）:"
  echo "$issues" | sed 's/^/  #/'
  exit 0
fi

mkdir -p "$LOG_DIR" "$WT_ROOT"
SUMMARY="$LOG_DIR/SUMMARY.md"
echo "# 夜間エージェント実行結果 ($STAMP)" > "$SUMMARY"
echo "" >> "$SUMMARY"
note() { echo "$1" | tee -a "$SUMMARY"; }

count=0        # 一晩の総着手件数
attempted=" "  # 着手済み issue 番号（再取得後の重複着手を防ぐ）
stop=0
round=1

while [ "$stop" = 0 ]; do
  # 着手済みを除外した残リストを作る
  pending=""
  while IFS=$'\t' read -r num title; do
    [ -z "$num" ] && continue
    case "$attempted" in *" $num "*) continue ;; esac
    pending="${pending}${num}	${title}
"
  done <<< "$issues"

  if [ -z "$pending" ]; then
    if [ "$round" -gt 1 ]; then
      note "- 🌙 再取得したが新着なし。終了。"
    fi
    break
  fi

  [ "$round" -gt 1 ] && note "- 🔄 再取得で新着 issue を検出（ラウンド ${round}）"

  while IFS=$'\t' read -r num title; do
    [ -z "$num" ] && continue
    if [ -f "$REPO_DIR/out/overnight/STOP" ]; then
      rm -f "$REPO_DIR/out/overnight/STOP"
      note "- 🛑 STOP ファイル検出。#$num 以降は未着手で終了。"
      stop=1
      break
    fi
    if [ "$MAX_ISSUES" -gt 0 ] && [ "$count" -ge "$MAX_ISSUES" ]; then
      note "- ⏹ 上限 MAX_ISSUES=$MAX_ISSUES に到達。#$num 以降は未着手。"
      stop=1
      break
    fi
    attempted="${attempted}${num} "
    count=$((count + 1))

    branch="issue/${num}-agent"

    # モデル選択: 既定 Sonnet、model:opus ラベル付き issue は Opus に昇格
    issue_model="$MODEL"
    if gh issue view "$num" --json labels --jq '[.labels[].name] | join(",")' 2>/dev/null | grep -q "model:opus"; then
      issue_model="$OPUS_MODEL"
    fi
    fb="$FALLBACK_MODEL"
    [ "$fb" = "$issue_model" ] && fb="$OPUS_MODEL"

    echo ""
    echo "=== [$count] issue #$num: $title (model: ${issue_model}) ==="

    # 前回の残骸（open PR）があればスキップ
    if [ -n "$(gh pr list --head "$branch" --state open --json number --jq '.[].number')" ]; then
      note "- ⏭ #$num $title — スキップ（同ブランチの open PR が既にある。先に処置を）"
      continue
    fi

    wt="$WT_ROOT/issue-$num"
    git -C "$REPO_DIR" fetch origin main -q
    git -C "$REPO_DIR" worktree remove --force "$wt" 2>/dev/null || true
    git -C "$REPO_DIR" branch -D "$branch" >/dev/null 2>&1 || true
    if ! git -C "$REPO_DIR" worktree add -q "$wt" -b "$branch" origin/main; then
      note "- 🔴 #$num $title — worktree 作成に失敗"
      continue
    fi

    prompt="$(sed "s/__ISSUE__/$num/g" "$PROMPT_TEMPLATE")"
    log="$LOG_DIR/issue-$num.log"
    t_start="$(date +%s)"

    ( cd "$wt" && "$CLAUDE_BIN" -p "$prompt" \
        --model "$issue_model" --fallback-model "$fb" \
        --dangerously-skip-permissions ) < /dev/null > "$log" 2>&1 &
    pid=$!
    ( sleep "$ISSUE_TIMEOUT"; kill "$pid" 2>/dev/null ) & watchdog=$!
    timed_out=0
    if ! wait "$pid"; then
      kill -0 "$watchdog" 2>/dev/null || timed_out=1
    fi
    { kill "$watchdog" && wait "$watchdog"; } 2>/dev/null || true
    pkill -P "$watchdog" 2>/dev/null || true

    # 所要時間
    t_end="$(date +%s)"
    dur="$(( (t_end - t_start) / 60 ))分$(( (t_end - t_start) % 60 ))秒"

    # 結果判定（issue の状態から逆引き）
    state="$(gh issue view "$num" --json state --jq .state)"
    labels="$(gh issue view "$num" --json labels --jq '[.labels[].name] | join(",")')"
    pr="$(gh pr list --head "$branch" --state all --limit 1 --json number,state --jq '.[] | "#\(.number) (\(.state))"')"

    if [ "$state" = "CLOSED" ]; then
      note "- ✅ #$num $title — 完了・マージ済み ${pr:+(PR $pr) }[${dur}]"
    elif echo "$labels" | grep -q needs-human; then
      note "- 🟡 #$num $title — ユーザー判断待ち（issue のコメント参照）[${dur}]"
    elif echo "$labels" | grep -q agent-failed; then
      note "- 🔴 #$num $title — 実装失敗（CI 赤など。${pr:+PR $pr / }ログ: ${log}）[${dur}]"
    elif [ "$timed_out" = 1 ]; then
      note "- 🔴 #$num $title — タイムアウト（${ISSUE_TIMEOUT}秒。ログ: ${log}）[${dur}]"
    else
      note "- 🔴 #$num $title — 未完了（ログ: ${log}）[${dur}]"
    fi

    # 救済ネット: push されないままセッションが終わった作業を退避（draft PR 化）して失わない
    if [ "$state" != "CLOSED" ] && ! echo "$labels" | grep -qE "needs-human|agent-failed"; then
      if [ -d "$wt" ] && [ -n "$(git -C "$wt" log --oneline origin/main..HEAD 2>/dev/null)" ] && [ -z "$pr" ]; then
        if git -C "$wt" push -u origin "$branch" >/dev/null 2>&1; then
          ( cd "$wt" && gh pr create --draft --head "$branch" \
              --title "[退避] ${title}" \
              --body "夜間エージェントがマージ未完了のまま終了したため、作業を自動退避した draft PR。中身の検証後に ready 化してマージすること。 Fixes #${num}" >/dev/null 2>&1 ) \
            && note "- 💾 #$num 未 push の作業を draft PR に退避した（朝に検証を）"
        fi
      fi
    fi

    # 後片付け（worktree とローカルブランチ。リモートは PR/マージ側に委ねる）
    git -C "$REPO_DIR" worktree remove --force "$wt" 2>/dev/null || true
    git -C "$REPO_DIR" branch -D "$branch" >/dev/null 2>&1 || true
  done <<< "$pending"

  # リストを消化しきったので再取得（実行中に増えた issue を拾う）
  round=$((round + 1))
  issues="$(fetch_issues)"
done

total_min="$(( ($(date +%s) - RUN_START) / 60 ))"
note ""
note "総所要時間: $(( total_min / 60 ))時間$(( total_min % 60 ))分（着手 ${count} 件）"

echo ""
echo "===================================="
echo "全処理終了。サマリ: $SUMMARY"
cat "$SUMMARY"
