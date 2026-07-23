#!/usr/bin/env bash
# 夜間 Issue 自動消化エージェント（並列ワーカー版）
#
# open な issue を複数ワーカーで並列に解決する。1 issue = 1 プロセス（毎回まっさらなコンテキスト）。
# セッション（claude -p）の責務は「実装→テスト緑→（UIなら）実機確認→push→PR作成」まで。
# **CI 待ち・rebase・マージ・DLL 同期はこのスクリプトが単一ロックで直列に行う**（内側セッションの
# 「CI待ちで消滅」「Monitor待ちで消滅」を構造的に根絶する）。
#
# 並列モデル:
#   - ISSUE_WORKERS 本のワーカーが issue を並列に実装する（既定 3）。
#   - area:unity の issue は 1 ワーカー（worker 0）に集約し直列化する（検証 Editor は 1 台・UI 衝突対策）。
#   - engine 系はワーカー間に分散。衝突は「マージ直前の rebase＋再テスト」で機械検出し、
#     コンフリクト時は needs-human 送り（main は壊さない）。
#   - マージは REPO ロックで直列化（rebase→禁止物除去→ローカル再テスト→squash マージ）。
#   - DLL（Assets/Plugins/KokoSim/KokoSim.Engine.dll）は **PR に含めない**。エンジンを触った
#     ラウンドの最後に、このスクリプトが main 上で 1 コミットとして同期 push する（PR 同士の
#     バイナリ衝突を消し、並列マージを可能にする）。※夜間ラン限定ルール。日中の手動コミットは従来通り。
#   - main の直近 CI が failure なら以降のマージを停止（赤 main への積み増し防止）。
#
# 使い方:
#   bash tools/overnight-agent.sh                    # open issue を消化し尽くすまで並列処理
#   ISSUE_WORKERS=2 bash tools/overnight-agent.sh    # 並列数を変える（既定 3。1 で従来相当の直列）
#   MAX_ISSUES=5 bash tools/overnight-agent.sh        # 一晩の総着手件数上限
#   DRY_RUN=1 bash tools/overnight-agent.sh           # 処理予定とワーカー割当だけ表示
#   ISSUE_TIMEOUT=5400 bash tools/overnight-agent.sh  # 1件あたりの制限秒数（既定 5400）
#   FOREGROUND=1 bash tools/overnight-agent.sh         # 前面実行（デバッグ用）
#
# 既定ではターミナルから切り離して起動する（nohup）。進捗: tail -f out/overnight/current.log
# 止める: kill $(cat out/overnight/current.pid) / 途中停止: touch out/overnight/STOP
# 朝の結果: out/overnight/<日時>/SUMMARY.md
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
  echo "夜間エージェント（並列版）をバックグラウンドで起動しました (PID $!)。"
  echo "このターミナルは閉じて大丈夫です。Mac は電源接続・蓋は開けたままで。"
  echo ""
  echo "  進捗を見る:  tail -f out/overnight/current.log"
  echo "  止める:      kill \$(cat out/overnight/current.pid)"
  echo "  途中停止:    touch out/overnight/STOP"
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
DOTNET="/usr/local/share/dotnet/dotnet"

CLAUDE_BIN="${CLAUDE_BIN:-$HOME/.local/bin/claude}"
MODEL="${OVERNIGHT_MODEL:-claude-sonnet-5}"          # 既定は Sonnet。model:opus ラベルで昇格
OPUS_MODEL="claude-opus-4-8"
FALLBACK_MODEL="${OVERNIGHT_FALLBACK:-claude-sonnet-4-6}"
ISSUE_TIMEOUT="${ISSUE_TIMEOUT:-5400}"               # 1件あたりのハード上限（Unity実機確認込みで90分）
STALL_TIMEOUT="${STALL_TIMEOUT:-600}"                # セッションの transcript が無進捗なら kill（既定10分）
MAX_ISSUES="${MAX_ISSUES:-0}"                        # 0 = 無制限
ISSUE_WORKERS="${ISSUE_WORKERS:-3}"                  # 並列ワーカー数
[ "$ISSUE_WORKERS" -lt 1 ] && ISSUE_WORKERS=1

