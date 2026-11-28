#!/usr/bin/env bash
# 夜間 Issue 自動消化エージェント（並列ワーカー版）
#
# open な issue を複数ワーカーで並列に解決する。1 issue = 1 プロセス（毎回まっさらなコンテキスト）。
# セッション（claude -p）の責務は「実装→テスト緑→WIPコミット→（UIなら）実機確認→push→PR作成」まで。
# **CI 待ち・rebase・マージはこのスクリプトが単一ロックで直列に行う**（内側セッションの
# 「CI待ちで消滅」「Monitor待ちで消滅」を構造的に根絶する）。
# **未コミットのまま消滅したセッションへの構造的保険（issue #433）**: 実機確認待ち（Play モード・
# スクショ完了待ち）は内側セッションが最も消滅しやすい区間で、テンプレは手順4でその前の WIP
# コミットを必須化したが、モデルの従順さに完全には依存できない。process_issue はセッション終了後、
# PR 未作成なら auto_wip_commit() で worktree の未コミット変更（DLL 等の生成物を除く）を
# 自動で [WIP] コミットしてから、下の退避ネット（#348）に渡す。
#
# 並列モデル:
#   - ISSUE_WORKERS 本のワーカーが issue を並列に実装する（既定 3）。
#   - area:unity の issue は 1 ワーカー（worker 0）に集約し直列化する（検証 Editor は 1 台・UI 衝突対策）。
#   - engine 系はワーカー間に分散。衝突は「マージ直前の rebase＋再テスト」で機械検出し、
#     コンフリクト時は needs-human 送り（main は壊さない）。ただし試合結果の指紋（determinism-baseline.txt）
#     だけの衝突は main 側を採って続け、rebase 後のコードで作り直す（rebase_onto_main・issue #1255）。
#   - 試合の解決を変える engine の束を流す夜は ISSUE_WORKERS=2（Unity 1本＋engine 1本の直列）を推奨。
#     engine を並列にすると、指紋は自動で解けても帯（Heavy）が合成で外れうる。
#   - マージは REPO ロックで直列化（rebase→禁止物除去→ローカル再テスト→squash マージ）。
#   - DLL（Assets/Plugins/KokoSim/KokoSim.Engine.dll）と Resources/KokoSimData の YAML 複製は Git 管理外の
#     生成物（issue #785）。各 worktree では post-checkout / post-rewrite フックと check-unity-compile.sh
#     が作る。万一ブランチに紛れ込んだらマージ工程で追跡から外す。
#   - main の直近 CI が failure なら以降のマージを停止（赤 main への積み増し防止）。
#   - PR ができたら、マージ工程の前に**別セッション（既定 Opus）がレビュー**する（review_pr）。
#     実装セッションと文脈を共有しない新しい claude -p が差分・issue・設計書を読み、
#     BLOCK なら PR に指摘をコメントして needs-human（マージしない）。判定不能もマージしない。
#
# レビューの設定:
#   OVERNIGHT_REVIEW_MODEL=claude-sonnet-5-5 bash tools/overnight-agent.sh  # レビュー担当のモデルを変える
#   REVIEW_TIMEOUT=1800 bash tools/overnight-agent.sh                        # レビュー1件の上限秒数
#   SKIP_REVIEW=1 bash tools/overnight-agent.sh                              # レビューを飛ばす（従来の動き）
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
# gh は「いまいるフォルダ」でリポジトリを判定する。リポジトリの外（~ 等）から起動すると
# issue 一覧が取れず「対象0件」で即終了するので、ここで必ずリポジトリへ移る。
cd "$REPO_DIR" || { echo "リポジトリへ移動できません: $REPO_DIR" >&2; exit 1; }
WT_ROOT="${REPO_DIR}-worktrees"
STAMP="$(date +%Y%m%d-%H%M%S)"
RUN_START="$(date +%s)"
LOG_DIR="$REPO_DIR/out/overnight/$STAMP"
PROMPT_TEMPLATE="$REPO_DIR/tools/overnight-prompt.template.md"
DOTNET="/usr/local/share/dotnet/dotnet"
# 生成物（DLL / Resources/KokoSimData 複製）の pathspec。Git 管理外（.gitignore 済み・issue #785）だが、
# `git add -f` 等で紛れ込んだ場合に備えて WIP 自動退避から除外し、マージ工程で追跡から外す。
GEN_FILES="Assets/Plugins/KokoSim/KokoSim.Engine.dll :(glob)Assets/Resources/KokoSimData/**/*.yaml Assets/Resources/KokoSimData/_index.txt"

