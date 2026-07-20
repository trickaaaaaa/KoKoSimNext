---
name: issue
description: KoKoSimNext の GitHub Issue を起票する。「issue立てて」「起票して」「バグ報告」「TODOをissue化」「これ後でやるからissueに」等で使用。gh CLI で trickaaaaaa/KoKoSimNext に日本語テンプレで作成し、設計書・不変条件・領域ラベルを紐づける。
---

# Issue 起票

`gh` CLI で `trickaaaaaa/KoKoSimNext` に Issue を作る。**本文は日本語、タイトルも日本語**（コードコメント規約と同じ）。

## 手順

### 1. 情報を集める（起票前に必ず）

会話や作業内容から以下を埋める。**足りない項目は推測で埋めず、ユーザーに1回だけまとめて聞く**。

- 種別（bug / enhancement / task / question / design）
- 領域（engine / unity / data / docs / balance）
- 該当設計書（`docs/design/design-NN-*.md`）と節番号 — 該当があれば必ず書く
- 再現手順・期待/実際（bug のとき必須）
- 完了条件（DoD）

コードに触れる話なら、関連ファイルを `grep`/`Read` で特定して `path:line` 形式で本文に入れる。曖昧な「〜のあたり」で起票しない。

### 2. ラベルを用意する

領域ラベルは無ければ作る（既存なら何もしない）:

```bash
gh label create area:engine  --color 1D3227 --description "engine/ 純C#エンジン" 2>/dev/null
gh label create area:unity   --color 264653 --description "unity/ UI・表示"      2>/dev/null
gh label create area:data    --color 5F6F52 --description "data/ YAML バランス"  2>/dev/null
gh label create area:docs    --color 0075CA --description "docs/design 設計書"   2>/dev/null
gh label create area:balance --color F5C64A --description "統計回帰・帯校正"     2>/dev/null
gh label create task         --color C5DEF5 --description "実装タスク"           2>/dev/null
```

種別ラベルは既存の `bug` / `enhancement` / `documentation` / `question` / `task` を使う。

### 3. 本文を書いて起票する

本文はヒアドキュメントでなく**ファイル経由**で渡す（改行・記号の事故防止）。スクラッチパッドに書いて `--body-file`。

```bash
gh issue create \
  --title "<日本語タイトル>" \
  --label "<種別>" --label "area:<領域>" \
  --body-file <scratchpad>/issue-body.md
```

作成後、返ってきた URL をユーザーに提示する。

## 本文テンプレ

### bug

```markdown
## 症状
<何が起きるか。1〜2行>

## 再現手順
1.
2.

## 期待 / 実際
- 期待:
- 実際:

## 関連
- 設計書: docs/design/design-NN-xxx.md §N
- コード: engine/KokoSim.Engine/.../Foo.cs:123
- 不変条件: #2 決定論 に抵触（該当する場合のみ）

## 完了条件
- [ ] 再現テストを追加して赤にする
- [ ] 修正して `dotnet test engine/ --filter "Category!=Heavy"` が緑
- [ ] （バランス影響あれば）`-c Release --filter "Category=Heavy"` が帯内
```

### enhancement / task

```markdown
## 目的
<なぜ必要か>

## やること
- [ ]
- [ ]

## 設計根拠
- docs/design/design-NN-xxx.md §N

## 完了条件
- [ ] 1機能=1テスト以上
- [ ] `dotnet test engine/ --filter "Category!=Heavy"` が緑
- [ ] （engine 変更時）`tools/sync-engine-dll.sh` で Unity へ反映
- [ ] （UI変更時）バッチスクショで UI原則7箇条 を自己レビュー
```

### design / question

未決事項は Issue の前に `docs/design/OPEN-QUESTIONS.md` に追記するのが原則（CLAUDE.md「進め方」）。
Issue 化する場合は OPEN-QUESTIONS の該当 Q番号を本文に必ず書き、逆に OPEN-QUESTIONS 側にも Issue URL を追記する。

## 禁止

- 中身が「あとで直す」だけの空 Issue を作らない。完了条件が書けないなら起票せず質問する
- 既存 Issue の重複を作らない。起票前に `gh issue list --search "<キーワード>" --state all` で確認する
- ユーザーが「起票して」と言っていないのに勝手に作らない（提案までに留める）
