#!/usr/bin/env bash
# Unity の Assets/ 配下（Assembly-CSharp / Assembly-CSharp-Editor）を **Editor を起動せずに**
# コンパイルし、error CS を検出する。約8秒。
#
# なぜ要るか（issue #349）: .github/workflows/ci.yml の発火条件は engine/ data/ global.json だけで、
# Assets/ 配下の C# はどの自動ゲートでもコンパイルされない。夜間エージェントの Unity 実機確認は
# ブリッジ不応答時にスキップできる仕様なので、Assets だけを触る変更は無検査で main に入れてしまう。
# 実際に PR #344 が using 漏れ（CS0103）のまま main に入り、Unity が丸ごとコンパイル不能になった。
#
# 仕組み: Unity が生成した Assembly-CSharp*.csproj を雛形として借り、Compile の**明示リストを
# グロブへ差し替えた**使い捨て csproj を作って dotnet build する。明示リストのままだと
# 「雛形を作った時点に無いファイル」を拾えず（新規追加ファイルが未検査）、逆に
# 「雛形にあって対象ブランチに無いファイル」で CS2001 の偽陽性が出る。
#
# 前提: このプロジェクトは asmdef を持たないので、Assets の .cs は次の2つにだけ割り振られる。
#   Assembly-CSharp        = Editor/ 配下**以外**のすべて
#   Assembly-CSharp-Editor = Editor/ 配下のすべて
# 加えて Assembly-CSharp を **UNITY_EDITOR 抜き**でもう一度通す（= Player ビルドと同じ条件）。
# （雛形の実測と件数が完全一致することを確認済み: 115件 / 13件）
#
# 使い方:
#   bash tools/check-unity-compile.sh                 # このリポジトリを検査
#   bash tools/check-unity-compile.sh /path/to/worktree   # 別の作業ツリーを検査
#   TEMPLATE_DIR=/path/to/unity-opened-project bash tools/check-unity-compile.sh <target>
#
# 終了コード: 0=緑 / 1=コンパイルエラー / 2=雛形 csproj が無い（検査不能・呼び出し側で判断する）
set -u

DOTNET="${DOTNET:-/usr/local/share/dotnet/dotnet}"
SELF_ROOT="$(cd "$(dirname "$0")/.." && pwd)"
TARGET="${1:-$SELF_ROOT}"
# 雛形は「Unity で一度開いたことのあるプロジェクト」にしか無い（*.csproj は .gitignore 済み）。
TEMPLATE_DIR="${TEMPLATE_DIR:-$SELF_ROOT}"

[ -x "$DOTNET" ] || { echo "dotnet が見つかりません: $DOTNET" >&2; exit 2; }
TARGET="$(cd "$TARGET" 2>/dev/null && pwd)" || { echo "対象ディレクトリがありません: ${1:-}" >&2; exit 2; }