CLAUDE_BIN="${CLAUDE_BIN:-$HOME/.local/bin/claude}"
MODEL="${OVERNIGHT_MODEL:-claude-sonnet-5-5}"          # 既定は Sonnet。model:opus ラベルで昇格
OPUS_MODEL="claude-opus-5-5"
FALLBACK_MODEL="${OVERNIGHT_FALLBACK:-claude-sonnet-5}"
ISSUE_TIMEOUT="${ISSUE_TIMEOUT:-5400}"               # 1件あたりのハード上限（Unity実機確認込みで90分）
STALL_TIMEOUT="${STALL_TIMEOUT:-1500}"               # セッションの transcript が無進捗なら kill（既定25分）
# transcript はツールの結果が返るまで更新されない。Heavy の前面実行（約9分＋ビルド）や
# ベースライン再生成の間も無進捗に見えるので、Bash ツールの上限（10分）を十分に超える値にする（issue #1255）。
MAX_ISSUES="${MAX_ISSUES:-0}"                        # 0 = 無制限
ISSUE_WORKERS="${ISSUE_WORKERS:-3}"                  # 並列ワーカー数
[ "$ISSUE_WORKERS" -lt 1 ] && ISSUE_WORKERS=1
# 別セッションによる PR レビュー（マージ前ゲート）。実装セッションと文脈を共有しない新しいセッションで読ませる。
REVIEW_PROMPT_TEMPLATE="$REPO_DIR/tools/overnight-review-prompt.template.md"
REVIEW_MODEL="${OVERNIGHT_REVIEW_MODEL:-$OPUS_MODEL}"     # レビュー担当は Opus
REVIEW_FALLBACK="${OVERNIGHT_REVIEW_FALLBACK:-claude-sonnet-5-5}"
REVIEW_TIMEOUT="${REVIEW_TIMEOUT:-1800}"             # レビュー1件あたりの上限（既定30分）
SKIP_REVIEW="${SKIP_REVIEW:-}"                        # 1 でレビュー工程を飛ばす（従来の動き）

command -v gh >/dev/null || { echo "gh が見つかりません" >&2; exit 1; }
[ -x "$CLAUDE_BIN" ] || { echo "claude CLI が見つかりません: $CLAUDE_BIN" >&2; exit 1; }
[ -f "$PROMPT_TEMPLATE" ] || { echo "プロンプトテンプレートがありません: $PROMPT_TEMPLATE" >&2; exit 1; }
[ -n "$SKIP_REVIEW" ] || [ -f "$REVIEW_PROMPT_TEMPLATE" ] || { echo "レビュー用テンプレートがありません: $REVIEW_PROMPT_TEMPLATE" >&2; exit 1; }
[ -x "$DOTNET" ] || { echo "dotnet が見つかりません: $DOTNET" >&2; exit 1; }

# 処理対象: needs-human / agent-failed を除く、所有者本人の open issue。
# post-1.0（1.0リリース後に回す）も拾う（2026-11-18 オーナー決定「全部夜間で回す」。ship-blocker が
# 尽きるのを待つと永遠に始まらないため）。ただし並びの最後＝必須の件を先に片付けてから着手する。
# バランスを揺らす件を並列で回すと互いの帯を壊し合うので、post-1.0 を流す夜は ISSUE_WORKERS=1 を推奨。
# 出力は "番号\tタイトル\tISUNITY"（ISUNITY=1 なら area:unity 付き）。
# 並びは ship-blocker（1.0までに必須）→ bug → その他 → post-1.0、同じ段の中は番号昇順。
# 取得上限は十分大きく取る。gh は新しい順に切るので、上限を超えると**古い ship-blocker から**漏れる
# （2026-11-21: 上限100・open 120件で #1080・#1205〜#1218 が対象から消えていた。issue #1255）。
fetch_issues() {
  gh issue list --state open --limit 1000 --author "trickaaaaaa" --json number,title,labels --jq '
    [ .[] | select( ( [.labels[].name] | (index("needs-human") or index("agent-failed")) ) | not ) ]
    | sort_by( ([.labels[].name]) as $l
        | (if ($l | index("ship-blocker")) then 0 elif ($l | index("post-1.0")) then 3
           elif ($l | index("bug")) then 1 else 2 end),
          .number )
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
REPO_LOCK="$LOG_DIR/.repolock"      # REPO_DIR の .git を触る操作＋マージの単一ミューテックス
CTR_LOCK="$LOG_DIR/.ctrlock"        # カウンタ/processed 追記用の軽量ロック

# ── ロック（mkdir はアトミック。LOG_DIR はラン毎に新規なので跨ぎ stale は無い） ──
# 持ち主の PID を <ロック>/owner に書き、**持ち主が死んでいるときだけ**奪う（issue #1232）。
# 以前は 2400 秒待つと無条件に rm -rf で奪っていたが、merge_sequence はロックを持ったまま
# CI 再実行待ち（最大 CI_RERUN_WAIT 秒）＋非Heavy＋Heavy を回すので正常でも超えうる。奪われた側の
# unlock が奪った側のロックまで消し、2つのマージが同時に走る事故になる。
# bash 3.2 に BASHPID が無いので、各ワーカー（サブシェル）の PID は exec した sh の PPID で取る
# （exec でコマンド置換のサブシェル自体を sh に置き換える＝PPID がこのシェルになる）。
MYPID=$$
self_pid() { MYPID="$(exec sh -c 'echo $PPID')"; }
lock() {
  local l="$1" owner
  until mkdir "$l" 2>/dev/null; do
    owner="$(cat "$l/owner" 2>/dev/null)"
    if [ -n "$owner" ] && ! kill -0 "$owner" 2>/dev/null; then
      echo "[lock] 持ち主 PID ${owner} が居ないので ${l##*/} を回収" >&2
      rm -rf "$l"; continue
    fi
    sleep 2
  done
  echo "$MYPID" > "$l/owner"
}
# 自分が持っているときだけ外す（他人のロックを消さない）
unlock() {
  local owner; owner="$(cat "$1/owner" 2>/dev/null)"
  if [ -z "$owner" ] || [ "$owner" = "$MYPID" ]; then rm -rf "$1" 2>/dev/null || true; fi
}

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
# ただし初回の赤は落ちたジョブだけ1回再実行して確かめる（2026-11-10: 時間計測の
# Trace_OverheadDoesNotBlowUp が1本だけ落ちて夜間ランが54分で HALT した。再実行で緑）。
# 再実行は最大 CI_RERUN_WAIT 秒待つ。2回目も赤なら本物の赤として真を返す。
CI_RERUN_WAIT="${CI_RERUN_WAIT:-1500}"
main_ci_failing() {
  local row id c attempt
  # 完了済みの最新1件を見る（issue #1255）。以前は最新1件を取ってから completed で絞っていたので、
  # 最新が実行中だと「赤ではない」になり、直前の赤を見逃していた。
  row="$(gh run list --branch main --workflow engine-ci --limit 20 --json databaseId,status,conclusion,attempt \
        --jq '[.[] | select(.status=="completed")][0] | select(. != null) | "\(.databaseId) \(.conclusion) \(.attempt)"' 2>/dev/null)"
  id="${row%% *}"; c="$(echo "$row" | awk '{print $2}')"; attempt="$(echo "$row" | awk '{print $3}')"
  [ "$c" = "failure" ] || return 1
  if [ "${attempt:-1}" -le 1 ] && gh run rerun "$id" --failed >/dev/null 2>&1; then
    echo "main CI が赤（run ${id}）。落ちたジョブを1回だけ再実行して確かめる…"
    local waited=0 st
    sleep 30; waited=30
    while [ "$waited" -lt "$CI_RERUN_WAIT" ]; do
      st="$(gh run view "$id" --json status,conclusion --jq '"\(.status) \(.conclusion)"' 2>/dev/null)"
      case "$st" in
        "completed success") echo "再実行で緑（フレーク）。続行"; return 1 ;;
        completed*) return 0 ;;
      esac
      sleep 60; waited=$((waited+60))
    done
    return 0  # 待ち切れなければ安全側（赤扱い）
  fi
  return 0
}

