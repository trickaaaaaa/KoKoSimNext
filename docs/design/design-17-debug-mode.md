# 設計書17 — デバッグモード（試合検証基盤）

> 本書は、**開発者（人間とAIエージェントの両方）が試合の内部を観測・再現・操作するための基盤**を定義する。
> 対象は主に試合（1球単位）だが、再現基盤はゲーム全体（シーズン・大会）に効く。
>
> 設計思想の背骨は一言で: **「デバッグは観測・再現・注入の3つに分解でき、そのうち観測と再現は結果を1ビットも変えない」**。
> 観測（トレース・HUD）と再現（シード・シーク）は既存の決定論をそのまま使うので**帯もdigestも不変**。
> 唯一結果を変えるのが注入（場面ジャンプ・強制発動）で、これは**デバッグビルド専用の閉じた経路**に隔離する。
>
> この分解は設計書12「観測は試合結果を1ビットも変えない」／設計書15「無指示なら結果は今日と1ビットも変わらない」の
> **デバッグ版**であり、同じ不変条件（#2 決定論・#5 統計回帰）の上に乗る。

関連: design-12（観測seam・PlayTimeline）, design-14（未実装プレー＝強制発動の検証対象）, design-15（1球単位・AtBatSession・GameStep.Pitch）, design-05（大会・裏試合）, design-16（UIリスタイル＝デバッグHUDは対象外）。

---

## 0. 決定事項（本書起票時に確定）

| 論点 | 決定 | 含意 |
|---|---|---|
| **観測データの置き場** | HUDとJSONLで**同一レコード（`PitchTrace`）を共有** | 表示用と出力用を二重実装しない。engine が1つの観測レコードを吐き、Unityが描き、CLIが書く |
| **観測のコスト** | `GameContext.CaptureTrace` 既定 **false**＝統計シムはゼロコスト | `CaptureTimelines`（design-12）と同型のゲート。帯もdigestも触らない |
| **engineのIO** | engine は `IDebugTraceSink` に**渡すだけ**。ファイル書き込みは Balance CLI / Unity 側 | 不変条件#3（エンジン純度）。IOは注入 |
| **強制発動（チート）** | **抽選をスキップして結果を固定**する方式。RNG消費が変わることを許容し、**デバッグビルド専用経路**へ隔離 | 「抽選結果の差し替え」は物理層を殺す（不変条件#1違反）ので採らない。強制した試合は digest 対象外・トレースに `forced:true` を刻む |
| **リリース混入防止** | Unity側は `KOKOSIM_DEBUG` scripting define で丸ごと落とす。engine側はフラグ既定offで常時コンパイル | engine に `#if` を持ち込まない（純度・テスト容易性） |
| **デバッグHUDとUI原則** | **7箇条の適用対象外**（等幅・高密度・情報最優先）。ただし `tokens.uss` の色変数は使う | 製品UIではない。design-16 のリスタイル対象にも含めない |
| **MCPからの操作** | `DebugBridge` の **static メソッド＋文字列(JSON)入出力のみ** | `execute_code` は C#6 相当で `required` メンバー型を new できない制約がある。プリミティブと文字列だけなら確実に叩ける |

---

## 1. 現状の穴（実測・2026-07-21）

| # | 穴 | 根拠 |
|---|---|---|
| 1 | **試合が再現できない**。母種が毎起動 `DateTime.UtcNow.Ticks` 由来 | `Assets/KokoSim/Shell/GameSeed.cs:20` |
| 2 | 大会の実戦経路（Fork注入）は**中断保存できない** | `MatchProgression.cs:105`（`_seedable=false`）→ `Save()` が例外 `:132` |
| 3 | 1球の**内部値を計算しているのに捨てている**。意図球種/コース（`PitchPlan`）、スイング確率（`BatterDecision.SwingProbability`）は `PitchRecord` に残らない | `Match/Pitching/PitchPlan.cs:9`, `Match/Batting/BatterDecision.cs:18`, `Match/AtBat/PitchRecord.cs:19` |
| 4 | engine の出力は **Markdown のみ**。機械可読なトレースが無い | `engine/KokoSim.Balance/Program.cs:51,244,292` |
| 5 | **狙った場面を作れない**。9回裏2死満塁も、振り逃げも、暴投も、出るまで回すしかない | 該当機構なし |
| 6 | Unity 側のデバッグ手段は**画面ごとに書き捨てのシューターMonoBehaviour**（8本）。都度スクリプトを書く必要がある | `Assets/KokoSim/Match/MatchLive*Shooter.cs` 他 |
| 7 | **RNG消費のトレースが無い**。決定論が壊れたとき「どこで乱数順が変わったか」を二分探索で探すしかない | 該当機構なし |

