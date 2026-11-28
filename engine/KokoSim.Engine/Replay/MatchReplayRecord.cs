using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Replay;

/// <summary>
/// 残す理由（issue #1580）。複数立つことがある。<see cref="OwnSchool"/> が立つ試合は全履歴、他は直近の年だけ残す
/// （OPEN-QUESTIONS Q15 未決3）。
/// </summary>
[Flags]
public enum NotableReason
{
    None = 0,
    /// <summary>自校が戦った試合。</summary>
    OwnSchool = 1,
    /// <summary>自県の上位ティア校が戦った試合。</summary>
    PrefectureTop = 2,
    /// <summary>甲子園（夏の甲子園・センバツ）の試合。</summary>
    Koshien = 4,
}

/// <summary>
/// 敵AIの采配の入力（<see cref="EnemyAiFactory.BrainFor(AiProfile, IReadOnlyList{ManagerTrait}, TacticsCoefficients?, BaserunningCoefficients?, EnemyAiCoefficients?, FormCoefficients?, LineupCoefficients?, SkillCoefficients?, Match.Fielding.FieldingCoefficients?, Nation.Roster.PersistentRosterCoefficients?)"/>
/// に渡すもの）。ブレイン本体は係数を抱えたオブジェクトなので保存せず、この3つ組から試合の係数で組み直す。
/// </summary>
public sealed record ReplayBrainSpec(AiProfile Profile, IReadOnlyList<ManagerTrait> Traits)
{
    public static ReplayBrainSpec FromSchool(School school)
        => new(EnemyAiFactory.ProfileFor(school), school.ManagerTraits.ToArray());

    public AiTacticsBrain Build() => EnemyAiFactory.BrainFor(Profile, Traits);
}

/// <summary>
/// 片チームぶんの再生入力。<see cref="Team"/> は采配（<see cref="Team.Tactics"/>）を外した素のチームで、
/// 采配は <see cref="Brain"/>（敵AI）が組み直す。自校は采配を持たない（プレイヤーの操作は決定列が持つ）。
/// </summary>
/// <param name="PitchRecovery">試合間の回復（実効目安投球数）。null＝持ち越しなし。</param>
/// <param name="PriorWeekPitches">週間球数制限の持ち越し球数。null＝持ち越しなし。</param>
public sealed record ReplayTeamSpec(
    Team Team, ReplayBrainSpec? Brain,
    IReadOnlyDictionary<Player, double>? PitchRecovery,
    IReadOnlyDictionary<Player, int>? PriorWeekPitches)
{
    /// <summary>
    /// 試合に渡したチームから再生入力を作る。采配が敵AI（<see cref="AiTacticsBrain"/>）なのに
    /// <paramref name="brain"/> が無い、または未知の采配が載っているチームは再生で組み直せないので null。
    /// </summary>
    public static ReplayTeamSpec? Capture(
        Team team, ReplayBrainSpec? brain,
        IReadOnlyDictionary<Player, double>? pitchRecovery, IReadOnlyDictionary<Player, int>? priorWeekPitches)
    {
        if (team.Tactics is not null && !(team.Tactics is AiTacticsBrain && brain is not null)) return null;
        if (team.Tactics is null) brain = null;
        return new ReplayTeamSpec(
            team with { Tactics = null }, brain,
            pitchRecovery is { Count: > 0 } ? pitchRecovery : null,
            priorWeekPitches is { Count: > 0 } ? priorWeekPitches : null);
    }

    /// <summary>試合に渡すチーム（采配を組み直したもの）。</summary>
    public Team ToTeam() => Brain is null ? Team : Team with { Tactics = Brain.Build() };
}

/// <summary>
/// 試合ごとに変わる試合設定（球場・ルール・天候・大会段階の重圧）。係数（打撃・守備・投球…）は持たない＝
/// 再生側が渡す基準の <see cref="GameContext"/>（実ゲームは C# 既定）に重ね、版（<see cref="MatchReplayVersion"/>）が
/// 食い違わないことだけを確かめる。
/// </summary>
public sealed record ReplayContextSpec
{
    public double CenterFenceM { get; init; } = 118.0;
    public double LeftFenceM { get; init; } = 95.0;
    public double RightFenceM { get; init; } = 95.0;
    public double FenceHeightM { get; init; } = 4.0;
    public bool Indoor { get; init; }
    public double BaseDistanceM { get; init; } = FieldGeometry.StandardBaseDistanceM;
    public double BackstopM { get; init; } = 18.3;
    public double FoulSideM { get; init; } = 13.0;
    public string? StadiumId { get; init; }
    public string? StadiumName { get; init; }

