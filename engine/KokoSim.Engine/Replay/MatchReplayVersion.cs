using System.Threading;
using KokoSim.Engine.Core;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;

namespace KokoSim.Engine.Replay;

/// <summary>
/// 試合エンジンの版（issue #1580）。保存した試合は「同じ入力を同じエンジンで解き直す」ので、係数や解決式が
/// 変わった版では同じ試合にならない。版を手で数える（変えるたびに +1 を忘れる）のでなく、<b>固定の試合を1つ
/// 解いた結果の指紋</b>を版とする＝係数・物理・采配のどこが変わっても、その試合の経過が動けば版が変わる。
///
/// <para>固定の試合は両校とも敵AIの采配つき・天候モデルありの既定設定（実ゲームの基準＝C# 既定）で、約10ms。
/// 初回に1度だけ計算して覚える（プロセスをまたいで同じ値）。</para>
/// </summary>
public static class MatchReplayVersion
{
    /// <summary>保存の形（<see cref="MatchReplayRecord"/> のフィールド構成）の版。形を変えたら +1。</summary>
    public const int Schema = 1;

    private static readonly Lazy<string> Probe = new(Compute, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>いまのエンジンの版（保存の形の版＋固定の試合の指紋）。</summary>
    public static string Current => Probe.Value;

    /// <summary>この記録は今のエンジンで解き直して同じ試合になるか。</summary>
    public static bool Matches(MatchReplayRecord record) => record.EngineVersion == Current;

    internal static string Compute()
    {
        var rng = new Xoshiro256Random(0x1580_0001UL);
        var traits = Array.Empty<ManagerTrait>();
        var away = Nation.StrengthTeamFactory.Create(58.0, "版A", rng.Fork(1))
            with { Tactics = EnemyAiFactory.BrainFor(new AiProfile(55, 3, SchoolStyle.Standard), traits) };
        var home = Nation.StrengthTeamFactory.Create(66.0, "版B", rng.Fork(2))
            with { Tactics = EnemyAiFactory.BrainFor(new AiProfile(70, 5, SchoolStyle.Standard), traits) };
        var ctx = new GameContext { Weather = new WeatherCoefficients(), TieBreakEnabled = true };
        var r = GameEngine.Play(away, home, ctx, rng.Fork(3));
        var fingerprint = CoefficientFingerprint.OfValue(new object[]
        {
            r.AwayRuns, r.HomeRuns, r.InningsPlayed, r.TotalPitches, r.PitcherChanges, r.AwayLineScore, r.HomeLineScore,
            r.AwayHits, r.HomeHits, r.AwayErrors, r.HomeErrors, r.DoublePlayCount, r.FieldersChoiceCount,
            r.WildPitchCount, r.Log, r.AwayBatting, r.HomeBatting, r.AwayPitching, r.HomePitching,
        }, hexLength: 12);
        return Schema + "-" + fingerprint;
    }
}
