using KokoSim.Balance;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Balance;

/// <summary>
/// 本塁打の出方の集計（issue #1014）が既知の小さな入力で正しい分布を返すこと。
/// 計測専用＝係数・帯・解決式には触れない。
/// </summary>
public sealed class HomeRunProfileTests
{
    [Fact]
    public void PerGameDistribution_MatchesKnownGames()
    {
        var p = new HomeRunProfile();
        // 両軍計の本塁打: 0,0,1,2,5,11 本の6試合（打席は各70）。得点は (先攻,後攻)。
        p.AddGame(0, awayRuns: 0, homeRuns: 1, plateAppearances: 70);
        p.AddGame(0, 2, 3, 70);
        p.AddGame(1, 4, 4, 70, doubles: 2);
        p.AddGame(2, 5, 9, 70, doubles: 1, triples: 1);
        p.AddGame(5, 10, 12, 70);
        p.AddGame(11, 3, 25, 70, doubles: 3);

        Assert.Equal(6, p.Games);
        Assert.Equal(19, p.HomeRuns);
        Assert.Equal(420, p.PlateAppearances);
        Assert.Equal(19.0 / 420.0, p.HomeRunRate, 12);
        Assert.Equal(19.0 / 6.0, p.HomeRunsPerGame, 12);
        Assert.Equal(11, p.MaxHomeRunsInGame);

        Assert.Equal(2.0 / 6.0, p.ShareExactly(0), 12);
        Assert.Equal(1.0 / 6.0, p.ShareExactly(1), 12);
        Assert.Equal(1.0 / 6.0, p.ShareExactly(2), 12);
        Assert.Equal(0.0, p.ShareExactly(3), 12);
        Assert.Equal(2.0 / 6.0, p.ShareAtLeast(5), 12);   // 5本・11本
        Assert.Equal(1.0 / 6.0, p.ShareAtLeast(10), 12);  // 11本
        Assert.Equal(1, p.GamesAtLeast(10));

        // チーム得点: 12件のうち 10点以上は 10・12・25 の3件、0点は1件、4点は2件。
        Assert.Equal(1.0 / 12.0, p.RunsShare(0), 12);
        Assert.Equal(2.0 / 12.0, p.RunsShare(4), 12);
        Assert.Equal(3.0 / 12.0, p.RunsShare(10), 12);
        Assert.Equal(78.0 / 12.0, p.RunsPerTeamAverage, 12);
        Assert.Equal(6.0 / 19.0, p.DoublesPerHomeRun, 12);
    }

    [Fact]
    public void BattedBallBins_AndFenceEdgeShare_MatchKnownBalls()
    {
        var p = new HomeRunProfile();
        p.AddHomeRunBall(139.0, 12.0, 0.5);   // 初速 140未満・角度 15未満・余裕 0〜1
        p.AddHomeRunBall(152.0, 27.0, 1.5);   // 150〜155・25〜30・1〜2
        p.AddHomeRunBall(152.4, 29.9, 3.0);   // 150〜155・25〜30・2〜4
        p.AddHomeRunBall(210.0, 60.0, 15.0);  // 200以上・55以上・10以上
        p.AddHomeRunBall(null, null, 2.0);    // 打球なし（初速・角度に入れない）・余裕 2〜4（境界は上側）

        Assert.Equal(4, p.WithBall);
        Assert.Equal(5, p.WithClearance);
        Assert.Equal(1, p.EvHist[0]);
        Assert.Equal(2, p.EvHist[3]);   // 150〜155 は 1+(150-140)/5 = 3番
        Assert.Equal(1, p.EvHist[HomeRunProfile.EvBins - 1]);
        Assert.Equal(1, p.LaHist[0]);
        Assert.Equal(2, p.LaHist[3]);   // 25〜30 は 1+(25-15)/5 = 3番
        Assert.Equal(1, p.LaHist[HomeRunProfile.LaBins - 1]);
        Assert.Equal(new long[] { 1, 1, 2, 0, 0, 1 }, p.ClearanceHist);
        Assert.Equal(2.0 / 5.0, p.FenceEdgeShare, 12);   // 0.5m と 1.5m
        Assert.Equal((139.0 + 152.0 + 152.4 + 210.0) / 4.0, p.MeanEvKmh, 9);
    }

