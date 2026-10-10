using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 一塁走者のみ・2死未満の内野ゴロで守備が「併殺を狙う／一塁だけ取る」をどう選ぶかの実測
/// （issue double-play-attempt-threshold, 2026-08-18）。
/// 旧 <c>DoublePlayAttemptMinRelaySuccess=0.80</c> では転送で刺せる見込みが6〜8割ある併殺まで「確実な一塁」へ逃げ、
/// 一塁走者あり内野ゴロの半分以上（実測 60%）で走者を二塁へ進めていた。実物の内野は封殺が取れる限りまず二塁へ投げ、
/// 転送負けは FC（二塁封殺のみ）として残る。0.55 で 一塁のみ ≈33%／併殺 ≈33%（ゴロアウト比 ≈41%）／FC ≈13%。
/// 0.55→0.65（issue double-play-per-game-band-review, 2026-08-30）: 併殺/試合の参考帯を高校野球の失策率実測
/// （issue #123）ベースへ再校正した追従で、一塁のみ ≈44%／併殺 ≈37%（ゴロアウト比）／FC ≈10% へ変化。
/// 300試合×2校（強さ50同士・采配無し）＝Category=Heavy。並列化の作法は <see cref="RunnerConservationTests"/> と同じ。
/// </summary>
[Trait("Category", "Heavy")]
public sealed class GroundBallDoublePlayDecisionTests
{
    private readonly ITestOutputHelper _out;
    public GroundBallDoublePlayDecisionTests(ITestOutputHelper o) { _out = o; }

    private static bool Infield(FieldPosition p) => p is FieldPosition.Pitcher or FieldPosition.Catcher
        or FieldPosition.FirstBase or FieldPosition.SecondBase or FieldPosition.ThirdBase or FieldPosition.Shortstop;

    /// <summary>一塁走者のみ・2死未満の内野ゴロ（犠打を除く）の帰結内訳。</summary>
    internal sealed record Tally(int Dp, int FcOnly, int FirstOnly, int InfieldHit, int Error, int Other)
    {
        public int Total => Dp + FcOnly + FirstOnly + InfieldHit + Error + Other;
        public int GroundOuts => Dp + FcOnly + FirstOnly;
    }

    internal static (Tally Tally, double DpPerGame, double FcPerGame, double RunsPerTeam) Measure(
        double threshold, int games = 300, ulong seedBase = 7100)
    {
        var baseCtx = new GameContext();
        var ctx = baseCtx with { Baserunning = baseCtx.Baserunning with { DoublePlayAttemptMinRelaySuccess = threshold } };
        var perGame = new int[games][];
        var dps = new int[games];
        var fcs = new int[games];
        var runs = new int[games];
        Parallel.For(0, games, i =>
        {
            var local = new int[6];
            var rng = new Xoshiro256Random(seedBase + (ulong)i);
            var away = StrengthTeamFactory.Create(50, "A", rng);
            var home = StrengthTeamFactory.Create(50, "B", rng);
            var r = GameEngine.Play(away, home, ctx, rng);
            dps[i] = r.DoublePlayCount;
            fcs[i] = r.FieldersChoiceCount;
            runs[i] = r.AwayRuns + r.HomeRuns;
            foreach (var e in r.Log)
            {
                if (!(e.BaseFirstBefore && !e.BaseSecondBefore && !e.BaseThirdBefore)) continue;
                if (e.OutsBefore >= 2) continue;
                if (e.BattedIsFly || e.FieldedBy is not { } fb || !Infield(fb)) continue;
                if (e.Result is PlateAppearanceResult.Strikeout or PlateAppearanceResult.Walk or PlateAppearanceResult.HitByPitch) continue;
                if (e.IsSacBunt) continue;
                if (e.Result == PlateAppearanceResult.ReachedOnError) { local[4]++; continue; }
                if (e.Result.IsHit()) { local[3]++; continue; }
                if (e.Result != PlateAppearanceResult.InPlayOut) { local[5]++; continue; }
                if (e.OutsThisPa >= 2) { local[0]++; continue; }                                    // 併殺
                if (e.OutsThisPa == 1 && e.BaseFirstAfter && !e.BaseSecondAfter) { local[1]++; continue; } // 二塁封殺のみ（FC）
                if (e.OutsThisPa == 1 && !e.BaseFirstAfter && e.BaseSecondAfter) { local[2]++; continue; } // 一塁アウトのみ（走者は二塁へ）
                local[5]++;
            }
            perGame[i] = local;
        });
        var acc = new int[6];
        long dpTotal = 0, fcTotal = 0, runsTotal = 0;
        for (var i = 0; i < games; i++)
        {
            for (var k = 0; k < 6; k++) acc[k] += perGame[i][k];
            dpTotal += dps[i]; fcTotal += fcs[i]; runsTotal += runs[i];
        }
        var t = new Tally(acc[0], acc[1], acc[2], acc[3], acc[4], acc[5]);
        return (t, (double)dpTotal / games, (double)fcTotal / games, runsTotal / (2.0 * games));
    }

    [Fact]
    public void RunnerOnFirst_InfieldGrounder_DefenseUsuallyGoesForTwo()
    {
        var (t, dpg, fcg, rpt) = Measure(new BaserunningCoefficients().DoublePlayAttemptMinRelaySuccess);
        _out.WriteLine($"n={t.Total} DP={t.Dp} FConly={t.FcOnly} FirstOnly={t.FirstOnly} IFH={t.InfieldHit} E={t.Error} " +
                       $"| DP/groundouts={100.0 * t.Dp / t.GroundOuts:F1}% | DP/g={dpg:F2} FC/g={fcg:F2} runs/team={rpt:F2}");
        Assert.True(t.Total > 600, $"標本不足 n={t.Total}");
        // 「一塁アウトのみ（走者を二塁へ進める）」は内野ゴロの 5 割未満（旧 0.80 では 60%）。
        // 0.65 への再校正（issue double-play-per-game-band-review, 2026-08-30）で ≈33%→≈44% に上昇したが、
        // 依然として旧値の「過半」からは区別される水準。
        Assert.True(t.FirstOnly <= t.Total * 0.48, $"一塁のみ {t.FirstOnly}/{t.Total}={100.0 * t.FirstOnly / t.Total:F1}% が多すぎる（併殺を狙いに行かない）");
        // 併殺はゴロアウトの 30〜45%（旧 21%、0.55 校正時は 35〜50%）。上限は「刺せない相手にも投げる」を防ぐ検収。
        var dpShare = (double)t.Dp / t.GroundOuts;
        Assert.InRange(dpShare, 0.30, 0.45);
        // 転送負けの FC は残差として残る（0 でも 併殺と同数でもない）。
        Assert.InRange((double)t.FcOnly / t.GroundOuts, 0.05, 0.20);
    }

    [Fact]
    public void LegacyThreshold_WasTakingTheSureOutTooOften()
    {
        // 旧値 0.80 は「一塁のみ」が過半（回帰の対照。値の意味づけを固定する）。
        var (t, _, _, _) = Measure(0.80, games: 150);
        Assert.True(t.FirstOnly > t.Total * 0.50, $"旧値で一塁のみ {100.0 * t.FirstOnly / t.Total:F1}%（想定 >50%）");
    }
}
