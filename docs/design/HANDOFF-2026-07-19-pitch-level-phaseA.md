# 引き継ぎ — 設計書15 Phase A（1球単位化の土台）2026-07-19

> 別チャットで **設計書15 Phase A** に着手するための単一引き継ぎ文書。
> 先に `docs/design/design-15-pitch-level-tactics.md` 全文を読むこと（本書はその Phase A 部分の実務ガイド）。

## 0. 何をやるか（1行）

`AtBatResolver.ResolveDetailed` の密ループを**再開可能な `AtBatSession` ステッパ**に作り替え、`GameStepKind` に `Pitch` 境界を足す。**無指示なら結果は今日と1ビットも変わらない**を機械的に保証する。UIは一切変えない。

## 1. 絶対に守る不変条件（Phase A の合否）

- **バイト一致**: `directive=null`（無指示）で回した `AtBatSession` は、現行 `AtBatResolver.ResolveDetailed` と**打席結果・消費RNG数・球数まで完全一致**。
- **digestカード全一致**: 既存の決定論カード（`avg` / `tactics` / `modern` × seed 1..50）が**1ビット差ゼロ**で通る。※Phase A では PitchLog はまだ digest に載せない（載せるのは Phase B）。
- **yield は乱数を消費しない**: 1球境界の `yield return Pitch(...)` を挟んでも RNG 消費順は不変。batch `Play`（drain）と対話進行が同一結果。
- **UI無変化**: Phase A ではエンジン内部のみ。`unity/` は触らない。engine DLL 同期も不要（Bで必要になる）。

## 2. 最初に書くテスト（テストファースト）

`Session(null) == ResolveDetailed` の差分固定テストを最初の1本にする:
- 同一 `AtBatContext`・同一シードで、①現行 `ResolveDetailed` と ②`AtBatSession` を `directive=null` で回した結果を突き合わせ、**打席結果 enum・総球数・消費した RNG 状態（or 消費回数）**が一致することを assert。
- 多数の状況（走者・カウント初期値・打者/投手能力バリエーション）で回して差分ゼロを固定。

その後、既存の `EngineDeterminismGateTests`（`engine/KokoSim.Engine.Tests/Match/Game/`）が緑のままであることを確認。

## 3. 触るファイル（コード位置）

| 対象 | 場所 | 作業 |
|---|---|---|
| 打席解決の密ループ | `engine/KokoSim.Engine/Match/AtBat/AtBatResolver.cs:47`（`for pitch...`） | ローカル変数を state に昇格し `AtBatSession.ThrowNextPitch(directive, rng)` に分解。RNG消費順（配球→散布→打者判断→コンタクト→守備）は厳守 |
| 進行イテレータ | `engine/KokoSim.Engine/Match/Game/GameEngine.cs`：`PlayHalfSteps`(L206) / `Steps`(L111) / `Pa()`(L217) | 打席内で各投球前に `yield return Pitch(...)` を挟む。`Pa()` 境界は据え置き |
| GameStep 種別 | `GameEngine.cs:895`（`GameStepKind`・予約コメント L890/L106/L216） | `Pitch`（＋将来 `Timeout`）を追加 |
| 決定論カード | `engine/KokoSim.Engine.Tests/Match/Game/GameResultDigest.cs`（`DeterminismCards` L65-128） | 変更しない（回帰基準）。Phase A では Canonical も不変 |

## 4. 決定論の作法（設計書15 §3）

- **no-opゲート**: directive=null / `IPitchTacticsBrain` 非実装なら投球窓で RNG を1発も引かない。
- **Fork隔離**（Phase C で使用・A では準備のみ）: 1球判断の RNG は `rng.Fork(pitchStreamId)` で分離。
- **既定オフ係数**: 新プレーは係数0で RNG 非消費（`GameEngine.cs:333/363/666` の踏襲）。
- **温存**: バント/スクイズ/盗塁/牽制/敬遠は Phase A では**従来経路のまま**（統一は Phase D）。`AtBatSession` は「通常打席の投球ループ」だけを担う。

## 5. コマンド（メモリ [[dotnet-path]] [[test-workflow]]）

```bash
# dotnet が PATH に無ければ: export PATH="$PATH:/usr/local/share/dotnet"
dotnet test engine/ --filter "Category!=Heavy"            # 日常ループ（約10秒）
dotnet test engine/ -c Release --filter "Category=Heavy"  # 統計回帰（Release必須・約15秒）
```

Phase A 完了条件のチェック順: ①新規 `Session(null)==ResolveDetailed` 緑 → ②`Category!=Heavy` 全緑 → ③`Category=Heavy`（digestカード）全緑。1タスク=1コミットサイズ、完了前に必ず `dotnet test` を通す。

## 6. Phase A の後（このチャットでは着手しない）

B実データ露出（PitchRecord+Trajectory観測・digest再ベースライン） → C 1球采配（全球で采配ショートカット常駐・AI1球化・帯再校正） → D統一（バント等をステッパへ・帯再校正） → E弾道判定（将来・全帯再校正）。詳細は設計書15 §6。UIの采配ショートカットは Phase C 着手時に ASCII ワイヤーフレーム3案を出してから（UI-BUILD-METHOD.md / 原則⑦）。

## 7. 参照

- 設計: `docs/design/design-15-pitch-level-tactics.md`（§0 決定事項・§2 中核アーキテクチャ・§3 決定論・§6 フェーズ）
- 采配仕様: `docs/design/design-09-in-game-tactics.md`（サインは1球ごと無制限・§1 方針×1球指示の優先）
- 投球ループ内イベント: `docs/design/design-14-rule-completeness.md`（暴投/パスボール・乱数順の既定オフ）
- 未決の追決定: `docs/design/OPEN-QUESTIONS.md` **Q12**（全クローズ済み）
