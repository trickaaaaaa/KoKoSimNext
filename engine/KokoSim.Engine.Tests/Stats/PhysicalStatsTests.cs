using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Stats;
using KokoSim.Engine.Tests.Saves;
using Xunit;

namespace KokoSim.Engine.Tests.Stats;

/// <summary>成績画面の物理データ（設計書06 §3.2b・issue #1493）: 平均打球初速/角度・最長本塁打・球速・キャリアハイ。</summary>
public sealed class PhysicalStatsTests
{
    private static BattingLine Bat(int hits, int rbi, int hr, double ev, double angle, int balls, double maxEv, double hrM)
        => new(1, FieldPosition.CenterField, "X", 5, 4, hits, 0, 0, hr, rbi, 0, 0)
        {
            BattedBalls = balls, ExitVelocitySumKmh = ev, LaunchAngleSumDeg = angle,
            MaxExitVelocityKmh = maxEv, LongestHomeRunM = hrM,
        };

    [Fact]
    public void Batting_AveragesAndCareerHighs_FoldAcrossGames()
    {
        var b = new BattingStatLine();
        b.Add(Bat(hits: 3, rbi: 1, hr: 1, ev: 400, angle: 40, balls: 4, maxEv: 150, hrM: 100));
        b.Add(Bat(hits: 1, rbi: 4, hr: 0, ev: 200, angle: 20, balls: 2, maxEv: 160, hrM: 0));

        Assert.Equal(6, b.BattedBalls);
        Assert.Equal(100.0, b.AverageExitVelocityKmh, 6);
        Assert.Equal(10.0, b.AverageLaunchAngleDeg, 6);
        Assert.Equal(160, b.MaxExitVelocityKmh);
        Assert.Equal(100, b.LongestHomeRunM);
        Assert.Equal(3, b.MaxHitsInGame);
        Assert.Equal(4, b.MaxRbiInGame);
        Assert.Equal(1, b.MaxHomeRunsInGame);
    }

    [Fact]
    public void Batting_NoBattedBalls_AveragesAreZero()
    {
        var b = new BattingStatLine();
        Assert.Equal(0.0, b.AverageExitVelocityKmh);
        Assert.Equal(0.0, b.AverageLaunchAngleDeg);
    }

    [Fact]
    public void Batting_Merge_SumsAndTakesMax()
    {
        var a = new BattingStatLine();
        a.Add(Bat(2, 1, 0, 300, 30, 3, 140, 0));
        var b = new BattingStatLine();
        b.Add(Bat(1, 3, 2, 100, 10, 1, 155, 120));
        a.Merge(b);

        Assert.Equal(4, a.BattedBalls);
        Assert.Equal(400, a.ExitVelocitySumKmh);
        Assert.Equal(155, a.MaxExitVelocityKmh);
        Assert.Equal(120, a.LongestHomeRunM);
        Assert.Equal(2, a.MaxHitsInGame);
        Assert.Equal(3, a.MaxRbiInGame);
        Assert.Equal(2, a.MaxHomeRunsInGame);
    }

    [Fact]
    public void Pitching_AverageMaxAndCareerHighs()
    {
        var p = new PitchingStatLine();
        p.Add(new PitchingLine("P", 21, 25, 4, 1, 9, 1, 100) { VelocitySamples = 100, VelocitySumKmh = 12000, MaxVelocityKmh = 138 }, started: true);
        p.Add(new PitchingLine("P", 27, 30, 5, 2, 6, 2, 110) { VelocitySamples = 100, VelocitySumKmh = 13000, MaxVelocityKmh = 142 }, started: true);

        Assert.Equal(125.0, p.AverageVelocityKmh, 6);
        Assert.Equal(142, p.MaxVelocityKmh);
        Assert.Equal(9, p.MaxStrikeOutsInGame);
        Assert.Equal(27, p.MaxOutsInGame);
        Assert.Equal("9", p.MaxInningsText);
    }

    [Fact]
    public void Save_RoundTrip_KeepsPhysicalStats_AndOldSaveReadsAsUnrecorded()
    {
        var state = SaveTestFixtures.BuildRichState();
        Assert.True(SaveCodec.TryDeserialize(SaveCodec.Serialize(state), out var s, out var err), err);
        var p1 = s.Stats.Career.Get(1)!;
        Assert.Equal(4, p1.Batting.BattedBalls);
        Assert.Equal(112.5, p1.Batting.LongestHomeRunM);
        Assert.Equal(158.4, p1.Batting.MaxExitVelocityKmh);
        Assert.Equal(1, p1.Batting.MaxHomeRunsInGame);
        Assert.Equal(141.2, p1.Pitching.MaxVelocityKmh);
        Assert.Equal(120, p1.Pitching.VelocitySamples);
        Assert.Equal(27, p1.Pitching.MaxOutsInGame);

        // 旧セーブ相当: 物理データのキーが無い → 0（未記録）で読め、読み込み自体は壊れない。
        var json = SaveCodec.Serialize(state);
        foreach (var key in new[] { "bbn", "evs", "las", "mev", "lhr", "mh", "mrbi", "mhr", "vn", "vs", "mv", "mso", "mouts" })
            json = System.Text.RegularExpressions.Regex.Replace(json, "\"" + key + "\":[-0-9.eE+]+,?", "");
        json = json.Replace(",}", "}");
        Assert.True(SaveCodec.TryDeserialize(json, out var old, out var err2), err2);
        var o1 = old.Stats.Career.Get(1)!;
        Assert.Equal(0, o1.Batting.BattedBalls);
        Assert.Equal(0.0, o1.Batting.MaxExitVelocityKmh);
        Assert.Equal(0, o1.Pitching.VelocitySamples);
        Assert.Equal(2, o1.Batting.Hits);
    }

    [Fact]
    public void FullGame_RecordsPhysicalData_InPlausibleRanges()
    {
        var away = StrengthTeamFactory.Create(55, "先攻", new Xoshiro256Random(11));
        var home = StrengthTeamFactory.Create(55, "後攻", new Xoshiro256Random(12));
        var r = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(7));

        var batters = r.AwayBatting.Concat(r.HomeBatting).ToList();
        var pitchers = r.AwayPitching.Concat(r.HomePitching).ToList();

        Assert.True(batters.Sum(b => b.BattedBalls) > 20);
        foreach (var b in batters.Where(b => b.BattedBalls > 0))
        {
            var avg = b.ExitVelocitySumKmh / b.BattedBalls;
            Assert.InRange(avg, 40, 190);
            Assert.True(b.MaxExitVelocityKmh >= avg - 1e-9);
        }
        // 投げた球のほとんどに球速が付く（球数と一致する＝1球も取りこぼさない or 打席外の球だけ差が出る）。
        var thrown = pitchers.Sum(p => p.VelocitySamples);
        Assert.InRange(thrown, 1, pitchers.Sum(p => p.Pitches));
        foreach (var p in pitchers.Where(p => p.VelocitySamples > 0))
        {
            Assert.InRange(p.VelocitySumKmh / p.VelocitySamples, 70, 170);
            Assert.True(p.MaxVelocityKmh >= p.VelocitySumKmh / p.VelocitySamples - 1e-9);
        }
        // 本塁打を打った選手は最長本塁打が付く。
        foreach (var b in batters.Where(b => b.HomeRuns > 0 && b.BattedBalls > 0))
            Assert.True(b.LongestHomeRunM > 50);
    }
}