    public int RegulationInnings { get; init; } = 9;
    public int MaxInnings { get; init; } = 15;
    public bool MercyRuleEnabled { get; init; }
    public bool TieBreakEnabled { get; init; }
    public int TieBreakStartInning { get; init; } = 10;
    public int WeeklyPitchLimit { get; init; } = int.MaxValue;
    public int PressureStageBonus { get; init; }
    public bool RetirementOnLine { get; init; }
    public bool PlayerControlsPitcherChanges { get; init; }

    /// <summary>天候の月別の値（Enabled・平均・σ・下限・上限・雨の確率）。null＝天候モデルなし。</summary>
    public bool HasWeather { get; init; }
    public bool WeatherEnabled { get; init; } = true;
    public double WeatherMeanC { get; init; }
    public double WeatherStdDevC { get; init; }
    public double WeatherMinC { get; init; }
    public double WeatherMaxC { get; init; }
    public double WeatherRainProb { get; init; }

    public static ReplayContextSpec From(GameContext ctx)
    {
        var f = ctx.Field;
        var w = ctx.Weather;
        return new ReplayContextSpec
        {
            CenterFenceM = f.CenterFenceM, LeftFenceM = f.LeftFenceM, RightFenceM = f.RightFenceM,
            FenceHeightM = f.FenceHeightM, Indoor = f.Indoor, BaseDistanceM = f.BaseDistanceM,
            BackstopM = f.BackstopM, FoulSideM = f.FoulSideM,
            StadiumId = ctx.StadiumId, StadiumName = ctx.StadiumName,
            RegulationInnings = ctx.RegulationInnings, MaxInnings = ctx.MaxInnings,
            MercyRuleEnabled = ctx.MercyRuleEnabled, TieBreakEnabled = ctx.TieBreakEnabled,
            TieBreakStartInning = ctx.TieBreakStartInning, WeeklyPitchLimit = ctx.WeeklyPitchLimit,
            PressureStageBonus = ctx.PressureStageBonus, RetirementOnLine = ctx.RetirementOnLine,
            PlayerControlsPitcherChanges = ctx.PlayerControlsPitcherChanges,
            HasWeather = w is not null,
            WeatherEnabled = w?.Enabled ?? true,
            WeatherMeanC = w?.MeanTemperatureC ?? 0, WeatherStdDevC = w?.TemperatureStdDevC ?? 0,
            WeatherMinC = w?.MinTemperatureC ?? 0, WeatherMaxC = w?.MaxTemperatureC ?? 0,
            WeatherRainProb = w?.RainProb ?? 0,
        };
    }

    /// <summary>基準の試合設定にこの試合の値を重ねる。観測のフラグ（タイムライン等）は <paramref name="baseCtx"/> のまま。</summary>
    public GameContext ApplyTo(GameContext baseCtx) => baseCtx with
    {
        Field = new FieldGeometry
        {
            CenterFenceM = CenterFenceM, LeftFenceM = LeftFenceM, RightFenceM = RightFenceM,
            FenceHeightM = FenceHeightM, Indoor = Indoor, BaseDistanceM = BaseDistanceM,
            BackstopM = BackstopM, FoulSideM = FoulSideM,
        },
        StadiumId = StadiumId, StadiumName = StadiumName,
        RegulationInnings = RegulationInnings, MaxInnings = MaxInnings,
        MercyRuleEnabled = MercyRuleEnabled, TieBreakEnabled = TieBreakEnabled,
        TieBreakStartInning = TieBreakStartInning, WeeklyPitchLimit = WeeklyPitchLimit,
        PressureStageBonus = PressureStageBonus, RetirementOnLine = RetirementOnLine,
        PlayerControlsPitcherChanges = PlayerControlsPitcherChanges,
        Weather = HasWeather
            ? (baseCtx.Weather ?? new WeatherCoefficients()) with
            {
                Enabled = WeatherEnabled, MeanTemperatureC = WeatherMeanC, TemperatureStdDevC = WeatherStdDevC,
                MinTemperatureC = WeatherMinC, MaxTemperatureC = WeatherMaxC, RainProb = WeatherRainProb,
            }
            : null,
    };
}

