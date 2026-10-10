using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Players;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 内野ゴロの処理点が<b>体の届く所</b>にあること（issue fielder-body-far-from-ball-at-catch）と、
/// 遊撃・二塁がボテボテのゴロに突っ込むこと（issue middle-infielder-charge-cap）。
///
/// <para>①半径 4.2m（内野を抜けるかの判定ノブ）で触れると判定した打球も、処理点・処理時刻は体が飛びつきの
/// 実効リーチ（<see cref="FieldingCoefficients.InfielderDiveReachM"/>）まで寄れた時刻・点で採る。以前は
/// 内野ゴロアウトの 22% で体が球から 3〜4m 離れたまま「捕球→送球」していた。</para>
/// <para>②ミドルの前進上限 4→10m: 79 km/h の遊撃正面ゴロで 3.3m しか出ず内野安打だったものが、8m 以上前へ出て
/// 1.8s 以内に捕りアウトになる。コーナーは不変。</para>
/// </summary>
public sealed class InfieldBodyReachTests
{
    private readonly ITestOutputHelper _out;
    public InfieldBodyReachTests(ITestOutputHelper output) => _out = output;

    private static readonly Aerodynamics Aero = new();
    private static readonly FieldGeometry Field = new();
    private static readonly FieldingCoefficients On = new() { ErrorBaseProb = 0, ThrowErrorBaseProb = 0 };

    private static FieldingPlay Grounder(
        double exitVelocityMps, double bearingDeg, FieldingCoefficients coeff, int fielding = 50, double launchAngleDeg = 4.0)
        => FieldingResolver.ResolveDetailed(
            new BattedBall { ExitVelocityMps = exitVelocityMps, LaunchAngleDeg = launchAngleDeg, BearingDeg = bearingDeg },
            Field, Aero, BatterAttributes.LeagueAverage,
            Field.StandardAlignment(new FielderAttributes { Fielding = fielding }),
            coeff, new Xoshiro256Random(777));

    private static double DistFrom(FieldingPlay p, Vector3D o)
    {
        var dx = (p.FieldedX ?? 0.0) - o.X;
        var dz = (p.FieldedZ ?? 0.0) - o.Z;
        return Math.Sqrt(dx * dx + dz * dz);
    }

    // ===== ② ミドルの前進 =====