**設計的含意**: 1〜2（再現）と 3〜4（観測）は既存の決定論資産（`GameReplay`・`Xoshiro256Random`・`CaptureTimelines`）の**素直な延長で取れる**。
5〜6（注入・操作）だけが新規の面であり、そこにリリース混入のリスクが集中する。よってフェーズは **再現 → 観測 → 操作** の順に積む。

---

## 2. 中核アーキテクチャ — 4層

```
 ┌────────────────────────────────────────────────────────┐
 │ L3 提示・操作                                            │
 │   Unity: DebugOverlay(F1 HUD) / DebugMenu               │
 │   MCP  : DebugBridge (static + JSON文字列)               │  ← KOKOSIM_DEBUG でのみ存在
 ├────────────────────────────────────────────────────────┤
 │ L2 注入                                                 │
 │   ScenarioBuilder (data/debug-scenarios.yaml)           │
 │   DebugDirective  (強制発動)                             │  ← 結果を変える唯一の層
 ├────────────────────────────────────────────────────────┤
 │ L1 観測                                                 │
 │   PitchTrace / PaTrace / GameTraceHeader                │
 │   IDebugTraceSink → JSONL / リングバッファ                │  ← 結果を1ビットも変えない
 ├────────────────────────────────────────────────────────┤
 │ L0 再現                                                 │
 │   RngState capture/restore, GameSaveState(pitch粒度)    │
 │   ReproToken "k1:<state>:<pa>:<pitch>:<fixture>"        │  ← 既存 GameReplay の拡張
 └────────────────────────────────────────────────────────┘
```

**依存は下向きのみ**。L0/L1 は `engine/` に置き（Unity非依存）、L2 の YAML パースも engine。L3 だけが Unity。

---

## 3. 機能A — 再現基盤＋場面ジャンプ

### 3.1 母種の露出と固定

- `GameSeed.Master` を**デバッグメニューに表示**し、コピー／貼り付けで `GameSeed.Reset(ulong)` できるようにする。
- 起動時ログに1行 `[KokoSim] master seed = 0x....` を必ず吐く（デバッグビルドのみ）。バグ報告に貼れば再現できる状態にする。

### 3.2 Fork経路の中断保存（穴#2の解消）

`Xoshiro256Random` は 4×64bit の状態しか持たないので、**状態そのものを保存**すれば Fork 由来でも復元できる。

```csharp
// engine/KokoSim.Engine/Core/Xoshiro256Random.cs
public ulong[] CaptureState();              // 4要素のコピー
public static Xoshiro256Random FromState(ulong[] state);
```

- `IRandomSource` には足さない（実装差し替えの自由度を残す）。`MatchProgression` は受け取った `IRandomSource` が
  `Xoshiro256Random` なら**開始時点の状態を控える**（そうでなければ従来どおり `Save()` 不可のまま）。
- `GameSaveState` を拡張:

```csharp
public sealed record GameSaveState(ulong Seed, int ConfirmedPlateAppearances)
{
    public IReadOnlyList<GameDecision> Decisions { get; init; } = Array.Empty<GameDecision>();
    public ulong[]? RngState { get; init; }        // 追加: 非null なら Seed より優先して復元
    public int ConfirmedPitchesInCurrentPa { get; init; }  // 追加: 打席途中まで復元（機能D のシークで使う）
}
```

- `GameReplay.Restore` は `RngState != null` ならそこから `Xoshiro256Random.FromState` で開始する。既存セーブ（`RngState == null`）は完全互換。

### 3.3 再現トークン

1本の文字列で「この試合のこの場面」を指す。デバッグHUDに常時表示＋クリップボードコピー。

```
k1:<rngStateHex(64桁)>:<pa>:<pitch>:<fixtureFp(8桁)>
例) k1:9f3a...:37:4:a1b2c3d4
```

- `fixtureFp` = 対戦カード指紋（両校ID・選手ID列・`GameContext` の主要係数のSHA256先頭8桁）。
  **トークンだけでは選手データまでは復元できない**ので、貼り付け時に指紋を照合し、不一致なら「別のロスターです」と警告する（黙って違う試合を再生しない）。
- 貼り付け → その場面まで再生してHUDごと復帰する。

### 3.4 場面ジャンプ（シナリオ）

**`data/debug/scenarios.yaml`**（OPEN-QUESTIONS Q18-2 確定）に「状況」を宣言的に置き、そこから直接 `MatchProgression` を組んで `MatchLive` を起動する。

`data/debug/` は**デバッグ専有ディレクトリ**として予約する（将来の固定ロスター・再現ケース集もここ）。ゲームデータ（`data/*.yaml`）と
混ざらないよう1階層切り、**リリースビルドからはディレクトリ単位で除外**する（§8）。除外される前提なので、
**ディレクトリが無い場合はシナリオ0件で正常起動し、例外を投げない**。