command -v gh >/dev/null || { echo "gh が見つかりません" >&2; exit 1; }
[ -x "$CLAUDE_BIN" ] || { echo "claude CLI が見つかりません: $CLAUDE_BIN" >&2; exit 1; }
[ -f "$PROMPT_TEMPLATE" ] || { echo "プロンプトテンプレートがありません: $PROMPT_TEMPLATE" >&2; exit 1; }
[ -x "$DOTNET" ] || { echo "dotnet が見つかりません: $DOTNET" >&2; exit 1; }

# 処理対象: needs-human / agent-failed を除く、所有者本人の open issue。
# 出力は "番号\tタイトル\tISUNITY"（ISUNITY=1 なら area:unity 付き）。bug を先に、あとは番号昇順。
fetch_issues() {
  gh issue list --state open --limit 100 --author "trickaaaaaa" --json number,title,labels --jq '
    [ .[] | select( ( [.labels[].name] | (index("needs-human") or index("agent-failed")) ) | not ) ]
    | sort_by( (if ([.labels[].name] | index("bug")) then 0 else 1 end), .number )
    | .[] | "\(.number)\t\(.title)\t\(if ([.labels[].name] | index("area:unity")) then 1 else 0 end)"'
}

if [ -n "${DRY_RUN:-}" ]; then
  issues="$(fetch_issues)"
  if [ -z "$issues" ]; then echo "処理対象の issue がありません。"; exit 0; fi
  echo "並列ワーカー数: $ISSUE_WORKERS"
  echo ""
  echo "== area:unity（worker 0 に集約・直列） =="
  echo "$issues" | awk -F'\t' '$3==1 {printf "  #%s  %s\n",$1,$2}'
  echo ""
  echo "== engine 系（worker 間に分散） =="
  # unity があれば worker 1.. に分散、無ければ 0.. に分散
  has_unity="$(echo "$issues" | awk -F'\t' '$3==1' | head -1)"
  if [ -n "$has_unity" ] && [ "$ISSUE_WORKERS" -gt 1 ]; then start=1; span=$((ISSUE_WORKERS-1)); else start=0; span=$ISSUE_WORKERS; fi
  echo "$issues" | awk -F'\t' -v s="$start" -v n="$span" '
    $3==0 { printf "  [worker %d] #%s  %s\n", s + (c % n), $1, $2; c++ }'
  exit 0
fi

mkdir -p "$LOG_DIR" "$WT_ROOT"
SUMMARY="$LOG_DIR/SUMMARY.md"
SYS_NOTES="$LOG_DIR/summary-sys.md"
: > "$SYS_NOTES"
PROCESSED="$LOG_DIR/processed.txt"; : > "$PROCESSED"
COUNTER="$LOG_DIR/counter.txt"; echo 0 > "$COUNTER"
HALT="$LOG_DIR/HALT"
REPO_LOCK="$LOG_DIR/.repolock"      # REPO_DIR の .git を触る操作＋マージ＋DLL同期の単一ミューテックス
CTR_LOCK="$LOG_DIR/.ctrlock"        # カウンタ/processed 追記用の軽量ロック

# ── ロック（mkdir はアトミック。bash 3.2 に BASHPID が無いので PID 監視はせず、
#    最大待ちで奪取するデッドロック回避のみ。LOG_DIR はラン毎に新規なので跨ぎstaleは無い） ──
lock()   { local l="$1" w=0; until mkdir "$l" 2>/dev/null; do sleep 2; w=$((w+2)); [ "$w" -ge 2400 ] && rm -rf "$l"; done; }
unlock() { rm -rf "$1" 2>/dev/null || true; }

