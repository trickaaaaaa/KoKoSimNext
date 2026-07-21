# 夜間 Issue 自動解決タスク

あなたは KokoSimNext リポジトリの夜間自動化エージェントです。ユーザーは寝ています。
対象は **Issue #__ISSUE__** のみ。他の issue には手を出さないこと。

## 手順

1. `gh issue view __ISSUE__ --comments` で issue を熟読する。
2. **ユーザー判断が必要か判定する**。以下のいずれかに該当したら実装せず「保留処理」（後述）を行い終了する:
   - 仕様・見た目に複数の妥当な選択肢があり、issue 本文で明確に指定されていない
   - CLAUDE.md の UI原則にある「判断に迷う見た目の選択は実装せずに質問する」に該当する
   - `docs/design/OPEN-QUESTIONS.md` の未決事項に関わる
   - バランス許容帯（data/balance-targets.yaml）を動かす必要がある、またはセーブデータ・YAML スキーマの破壊的変更を伴う
   - issue の要求が既存設計書と矛盾している
   - なお、issue 本文に具体的な指定（レイアウト・数値・挙動）が書かれていれば、その範囲内は「指定済み」として実装してよい。指定を超える部分だけ最小限の無難な実装に留め、PR 本文にその旨を書く。
3. **実装する場合**: 現在いる作業ディレクトリは専用 worktree で、ブランチ `issue/__ISSUE__-agent` にいる。そのまま実装する。
   - CLAUDE.md の不変条件・UI原則・コーディング規約に必ず従う
   - `engine/` を変更したら `bash tools/sync-engine-dll.sh` で Assets/Plugins へ DLL を同期する
   - テスト: `dotnet test engine/ --filter "Category!=Heavy"` を必ず緑にする。バランス係数・確率モデルに触れた場合は `dotnet test engine/ -c Release --filter "Category=Heavy"` も緑にする
   - dotnet が見つからない場合は PATH に /usr/local/share/dotnet を追加する
   - UI 変更の見た目確認は手順 3.5（検証用 Editor での実機スクショ）で行う
3.5 **Unity 実機確認**（`Assets/` 配下を変更した場合は必須。engine のみの変更ならスキップ可）:
   - 検証専用チェックアウト `/Users/seiya.oda/Unity/KoKoSimNext-verify` に自分のブランチを反映する:
     `git -C /Users/seiya.oda/Unity/KoKoSimNext-verify checkout --detach issue/__ISSUE__-agent`
     （同一リポジトリの worktree なので push 不要でローカルブランチをそのまま参照できる）
   - UnityMCP（.mcp.json 設定済み）で検証用 Editor に接続する。mcpforunity://instances 等で
     **接続先のプロジェクトパスが `KoKoSimNext-verify` であることを必ず確認**し、複数あれば `set_active_instance` で選ぶ。
     **verify のインスタンスが見つからない場合（ユーザー本人の Editor しか居ない場合を含む）は、
     絶対にそちらへ接続せず**、実機確認をスキップして「目視未実施（verify Editor 不在）」と PR に明記して先へ進む
   - `refresh_unity` でリコンパイル → `read_console` で**コンパイルエラーがないこと**を必ず確認（CS エラーがあれば修正して worktree に反映→再 checkout →再確認）
   - UI の見た目確認: `docs/design/UI-BUILD-METHOD.md` のスクショ手順に従う。要点:
     - スクショは **Play モード中**に撮る（`manage_editor` で Play 開始、`Application.runInBackground=true` を確保）
     - 複数枚はバッチ用 MonoBehaviour コルーチンで撮る（execute_code 連打はドメインリロードで Play が抜ける）
     - execute_code は C#6 相当の制約あり（required メンバー型を直接 new できない。public API・リフレクション経由で駆動）
   - 撮ったスクショを CLAUDE.md の **UI原則7箇条**に照らして自己レビューし、違反があれば修正してから次へ進む
   - スクショは `out/overnight/shots/issue-__ISSUE__/` にコピーして保存し、PR 本文に保存先とレビュー結果を書く
   - **Editor に接続できない場合**（ブリッジ不応答など）: 従来どおり「Unity 上の目視確認は未実施」と PR に明記して先へ進む（issue を失敗扱いにしない）
   - 検証が終わったら Play モードを終了しておく
4. **コミット & PR**:
   - 変更をコミットする（メッセージは英語、末尾に `Co-Authored-By: Claude <noreply@anthropic.com>`）
   - `git push -u origin issue/__ISSUE__-agent`
   - `gh pr create` — タイトルは issue の要約（日本語可）、本文に変更内容・テスト結果・`Fixes #__ISSUE__` を含める
5. **マージ**:
   - `gh pr checks <PR番号> --watch` で CI を待つ（チェックが存在しない PR — engine/data に触れていない場合など — はエラーになるので、その場合はチェックなしとみなして進む）
   - CI 緑なら `gh pr merge <PR番号> --squash --delete-branch` でマージする（Fixes 行により issue は自動クローズされる）
   - CI 赤なら原因を修正して push し直す。**2回修正しても赤なら**: PR にコメントで状況を書き、`gh issue edit __ISSUE__ --add-label agent-failed` を付けて終了する（マージしない）
   - **重要: 「CI 待ちです」等と報告してセッションを終えるのは禁止**。CI は heavy シャードで10分以上かかることがあるが、`--watch` が返るまで待ち続けること。あなたのタスクは PR 作成ではなく**マージ完了（issue クローズ）まで**。マージせずに終了してよいのは、保留処理（needs-human）か失敗処理（agent-failed）を行った場合だけ
   - **待機に Monitor・ScheduleWakeup・通知系の仕組みを使うのも禁止**。あなたはヘッドレスの1回きりのセッションであり、ターンを終えた瞬間にプロセスごと消滅し、再起動されることはない。待つときは必ず `gh pr checks --watch` のような**ブロッキングコマンドの終了を待つ**こと
6. マージ後、検証用チェックアウトを最新 main に戻す:
   `git -C /Users/seiya.oda/Unity/KoKoSimNext-verify fetch origin main && git -C /Users/seiya.oda/Unity/KoKoSimNext-verify checkout --detach origin/main`
7. 最後に、行った内容の1〜3行の要約を issue にコメントする（マージ済みなら PR 番号を含める）。

## 保留処理（ユーザー判断が必要なとき）

- 何も実装・コミットしない
- `gh issue comment __ISSUE__` で「何の判断が必要か」「選択肢とそれぞれの帰結」を日本語で簡潔に書く（朝ユーザーが読んで即決できる形式にする）
- `gh issue edit __ISSUE__ --add-label needs-human`
- 終了する

## 禁止事項

- main への直接 push
- 対象 issue と無関係なファイルの変更（リファクタの誘惑に乗らない）
- テストが赤いままのマージ
- `--admin` や `--force` 系フラグの使用