```yaml
scenarios:
  - id: bases-loaded-9th
    name: 9回裏2死満塁・1点ビハインド
    away: { school: "AI:tier=A", score: 4 }
    home: { school: "player", score: 3 }
    inning: 9
    top: false
    outs: 2
    bases: [1, 2, 3]          # 占有塁
    count: { balls: 3, strikes: 2 }
    batter: 4                 # 打順（1始まり）
    pitcher_fatigue: 120      # 当該投手の当日球数
    modern_rules: { dh: false, tiebreak: true }
    seed: 20260721

  - id: tiebreak-10th
    name: タイブレーク10回表・無死一二塁
    inning: 10
    top: true
    outs: 0
    bases: [1, 2]
    modern_rules: { tiebreak: true }

  - id: wild-pitch-lab
    name: 暴投検証（3ボール・走者三塁）
    inning: 5
    outs: 1
    bases: [3]
    count: { balls: 3, strikes: 0 }
    force: wild_pitch          # 機能D と併用
```

- 実装は `ScenarioBuilder.Build(scenarioId, ScenarioCatalog, RosterSource) → (Team away, Team home, GameContext ctx, GameProgress seeded)`。
- **注入で作った試合は digest 対象外**（開始状態が baseline と違うため）。トレースヘッダに `scenario: "<id>"` を刻む。
- `school: "AI:tier=A"` のように**その場で生成**もできる（`EnemyAiFactory`／既存の学校生成を再利用）。

---

## 4. 機能C — 構造化トレース（engine JSONL） ※実装順ではF1

> 機能Bより先に書くのは、**HUDがこのレコードを読むから**（0. の決定事項）。

### 4.1 観測レコード

```csharp
// engine/KokoSim.Engine/Debug/DebugTrace.cs
public readonly record struct PitchTrace(
    int Inning, bool IsTop, int Outs, string BatterId, string PitcherId,
    int BallsBefore, int StrikesBefore, int PitchNoInPa, int PitchNoInGame,
    // 意図（PitchPlan）
    PitchType PlanType, double PlanAimX, double PlanAimY, double PlanVelocityKmh, double PlanStuff,
    // 実着弾（ControlScatter後）＋弾道（PitchTrajectory）
    double ActualX, double ActualY, double ActualKmh,
    double FlightTimeSeconds, double InducedVerticalBreakM, double InducedHorizontalBreakM,
    // 打者判断（BatterDecision）
    double SwingProbability, bool Swung, bool InZone,
    // 結果
    PitchKind Kind,
    // 打球（InPlayのときのみ）
    double? ExitVelocityKmh, double? LaunchAngleDeg, double? SprayAngleDeg,
    // 状態
    int PressureIndex, bool Rattled, int PitchingFatigue, PitcherGear Gear, PitchPolicy Policy,
    // 采配（AI/委任が動いたとき）
    string? ChosenSign, string? SignCandidatesCsv,
    // RNG
    ulong RngStreamId, int RngDrawsInPitch,
    // 注入
    bool Forced);
```

- `PaTrace`（打席1件＝結果・打点・走者進塁・`PlayTimeline` 要約）と `GameTraceHeader`（母種／RNG状態／fixture指紋／`GameContext` 主要係数／シナリオID）も同時に定義する。
- **`PitchRecord` は触らない**（determinism digest の正規化対象なので、拡張すると再ベースラインが要る）。`PitchTrace` は
  `PlayLogEntry.Traces`（`CaptureTrace` 時のみ非空）に別枠で持ち、digest からは `Trajectory` と同様に除外する（`PlayLogEntry.cs:37` と同じ扱い）。

### 4.2 シンクと有効化

```csharp
public interface IDebugTraceSink
{
    void OnGameStart(GameTraceHeader header);
    void OnPitch(in PitchTrace t);
    void OnPlateAppearance(in PaTrace t);
    void OnGameEnd(GameResult result);
}
```

- `GameContext.CaptureTrace { get; init; }`（既定 false）＋ `GameContext.TraceSink`（既定 null）。
  両方揃ったときだけ観測が走る。既定パスは**分岐1回**のコストしか払わない。
- 実装:
  - `JsonlTraceSink`（Balance CLI 側・`System.Text.Json`）
  - `RingBufferTraceSink`（Unity 側・直近N球をメモリ保持。HUDが読む。既定 N=64）

### 4.3 JSONL スキーマ（1行1イベント）