# ワーカー用: 自分の summary ファイルへ追記（NOTE_TARGET は各ワーカーが設定）
NOTE_TARGET="$SYS_NOTES"
note() { echo "$1" | tee -a "$NOTE_TARGET"; }

# 一晩の総着手件数を +1 して現在値を返す（ロック付き）。処理済み番号も記録する。
bump_and_mark() { # $1=issue番号
  lock "$CTR_LOCK"
  echo "$1" >> "$PROCESSED"
  local c; c="$(cat "$COUNTER")"; c=$((c+1)); echo "$c" > "$COUNTER"
  unlock "$CTR_LOCK"
  echo "$c"
}
count_now() { cat "$COUNTER"; }

# main の直近 CI（完了済み）が failure なら真。赤 main への積み増しを止める。
main_ci_failing() {
  local c
  c="$(gh run list --branch main --workflow engine-ci --limit 1 --json status,conclusion \
        --jq '.[0] | select(.status=="completed") | .conclusion' 2>/dev/null)"
  [ "$c" = "failure" ]
}

# ── DLL 同期用の専用 worktree（REPO_DIR の作業ツリー＝ユーザーの WIP には絶対触れない） ──
DLLWT="$WT_ROOT/dll-sync"
git -C "$REPO_DIR" fetch origin +refs/heads/main:refs/remotes/origin/main -q 2>/dev/null || true
git -C "$REPO_DIR" worktree remove --force "$DLLWT" 2>/dev/null || true
if ! git -C "$REPO_DIR" worktree add -q --detach "$DLLWT" refs/remotes/origin/main 2>/dev/null; then
  echo "警告: dll-sync worktree の作成に失敗（DLL 同期はスキップされる）" | tee -a "$SYS_NOTES"
  DLLWT=""
fi

# エンジンをビルドして DLL/StreamingAssets を main へ同期 push（ラウンド末に1回・ロック内）
sync_dll_to_main() {
  [ -z "$DLLWT" ] && return
  lock "$REPO_LOCK"
  if git -C "$DLLWT" fetch origin +refs/heads/main:refs/remotes/origin/main -q 2>/dev/null; then
    git -C "$DLLWT" reset --hard refs/remotes/origin/main -q 2>/dev/null || { unlock "$REPO_LOCK"; return; }
    if "$DOTNET" build "$DLLWT/engine/KokoSim.Engine/KokoSim.Engine.csproj" -c Release >/dev/null 2>&1; then
      cp "$DLLWT/engine/KokoSim.Engine/bin/Release/netstandard2.1/KokoSim.Engine.dll" \
         "$DLLWT/Assets/Plugins/KokoSim/KokoSim.Engine.dll" 2>/dev/null || true
      cp "$DLLWT/data/school-names.yaml" "$DLLWT/Assets/StreamingAssets/KokoSim/school-names.yaml" 2>/dev/null || true
      if ! git -C "$DLLWT" diff --quiet 2>/dev/null; then
        git -C "$DLLWT" add Assets/Plugins/KokoSim/KokoSim.Engine.dll Assets/StreamingAssets/KokoSim/school-names.yaml 2>/dev/null || true
        git -C "$DLLWT" commit -q -m "chore: sync engine DLL（夜間エージェント自動同期）" 2>/dev/null || true
        if git -C "$DLLWT" push -q origin HEAD:main 2>/dev/null; then
          note "- 🔧 DLL を main へ同期 push した"
        else
          note "- ⚠ DLL の push に失敗（次ラウンドで再試行）"
        fi
      fi
    else
      note "- ⚠ DLL 同期ビルドに失敗（朝に手動 sync-engine-dll.sh を）"
    fi
  fi
  unlock "$REPO_LOCK"
}

