using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;

namespace KokoSim.Engine.Match.Game;

/// <summary>試合の全係数・ルール一式（YAML駆動）。打席解決はこの共有係数＋守備陣で AtBatContext を都度作る。</summary>
public sealed record GameContext
{
    public Aerodynamics Aerodynamics { get; init; } = new();
    public MoundGeometry Mound { get; init; } = new();
    public StrikeZone StrikeZone { get; init; } = new();
    public FieldGeometry Field { get; init; } = new();
    public PitchingCoefficients Pitching { get; init; } = new();
    public BattingCoefficients Batting { get; init; } = new();
    public FieldingCoefficients Fielding { get; init; } = new();
    public BaserunningCoefficients Baserunning { get; init; } = new();
    public FatigueCoefficients Fatigue { get; init; } = new();
    public PitchRecoveryCoefficients PitchRecovery { get; init; } = new();

    /// <summary>
    /// 試合ごとの天候（気温）モデル（Issue #120, design-05 大会単位の味付け）。null または無効の間は
    /// 気温生成も派生も行わず従来挙動。有効時は <see cref="WeatherModel.ApplyForMatch"/> が試合開始時に一度だけ
    /// 専用Forkストリームで気温を引き、空気密度・投手消耗を派生させる（本体の乱数順・帯は不変）。
    /// </summary>
    public WeatherCoefficients? Weather { get; init; }

    public Players.FormCoefficients Form { get; init; } = new();
    public Players.SkillCoefficients Skills { get; init; } = new();
    public PressureCoefficients Pressure { get; init; } = new();

    /// <summary>
    /// 試合中の受傷係数（設計書03 §3.5）。判定は Fork した専用ストリームで引くため、
    /// 値を変えても試合結果・乱数順は動かない（観測データのみ）。
    /// </summary>
    public MatchInjuryCoefficients MatchInjury { get; init; } = new();

    /// <summary>傷病カタログ（data/injuries.yaml）。空にすると試合中の受傷は一切起きない。</summary>
    public Players.InjuryCatalog InjuryCatalog { get; init; } = Players.InjuryCatalog.Default;

    /// <summary>大会段階のプレッシャー加点（練習試合0 / 予選+1 / 甲子園+2 / 決勝+3, 設計書02 §3.1）。</summary>
    public int PressureStageBonus { get; init; }
    /// <summary>負けたら引退（3年夏）のプレッシャー加点フラグ。</summary>
    public bool RetirementOnLine { get; init; }

    /// <summary>采配まわりの係数（設計書09, YAML駆動）。判断閾値と効果量の両方。</summary>
    public Tactics.TacticsCoefficients Tactics { get; init; } = new();

    /// <summary>在フィールド野手の緊急登板時の backfill 経路判定係数（issue #209 Phase C path②）。</summary>
    public ReliefBackfillCoefficients ReliefBackfill { get; init; } = new();

    // --- 試合ルール ---
    public int RegulationInnings { get; init; } = 9;
    public int MaxInnings { get; init; } = 15;         // 延長上限（無限ループ防止）
    public bool MercyRuleEnabled { get; init; } = false;

    /// <summary>
    /// タイブレーク（設計書09 §7, 現代ルールトグル・年代連動は設計書05 §1.3）。
    /// 有効時、開始イニング以降は無死一・二塁＋継続打順で始まる。既定オフ＝従来挙動。
    /// </summary>
    public bool TieBreakEnabled { get; init; }
    public int TieBreakStartInning { get; init; } = 10;

    /// <summary>
    /// スモールボール（盗塁・バント）の自動発生。既定オフ。
    /// 本来は監督采配（設計書09）が駆動する。09 実装までの暫定ヒューリスティックのゲート。
    /// オフの間は既存の試合挙動・統計帯を一切変えない。
    /// </summary>
    public bool EnableSmallBall { get; init; }
    /// <summary>暫定: 盗塁を試みる盗塁パラメータの下限（これ以上の走者が単独盗塁を試みる）。</summary>
    public int StealAttemptThreshold { get; init; } = 62;

    /// <summary>
    /// 1投手の球数上限（設計書05 §1.3, 現代ルール）。既定 int.MaxValue＝上限なし＝従来挙動。
    /// ModernRules.EffectiveWeeklyPitchLimit(year) を流し込む。週の持ち越し球数（GameEngine.Play の
    /// priorWeekPitches）＋当試合の球数がこの値に達した投手は、打者を打ち終えた時点で継投へ回す。
    /// 単一試合は上限に達しない（~150球）ため、実質は大会週をまたぐ持ち越しで効く。
    /// </summary>
    public int WeeklyPitchLimit { get; init; } = int.MaxValue;

    /// <summary>
    /// タイムライン出力（CHANGELOG 32）。既定オフ＝統計シムはゼロコスト・帯不変。
    /// UI再生（詳細/高速モードのリプレイ）時のみオンにする。
    /// </summary>
    public bool CaptureTimelines { get; init; }