```jsonl
{"t":"game","seed":"0x9f3a...","rng":"9f3a...","fixture":"a1b2c3d4","away":"県立A","home":"県立B","scenario":null,"coeff":"data/coefficients.yaml@sha:1122"}
{"t":"pitch","i":9,"top":false,"o":2,"b":"P0123","p":"P0456","cnt":"3-2","n":7,"N":118,
 "plan":{"ty":"Slider","ax":0.12,"ay":0.55,"kmh":128.4,"stuff":62},
 "act":{"x":0.31,"y":0.22,"kmh":127.9,"ft":0.441,"ivb":0.11,"ihb":-0.18,"zone":false},
 "bat":{"pSw":0.62,"sw":true},
 "res":{"k":"SwingingStrike"},
 "st":{"press":41,"rat":false,"pf":88,"gear":"Normal","pol":"Aggressive"},
 "ai":{"sign":"Steal","cand":"Steal:0.61,Normal:0.55,Bunt:0.12"},
 "rng":{"sid":9214,"d":14},"forced":false}
{"t":"pa","i":9,"top":false,"res":"Strikeout","rbi":0,"outsAfter":3,"pitches":7}
{"t":"end","away":4,"home":3,"innings":9}
```

- キーは短縮（1試合で~300行×数百試合を扱うため）。**フィールド名は本節を単一ソースとする**。
- `jq` で切れることを要件にする（例: `jq -c 'select(.t=="pitch" and .bat.pSw>0.8 and .res.k=="Ball")' trace.jsonl` ＝「振るはずが見送った球」）。

### 4.4 Balance CLI サブコマンド

```bash
# 1試合のフルトレース
dotnet run --project engine/KokoSim.Balance -- trace --games 1 --seed 42 --out out/trace.jsonl

# シナリオから
dotnet run --project engine/KokoSim.Balance -- trace --scenario bases-loaded-9th --out out/trace.jsonl

# 100試合ぶんを集計向けに（pitchのみ）
dotnet run --project engine/KokoSim.Balance -- trace --games 100 --seed 42 --only pitch --out out/pitches.jsonl

# 2つのトレースの差分要点（係数変更の影響を見る）
dotnet run --project engine/KokoSim.Balance -- trace-diff --a out/before.jsonl --b out/after.jsonl --report out/diff.md
```

`trace-diff` は「最初に食い違った球（イニング・カウント・フィールド名）」と分布サマリ差（球種比・スイング率・ゾーン率・平均初速）を出す。
**回帰の原因特定を二分探索から1コマンドに落とす**のがこのサブコマンドの存在理由。

### 4.5 RNG消費トレース

- `IRandomSource` に**カウンタ用のデコレータ** `CountingRandomSource : IRandomSource` を用意し、`CaptureTrace` 時のみ包む。
  `Fork` は包んだまま子を返し、`(streamId, draws)` を集計する。**包まない既定パスは一切変わらない**。
- 決定論が壊れたときは `trace-diff` が「乱数消費数が食い違った最初の球」を指す。

---

## 5. 機能B — 試合デバッグHUD（Unity）

`KOKOSIM_DEBUG` 時のみ `MatchLive` / `MatchDetail` に常駐する UI Toolkit オーバーレイ。**F1 でトグル**。

- データ源は `RingBufferTraceSink`（§4.2）。HUD は表示専用で、**engine を1回も追加で呼ばない**。
- レイアウトは実装前に**ASCIIワイヤーフレーム3案を提示**してから作る（CLAUDE.md「UI作業の定型」）。以下は内容の要件のみ。

| ペイン | 内容 |
|---|---|
| **P1 今の球** | 意図（球種/コース/球速/キレ） vs 実着弾、散布量、ホップ・横変化・到達時間、ゾーン内外 |
| **P2 打者判断** | スイング確率、実際のスイング、チェイスか、コンタクト結果（初速/角度/方向） |
| **P3 状態** | PressureIndex, Rattled, 投手球数・スタミナ, ギア, 配球方針, 調子, 主要能力の実効値 |
| **P4 采配** | 直近で `ITacticsBrain` が選んだサインと候補スコア上位3（AI校の思考の可視化） |
| **P5 再現** | 再現トークン、母種、fixture指紋、コピーボタン、RNG消費数 |
| **P6 球ログ** | 直近64球のテーブル（等幅・1行1球・`PitchTrace` の主要列） |

- **色は状態コード**として使う（design-16 の掲示板文法ではなく、`tokens.uss` の `--rank-*` / `--alert` をそのまま使う）。
- P4 は `ITacticsBrain` が候補スコアを返さないと出せないため、`TacticsDecision` に**観測専用の候補スコア配列**を足す（`CaptureTrace` 時のみ埋める）。

---

## 6. 機能D — 強制発動＋シーク

### 6.1 強制発動（DebugDirective）