    [Fact]
    public void ShortstopChargesASlowRoller_HitStraightAtHim()
    {
        // 遊撃正面（守備位置 (-9,40) の方位 ≈ −12.7°）の 79 km/h（21.9 m/s）。
        var ss = Field.StandardAlignment()[0].Location; // dummy to keep types; overwritten below
        foreach (var f in Field.StandardAlignment()) if (f.Position == FieldPosition.Shortstop) ss = f.Location;
        var bearing = Math.Atan2(ss.X, ss.Z) * 180.0 / Math.PI;
        var p = Grounder(21.9, bearing, On);
        Assert.Equal(FieldPosition.Shortstop, p.FielderRole);
        var advanced = DistFrom(p, ss);
        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "遊撃正面 79km/h: 前進 {0:F1}m / 捕球 {1:F2}s / {2}", advanced, p.FieldedAtSeconds, p.Result));
        // 上限 4m 時代は 3.3m/2.25s で内野安打。上限 10m×緩さ係数（最接近点 12 m/s → 0.6）で 5.9m/2.05s のアウト。
        // 上限を撤廃（≈14m）すると 9.6m/1.76s まで出るが、三遊間の深い緩いゴロが 22%→さらに下がるので 10m に留めた。
        Assert.True(advanced >= 5.5, $"遊撃が {advanced:F1}m しか前へ出ていない（期待 5.5m 以上）");
        Assert.True(p.FieldedAtSeconds <= 2.1, $"捕球 {p.FieldedAtSeconds:F2}s（期待 2.1s 以内）");
        Assert.Equal(BattedBallResult.Out, p.Result);
    }

    [Fact]
    public void CornerCatchPoints_AreUnchangedByTheMiddleCap()
    {
        // コーナー（三塁線・一二塁間）はミドルの上限をどう振っても同じ捕球点・時刻。
        foreach (var (exit, bearing) in new[] { (18.0, -30.0), (14.0, 12.0), (12.0, -6.0) })
        {
            var a = Grounder(exit, bearing, On with { ChargeMaxAdvanceMiddleM = 4.0 });
            var b = Grounder(exit, bearing, On);
            Assert.True(a.FielderRole is FieldPosition.ThirdBase or FieldPosition.FirstBase or FieldPosition.Pitcher, $"{a.FielderRole}");
            Assert.Equal(a.FielderRole, b.FielderRole);
            Assert.Equal(a.FieldedAtSeconds, b.FieldedAtSeconds);
            Assert.Equal(a.FieldedX, b.FieldedX);
        }
    }

    // ===== ① 処理点は体の届く所 =====

    private static Player Pos(FieldPosition pos, int fielding) => new()
    {
        Position = pos, Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = fielding, Catching = 50,
    };

    private static Team BuildTeam(string name, int fielding)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher, fielding), Pos(FieldPosition.FirstBase, fielding),
            Pos(FieldPosition.SecondBase, fielding), Pos(FieldPosition.ThirdBase, fielding),
            Pos(FieldPosition.Shortstop, fielding), Pos(FieldPosition.LeftField, fielding),
            Pos(FieldPosition.CenterField, fielding), Pos(FieldPosition.RightField, fielding),
            Pos(FieldPosition.Pitcher, fielding) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Reserves = new[] { Pos(FieldPosition.Pitcher, fielding) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage } },
        };
    }

    /// <summary>
    /// 内野ゴロアウトの処理野手の「レッグ終端（体）」と「打球セグメント終端（球）」の距離。
    /// 守備50で >2m が 2% 未満、守備80で 5% 未満・最大 2.5m。以前は 22% / 4.2m。
    /// </summary>
    [Theory]
    [Trait("Category", "Heavy")]
    [InlineData(50, false, 0.02, 2.5)]
    [InlineData(80, false, 0.05, 2.5)]
    [InlineData(50, true, 0.02, 2.5)]   // フライ: 守備50は routeFactor=1 で従来から 0%
    [InlineData(80, true, 0.02, 2.5)]   // フライ: 守備80は routeFactor で体が届かなかった（2%・最大3.4m）→ 出足を早める（案C）
    public void GrounderOuts_BodyIsAtTheBall(int fielding, bool fly, double maxFarShare, double maxDistM)
    {
        const int games = 120;
        var far = new int[games];
        var total = new int[games];
        var worst = new double[games];
        System.Threading.Tasks.Parallel.For(0, games, i =>
        {
            var r = GameEngine.Play(BuildTeam("A", fielding), BuildTeam("H", fielding),
                new GameContext { CaptureTimelines = true }, new Xoshiro256Random((ulong)(i + 1)));
            foreach (var e in r.Log)
            {
                if (e.Timeline is null || e.BattedIsFly != fly || e.Result != PlateAppearanceResult.InPlayOut) continue;
                if (e.FieldedBy is not { } role) continue;
                if (!fly && (role is FieldPosition.LeftField or FieldPosition.CenterField or FieldPosition.RightField)) continue; // ゴロは内野だけ
                var leg = e.Timeline.Moves.Where(m => m.Role == role && m.Task == FielderTask.FieldBall)
                    .OrderBy(m => m.T0).LastOrDefault();
                if (leg is null) continue;
                // 打球の終端＝処理点（Roll/Bounce/Flight の最後のセグメントの To）
                var ballEnd = e.Timeline.Ball
                    .Where(b => b.Kind is BallSegmentKind.Roll or BallSegmentKind.Bounce or BallSegmentKind.Flight)
                    .OrderBy(b => b.T1).LastOrDefault();
                if (ballEnd is null) continue;
                var d = Math.Sqrt(Math.Pow(leg.To.X - ballEnd.To.X, 2) + Math.Pow(leg.To.Z - ballEnd.To.Z, 2));
                total[i]++;
                if (d > 2.0) far[i]++;
                if (d > worst[i]) worst[i] = d;
            }
        });
        var n = total.Sum();
        var f = far.Sum();
        var w = worst.Max();
        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "守備{0} {5}: アウト {1} 件・体と球 >2m {2} 件（{3:P1}）・最大 {4:F2}m", fielding, n, f, (double)f / Math.Max(1, n), w, fly ? "フライ" : "内野ゴロ"));
        Assert.True(n > 500);
        Assert.True((double)f / n < maxFarShare, $"体が球から 2m 超で処理したアウトが {f}/{n}（{(double)f / n:P1}）");
        Assert.True(w <= maxDistM, $"体と球の最大距離 {w:F2}m");
    }
}