# ── 試合結果の指紋（determinism-baseline.txt）の衝突を機械で解く（issue #1255） ──
# 試合の解決を変える PR はみな指紋を作り直すので、2本続けてマージすると必ず rebase で衝突する
# （CLAUDE.md「手で直さない。片側を採って rebase を終え、作り直してコミット」）。以前は needs-human に
# 送っていたため、engine の束を夜に流すと2本目以降が全部止まっていた。
# 衝突が指紋だけなら main 側を採って rebase を続け、後で merge_sequence が作り直す。
# 返り値: 0=rebase 完了 / 1=指紋以外の衝突（呼び出し側で abort）
BASELINE_PATH="engine/KokoSim.Engine.Tests/Match/Game/determinism-baseline.txt"
rebase_onto_main() { # $1=wt $2=log
  local wt="$1" log="$2" conf guard=0
  git -C "$wt" rebase refs/remotes/origin/main >>"$log" 2>&1 && return 0
  while [ "$guard" -lt 50 ]; do
    guard=$((guard+1))
    conf="$(git -C "$wt" diff --name-only --diff-filter=U 2>/dev/null)"
    if [ -n "$conf" ]; then
      [ "$conf" = "$BASELINE_PATH" ] || return 1
      echo "[merge] 指紋の衝突だけ → main 側を採って続行" >>"$log"
      git -C "$wt" checkout --ours -- "$BASELINE_PATH" >>"$log" 2>&1 || return 1
      git -C "$wt" add -- "$BASELINE_PATH" >>"$log" 2>&1 || return 1
    fi
    GIT_EDITOR=true git -C "$wt" rebase --continue >>"$log" 2>&1 && return 0
    # 指紋だけのコミットは main 側を採ると空になる → 落として続ける
    if [ -z "$(git -C "$wt" diff --name-only --diff-filter=U 2>/dev/null)" ] \
       && [ -z "$(git -C "$wt" status --porcelain --untracked-files=no 2>/dev/null)" ]; then
      git -C "$wt" rebase --skip >>"$log" 2>&1 && return 0
    fi
    [ -d "$(git -C "$wt" rev-parse --git-path rebase-merge 2>/dev/null)" ] \
      || [ -d "$(git -C "$wt" rev-parse --git-path rebase-apply 2>/dev/null)" ] || return 1
  done
  return 1
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

  # rebase 前に未コミットの変更を捨てる（issue #416）。セッションが未コミットの変更を
  # 残していると、dirty tree のままだと rebase が
  # 「unstaged changes」で拒否され、下の分岐が本物のコンフリクトと誤判定して PR を
  # クローズしてしまう（2026-08-03 の夜間ランで #396/#397 が誤クローズされた実害）。
  # セッションの成果は push 済みコミットが全てなので、ここで捨ててよい。
  git -C "$wt" reset --hard -q HEAD >/dev/null 2>&1 || true

  # この PR が指紋を作り直していたか（rebase で main 側を採ると差分から消えるので、rebase の前に見る）
  local touched_baseline=0
  git -C "$wt" diff --name-only 'refs/remotes/origin/main...HEAD' 2>/dev/null | grep -qx "$BASELINE_PATH" && touched_baseline=1

  # 最新 main の上へ rebase（cut 後に他 PR が landed していても巻き戻しを混ぜない）
  if ! rebase_onto_main "$wt" "$log"; then
    git -C "$wt" rebase --abort >/dev/null 2>&1 || true
    gh pr close "$branch" --comment "自動: 最新 main へ rebase 時にコンフリクト。手動確認のため保留します。" >/dev/null 2>&1 || true
    gh issue edit "$num" --add-label needs-human >/dev/null 2>&1 || true
    note "- 🟡 #$num $title — rebase コンフリクト→needs-human（他 PR と同一箇所を編集した可能性）"
    unlock "$REPO_LOCK"; return
  fi

  # 指紋を作り直していた PR は、rebase 後のコードで作り直す（main 側の指紋と合成した結果を凍結し直す）。
  # 指紋を触っていない PR は作り直さない＝意図せず試合結果を変えていれば下の非Heavy 再テストで赤になる。
  if [ "$touched_baseline" = 1 ]; then
    if ! ( cd "$wt" && DOTNET="$DOTNET" bash tools/regen-determinism-baseline.sh ) >>"$log" 2>&1; then
      gh issue edit "$num" --add-label agent-failed >/dev/null 2>&1 || true
      note "- 🔴 #$num $title — rebase 後の指紋の作り直しに失敗→agent-failed（ログ: ${log}）"
      unlock "$REPO_LOCK"; return
    fi
    if [ -n "$(git -C "$wt" status --porcelain -- "$BASELINE_PATH" 2>/dev/null)" ]; then
      git -C "$wt" add -- "$BASELINE_PATH" >>"$log" 2>&1
      git -C "$wt" commit -q -m "Regenerate determinism baseline after rebase onto main" >>"$log" 2>&1 || true
      echo "[merge] rebase 後に指紋を作り直してコミット" >>"$log"
    fi
  fi

  # 生成物（DLL / Resources/KokoSimData 複製）は Git 管理外（issue #785）。紛れ込んでいたら追跡から外す
  # （作業ツリーのファイルは残す＝Unity 検査で使う）。
  if [ -n "$(git -C "$wt" ls-files -- $GEN_FILES 2>/dev/null)" ]; then
    git -C "$wt" rm -r -q --cached -- $GEN_FILES >/dev/null 2>&1 || true
    git -C "$wt" commit -q -m "chore: untrack generated engine DLL / YAML copies (#785)" >/dev/null 2>&1 || true
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

  # rebase 後のブランチを更新（pre-push フックの Unity 検査は check-unity-compile.sh が
  # このブランチの engine/ から DLL を作り直してから走る＝旧 DLL での誤検知は起きない。issue #432/#785）
  if ! git -C "$wt" push --force-with-lease -q origin "$branch" >>"$log" 2>&1; then
    note "- 🔴 #$num $title — rebase 後の push に失敗（マージ見送り。ログ: ${log}）"
    unlock "$REPO_LOCK"; return
  fi

  # ローカル再テスト（CI と同じ発火条件＝engine/data/global.json を触ったときだけ）。
  # main 未保護のため CI は待たず、この再テストの緑をマージ可否の判定に使う（post-merge CI は監視）。
  local ci_paths
  ci_paths="$(git -C "$wt" diff --name-only 'refs/remotes/origin/main...HEAD' 2>/dev/null | grep -E '^(engine/|data/|global\.json$)' || true)"
  if [ -n "$ci_paths" ]; then
    if ! ( cd "$wt" && "$DOTNET" test engine/ -c Release --filter "Category!=Heavy" ) >>"$log" 2>&1; then
      gh issue edit "$num" --add-label agent-failed >/dev/null 2>&1 || true
      note "- 🔴 #$num $title — rebase 後の non-Heavy 再テストが赤→agent-failed（ログ: ${log}）"
      unlock "$REPO_LOCK"; return
    fi
    # Heavy は帯が動きうる変更のときだけ（判定は tools/needs-heavy.sh＝試合結果の指紋・データ・
    # 指紋で測っていない engine を見る）。省いた分は post-merge の CI が全 Heavy を回して拾い、
    # 赤なら上の「main の直近 CI が赤」ガードで以降のマージが止まる。
    local heavy_why
    if heavy_why="$(bash "$REPO_DIR/tools/needs-heavy.sh" refs/remotes/origin/main "$wt")"; then
      if ! ( cd "$wt" && "$DOTNET" test engine/ -c Release --filter "Category=Heavy" ) >>"$log" 2>&1; then
        gh issue edit "$num" --add-label agent-failed >/dev/null 2>&1 || true
        note "- 🔴 #$num $title — rebase 後の Heavy 統計回帰が赤→agent-failed（ログ: ${log}）"
        unlock "$REPO_LOCK"; return
      fi
    else
      echo "[merge] Heavy 省略: ${heavy_why}" >>"$log"
    fi
  fi

  # Assets/ の C# を触ったら Unity コンパイル検査（issue #349）。
  # ci.yml の発火条件は engine/ data/ global.json だけで Assets/ は入っていないため、
  # Assets だけを触る PR は CI もローカルテストも走らない。加えて内側セッションの
  # Unity 実機確認はブリッジ不応答時にスキップ可なので、無検査でマージされてしまう。
  # 実際に PR #344 が using 漏れ（CS0103）のまま入り Unity が丸ごと落ちた。
  # check-unity-compile.sh は Editor を起動せず約10秒で Assets 全体をコンパイルする。
  # 終了コード 2（雛形 csproj が無い＝検査不能）はマージを止めない（環境要因で夜が全滅するのを避ける）。
  local unity_paths
  # engine の public API を変えるだけの PR も Assets を壊しうる（DLL 経由。issue #1232）ので、engine 本体の .cs も対象
  unity_paths="$(git -C "$wt" diff --name-only 'refs/remotes/origin/main...HEAD' 2>/dev/null | grep -E '^(Assets/.*|engine/KokoSim\.Engine/.*)\.cs$' || true)"
  if [ -n "$unity_paths" ]; then
    local ucheck urc
    ucheck="$(TEMPLATE_DIR="$REPO_DIR" DOTNET="$DOTNET" bash "$REPO_DIR/tools/check-unity-compile.sh" "$wt" 2>&1)"
    urc=$?
    printf '%s\n' "$ucheck" >>"$log"
    if [ "$urc" = "1" ]; then
      gh issue edit "$num" --add-label agent-failed >/dev/null 2>&1 || true
      local firsterr; firsterr="$(printf '%s\n' "$ucheck" | grep -m1 'error CS' | sed 's/^ *//')"
      gh pr comment "$branch" --body "自動ガード: Unity（Assets/）のコンパイルが通りません。マージを止めました。

\`\`\`
${ucheck}
\`\`\`

再現: \`bash tools/check-unity-compile.sh\`（Editor 不要・約10秒）" >/dev/null 2>&1 || true
      note "- 🔴 #$num $title — Unity コンパイル検査が赤→agent-failed（${firsterr}）"
      unlock "$REPO_LOCK"; return
    elif [ "$urc" = "2" ]; then
      # 検査不能（雛形 csproj 不在）は**マージを止める**（issue #349）。
      # 以前は「環境要因で夜が全滅するのを避ける」ため続行していたが、それだと Assets を触る PR が
      # 黙って無検査で main に入る＝この issue で塞ぎたかった穴がそのまま残る。雛形は検証用 Editor が
      # 一度開いていれば必ずあるので、無いこと自体が環境の異常＝人が見るべき状態。
      gh issue edit "$num" --add-label needs-human >/dev/null 2>&1 || true
      gh pr comment "$branch" --body "自動ガード: Unity（Assets/）のコンパイル検査を実行できませんでした（雛形 csproj 不在）。マージを止めました。

Assets/ を触る変更を無検査で main へ入れないための停止です。検証用 Unity で一度プロジェクトを開くと
Assembly-CSharp.csproj が生成され、\\`bash tools/check-unity-compile.sh\\` が通るようになります。" >/dev/null 2>&1 || true
      note "- 🟡 #$num $title — Unity コンパイル検査が実行不能（雛形 csproj 不在）→needs-human"
      unlock "$REPO_LOCK"; return
    fi
  fi

  # マージ（main 未保護なので即マージ。Fixes 行で issue クローズ）。
  # gh の終了コードは信用しない: マージ成功後に --delete-branch だけ失敗すると非ゼロになり
  # 「マージ成功なのに🔴」と誤報するため、PR の実状態(MERGED)で判定する。
  # PR 番号はマージ前に open のものを取る（--state all だと、同じ issue の前の夜にマージ済みの
  # 同名ブランチ PR を拾い、今夜のマージが失敗しても「マージ済み」と誤報しうる）。
  local prnum; prnum="$(gh pr list --head "$branch" --state open --limit 1 --json number --jq '.[0].number' 2>/dev/null)"
  gh pr merge "$branch" --squash --delete-branch >/dev/null 2>&1
  if [ -n "$prnum" ] && [ "$(gh pr view "$prnum" --json state --jq .state 2>/dev/null)" = "MERGED" ]; then
    note "- ✅ #$num $title — マージ済み（PR #${prnum}）"
    git push origin --delete "$branch" >/dev/null 2>&1 || true   # 消し残ったリモートブランチの掃除
  else
    note "- 🔴 #$num $title — squash マージに失敗（ログ: ${log}・朝に確認を）"
  fi
  unlock "$REPO_LOCK"
}

# ── 別セッションによる PR レビュー（マージ前ゲート） ──
# 実装セッションと文脈を共有しない新しい claude -p に差分・issue・設計書を読ませ、最終行の
# 「VERDICT: PASS / BLOCK」で判定する。レビュー担当は読むだけ（編集・テスト・GitHub への書き込みは
# ツール制限で塞ぐ）。結果のコメント投稿とラベル付けはこのスクリプトが行う。
# 返り値: 0=PASS（マージ工程へ）/ 1=BLOCK / 2=レビュー不能（判定行が無い・タイムアウト等）。
# 1 と 2 はどちらもマージしない（人が見ていない変更を判定なしで入れないため）。
# ロック外で走る＝ワーカー間で並列にレビューできる。
review_pr() { # $1=num $2=pr $3=wt $4=log
  local num="$1" pr="$2" wt="$3" log="$4"
  local rlog="${log%.log}-review.log" out="${log%.log}-review.md" prompt
  prompt="$(sed -e "s/__ISSUE__/${num}/g" -e "s/__PR__/${pr}/g" "$REVIEW_PROMPT_TEMPLATE")"

  ( cd "$wt" && exec "$CLAUDE_BIN" -p "$prompt" \
      --model "$REVIEW_MODEL" --fallback-model "$REVIEW_FALLBACK" \
      --allowedTools "Read,Grep,Glob,Bash(gh pr view *),Bash(gh pr diff *),Bash(gh issue view *),Bash(git diff *),Bash(git log *),Bash(git show *),Bash(git grep *)" \
      --disallowedTools "Edit,Write,NotebookEdit" ) < /dev/null > "$out" 2> "$rlog" &
  local pid=$! watchdog
  ( sleep "$REVIEW_TIMEOUT"; kill "$pid" 2>/dev/null; pkill -P "$pid" 2>/dev/null ) & watchdog=$!
  wait "$pid" 2>/dev/null
  { kill "$watchdog" && wait "$watchdog"; } 2>/dev/null || true
  pkill -P "$watchdog" 2>/dev/null || true
  pkill -P "$pid" 2>/dev/null || true

  # 判定は「最後の空でない行」だけを見る（本文中に VERDICT の文字列が出ても拾わない）。
  # 手本どおり ``` で囲んで返すと最後の行が ``` になるので、バッククォートと **・CR を先に消してから
  # 空行を除く（以前は空行を除いてから消していたので、最後が空文字になり判定不能＝needs-human。issue #1255）。
  local verdict
  verdict="$(tr -d '\r`*' < "$out" 2>/dev/null | sed 's/^[[:space:]]*//;s/[[:space:]]*$//' | grep -v '^$' | tail -1)"
  case "$verdict" in
    "VERDICT: PASS")  gh pr comment "$pr" --body-file "$out" >/dev/null 2>&1 || true; return 0 ;;
    "VERDICT: BLOCK") gh pr comment "$pr" --body-file "$out" >/dev/null 2>&1 || true; return 1 ;;
  esac
  gh pr comment "$pr" --body "自動: 夜間レビューが判定を出せませんでした（タイムアウトまたは出力形式の崩れ）。人が差分を見てからマージしてください。