/// <summary>
/// 試合を解いた入力一式（保存前の素材）。<see cref="Match.Timeline.Playback.MatchProgression.CaptureReplayInputs"/> が
/// ライブ観戦した試合から、<see cref="Nation.Tournaments.BackgroundMatchResolver"/> や Shell が一括解決した試合から作る。
/// </summary>
public sealed record MatchReplayInputs(
    Team Away, Team Home, GameContext Ctx, ulong[] RngState,
    IReadOnlyDictionary<Player, double>? AwayPitchRecovery, IReadOnlyDictionary<Player, double>? HomePitchRecovery,
    IReadOnlyDictionary<Player, int>? AwayPriorWeekPitches, IReadOnlyDictionary<Player, int>? HomePriorWeekPitches,
    IReadOnlyList<GameDecision> Decisions);

/// <summary>
/// 注目試合1つぶんの再生データ（issue #1580・オーナー判断 2026-11-28）。<b>再生した中身（打席・1球）は持たない</b>。
/// 試合の開始乱数状態・両チームの出場メンバーと能力・試合設定・采配の入力だけを残し、見るときに
/// <see cref="MatchReplayer"/> が同じエンジンで解き直す（容量が小さい）。係数やエンジンが変わった版では
/// 同じ試合にならないので、<see cref="EngineVersion"/> が今と違えば「この版では再生できない」と扱う。
/// </summary>
public sealed record MatchReplayRecord
{
    /// <summary>試合の同定（先攻校・後攻校・開始乱数状態から決まる）。同じ試合を二度積むと上書きになる。</summary>
    public string Id { get; init; } = "";

    /// <summary>この記録を作ったときの試合エンジンの版（<see cref="MatchReplayVersion.Current"/>）。</summary>
    public string EngineVersion { get; init; } = "";

    public NotableReason Reasons { get; init; }

    /// <summary>年度（YearIndex・1始まり）。暦年への変換は Unity 側の担当（不変条件#3）。</summary>
    public int Year { get; init; }

    /// <summary>大会の種別。null＝練習試合。</summary>
    public TournamentKind? Kind { get; init; }

    /// <summary>試合日の月日。0＝不明。</summary>
    public int Month { get; init; }
    public int Day { get; init; }
    public string RoundName { get; init; } = "";

    /// <summary>大会内の試合日（不明は -1）。<see cref="Month"/>／<see cref="Day"/> を後から引くための鍵。</summary>
    public int MatchDay { get; init; } = -1;

    /// <summary>決勝までの残りラウンド数（決勝=1・不明は -1）。</summary>
    public int RoundsRemaining { get; init; } = -1;

    public int AwaySchoolId { get; init; }
    public int HomeSchoolId { get; init; }
    public string AwayName { get; init; } = "";
    public string HomeName { get; init; } = "";
    public int AwayRuns { get; init; }
    public int HomeRuns { get; init; }
    public bool MercyEnded { get; init; }

    /// <summary>試合開始前の乱数の内部状態（<see cref="Core.Xoshiro256Random.CaptureState"/>）。</summary>
    public ulong[] RngState { get; init; } = Array.Empty<ulong>();

    public ReplayTeamSpec Away { get; init; } = null!;
    public ReplayTeamSpec Home { get; init; } = null!;
    public ReplayContextSpec Context { get; init; } = new();

    /// <summary>プレイヤーの采配の決定列（自校のライブ観戦）。裏試合・一括解決は空。</summary>
    public IReadOnlyList<GameDecision> Decisions { get; init; } = Array.Empty<GameDecision>();

    public bool IsOwn => (Reasons & NotableReason.OwnSchool) != 0;

    /// <summary>試合の同定（先攻校ID・後攻校ID・開始乱数状態の先頭2語）。</summary>
    public static string IdFor(int awaySchoolId, int homeSchoolId, IReadOnlyList<ulong> rngState)
    {
        var s0 = rngState.Count > 0 ? rngState[0] : 0UL;
        var s1 = rngState.Count > 1 ? rngState[1] : 0UL;
        return $"{awaySchoolId}-{homeSchoolId}-{s0:x16}{s1:x16}";
    }
}