# ── マージ工程（ロック内で直列）: rebase→禁止物除去→ガード→ローカル再テスト→squash マージ ──
# 返り値ではなく note で結果を残す。呼び出しは PR が存在するときのみ。
merge_sequence() { # $1=num $2=title $3=wt $4=branch $5=log
  local num="$1" title="$2" wt="$3" branch="$4" log="$5"
  lock "$REPO_LOCK"

  # 赤 main への積み増し防止
  if main_ci_failing; then
    touch "$HALT"
    note "- 🛑 #$num main の直近 CI が赤。以降のマージを停止（朝に main を点検し復旧を）"
    unlock "$REPO_LOCK"; return
  fi

  if ! git -C "$REPO_DIR" fetch origin +refs/heads/main:refs/remotes/origin/main -q 2>/dev/null; then
    note "- 🔴 #$num $title — origin/main 取得に失敗（マージ見送り）"
    unlock "$REPO_LOCK"; return
  fi

  # 最新 main の上へ rebase（cut 後に他 PR が landed していても巻き戻しを混ぜない）
  if ! git -C "$wt" rebase refs/remotes/origin/main >/dev/null 2>&1; then
    git -C "$wt" rebase --abort >/dev/null 2>&1 || true
    gh pr close "$branch" --comment "自動: 最新 main へ rebase 時にコンフリクト。手動確認のため保留します。" >/dev/null 2>&1 || true
    gh issue edit "$num" --add-label needs-human >/dev/null 2>&1 || true
    note "- 🟡 #$num $title — rebase コンフリクト→needs-human（他 PR と同一箇所を編集した可能性）"
    unlock "$REPO_LOCK"; return
  fi

  # 生成物（DLL / StreamingAssets 複製）は PR に含めない。main 版へ戻してコミットに残さない。
  git -C "$wt" checkout refs/remotes/origin/main -- \
    Assets/Plugins/KokoSim/KokoSim.Engine.dll Assets/StreamingAssets/KokoSim/school-names.yaml >/dev/null 2>&1 || true
  if ! git -C "$wt" diff --quiet -- Assets/Plugins/KokoSim/KokoSim.Engine.dll Assets/StreamingAssets/KokoSim/school-names.yaml 2>/dev/null; then
    git -C "$wt" add Assets/Plugins/KokoSim/KokoSim.Engine.dll Assets/StreamingAssets/KokoSim/school-names.yaml >/dev/null 2>&1 || true
    git -C "$wt" commit -q -m "chore: drop generated DLL from PR（マージ後に自動同期する）" >/dev/null 2>&1 || true
  fi

  # 禁止物ガード: フォントバイナリ / 静的フォント資産 / CI 定義の改変はマージしない
  local fonts wf bad
  fonts="$(git -C "$wt" ls-files 'Assets/Fonts/*.ttc' 'Assets/Fonts/*.ttf' 'Assets/Fonts/*.otf' 'Assets/UI/KokoSimFont.asset' 2>/dev/null | grep -v '^$' || true)"
  wf="$(git -C "$wt" diff --name-only 'refs/remotes/origin/main...HEAD' 2>/dev/null | grep -E '^\.github/workflows/' || true)"
  bad="$(printf '%s\n%s\n' "$fonts" "$wf" | grep -v '^$' || true)"
  if [ -n "$bad" ]; then
    local badline; badline="$(printf '%s' "$bad" | tr '\n' ' ')"
    gh pr close "$branch" --comment "自動ガード: 禁止物（フォント同梱 / CI 定義改変）を検出し自動クローズ。最新 main から切り直して再実装を。" >/dev/null 2>&1 || true
    gh issue edit "$num" --add-label needs-human >/dev/null 2>&1 || true
    note "- 🛡 #$num 禁止物混入を検出→PR クローズ＋needs-human（${badline}）"
    unlock "$REPO_LOCK"; return
  fi

  # rebase 後のブランチを更新
  if ! git -C "$wt" push --force-with-lease -q origin "$branch" 2>/dev/null; then
    note "- 🔴 #$num $title — rebase 後の push に失敗（マージ見送り）"
    unlock "$REPO_LOCK"; return
  fi

  # ローカル再テスト（CI と同じ発火条件＝engine/data/global.json を触ったときだけ）。
  # main 未保護のため CI は待たず、この再テストの緑をマージ可否の判定に使う（post-merge CI は監視）。
  local ci_paths
  ci_paths="$(git -C "$wt" diff --name-only 'refs/remotes/origin/main...HEAD' 2>/dev/null | grep -E '^(engine/|data/|global\.json$)' || true)"
  if [ -n "$ci_paths" ]; then
    if ! ( cd "$wt" && "$DOTNET" test engine/ --filter "Category!=Heavy" ) >>"$log" 2>&1; then
      gh issue edit "$num" --add-label agent-failed >/dev/null 2>&1 || true
      note "- 🔴 #$num $title — rebase 後の non-Heavy 再テストが赤→agent-failed（ログ: ${log}）"
      unlock "$REPO_LOCK"; return
    fi
    if ! ( cd "$wt" && "$DOTNET" test engine/ -c Release --filter "Category=Heavy" ) >>"$log" 2>&1; then
      gh issue edit "$num" --add-label agent-failed >/dev/null 2>&1 || true
      note "- 🔴 #$num $title — rebase 後の Heavy 統計回帰が赤→agent-failed（ログ: ${log}）"
      unlock "$REPO_LOCK"; return
    fi
  fi

  # マージ（main 未保護なので即マージ。Fixes 行で issue クローズ）。
  # gh の終了コードは信用しない: マージ成功後に --delete-branch だけ失敗すると非ゼロになり
  # 「マージ成功なのに🔴」と誤報するため、PR の実状態(MERGED)で判定する。
  local prnum; prnum="$(gh pr list --head "$branch" --state all --limit 1 --json number --jq '.[0].number' 2>/dev/null)"
  gh pr merge "$branch" --squash --delete-branch >/dev/null 2>&1
  if [ -n "$prnum" ] && [ "$(gh pr view "$prnum" --json state --jq .state 2>/dev/null)" = "MERGED" ]; then
    note "- ✅ #$num $title — マージ済み（PR #${prnum}）"
    git push origin --delete "$branch" >/dev/null 2>&1 || true   # 消し残ったリモートブランチの掃除
  else
    note "- 🔴 #$num $title — squash マージに失敗（ログ: ${log}・朝に確認を）"
  fi
  unlock "$REPO_LOCK"
}