ログ: \`${rlog}\` / \`${out}\`" >/dev/null 2>&1 || true
  return 2
}

# 未コミットの変更を退避直前に自動で WIP コミットする（issue #433）。実機確認待ち（Play モード・
# スクショ完了待ち）は内側セッションが最も消滅しやすい区間で、そこで未コミットのまま消えると
# 退避ネット（#348・下の「未 push の作業を失わないための退避」）はコミット済みしか救えず
# worktree ごと全損する（実例: #393、45分の実装が跡形もなく消えた）。gen_files（DLL /
# Resources/KokoSimData 複製）は対象から除外する（.gitignore 済みだが、念のため明示的に外す。
# ここで巻き込むと退避 PR に生成物が混ざる）。PR が既にある場合は呼ばない
# （merge_sequence が別途 reset --hard するため）。
auto_wip_commit() { # $1=wt $2=log $3=num
  local wt="$1" log="$2" num="$3" f
  [ -d "$wt" ] || return
  local excludes=()
  # ":(glob)x" は ":(exclude,glob)x" へ組み直す（":(exclude):(glob)x" は不正な pathspec で
  # status ごと失敗し、WIP 退避が黙って空振りする）
  for f in $GEN_FILES; do
    case "$f" in
      ":(glob)"*) excludes+=(":(exclude,glob)${f#:(glob)}") ;;
      *)          excludes+=(":(exclude)$f") ;;
    esac
  done
  [ -z "$(git -C "$wt" status --porcelain -- . "${excludes[@]}" 2>/dev/null)" ] && return
  git -C "$wt" add -A -- . "${excludes[@]}" >>"$log" 2>&1
  if git -C "$wt" commit -q -m "$(printf '[WIP] auto-evacuate uncommitted changes at session end\n\nCo-Authored-By: Claude <noreply@anthropic.com>')" >>"$log" 2>&1; then
    note "- 💾 #$num 未コミットの変更を自動 WIP コミットした（下の退避へ）"
  fi
}

# 前回ランの残り（同名のリモートブランチ・その open PR）を退避ブランチへ移す。REPO ロック内で呼ぶ。
# 出力: 何も無ければ空。退避したら「<退避ブランチ>|<セッションへの指示文>」。失敗したら FAILED。
# 退避ブランチへの push が成功してから元を消す順なので、途中で落ちても作業は必ずどちらかに残る。
set_aside_previous_branch() { # $1=num $2=branch
  local num="$1" branch="$2" sha prev pr
  sha="$(git -C "$REPO_DIR" ls-remote origin "refs/heads/$branch" 2>/dev/null | cut -f1)"
  [ -z "$sha" ] && return 0
  prev="${branch}-prev"
  if [ -n "$(git -C "$REPO_DIR" ls-remote origin "refs/heads/$prev" 2>/dev/null)" ]; then
    prev="${branch}-prev-$(date +%Y%m%d%H%M%S)"
  fi
  if ! git -C "$REPO_DIR" push -q origin "$sha:refs/heads/$prev" >/dev/null 2>&1; then
    echo "FAILED"; return 0
  fi
  pr="$(gh pr list --head "$branch" --state open --json number --jq '.[0].number' 2>/dev/null)"
  if [ -n "$pr" ]; then
    gh pr close "$pr" --comment "自動: 夜間エージェントが続きから着手するため閉じる。作業はブランチ \`${prev}\`（${sha}）に残した。" >/dev/null 2>&1 || true
  fi
  if ! git -C "$REPO_DIR" push -q origin --delete "$branch" >/dev/null 2>&1; then
    echo "FAILED"; return 0
  fi
  local from="前回の作業"
  [ -n "$pr" ] && from="前回の PR #${pr}（閉じた）の作業"
  echo "$prev|${from}がブランチ \`$prev\` にある。まず \`git fetch origin $prev\` して差分と、その PR・issue のコメント（レビューの指摘・オーナーの判断）を読むこと。使える部分は取り込み（cherry-pick か merge）、指摘と判断を反映して続きから仕上げる。古い前提で不要になった部分は捨ててよい。"
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

  # 前回ランの作業が同じブランチ名で残っていたら、退避名へ移して続きから取り組ませる。
  # ここまで来た issue は needs-human が外れている＝オーナーが判断を返した／差し戻しを解いた後なので、
  # 以前のように「open PR があるからスキップ」すると判断待ちが解けた issue を毎回取りこぼす。
  # 作業は消さない（退避ブランチに残す）。新しいセッションは main から切った同名ブランチで始め、
  # 退避ブランチを取り込んで続ける＝push が non-fast-forward で弾かれない（--force は禁止のまま）。
  local resume_note=""
  lock "$REPO_LOCK"
  resume_note="$(set_aside_previous_branch "$num" "$branch")"
  unlock "$REPO_LOCK"
  if [ "$resume_note" = "FAILED" ]; then
    note "- 🔴 #$num $title — 前回のブランチを退避できなかった（作業を失わないためスキップ）"
    return
  fi
  [ -n "$resume_note" ] && note "- ♻️ #$num 前回の作業を退避して続きから着手（${resume_note%%|*}）"

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
  if [ -n "$resume_note" ]; then
    prompt="${prompt}

## 前回の作業（必読）
${resume_note#*|}"
  fi
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

  # PR 未作成のまま終了 → 残った未コミット変更を退避直前に自動 WIP コミット（issue #433）。
  # PR がある場合は merge_sequence が別途 reset --hard するのでここでは触らない。
  [ -z "$pr" ] && auto_wip_commit "$wt" "$log" "$num"

  if echo "$labels" | grep -q needs-human; then
    note "- 🟡 #$num $title — ユーザー判断待ち（issue のコメント参照）[${dur}]"
  elif echo "$labels" | grep -q agent-failed; then
    note "- 🔴 #$num $title — 実装失敗（${log}）[${dur}]"
  elif [ "$state" = "CLOSED" ]; then
    note "- ✅ #$num $title — 既にクローズ済み [${dur}]"
  elif [ -n "$pr" ]; then
    # PR ができている → マージ工程（ロック内で直列）
    # 別セッションのレビューを通ったものだけマージ工程へ（A案: BLOCK なら朝に人が見る）
    local rc=0
    if [ -z "$SKIP_REVIEW" ]; then
      review_pr "$num" "$pr" "$wt" "$log"; rc=$?
    fi
    if [ "$rc" = 0 ]; then
      note "- ⏩ #$num $title — PR #${pr} 作成・レビュー通過。マージ工程へ [${dur}]"
      merge_sequence "$num" "$title" "$wt" "$branch" "$log"
    elif [ "$rc" = 1 ]; then
      gh issue edit "$num" --add-label needs-human >/dev/null 2>&1 || true
      gh issue comment "$num" --body "自動: PR #${pr} は夜間レビューで差し戻しになりました（指摘は PR のコメント参照）。マージしていません。" >/dev/null 2>&1 || true
      note "- 🔍 #$num $title — PR #${pr} はレビューで差し戻し→needs-human（指摘は PR コメント）[${dur}]"
    else
      gh issue edit "$num" --add-label needs-human >/dev/null 2>&1 || true
      note "- 🟡 #$num $title — PR #${pr} のレビューが判定を出せず→マージせず needs-human（${log%.log}-review.log）[${dur}]"
    fi
  elif [ "$stalled" = 1 ]; then
    note "- 🔴 #$num $title — stall 検知で kill（transcript が ${STALL_TIMEOUT}秒 無進捗＝ハング。${log}）[${dur}]"
  elif [ "$timed_out" = 1 ]; then
    note "- 🔴 #$num $title — タイムアウト（${ISSUE_TIMEOUT}秒。${log}）[${dur}]"
  elif [ -d "$wt" ] && [ -n "$(git -C "$wt" log --oneline refs/remotes/origin/main..HEAD 2>/dev/null)" ]; then
    note "- 🔴 #$num $title — 未完了（作業は下の退避で残す。${log}）[${dur}]"
  else
    note "- 🔴 #$num $title — 未完了（変更なし。${log}）[${dur}]"
  fi

  # ── 未 push の作業を失わないための退避（issue #348） ──
  # この後の branch -D は**結果に関係なく無条件で走る**。以前は退避処理が最後の else 分岐の
  # 中にしか無かったため、needs-human / agent-failed / stall / timeout で終わった場合は
  # ローカルにコミットがあっても消えていた（#318 は実装完成・全緑のまま消滅し、
  # git fsck の dangling commit から救出した）。分岐の外へ出して常に通す。
  # マージ済みなら origin/main..HEAD が空になるのでこの条件で自然に除外される。
  # squash マージ済みでもブランチのコミットは origin/main..HEAD に残るので、PR の実状態で除外する。
  # ただし「同じブランチ名のマージ済み PR がある」だけで除外してはいけない。同じ issue を別の夜に
  # 続けると同名ブランチ（issue/N-agent）を使い回すため、前の夜にマージした PR が見えてしまい、
  # 今夜の作業が退避されずに消えた（実例: #1789。前夜の測定 PR #1800 がマージ済みだったため、
  # タイムアウト時の WIP コミットが push されず worktree ごと消え、git fsck から救出した）。
  # マージされた PR の先頭が今の HEAD を含む（HEAD がその祖先）ときだけ「マージ済み」とみなす。
  # 判定できない（オブジェクトが手元に無い等）ときは退避する側に倒す。
  local merged_heads already_merged=0 h head_sha
  head_sha="$(git -C "$wt" rev-parse HEAD 2>/dev/null)"
  merged_heads="$(gh pr list --head "$branch" --state merged --limit 20 --json headRefOid --jq '.[].headRefOid' 2>/dev/null)"
  for h in $merged_heads; do
    if [ -n "$head_sha" ] && { [ "$h" = "$head_sha" ] || git -C "$wt" merge-base --is-ancestor "$head_sha" "$h" 2>/dev/null; }; then
      already_merged=1; break
    fi
  done
  if [ "$already_merged" = 0 ] && [ -d "$wt" ] && [ -n "$(git -C "$wt" log --oneline refs/remotes/origin/main..HEAD 2>/dev/null)" ]; then
    local why="PR 作成前に終了"
    if echo "$labels" | grep -q needs-human; then why="判断待ち（needs-human）"
    elif echo "$labels" | grep -q agent-failed; then why="実装失敗（agent-failed）"
    elif [ "$stalled" = 1 ]; then why="stall 検知で kill"
    elif [ "$timed_out" = 1 ]; then why="タイムアウト"
    fi
    lock "$REPO_LOCK"
    # 既に open の PR がある（マージ工程で弾かれた等）ならブランチを push し直すだけでよい。
    # 閉じた・マージ済みの PR（前の夜の同名ブランチ）へ push しても PR には載らず、
    # 「保全した」と報告しながら誰も見ない場所に置くだけになるので open に限る。
    local existing; existing="$(gh pr list --head "$branch" --state open --limit 1 --json number --jq '.[0].number' 2>/dev/null)"
    if [ -n "$existing" ]; then
      git -C "$wt" push origin "$branch" >>"$log" 2>&1 \
        && note "- 💾 #$num 作業は PR #${existing} に push 済みで保全（理由: ${why}）" \
        || note "- 🔴 #$num $title — 既存 PR #${existing} への push に失敗（${log}）"
    elif git -C "$wt" push -u origin "$branch" >>"$log" 2>&1; then
      ( cd "$wt" && gh pr create --draft --head "$branch" \
          --title "[退避] ${title}" \
          --body "夜間エージェントの作業を自動退避した draft PR（理由: ${why}）。

このブランチはローカルの後片付けで削除されるため、失わないよう push している。
issue のコメント・PR の差分を見て、続行するか捨てるか判断を。続行するなら ready 化を。

Refs #${num}" >/dev/null 2>&1 ) \
        && note "- 💾 #$num 未 push の作業を draft PR に退避した（理由: ${why}・朝に検証を）"
    else
      note "- 🔴 #$num $title — 退避 push に失敗（${log}）"
    fi
    unlock "$REPO_LOCK"
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
  self_pid   # このワーカー（サブシェル）の PID をロックの持ち主として使う
  # 追記のみ。ここで空にするとラウンド2以降が前のラウンドの結果を消す（2026-11-24 の夜に SUMMARY が空になった）。
  # 空にするのはラウンドループの前で1回だけ。
  NOTE_TARGET="$LOG_DIR/summary-w$w.md"
  local num title _u
  while IFS=$'\t' read -r num title _u; do
    [ -z "$num" ] && continue
    process_issue "$num" "$title"
  done < "$list"
}

# ── ラウンドループ: 割当→並列実行→再取得（新着があれば続行） ──
w=0; while [ "$w" -lt "$ISSUE_WORKERS" ]; do : > "$LOG_DIR/summary-w$w.md"; w=$((w+1)); done
round=1
while :; do
  [ -f "$REPO_DIR/out/overnight/STOP" ] && { rm -f "$REPO_DIR/out/overnight/STOP"; NOTE_TARGET="$SYS_NOTES"; note "- 🛑 STOP ファイル検出。終了。"; break; }
  [ -f "$HALT" ] && { NOTE_TARGET="$SYS_NOTES"; note "- 🛑 HALT（赤 main 検出）。終了。"; break; }
  # 上限に達したら再取得しない。上限で着手しなかった issue は processed に入らないので、
  # 次の再取得で毎回「新着」に見え、何もしないラウンドを延々と回していた（2026-11-24 の夜に約2,100周）。
  if [ "$MAX_ISSUES" -gt 0 ] && [ "$(count_now)" -ge "$MAX_ISSUES" ]; then
    NOTE_TARGET="$SYS_NOTES"; note "- 🔢 着手上限（MAX_ISSUES=${MAX_ISSUES}）に達した。終了。"; break
  fi
  round_start_count="$(count_now)"

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

  # 保険: 1件も着手しないラウンドは、同じ割当で回し直しても進まない（上限・STOP 以外の理由でも空回りさせない）。
  if [ "$(count_now)" -eq "$round_start_count" ]; then
    NOTE_TARGET="$SYS_NOTES"; note "- 🌙 このラウンドは1件も着手しなかった。終了。"; break
  fi

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

# 昼の作業やサブエージェントが残した worktree のうち、PR がマージ済みで変更も使用中のプロセスも無いものを片付ける。
# 夜間分は上で都度消しているので、ここで拾うのは主に .claude/worktrees/agent-* の残骸（1個約400MB）。
( cd "$REPO_DIR" && bash tools/prune-worktrees.sh --apply ) >> "$LOG_DIR/prune-worktrees.log" 2>&1 || true

echo ""
echo "===================================="
echo "全処理終了。サマリ: $SUMMARY"
cat "$SUMMARY"
