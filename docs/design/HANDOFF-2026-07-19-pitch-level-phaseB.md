# 引き継ぎ — 設計書15 Phase B（1球実データ露出＋digest再ベースライン）2026-07-19

> 別チャットで **設計書15 Phase B** に着手するための単一引き継ぎ文書。
> 先に `docs/design/design-15-pitch-level-tactics.md` 全文（特に §0/§0.1/§4/§6）を読むこと。

## 0. 前提（Phase A 完了済み・2026-07-19）

- `AtBatSession` ステッパ化完了（`Match/AtBat/AtBatSession.cs`）。`ResolveDetailed` は Session の drain に一本化。
- `GameStepKind.Pitch` 追加済み。通常打席は各投球前に `yield return Pitch()`（乱数非消費）。
- `MatchProgression` / `GameReplay` は Pitch 窓を読み飛ばし PA 添字不変。
- 検証済み: `Session==ResolveDetailed` 差分テスト緑（6マッチアップ×60シード＋スキル経路、結果enum・総球数・消費RNG数一致）／決定論カード（avg/tactics/modern×seed1..50）ベースライン無変更で緑／!Heavy 591緑・Heavy 20緑。
- バント/スクイズ/盗塁/牽制/敬遠は従来経路のまま温存（統一は Phase D）。

## 1. 何をやるか（1行）

`AtBatSession` が解いている**実1球データを `PitchRecord` として露出**し、架空合成 `PitchSequenceSynthesizer` を実データ供給に置換、UIの1球B/S表示を本物にする。**digest に PitchLog を載せて一度だけ再ベースライン**。統計帯は動かさない。

## 2. 絶対に守る不変条件（Phase B の合否）

- **帯不変**: 得点分布・全統計（`balance-targets.yaml`）は Phase A と完全一致。1球データの露出・弾道計算は**観測専用**で、RNG消費順を1発も変えない（design-12「観測は試合結果を1ビットも変えない」）。
- **digest は一度だけ変わる**: `GameResultDigest.Canonical` に PitchLog を追加した時点でハッシュ値は変わる（想定内）。**Canonical 変更とピン済みハッシュのスナップショット再取得を同一コミットに束ねる**。以降は固定。「同シード→同ハッシュ／batch==manual／中断再開==全消化」の3ゲートは新ハッシュで成立し続けること。
- **Trajectory は RNG を引かない**: `PitchSimulator`/`BallisticIntegrator` は決定論的積分（乱数不使用）。毎球呼んでも主RNGに触れないことをテストで固定。
- **PitchRecord は解決済みの値の写し**: 新たな抽選をしない。ループ内で既に計算された球種/狙い/着弾/球速/PitchKind/カウント後をそのまま記録する。

## 3. 作業ブロック（この順で）

### B-1: PitchRecord 露出（engine）
- `PitchRecord` 型を新設: `Kind(PitchKind) / BallsAfter / StrikesAfter / PitchType / LocationX / LocationY / VelocityKmh / Trajectory?`（設計書15 §4・Q12-5で確定）。
- `AtBatSession.ThrowNextPitch` が1球分を記録し、`AtBatResult` 経由で `PlayLogEntry.PitchLog: IReadOnlyList<PitchRecord>?` に載せる。
- 敬遠（打席スキップ）・バント/スクイズ（従来経路）は PitchLog=null または空で良い（Phase D で統一）。null 経路が既存挙動を壊さないこと。

### B-2: Trajectory 観測（engine）
- デッドコード `PitchSimulator`/`PitchTrajectory`/`PitchSpec`（`Match/Pitching/`、現状テスト参照のみ）を `AtBatSession` から毎球呼び、`PitchRecord.Trajectory` に載せる。
- **コスト検証**: Balance CLI（10000試合級）で毎球積分の実測時間を取り、シミュレーション速度が許容外なら「詳細モード（観戦時）のみ Trajectory を積分し、バッチでは null」に切る。判断材料を報告に残す。