    /// <summary>
    /// 1球ぶんの観測（設計書15 Phase D-1 のUI採用）。投げ終えた球と、その時点の局面・対戦を
    /// <see cref="GameProgress.LastPitch"/> へ書き出す。<b>打席が確定する前に</b>1球ずつ絵にするための供給口で、
    /// <see cref="Timeline.Playback.MatchProgression.AdvancePitch"/> と対で使う。
    ///
    /// <para>既定オフ＝統計シム・裏試合はゼロコスト。書き出しは既に解決済みの値の写しだけで、
    /// 乱数を1回も追加消費しない＝結果・digest・統計帯は不変（<see cref="CaptureTimelines"/> と同型のゲート）。</para>
    /// </summary>
    public bool CapturePitches { get; init; }

    /// <summary>
    /// 自校の投手交代をプレイヤーの采配権にするか（issue: 試合中に投手が勝手に交代される・2026-08-25）。
    /// 既定オフ＝裏試合・統計シム・既存テストは全部これ＝<see cref="GameEngine.MaybeChangePitcher"/> の
    /// 挙動もその帯も従来どおり変わらない。真のときだけ、<see cref="TeamState.Tactics"/> が null の自校に対して、
    /// 週間球数制限（<see cref="TeamState.CurrentPitcherAtWeeklyLimit"/>）到達時の自動交代を止め、
    /// <see cref="Timeline.Playback.MatchProgression.PendingForcedPitcherChange"/> でプレイヤーの人選待ちにする。
    /// 疲労トリガー（目安に過ぎない）は真偽どちらでも自動交代・通知の対象から外す。
    /// </summary>
    public bool PlayerControlsPitcherChanges { get; init; }

    /// <summary>守備陣形ルール表（CHANGELOG 33, data/defensive-formations.yaml から注入。null=既定表）。</summary>
    public Match.Timeline.FormationTable? Formations { get; init; }

    // ===== デバッグ観測（設計書17 §4.2, F1）。CaptureTimelines と同型のゲート。 =====
    // 既定 false＝統計シム・裏試合はゼロコスト。観測は乱数を1回も追加消費しないので帯も digest も不変。

    /// <summary>1球単位の構造化トレース（<see cref="Debugging.PitchTrace"/>）を出すか。既定オフ。</summary>
    public bool CaptureTrace { get; init; }

    /// <summary>観測の出口。engine は渡すだけで IO は持たない（不変条件#3）。null なら観測しない。</summary>
    public Debugging.IDebugTraceSink? TraceSink { get; init; }

    /// <summary>注入シナリオid（設計書17 §3.4）。非nullの試合は digest・統計集計の対象外。通常はnull。</summary>
    public string? ScenarioId { get; init; }

    /// <summary>観測が実際に走るか（フラグとシンクの両方が揃ったときだけ）。</summary>
    public bool TracingEnabled => CaptureTrace && TraceSink is not null;

    /// <summary>
    /// 表示→物理の効き（設計書21）を打撃/守備/走塁の3ブロックへ同じ値で配った写し。正典は1つ（YAML <c>ability_effect:</c>）で
    /// 3ブロックが食い違うと「走者は寝ているのに野手は寝ていない」世界になるので、差し替えは必ずここを通す
    /// （テストで恒等 <see cref="Players.AbilityEffectCoefficients.Identity"/> に戻すときも同じ）。
    /// </summary>
    public GameContext WithAbilityEffect(Players.AbilityEffectCoefficients e) => this with
    {
        Batting = Batting with { AbilityEffect = e },
        Fielding = Fielding with { AbilityEffect = e },
        Baserunning = Baserunning with { AbilityEffect = e },
    };

    /// <summary>指定守備陣で打席解決用コンテキストを作る。gear/directive/take は采配（設計書09）、skills はスキル（設計書10）が設定する。</summary>
    public AtBatContext ToAtBatContext(IReadOnlyList<Fielder> fielders, bool runnersOn = false,
        PitcherGear gear = PitcherGear.Normal, Tactics.PitchDirective? directive = null,
        bool takeFirstPitch = false, SkillPlayMods? skills = null, int catcherLead = 50,
        bool intentionalWalk = false, double swingProbabilityFactor = 1.0, int familiarityPa = 0) => new()
    {
        FamiliarityPa = familiarityPa,
        Aerodynamics = Aerodynamics,
        Mound = Mound,
        StrikeZone = StrikeZone,
        Field = Field,
        Pitching = Pitching,
        Batting = Batting,
        Fielding = Fielding,
        Fielders = fielders,
        Gear = gear,
        Directive = directive,
        TakeFirstPitch = takeFirstPitch,
        SwingProbabilityFactor = swingProbabilityFactor,
        CatcherLead = catcherLead,
        Skills = skills ?? SkillPlayMods.None,
        CaptureTimeline = CaptureTimelines,
        CaptureTrace = TracingEnabled,
        Formations = Formations,
        RunnersOn = runnersOn,
        IntentionalWalk = intentionalWalk,
        Baserunning = Baserunning,
    };
}