# ── 1 issue の処理（ワーカー内から呼ばれる。並列で複数走る） ──
process_issue() { # $1=num $2=title
  local num="$1" title="$2"

  # 途中停止 / 上限チェック
  if [ -f "$REPO_DIR/out/overnight/STOP" ] || [ -f "$HALT" ]; then return; fi
  if [ "$MAX_ISSUES" -gt 0 ] && [ "$(count_now)" -ge "$MAX_ISSUES" ]; then return; fi

  local c; c="$(bump_and_mark "$num")"
  if [ "$MAX_ISSUES" -gt 0 ] && [ "$c" -gt "$MAX_ISSUES" ]; then return; fi

  local branch="issue/${num}-agent"

  # モデル選択
  local issue_model="$MODEL"
  if gh issue view "$num" --json labels --jq '[.labels[].name] | join(",")' 2>/dev/null | grep -q "model:opus"; then
    issue_model="$OPUS_MODEL"
  fi
  local fb="$FALLBACK_MODEL"
  [ "$fb" = "$issue_model" ] && fb="$OPUS_MODEL"

  echo "=== [$c] issue #$num: $title (model: ${issue_model}) ==="

  # 既存の open PR があればスキップ（前回ランの残骸）
  if [ -n "$(gh pr list --head "$branch" --state open --json number --jq '.[].number' 2>/dev/null)" ]; then
    note "- ⏭ #$num $title — スキップ（同ブランチの open PR が既にある。先に処置を）"
    return
  fi

  # worktree を最新 main の正確な tip から切る（ref 操作は REPO ロックで直列化）
  local wt="$WT_ROOT/issue-$num" base_sha
  lock "$REPO_LOCK"
  if ! git -C "$REPO_DIR" fetch origin +refs/heads/main:refs/remotes/origin/main -q 2>/dev/null; then
    unlock "$REPO_LOCK"
    note "- 🔴 #$num $title — origin/main の取得に失敗（古い main で切らないためスキップ）"
    return
  fi
  base_sha="$(git -C "$REPO_DIR" rev-parse refs/remotes/origin/main)"
  git -C "$REPO_DIR" worktree remove --force "$wt" 2>/dev/null || true
  git -C "$REPO_DIR" branch -D "$branch" >/dev/null 2>&1 || true
  if ! git -C "$REPO_DIR" worktree add -q "$wt" -b "$branch" "$base_sha" 2>/dev/null; then
    unlock "$REPO_LOCK"
    note "- 🔴 #$num $title — worktree 作成に失敗"
    return
  fi
  unlock "$REPO_LOCK"

  # ── セッション実行（ロック外＝並列。PR 作成で終了。CI待ち/マージはしない設計） ──
  local prompt log t_start
  prompt="$(sed "s/__ISSUE__/$num/g" "$PROMPT_TEMPLATE")"
  log="$LOG_DIR/issue-$num.log"
  t_start="$(date +%s)"

  # exec で claude をサブシェルに置き換える＝$pid が「殻」ではなく claude 本体を指すようにする。
  # これをしないと kill "$pid" は殻だけ殺し、claude が孤児(PPID=1)として生き残って API 枠を握り続ける。
  ( cd "$wt" && exec "$CLAUDE_BIN" -p "$prompt" \
      --model "$issue_model" --fallback-model "$fb" \
      --dangerously-skip-permissions ) < /dev/null > "$log" 2>&1 &
  local pid=$! watchdog stallmon timed_out=0
  # kill は本体＋その子(node 等)の両方に送る（取り逃し防止）
  ( sleep "$ISSUE_TIMEOUT"; kill "$pid" 2>/dev/null; pkill -P "$pid" 2>/dev/null ) & watchdog=$!

  # stall 監視: セッションの transcript(JSONL)が STALL_TIMEOUT 無進捗ならハングとみなし kill する。
  # ハードな 90 分タイムアウトより遥かに早く抜けて次の issue へ進める（ハング API 待ちの救済）。
  # transcript は ~/.claude/projects/<worktree パスの / と . を - に置換した名前>/ に置かれる。
  local tdir="$HOME/.claude/projects/$(echo "$wt" | sed 's#[/.]#-#g')"
  local stall_flag="$LOG_DIR/stalled-$num"
  rm -f "$stall_flag"
  (
    while kill -0 "$pid" 2>/dev/null; do
      sleep 60
      jf="$(ls -t "$tdir"/*.jsonl 2>/dev/null | head -1)"
      [ -z "$jf" ] && continue          # まだ transcript が無い（起動直後）→ 監視継続
      age=$(( $(date +%s) - $(stat -f %m "$jf" 2>/dev/null || echo 0) ))
      if [ "$age" -ge "$STALL_TIMEOUT" ]; then
        touch "$stall_flag"
        kill "$pid" 2>/dev/null; pkill -P "$pid" 2>/dev/null
        break
      fi
    done
  ) & stallmon=$!

  if ! wait "$pid"; then
    kill -0 "$watchdog" 2>/dev/null || timed_out=1
  fi
  { kill "$watchdog" && wait "$watchdog"; } 2>/dev/null || true
  pkill -P "$watchdog" 2>/dev/null || true
  { kill "$stallmon" && wait "$stallmon"; } 2>/dev/null || true
  pkill -P "$stallmon" 2>/dev/null || true
  pkill -P "$pid" 2>/dev/null || true    # claude 本体が残した子(node 等)の取りこぼしを最終掃除
  local stalled=0; [ -f "$stall_flag" ] && stalled=1

  local t_end dur; t_end="$(date +%s)"
  dur="$(( (t_end - t_start) / 60 ))分$(( (t_end - t_start) % 60 ))秒"

  # セッション結果の逆引き
  local state labels pr
  state="$(gh issue view "$num" --json state --jq .state 2>/dev/null)"
  labels="$(gh issue view "$num" --json labels --jq '[.labels[].name] | join(",")' 2>/dev/null)"
  pr="$(gh pr list --head "$branch" --state open --json number --jq '.[0].number' 2>/dev/null)"

  if echo "$labels" | grep -q needs-human; then
    note "- 🟡 #$num $title — ユーザー判断待ち（issue のコメント参照）[${dur}]"
  elif echo "$labels" | grep -q agent-failed; then
    note "- 🔴 #$num $title — 実装失敗（${log}）[${dur}]"
  elif [ "$state" = "CLOSED" ]; then
    note "- ✅ #$num $title — 既にクローズ済み [${dur}]"
  elif [ -n "$pr" ]; then
    # PR ができている → マージ工程（ロック内で直列）
    note "- ⏩ #$num $title — PR #$pr 作成。マージ工程へ [${dur}]"
    merge_sequence "$num" "$title" "$wt" "$branch" "$log"
  elif [ "$stalled" = 1 ]; then
    note "- 🔴 #$num $title — stall 検知で kill（transcript が ${STALL_TIMEOUT}秒 無進捗＝ハング。${log}）[${dur}]"
  elif [ "$timed_out" = 1 ]; then
    note "- 🔴 #$num $title — タイムアウト（${ISSUE_TIMEOUT}秒。${log}）[${dur}]"
  else
    # PR 無し・未完了。未 push の作業があれば退避 draft PR にして失わない
    if [ -d "$wt" ] && [ -n "$(git -C "$wt" log --oneline refs/remotes/origin/main..HEAD 2>/dev/null)" ]; then
      lock "$REPO_LOCK"
      if git -C "$wt" push -u origin "$branch" >/dev/null 2>&1; then
        ( cd "$wt" && gh pr create --draft --head "$branch" \
            --title "[退避] ${title}" \
            --body "夜間エージェントが PR 作成前に終了したため作業を自動退避した draft PR。検証後に ready 化を。 Fixes #${num}" >/dev/null 2>&1 ) \
          && note "- 💾 #$num 未 push の作業を draft PR に退避した（朝に検証を）[${dur}]"
      else
        note "- 🔴 #$num $title — 未完了・退避 push も失敗（${log}）[${dur}]"
      fi
      unlock "$REPO_LOCK"
    else
      note "- 🔴 #$num $title — 未完了（変更なし。${log}）[${dur}]"
    fi
  fi

  # 後片付け（worktree とローカルブランチ。リモートはマージ側に委ねる）
  lock "$REPO_LOCK"
  git -C "$REPO_DIR" worktree remove --force "$wt" 2>/dev/null || true
  git -C "$REPO_DIR" branch -D "$branch" >/dev/null 2>&1 || true
  unlock "$REPO_LOCK"
}