    [Fact]
    public void Merge_EqualsAddingEverythingIntoOne()
    {
        var a = new HomeRunProfile();
        var b = new HomeRunProfile();
        var all = new HomeRunProfile();
        a.AddGame(3, 5, 2, 72); all.AddGame(3, 5, 2, 72);
        b.AddGame(7, 11, 9, 80); all.AddGame(7, 11, 9, 80);
        a.AddHomeRunBall(160, 30, 1.0); all.AddHomeRunBall(160, 30, 1.0);
        b.AddHomeRunBall(170, 35, 6.0); all.AddHomeRunBall(170, 35, 6.0);
        a.Merge(b);
        Assert.Equal(all.PerGame, a.PerGame);
        Assert.Equal(all.RunsPerTeam, a.RunsPerTeam);
        Assert.Equal(all.EvHist, a.EvHist);
        Assert.Equal(all.ClearanceHist, a.ClearanceHist);
        Assert.Equal(all.MaxHomeRunsInGame, a.MaxHomeRunsInGame);
        Assert.Equal(all.HomeRunRate, a.HomeRunRate, 12);
    }

    [Fact]
    public void Binomial_MatchesClosedForm()
    {
        // B(2, 0.5): P(X≥1)=0.75, P(X=1)=0.5。B(76, 0.0158) の平均 ≈ 1.2。
        Assert.Equal(0.75, AtBatSimulation.BinomialAtLeast(2, 0.5, 1), 12);
        Assert.Equal(0.5, AtBatSimulation.BinomialExactly(2, 0.5, 1), 12);
        Assert.Equal(1.0, AtBatSimulation.BinomialAtLeast(76, 0.0158, 0), 12);
        var mean = 0.0;
        for (var k = 0; k <= 76; k++) mean += k * AtBatSimulation.BinomialExactly(76, 0.0158, k);
        Assert.Equal(76 * 0.0158, mean, 9);
        Assert.Equal(1.0, AtBatSimulation.BinomialExactly(10, 0.0, 0), 12);
    }

    /// <summary>
    /// 実際の試合から積むと、ログの本塁打と数が合い、柵越えの本塁打は全部「フェンス上の余裕 ≥ 0」で拾える
    /// （engine の <see cref="PlayLogEntry.FenceCrossHeightM"/> の写しが本塁打に付いていること）。
    /// </summary>
    [Fact]
    public void FromRealGames_CountsMatchLog_AndEveryOverTheFenceHomeRunHasClearance()
    {
        var field = Stadium.MunicipalSmall01.ToFieldGeometry();   // フェンス2m・両翼91m＝本塁打が出やすい
        var p = HomeRunMeasurement.MeasureProfile(16, 1014UL, (i, g) =>
            (KokoSim.Engine.Nation.StrengthTeamFactory.Create(60, "A", g), KokoSim.Engine.Nation.StrengthTeamFactory.Create(60, "H", g), new GameContext { Field = field }));

        var hrInLog = 0L;
        var pa = 0L;
        var root = new Xoshiro256Random(1014UL);
        for (var i = 0; i < 16; i++)
        {
            var g = root.Fork((ulong)i);
            var r = GameEngine.Play(KokoSim.Engine.Nation.StrengthTeamFactory.Create(60, "A", g), KokoSim.Engine.Nation.StrengthTeamFactory.Create(60, "H", g),
                new GameContext { Field = field }, g);
            pa += r.Log.Count;
            foreach (var e in r.Log)
            {
                if (e.Result != PlateAppearanceResult.HomeRun) continue;
                hrInLog++;
                Assert.NotNull(e.FenceCrossHeightM);
                Assert.True(e.FenceCrossHeightM >= field.FenceHeightM, $"本塁打なのにフェンス上 {e.FenceCrossHeightM:F2}m < {field.FenceHeightM}m");
            }
            if (r.Log.Count > 0)
                Assert.All(r.Log, e => Assert.True(e.Result == PlateAppearanceResult.HomeRun || e.FenceCrossHeightM is null));
        }

        Assert.True(hrInLog > 0, "16試合で本塁打が1本も出ていない（検査にならない）");
        Assert.Equal(16, p.Games);
        Assert.Equal(hrInLog, p.HomeRuns);
        Assert.Equal(pa, p.PlateAppearances);
        Assert.Equal(hrInLog, p.WithClearance);
        Assert.Equal(hrInLog, p.WithBall);
        Assert.Equal(hrInLog, p.PerGame.Select((n, k) => n * k).Sum());
    }
}
