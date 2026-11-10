using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Tactics;

/// <summary>
/// くさい所（issue #872）: ゾーンの縁を突き、甘い球を投げない打席単位の守備指示。
/// ①狙いが縁へ集まる ②指示なしは乱数を増やさない ③その打席だけで解ける ④際の球は芯を外しやすい（物理層）。
/// </summary>
public sealed class PitchAroundTests
{
    private static readonly TacticsCoefficients C = new();
    private static readonly StrikeZone Zone = new();

    private static PitchDirective Around(Handedness bats = Handedness.Right)
        => PitchDirective.Identity with { Edge = C.EdgeAimFor(bats) };

    private static double MeanAimEdgeProximity(PitchDirective? d, ulong seed)
    {
        var pitcher = PitcherAttributes.LeagueAverage;
        var rng = new Xoshiro256Random(seed);
        var sum = 0.0;
        const int n = 2000;
        for (var i = 0; i < n; i++)
        {
            var plan = PitchSelection.Select(pitcher, Zone, new(), new(), rng, PitcherGear.Normal, d);
            sum += Zone.EdgeProximity(plan.AimX, plan.AimY);
        }
        return sum / n;
    }

    [Fact]
    public void Aim_ClustersOnZoneEdge_MoreThanCenterAiming()
    {
        var plain = MeanAimEdgeProximity(null, 21);
        var around = MeanAimEdgeProximity(Around(), 21);
        Assert.True(around > plain + 0.3, $"縁への近さ 方針なし{plain:F2} → くさい所{around:F2}");
        Assert.True(around > 0.99, "縁を狙うので、狙い位置は縁の上か外のはず");
    }

    [Fact]
    public void Aim_EdgeChoice_FollowsWeights_AndOutsideDependsOnHandedness()
    {
        var pitcher = PitcherAttributes.LeagueAverage;
        int low = 0, high = 0, outside = 0, inside = 0;
        var rng = new Xoshiro256Random(5);
        const int n = 4000;
        for (var i = 0; i < n; i++)
        {
            var p = PitchSelection.Select(pitcher, Zone, new(), new(), rng, PitcherGear.Normal, Around(Handedness.Right));
            if (p.AimY < Zone.BottomM) low++;
            else if (p.AimY > Zone.TopM) high++;
            else if (p.AimX > 0) outside++;     // 右打者の外角は一塁側(+X)
            else inside++;
        }
        Assert.True(low > outside && outside > inside && inside > high, $"低{low} 外{outside} 内{inside} 高{high}");

        // 左打者は外角が三塁側(−X)になる
        var rngL = new Xoshiro256Random(5);
        var leftOutside = 0;
        for (var i = 0; i < n; i++)
        {
            var p = PitchSelection.Select(pitcher, Zone, new(), new(), rngL, PitcherGear.Normal, Around(Handedness.Left));
            if (p.AimY >= Zone.BottomM && p.AimY <= Zone.TopM && p.AimX < 0) leftOutside++;
        }
        Assert.True(leftOutside > n * 0.25, $"左打者の外角(−X)が出ていない: {leftOutside}");
    }

    /// <summary>指示なしは縁の抽選を引かない＝乱数の引き順が改定前と同じ（Edge=null は Identity と同値）。</summary>
    [Fact]
    public void NoEdge_DrawsNoExtraRandomNumber()
    {
        var pitcher = PitcherAttributes.LeagueAverage;
        var a = new Xoshiro256Random(77);
        var b = new Xoshiro256Random(77);
        for (var i = 0; i < 50; i++)
        {
            var pa = PitchSelection.Select(pitcher, Zone, new(), new(), a, PitcherGear.Normal, null);
            var pb = PitchSelection.Select(pitcher, Zone, new(), new(), b, PitcherGear.Normal, PitchDirective.Identity);
            Assert.Equal(pa, pb);
        }
        Assert.Equal(a.NextDouble(), b.NextDouble());

        // 縁狙いは縁の抽選1回ぶんだけ多く引く
        var c = new Xoshiro256Random(77);
        var d = new Xoshiro256Random(77);
        PitchSelection.Select(pitcher, Zone, new(), new(), c, PitcherGear.Normal, null);
        PitchSelection.Select(pitcher, Zone, new(), new(), d, PitcherGear.Normal, Around());
        d.NextDouble();
        Assert.NotEqual(c.NextDouble(), d.NextDouble());
    }

