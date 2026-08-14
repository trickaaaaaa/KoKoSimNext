using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Players;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Timeline;

/// <summary>
/// 一塁の絵と判定が一致する（issue: 判定境界の校正項を物理層へ移す）。
///
/// <para>従来は判定境界に <c>ForceOutMarginSeconds = 0.60</c> が乗っており、
/// <b>判定の秒は動くのに絵の秒には出ない</b>ため「送球が先に届いた絵なのにセーフ」が構造的に出ていた
/// （実測: 内野安打 1,374件のうち 1,147件＝83.5%・平均 0.37秒先着）。校正項を物理層のもたつき
/// （<c>InfieldHandleJitterMeanSeconds</c>）へ移し、判定を素のタイミング比較へ戻した結果、
/// <b>絵の先着＝判定</b>になる。ここはその不変を母集団で固定する。</para>
/// </summary>
public sealed class FirstBasePictureMatchesVerdictTests
{
    private readonly ITestOutputHelper _out;
    public FirstBasePictureMatchesVerdictTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// 一塁送球が絡んだ内野ゴロで、タイムライン上の先着（送球到達 vs 打者の一塁到達）が
    /// 判定（アウト/内野安打）と1件も食い違わない。
    /// </summary>
    [Fact]
    public void ThrowArrivalOrder_NeverContradictsTheCall()
    {
        int outs = 0, hits = 0, contradictions = 0;
        double worstSafe = 0;

        foreach (var e in Plays())
        {
            if (e.Timeline is null) continue;
            // 打者走者の一塁レッグ（"打" の最初のレッグ）と、一塁への送球
            var leg = e.Timeline.Runners.FirstOrDefault(r => r.Label == "打");
            var throwToFirst = e.Timeline.Ball.FirstOrDefault(
                b => b.Kind == BallSegmentKind.Throw && b.ToBase == "first");
            if (leg is null || throwToFirst is null) continue;

            var throwFirst = throwToFirst.T1 <= leg.T1;   // 絵の上で送球が先着したか
            var called = e.Result != PlateAppearanceResult.InPlayOut;   // 判定はセーフ側か

            if (called && throwFirst)
            {
                contradictions++;
                worstSafe = System.Math.Max(worstSafe, leg.T1 - throwToFirst.T1);
            }
            if (called) hits++; else outs++;
        }

        _out.WriteLine($"一塁の勝負 {outs + hits} 件（アウト {outs} / セーフ {hits}）＝食い違い {contradictions} 件"
                       + (contradictions > 0 ? $"・最悪 {worstSafe:F2}s 先着" : ""));

        Assert.True(outs + hits >= 500, $"一塁の勝負が {outs + hits} 件しか採れていない＝テストが空回り");
        Assert.Equal(0, contradictions);
    }

    /// <summary>
    /// セーフの際どさが一様でない（案B＝表示側のガードでは、境界の帯が一律 0.2 秒差へ潰れていた）。
    /// もたつきが実際の秒として払われるので、走者が勝つ量はプレーごとに散る。
    /// </summary>
    [Fact]
    public void SafeMargins_AreNotAllTheSame()
    {
        var margins = new List<double>();
        foreach (var e in Plays())
        {
            if (e.Timeline is null || e.Result == PlateAppearanceResult.InPlayOut) continue;
            var leg = e.Timeline.Runners.FirstOrDefault(r => r.Label == "打");
            var throwToFirst = e.Timeline.Ball.FirstOrDefault(
                b => b.Kind == BallSegmentKind.Throw && b.ToBase == "first");
            if (leg is null || throwToFirst is null) continue;
            margins.Add(throwToFirst.T1 - leg.T1);   // 正＝走者が勝った量
        }

        Assert.True(margins.Count >= 50, $"内野安打が {margins.Count} 件しか採れていない");
        margins.Sort();
        var p10 = margins[margins.Count / 10];
        var p90 = margins[margins.Count * 9 / 10];
        _out.WriteLine($"走者の勝ち幅[s]: p10 {p10:F2} / p50 {margins[margins.Count / 2]:F2} / p90 {p90:F2}");

        Assert.True(p10 > 0, "セーフなのに走者が勝っていない（判定と絵が食い違っている）");
        Assert.True(p90 - p10 > 0.25,
            $"勝ち幅が {p90 - p10:F2}s に潰れている＝際どさの濃淡が失われている（案Bの症状）");
    }

    // ---- 補助 ----

    private static IEnumerable<PlayLogEntry> Plays()
    {
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var r = GameEngine.Play(BuildTeam("A"), BuildTeam("H"),
                new GameContext { CaptureTimelines = true }, new Xoshiro256Random(seed));
            foreach (var e in r.Log) yield return e;
        }
    }

    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Team BuildTeam(string name)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Reserves = new[]
            {
                Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage },
            },
        };
    }
}