rc=0
# 3パス目 "Assembly-CSharp-Player" は Assembly-CSharp を **UNITY_EDITOR 抜き**で通す
# （issue: 打球処理3D化 F8）。エディタ定義付きでしか通していないと、製品コードが
# `#if UNITY_EDITOR` の中に紛れていても手元では一切気づけず、**Player ビルドで初めて落ちる**。
# 実際に DuelStageRig の左右ミラー（左打者・左投手）一式が録画ハーネスの #if の中に
# 入っており、最初の Player ビルドで CS0103 になった。
for asm in Assembly-CSharp Assembly-CSharp-Editor Assembly-CSharp-Player; do
  # Assembly-CSharp-Editor は Assembly-CSharp を参照する。前者が落ちていると
  # 「KokoSim.Unity.Match が無い」等の派生エラーで溢れて本当の原因が埋まるので、
  # 先が赤なら Editor 側は回さない（原因だけを報告する）。
  if [ "$asm" != "Assembly-CSharp" ] && [ "$rc" -ne 0 ]; then
    echo "⏭ ${asm}: Assembly-CSharp が赤のため検査を省略（先に上を直す）"
    continue
  fi

  # Player パスは Assembly-CSharp の雛形を借りる（定義だけ差し替える）
  src="$asm"
  [ "$asm" = "Assembly-CSharp-Player" ] && src="Assembly-CSharp"
  tpl="$TEMPLATE_DIR/$src.csproj"
  if [ ! -f "$tpl" ]; then
    echo "雛形 csproj がありません: ${tpl}（Unity で一度プロジェクトを開くと生成されます）" >&2
    exit 2
  fi

  gate="$TARGET/$asm.unitygate.csproj"
  # ① Compile の明示リストと Analyzer 参照を落とす（Analyzer は絶対パス依存で環境差が出るため外す）
  # ② 代わりにグロブの ItemGroup を </Project> の直前へ差し込む
  # ③ HintPath の Library/PackageCache/... は**雛形プロジェクト側の絶対パスへ向ける**。
  #    パッケージの解決結果はプロジェクトごとの Library/ にあり、git 管理外なので
  #    素の worktree には存在しない。相対のままだと InputSystem や URP が見つからず
  #    「本当は緑なのに赤」になる（実際に CS0234 の偽陽性が出た）。
  #    Assets/ 配下（engine DLL 等）は**検査対象ブランチのもの**を使うので相対のまま残す。
  if [ "$asm" = "Assembly-CSharp-Editor" ]; then
    glob='    <Compile Include="Assets/**/Editor/**/*.cs" />'
  else
    glob='    <Compile Include="Assets/**/*.cs" Exclude="Assets/**/Editor/**/*.cs" />'
  fi
  # ④ Assembly-CSharp-Editor は Assembly-CSharp.csproj を ProjectReference しているが、
  #    その実体は git 管理外なので worktree には無い。ゲート用 csproj へ向け直す
  #    （向けないと KokoSim.Unity.Match 等が全滅して CS0234 の偽陽性になる）。
  # Player パスだけ UNITY_EDITOR 系の定義を落とす（Player ビルドと同じ条件でコンパイルする）
  strip_editor=0
  [ "$asm" = "Assembly-CSharp-Player" ] && strip_editor=1
  awk -v glob="$glob" -v tpldir="$TEMPLATE_DIR" -v strip="$strip_editor" '
    strip && /<DefineConstants>/ {
      gsub(/UNITY_EDITOR[A-Z0-9_]*;?/, ""); print; next }
    /<Compile Include=/ { next }
    /<Analyzer Include=/ { next }
    /<HintPath>Library\// { sub(/<HintPath>Library\//, "<HintPath>" tpldir "/Library/"); print; next }
    /<ProjectReference Include="Assembly-CSharp\.csproj"/ {
      sub(/Assembly-CSharp\.csproj/, "Assembly-CSharp.unitygate.csproj"); print; next }
    /<\/Project>/ { print "  <ItemGroup>"; print glob; print "  </ItemGroup>"; print; next }
    { print }
  ' "$tpl" > "$gate"

  out="$( cd "$TARGET" && "$DOTNET" build "$gate" -v q --nologo 2>&1 )"
  # dotnet は同じ診断を2回出す（restore 相当と build）ので重複を落とし、
  # 絶対パスと末尾の [.../*.csproj] を削って読める1行にする。
  errs="$(printf '%s\n' "$out" \
    | grep -E ': error [A-Z]+[0-9]+' \
    | sed -e "s#${TARGET}/##g" -e 's# \[[^]]*\.csproj\]$##' \
    | awk '!seen[$0]++' || true)"
  warns="$(printf '%s\n' "$out" | grep -Eo '[0-9]+ Warning\(s\)' | head -1 || true)"

  if [ -n "$errs" ]; then
    echo "🔴 ${asm}: コンパイルエラー $(printf '%s\n' "$errs" | wc -l | tr -d ' ') 件"
    printf '%s\n' "$errs" | sed 's/^/    /' | head -20
    rc=1
  else
    echo "✅ ${asm}: エラーなし（${warns:-warnings 不明}）"
  fi
done

# Editor 側が Assembly-CSharp.unitygate.csproj を参照するので、両方終わってから消す。
rm -f "$TARGET/Assembly-CSharp.unitygate.csproj" "$TARGET/Assembly-CSharp-Editor.unitygate.csproj" \
      "$TARGET/Assembly-CSharp-Player.unitygate.csproj"

exit "$rc"