```csharp
public enum ForcedOutcome
{
    None,
    // 打席結果
    HomeRun, Triple, Double, Single, Strikeout, Walk, HitByPitch, GroundOut, FlyOut,
    // 稀プレー（design-14 の検証対象）
    WildPitch, PassedBall, DroppedThirdStrike, FieldersChoice, Error, DoublePlay, TriplePlay,
    IntentionalWalk, Squeeze, Balk,
    // 走塁
    StealSuccess, StealCaught, PickoffOut, DoubleSteal,
    // 試合状況
    EnterTieBreak, WalkOff,
}
```

- 適用は「次の1球」または「次の1打席」の**一度きり**（1球采配の override と同型）。
- **実装方針**: 該当の抽選を丸ごとスキップし、結果を固定して以降のパイプライン（走者進塁・記録・タイムライン生成）は通常どおり流す。
  → **打球の物理層を偽装しない**（不変条件#1）。「本塁打を強制」は「本塁打になる `BattedBall` を注入する」ではなく
  「打席結果を HomeRun として確定させ、タイムラインは代表値で作る」。物理層の検証に使うものではなく、**下流（表現・記録・UI）の検証**に使う道具だと明示する。
- 強制した試合は `GameResult.HasForcedOutcomes = true` を立て、digest／統計集計から**自動的に除外**する。

### 6.2 シーク

- **任意PAへ**: 既存 `GameReplay.Restore`（先頭から再生）で実現済み。UIから叩けるようにするだけ。
- **1球戻る／任意の球へ**: `GameSaveState.ConfirmedPitchesInCurrentPa`（§3.2）を使い、同じ再生機構を pitch 粒度へ拡張。
- **早送り**: N打席 / このイニング終了まで / 試合終了まで。既存 `FinishRemaining()` を分割する。
- 再生コストは1試合ぶんの再シミュレーション（数ms〜）で、体感上問題にならない見込み。実測をDoDに含める。

---

## 7. MCP デバッグAPI（DebugBridge）

`Assets/KokoSim/Debug/DebugBridge.cs`（`#if KOKOSIM_DEBUG || UNITY_EDITOR`）。

**制約**: `mcp__UnityMCP__execute_code` は C#6 相当のコンパイラで動き、`required` メンバーを持つ型を `new` できない。
よって **全メソッドを static、引数と戻り値をプリミティブ＋文字列(JSON)に限定**する。これで `execute_code` から1行で叩ける。

```csharp
public static class DebugBridge
{
    // 一覧・起動
    public static string ListScenarios();                             // → {"ok":true,"scenarios":[{"id","name"}...]}
    public static string StartMatch(string scenarioId, string seedHex);
    public static string Restore(string reproToken);

    // 進行
    public static string AdvancePitch(int n);                         // → 進めたぶんの PitchTrace 配列
    public static string AdvancePa(int n);
    public static string AdvanceUntil(string what);                   // "inning-end" | "game-end" | "next-runner-on"
    public static string SeekTo(int pa, int pitch);

    // 注入
    public static string Force(string forcedOutcome);                 // "WildPitch" 等（enum名の文字列）
    public static string SetBattingOverride(string sign);
    public static string SetDefenseOverride(string policy, string gear);

    // 観測
    public static string DumpState();                                 // スコア/走者/守備位置/打者投手/カウント
    public static string DumpTrace(int lastN);                        // JSONL文字列
    public static string DumpSnapshot();                              // MatchLiveSnapshot の JSON

    // 画面
    public static string Screenshot(string path);                     // 既存 ScreenshotCapture 経由
    public static string ToggleHud(bool on);
    public static string Goto(string screenName);                     // ScreenRouter 経由
}
```

- 戻り値は**必ず単一のJSON文字列**。失敗は `{"ok":false,"err":"..."}`。例外を投げない（`execute_code` からスタックトレースを読むのが高コストなため）。
- 進行系は Play モード中のみ有効。Play でないときは `{"ok":false,"err":"not-playing"}` を返す。
- `KokoSim/Debug/...` の Editor メニューから同じ関数を叩けるようにし、人間も同じ経路を使う（**経路を二重化しない**）。

---

## 8. 安全装置と不変条件

| 不変条件 | 本設計での担保 |
|---|---|
| **#1 二層構造** | 強制発動は物理層を偽装せず「打席結果の確定」だけを行い、用途を下流検証に限定（§6.1） |
| **#2 決定論** | 観測・再現は乱数を1回も追加消費しない。`CountingRandomSource` は既定パスで存在しない。強制発動のみ消費順を変えるが専用フラグで隔離 |
| **#3 エンジン純度** | engine には `IDebugTraceSink` の口だけ。ファイルIOもUnity参照も持ち込まない |
| **#4 データ駆動** | シナリオは `data/debug/scenarios.yaml`。C#にハードコードしない |
| **#5 統計回帰** | `CaptureTrace` off の帯は不変。DoDで Heavy 緑を確認 |

