#!/bin/bash
# UIファイル(.uss/.uxml、Assets配下の.cs)を編集したら、UI原則とUI-BUILD-METHODの参照を再注入する。
# CLAUDE.md「UI原則」7箇条と docs/design/UI-BUILD-METHOD.md を毎回リマインドするための PostToolUse hook。
f=$(jq -r '.tool_input.file_path // .tool_response.filePath // empty' 2>/dev/null)
case "$f" in
  *.uss|*.uxml|*/Assets/*.cs)
    msg="【UIリマインド】UIファイルを編集中です。着手・修正の前に必ず docs/design/UI-BUILD-METHOD.md（作り方の正典）と CLAUDE.md「UI原則」7箇条の両方に従うこと。\n要点(7箇条): ①密度は正義・整列で捌く(行高32px基準) ②アクセント#F5C64Aは1画面3箇所まで/警告#E86A4Aは本当の警告だけ ③数値は右揃え・桁揃え・装飾(影/グロー/グラデ)禁止 ④区切りは余白と背景色で・線は最小限 ⑤色/サイズ/余白はtokens.ussの変数のみ・新規見た目は部品辞書に足してから使う ⑥状態(調子/ランク/疲労怪我)は色と表情で一目 ⑦主要操作は1画面1つ大きく。\n手順: 新画面はいきなり組まずASCIIワイヤー3案を提示→選択待ち。HTMLモックは参照専用(機械移植しない)。完成後はバッチスクショで7箇条を自己レビューしてから提出。USS落とし穴(gap/grid非対応・UXMLコメントの--禁止・flex-shrink:0等)はUI-BUILD-METHOD.md末尾を確認。"
    # additionalContext としてモデル文脈へ注入
    jq -n --arg c "$msg" '{hookSpecificOutput:{hookEventName:"PostToolUse",additionalContext:$c}}'
    ;;
esac