# ── ワーカー: 自分の割当ファイルを上から直列処理 ──
run_worker() { # $1=worker番号 $2=割当ファイル
  local w="$1" list="$2"
  NOTE_TARGET="$LOG_DIR/summary-w$w.md"; : > "$NOTE_TARGET"
  local num title _u
  while IFS=$'\t' read -r num title _u; do
    [ -z "$num" ] && continue
    process_issue "$num" "$title"
  done < "$list"
}

# ── ラウンドループ: 割当→並列実行→DLL同期→再取得（新着があれば続行） ──
round=1
while :; do
  [ -f "$REPO_DIR/out/overnight/STOP" ] && { rm -f "$REPO_DIR/out/overnight/STOP"; NOTE_TARGET="$SYS_NOTES"; note "- 🛑 STOP ファイル検出。終了。"; break; }
  [ -f "$HALT" ] && { NOTE_TARGET="$SYS_NOTES"; note "- 🛑 HALT（赤 main 検出）。終了。"; break; }

  issues="$(fetch_issues)"
  # 処理済みを除外
  if [ -s "$PROCESSED" ]; then
    issues="$(echo "$issues" | awk -F'\t' 'NR==FNR{p[$1];next} !($1 in p)' "$PROCESSED" -)"
  fi
  if [ -z "$issues" ]; then
    [ "$round" -gt 1 ] && { NOTE_TARGET="$SYS_NOTES"; note "- 🌙 再取得したが新着なし。終了。"; }
    break
  fi
  [ "$round" -gt 1 ] && { NOTE_TARGET="$SYS_NOTES"; note "- 🔄 再取得で新着 issue を検出（ラウンド ${round}）"; }

  # 割当ファイルを作る: unity→worker0、engine 系→残りワーカーに分散
  w=0; while [ "$w" -lt "$ISSUE_WORKERS" ]; do : > "$LOG_DIR/assign-$w.txt"; w=$((w+1)); done
  has_unity="$(echo "$issues" | awk -F'\t' '$3==1' | head -1)"
  # unity は worker 0 へ
  echo "$issues" | awk -F'\t' '$3==1' >> "$LOG_DIR/assign-0.txt"
  # engine 系の分散先
  if [ -n "$has_unity" ] && [ "$ISSUE_WORKERS" -gt 1 ]; then start=1; span=$((ISSUE_WORKERS-1)); else start=0; span=$ISSUE_WORKERS; fi
  echo "$issues" | awk -F'\t' -v s="$start" -v n="$span" -v dir="$LOG_DIR" '
    $3==0 { print $0 >> (dir "/assign-" (s + (c % n)) ".txt"); c++ }'

  # 並列起動
  w=0; pids=""
  while [ "$w" -lt "$ISSUE_WORKERS" ]; do
    run_worker "$w" "$LOG_DIR/assign-$w.txt" &
    pids="$pids $!"
    w=$((w+1))
  done
  for p in $pids; do wait "$p"; done

  # ラウンド末に DLL を main へ同期（エンジン変更があった場合のみ commit される）
  sync_dll_to_main

  round=$((round + 1))
done

# ── SUMMARY 組み立て（ヘッダ＋ワーカー別＋システム＋合計） ──
{
  echo "# 夜間エージェント実行結果 ($STAMP)"
  echo ""
  w=0
  while [ "$w" -lt "$ISSUE_WORKERS" ]; do
    if [ -s "$LOG_DIR/summary-w$w.md" ]; then
      echo "## ワーカー $w"
      cat "$LOG_DIR/summary-w$w.md"
      echo ""
    fi
    w=$((w+1))
  done
  if [ -s "$SYS_NOTES" ]; then
    echo "## システム"
    cat "$SYS_NOTES"
    echo ""
  fi
  total_min="$(( ($(date +%s) - RUN_START) / 60 ))"
  echo "総所要時間: $(( total_min / 60 ))時間$(( total_min % 60 ))分（着手 $(count_now) 件・並列 ${ISSUE_WORKERS}）"
} > "$SUMMARY"

# dll-sync worktree の後片付け
[ -n "$DLLWT" ] && git -C "$REPO_DIR" worktree remove --force "$DLLWT" 2>/dev/null || true

echo ""
echo "===================================="
echo "全処理終了。サマリ: $SUMMARY"
cat "$SUMMARY"