**リリース混入防止**（OPEN-QUESTIONS Q18-1 確定・2026-07-21）:

方針は **「観測は残す、操作は消す」**。

| 要素 | リリースビルド | 根拠 |
|---|---|---|
| **母種ログ 1行** `[KokoSim] master seed = 0x...` | **残す** | 読み取り専用の数字＝操作経路にならない。バグ報告からその人のプレイを丸ごと再現できる価値が大きい |
| `GameSeed.Reset(ulong)`（母種の固定） | 消す | 表示と固定は別物。固定できると任意のシードを引き直せてしまう |
| `DebugBridge` / HUD / Editorメニュー | 消す | 操作面 |
| シナリオ注入 / 強制発動 | 消す | 結果を変える層 |
| **`data/debug/` ディレクトリ**（シナリオYAML等） | 消す | Q18-2 確定。ディレクトリ単位で同梱除外。欠損は正常系（シナリオ0件で起動） |

- 実装: 母種ログは `GameSeed.InitMaster()` 直後、`KOKOSIM_DEBUG` の**外**（define非依存）に置く。出力先は `Debug.Log`（Player.log に残る）。
  それ以外は `KOKOSIM_DEBUG` scripting define（Development Build と Editor のみ）で**型ごと消える**。
- engine: フラグ既定 off で常時コンパイル（`#if` を持ち込まない）。`CaptureTrace=true` を製品コードから立てる箇所が無いことをテストで固定する。

---

## 9. フェーズ計画とDoD

> **状態: F0〜F4 すべて完了（2026-07-21）**。実装での逸脱と実測値は §12 を参照。

| # | フェーズ | 内容 | 結果への影響 | 状態 |
|---|---|---|---|---|
| **F0** | 再現基盤 | 母種の露出・固定・起動ログ、`Xoshiro256Random.CaptureState/FromState`、`GameSaveState.RngState`、再現トークン、Fork経路の `Save()` 解禁 | なし | ✅ |
| **F1** | 観測（engine） | `PitchTrace`/`PaTrace`/`GameTraceHeader`、`IDebugTraceSink`、`GameContext.CaptureTrace`、`JsonlTraceSink`、CLI `trace`／`trace-diff`、`CountingRandomSource` | なし | ✅ |
| **F2** | 注入（場面）＋MCP | `data/debug/scenarios.yaml`＋`ScenarioBuilder`、`DebugBridge`、Editorメニュー | 注入時のみ | ✅ |
| **F3** | HUD（Unity） | `RingBufferTraceSink`、F1オーバーレイ（A案＝右サイドバー縦一列）、采配の判断内訳の露出 | なし | ✅ |
| **F4** | 強制発動＋シーク | `ForcedOutcome`、pitch粒度シーク、早送り、`HasForcedOutcomes` 除外 | 強制時のみ | ✅ |

**順序の根拠**: F1 を最優先にするのは、**トレースがあるとF2以降の検証自体が安くなる**から（自分で作った機能を自分で検証できる）。
F4 を最後にするのは、**唯一RNG消費順を変える層**で、他の層が緑であることを前提にしたいため。

### 共通DoD（各フェーズ）

- [ ] 1機能=1テスト以上
- [ ] `dotnet test engine/ --filter "Category!=Heavy"` が緑
- [ ] engine 変更時は `tools/sync-engine-dll.sh` で Unity へ反映

### フェーズ固有DoD

**F0**
- [ ] Fork注入で構築した `MatchProgression` を `Save()` → `Restore()` して、そのまま進行した場合と `GameResultDigest` が一致
- [ ] 既存セーブ（`RngState == null`）が従来どおり復元できる（後方互換テスト）
- [ ] 再現トークンの往復（生成→解釈→復元）で同一場面になる

**F1**
- [ ] **`CaptureTrace` on/off で `GameResultDigest` が完全一致**（観測は結果を変えない）← 本設計の中核テスト
- [ ] `determinism-baseline.txt` の全カードが再ベースライン無しで緑
- [ ] `-c Release --filter "Category=Heavy"` が帯内（`CaptureTrace` off の統計シムが不変であることの確認）
- [ ] `CaptureTrace` on のオーバーヘッドを実測して記録（目標: 1試合あたり +10% 未満）
- [ ] `trace-diff` が「係数を1つ変えたトレース」の最初の食い違い球を正しく指す