### B-3: digest 再ベースライン（Tests）
- `GameResultDigest.Canonical` に PitchLog を追加（1球を `P {kind}{ballsAfter}-{strikesAfter} {type} v{velocity}` 等の正規化行で。Trajectory はハッシュに**含めない**＝コスト切替（B-2）でハッシュが変わらないように）。
- ピン済みハッシュを再スナップショット。**同一コミット**で。

### B-4: 合成器の置換（engine 観測seam）
- `MatchProgression.LivePlateAppearance.PitchSeq` の供給元を `PitchSequenceSynthesizer`（架空合成）から `PlayLogEntry.PitchLog`（実データ）へ差し替え。既存の `PitchToken(PitchKind, BallsAfter, StrikesAfter)` 形は維持すると UI 改修が最小。
- Synthesizer は PitchLog が無い経路（バント等・旧セーブ互換が要るなら）のフォールバックとして残すか、削除するか判断して報告。

### B-5: UI接続（unity）
- `tools/sync-engine-dll.sh` で engine DLL を Plugins へ反映（**忘れると CS0117**）。
- `MatchLiveController` は既に `PitchSeq.Pitches` で1球点灯を実装済み → データ源が本物になるだけで表示コードはほぼ不変のはず。表示上の差異（実データはファウル連発・9球粘り等が出る）を確認。
- 検証はバッチスクショ（UIScreenshot.Capture・Play中＋runInBackground必須）で試合ライブ画面を撮り、B/S点灯が実カウントと整合することを目視自己レビュー。**新しい見た目は作らない**（データ差し替えのみ。見せ方の変更・追加表示は指示がない限りしない）。

## 4. テスト（テストファースト）

1. **PitchLog 整合**: 任意シードで、PitchLog の並びから再構成した最終カウント・総球数・PitchKind が `AtBatResult`（結果enum・Pitches）と一致。ファウルの2ストライク後挙動（ストライク数が増えない）も実データで自然に成立していることを固定。
2. **観測ゼロ影響**: PitchLog/Trajectory の記録を ON/OFF しても `GameResult` の勝敗・スコア・全統計が不変（design-12 の `Capture_DoesNotAffectGameOutcome` と同型）。
3. **Trajectory 無乱数**: 毎球積分の前後で RNG 状態が不変。
4. **digest 3ゲート**: 新ハッシュで「同シード→同ハッシュ／batch==manual／中断再開==全消化」緑。
5. Heavy（統計帯）全緑＝帯不変の実証。

## 5. コマンド

```bash
# dotnet が PATH に無ければ: export PATH="$PATH:/usr/local/share/dotnet"
dotnet test engine/ --filter "Category!=Heavy"            # 日常ループ（約10秒）
dotnet test engine/ -c Release --filter "Category=Heavy"  # 統計回帰（Release必須・約15秒）
dotnet run --project engine/KokoSim.Balance -- simulate --games 10000 --seed 42 --report out/report.md  # B-2コスト実測
bash tools/sync-engine-dll.sh                             # engine変更をUnityへ反映（B-5前に必須）
```

## 6. Phase B の後（このチャットでは着手しない）

C 1球采配（`IPitchTacticsBrain`・AI1球化・全球で采配ショートカット常駐・**帯再校正**。UIはASCIIワイヤーフレーム3案→選択後） → D統一（バント等をステッパへ・帯再校正） → E弾道判定（将来・全帯再校正）。詳細は設計書15 §6。

## 7. 参照

- 設計: `docs/design/design-15-pitch-level-tactics.md`（§4 観測seam・§3.2 digest・§0.1 Q12決定）
- Phase A 引き継ぎ（完了済み・前提知識）: `docs/design/HANDOFF-2026-07-19-pitch-level-phaseA.md`
- 観測の原則: `docs/design/design-12-play-representation.md`（観測は結果を変えない・PlayLogEntry の digest 不変ゾーンの前例）
- 合成器の現物: `Match/Timeline/Playback/PitchSequence.cs`（冒頭コメントに「架空」と明記）