    [Fact]
    public void ContactQuality_EdgeBall_IsWeakerThanMiddle_AndNullIsNeutral()
    {
        var coeff = new BattingCoefficients();
        var features = default(PitchTrajectoryFeatures);
        var plan = new PitchPlan { Type = PitchType.Fastball, AimX = 0, AimY = 0.8, VelocityKmh = 132, Stuff = 0 };

        var neutral = ContactModel.PitcherContactQualityBias(plan, features, coeff);
        var middle = ContactModel.PitcherContactQualityBias(plan with { EdgeProximity = 0.0 }, features, coeff);
        var edge = ContactModel.PitcherContactQualityBias(plan with { EdgeProximity = 1.0 }, features, coeff);
        var atRef = ContactModel.PitcherContactQualityBias(plan with { EdgeProximity = coeff.ContactQualityEdgeRef }, features, coeff);

        Assert.True(edge < middle, "際の球が真ん中より芯を外させていない");
        Assert.Equal(neutral, atRef, 12);   // 未設定は基準値扱い＝直接組んだ PitchPlan は従来と同値
    }

    // ── 打席単位（その打席だけ）──

    [Fact]
    public void PitchAround_DoesNotCarryOverToNextPlateAppearance()
    {
        var prog = NewGame(42);
        prog.SetPitchAround(teamIsAway: false, on: true);   // 先攻の攻撃＝後攻(home)が守備
        Assert.True(prog.PitchAroundOn(false));
        Assert.True(prog.Advance());
        Assert.False(prog.PitchAroundOn(false), "打席が終わったのにくさい所が残っている");
    }

    [Fact]
    public void PitchAround_ChangesTheGame_AndRaisesWalks()
    {
        int plainWalks = 0, aroundWalks = 0;
        var changed = false;
        foreach (var seed in new ulong[] { 1, 2, 3, 4, 5, 6, 7, 8 })
        {
            var plain = Play(seed, around: false);
            var around = Play(seed, around: true);
            plainWalks += CountWalks(plain);
            aroundWalks += CountWalks(around);
            changed |= GameResultDigest.Sha256Of(plain) != GameResultDigest.Sha256Of(around);
        }
        Assert.True(changed, "くさい所を押しても試合結果が一切変わらない");
        Assert.True(aroundWalks > plainWalks, $"四球が増えていない（なし{plainWalks} → あり{aroundWalks}）");
    }

    private static int CountWalks(GameResult r)
        => r.Log.Count(e => e.IsTop && e.Result == PlateAppearanceResult.Walk);

    /// <summary>後攻（守備側）が先攻の全打席でくさい所を押した試合。</summary>
    private static GameResult Play(ulong seed, bool around)
    {
        var prog = NewGame(seed);
        do
        {
            if (around) prog.SetPitchAround(teamIsAway: false, on: true);
        } while (prog.Advance());
        return prog.BuildResult();
    }

    [Fact]
    public void PitchAround_ReproducesViaSaveRestore()
    {
        var prog = NewGame(42);
        while (prog.Advance() && prog.Current!.Inning < 3) { }
        prog.SetPitchAround(teamIsAway: !prog.Current!.IsTop, on: true);
        while (prog.Advance()) { }
        var direct = GameResultDigest.Sha256Of(prog.BuildResult());

        var save = prog.Save();
        var resumed = GameReplay.Restore(Team("A"), Team("H"), new GameContext(), save);
        while (resumed.Steps.MoveNext()) { }
        var restored = GameResultDigest.Sha256Of(GameEngine.BuildResult(resumed.Progress));

        Assert.Equal(direct, restored);
        Assert.Contains(save.Decisions, d => d.Kind == GameDecisionKind.PitchAround);
    }

    private static MatchProgression NewGame(ulong seed) => new(Team("A"), Team("H"), new GameContext(), seed);

    private static Team Team(string name)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8 };
    }

    private static Player Pos(FieldPosition p) => new()
    {
        Name = p.ToString(),
        Position = p,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50, Speed = 50,
    };
}