**F2**
- [ ] 全シナリオが例外なく起動し、宣言した状況（イニング・アウト・走者・カウント）が `DumpState()` と一致
- [ ] `DebugBridge` の全メソッドが `execute_code`（C#6）から呼べる＝プリミティブ／文字列のみで構成されている
- [ ] `KOKOSIM_DEBUG` を外したビルドで `DebugBridge` を参照するコードが1つも残らない（コンパイルが通る）

**F3**
- [ ] ASCIIワイヤー3案を提示してから実装
- [ ] HUD 表示中／非表示で `GameResultDigest` が一致（HUDは engine を追加で呼ばない）
- [ ] バッチスクショで視認性を自己レビュー（7箇条ではなく「等幅・情報密度・見落とし零」の基準で）

**F4**
- [ ] 全 `ForcedOutcome` が1回で発動し、下流（記録・タイムライン・UI）が破綻しない
- [ ] 強制発動した試合が digest／統計集計から除外される
- [ ] pitch粒度シークの往復（進む→戻る→進む）で同一トレースになる
- [ ] シークの実測レイテンシを記録（目標: 9回終盤への復元が 100ms 未満）

---

## 10. 使用例（本設計が完成したときに何ができるか）

```bash
# 「9回裏2死満塁で振り逃げが起きたときの表現」を1コマンドで再現
dotnet run --project engine/KokoSim.Balance -- trace --scenario bases-loaded-9th --force DroppedThirdStrike --out out/t.jsonl

# 「スイング確率が高いのに見送った球」を100試合から抽出（打者判断モデルの検算）
dotnet run --project engine/KokoSim.Balance -- trace --games 100 --only pitch --out out/p.jsonl
jq -c 'select(.bat.pSw>0.8 and .bat.sw==false)' out/p.jsonl | head

# 係数変更の影響が最初にどの球で出るか
dotnet run --project engine/KokoSim.Balance -- trace-diff --a out/before.jsonl --b out/after.jsonl --report out/diff.md
```

```csharp
// MCP: execute_code から（C#6 で書ける）
KokoSim.Unity.Debugging.DebugBridge.StartMatch("bases-loaded-9th", "0x9f3a5511");
KokoSim.Unity.Debugging.DebugBridge.AdvancePitch(3);
KokoSim.Unity.Debugging.DebugBridge.Force("WildPitch");
KokoSim.Unity.Debugging.DebugBridge.AdvancePitch(1);
return KokoSim.Unity.Debugging.DebugBridge.DumpState();   // JSON が返る
```

---

## 11. 未決事項

**なし**（`docs/design/OPEN-QUESTIONS.md` **Q18** は 2026-07-21 に Q18-1・Q18-2 とも確定してクローズ）。
実装中に新たな未決を発見したら OPEN-QUESTIONS に追記して報告する。

---

## 12. 実装記録（2026-07-21・F0〜F4 完了）

本書のスケッチから**意図的に変えた点**と、DoDの**実測値**を残す。以後はこちらが実装の単一ソース。

### 12.1 設計からの逸脱と理由

| 箇所 | 本書のスケッチ | 実装 | 理由 |
|---|---|---|---|
| §3.2 `CaptureState()` | 4要素（xoshiro の4ワード） | **6要素**（4ワード＋Box-Muller予備値＋有無フラグ） | 4要素だと `NextGaussian` を奇数回引いた直後の復元で正規乱数が半周ズレる＝決定論が壊れる。`FromState` は旧式の4要素も受ける |
| §4.1 `PitchTrace` | `readonly record struct`（位置引数30個） | `sealed record`（init プロパティ） | 打席解決層（意図・実着弾・打者判断）と試合層（局面・状態・采配・RNG）の2箇所から埋めるため。生成は `CaptureTrace` 時のみ |
| §4.2 `RingBufferTraceSink` | Unity 側の部品 | **engine 側**（`KokoSim.Engine.Debugging`） | IOもUnity参照も持たない純データ構造。engine に置けばリングの巻き戻り境界をテストで固定できる（不変条件#3は守ったまま） |
| §4.3 JSONL の組み立て | シンク側で実装 | engine の `TraceJson`（純関数）へ集約 | CLI と Unity(`DebugBridge`) の両方が同じ関数を通す。二重実装すると `jq` のレシピが片方だけ壊れる |
| §3.4 YAML パース | （未指定） | engine の `ScenarioYamlParser`（純パーサ）＋ IO は Config/Unity 側 | Unity は YamlDotNet を持たない。`SchoolNameVocabParser` と同じ分け方で、CLI と Unity の解釈のズレを構造的に潰す |
| §3.4 `data/debug/` の Unity 供給 | StreamingAssets 経路を流用 | **StreamingAssets へ複製しない**。Editor/Development Build はリポジトリの `data/debug/` を直読み | StreamingAssets へ置くとビルドに同梱されてしまい Q18-2 の「ディレクトリ単位で除外」と矛盾する。直読みなら除外を1行も書かずに除外できる |
| §5 P4 候補スコア | `TacticsDecision` に候補スコア配列 | `PitchTacticsDirective.Explanation`（文字列）＋ `IExplainTactics` | `AiTacticsBrain` は「スコア比較」ではなく「①能力値の当たり外れ×②ティア関門」で手を絞る構造。候補スコアを捏造せず、実際の関門通過状況を出す |
| §6.1 `ForcedOutcome` | 26種 | 19種（`TriplePlay` / `Balk` / `Squeeze` / `EnterTieBreak` / `WalkOff` を除外） | いずれも単独の抽選ゲートを持たず、守備の共同解決や試合状況の帰結として現れる。固定するには物理層の偽装か第二の解決経路が要り、どちらも不変条件#1に反する。`EnterTieBreak` は `TieBreakEnabled` ＋シナリオの開始イニング指定で作れる |
| §6.1 実装方式 | 抽選をスキップして固定 | 打席結果系は**投球ループごとスキップ**（敬遠と同じ・物理層を1回も回さない）／稀プレー系は**該当ゲートの確率を1.0にした1打席コピーの走塁係数**（抽選自体は残す） | 後者は乱数消費順が「そのゲートを既定オンにした状態」と一致するので、副作用の範囲が読める |
| §5 HUD の等幅 | 等幅フォント | 固定幅 Label を横に並べて整列 | プロジェクトに等幅フォントが無く、空白詰めでは球種名の長さ差（Fork/Slider/Fastball）で桁が崩れる。整列はフォントに頼らずレイアウトで取る |

### 12.2 副次的に直したもの

- `MatchProgression.AwayScore/HomeScore` を **engine の実スコア直読み**に変更（従来は `PlayLogEntry.RunsScored` の自前合計）。
  場面ジャンプの注入得点・暴投での生還・重盗の生還など「打席に紐づかない得点」を取りこぼしていた。
- `GameReplay.Restore` / `MatchProgression` に `ScenarioStart` を通す口を追加（注入で始めた試合をシークしても同じ局面から起きる）。

### 12.3 DoD 実測

| 項目 | 結果 |
|---|---|
| **F1 中核: `CaptureTrace` on/off の digest 一致** | 全4カード × 50シード = 200試合で完全一致 |
| `determinism-baseline.txt` | 再ベースライン無しで緑 |
| `dotnet test --filter "Category!=Heavy"` | 856緑 |
| `-c Release --filter "Category=Heavy"` | 統計帯すべて緑 |
| **観測オンのオーバーヘッド** | **5.4〜7.3%**（300試合・`trace --measure` を単独実行。目標 +10% 未満）※ xUnit の Heavy は並列実行で CPU を取り合うため、目標値の判定には使わない |
| `trace-diff` | 係数（`straight_share` +0.02）を1つ変えたトレースの最初の食い違い球（1回裏 0-0 1球目・通算40球目・`plan.ty` が Fork→Fastball）を正しく指した |
| F0 Fork注入の Save/Restore | digest 一致。既存セーブ（`RngState=null`）も従来どおり復元 |
| F2 全シナリオ | 6件すべて宣言どおりの局面（イニング・表裏・アウト・走者・カウント・得点・打順・球数）で起動 |
| F2 `DebugBridge` | 全メソッドを `execute_code`（C#6/codedom）から実行して確認。異常系はすべて `{"ok":false,"err":...}` を返し例外を投げない |
| F2 `data/debug/` 欠損 | `ListScenarios()` が 0件で ok を返す（例外なし） |
| F4 全 `ForcedOutcome` | 19種すべて1回で発動し、下流（進塁・記録・タイムライン）が破綻せず完走 |
| F4 pitch粒度シーク | 往復（進む→戻る→進む）で同一トレース。シーク後に最後まで流すと中断なし実行と digest 一致 |
| F3 HUD | A案（右サイドバー縦一列）で実装。表示専用でリングバッファを読むだけ＝`CaptureTrace` on/off の digest 一致に含まれる |

### 12.4 積み残し

いずれも個別 issue に切り出し済み。

- **#95**: 大会フローから起こすライブ観戦（`MatchLiveController.Pending` 経路）は、`GameContext` を
  `PlayerMatchResolver.BeginLive` が組むため HUD の観測が刺さっていない。
  デモ生成の試合と `DebugBridge` 起点の試合は観測付き。
- **#96**: `ScenarioBuilder` の `"player"` 解決は現状フォールバックの生成校。
  `DebugBridge.StartMatch` が `playerTeam` を渡していないため、Unity 側の実部員（`RosterService`）との接続は未配線。
