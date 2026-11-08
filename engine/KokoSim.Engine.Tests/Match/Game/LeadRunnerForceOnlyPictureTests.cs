using System.Linq;
using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 先の走者だけを封殺して打者を一塁に残すプレー（issue #797）の絵と記録が、判定と食い違わないこと。
/// 絵は「先の塁へ送球（または自分で運ぶ）して終わり・一塁へは投げない」、打者の一塁到達にアウトの判定は出ない。
/// 数え方: 記録（<see cref="GameResult.LeadRunnerForceOnlyCount"/>）＝絵でそう描かれた打席の数。
/// </summary>
public sealed class LeadRunnerForceOnlyPictureTests
{
    [Fact]
    public void ForceOnlyPlays_AreDrawnWithoutARelayToFirst_AndCountedOnce()
    {
        const int games = 80;
        var counted = new int[games];
        var drawn = new int[games];
        var batterCalledOut = new int[games];
        Parallel.For(0, games, i =>
        {
            var rng = new Xoshiro256Random(9700UL + (ulong)i);
            var away = StrengthTeamFactory.Create(55, "A", rng);
            var home = StrengthTeamFactory.Create(55, "B", rng);
            var r = GameEngine.Play(away, home, new GameContext { CaptureTimelines = true }, rng);
            counted[i] = r.LeadRunnerForceOnlyCount;
            foreach (var e in r.Log)
            {
                if (e.Timeline is not { } tl || e.Result != PlateAppearanceResult.InPlayOut || e.OutsThisPa != 1) continue;
                if (!e.BaseFirstAfter || e.BattedIsFly) continue;
                // 先の走者が封殺された（Force のアウトのレッグがある）のに、一塁への送球が1本も無い打席
                var leadForced = tl.Runners.Any(l => l.OutAtEnd && l.Label != "打");
                var throwToFirst = tl.Ball.Any(b => b.Kind == BallSegmentKind.Throw && b.ToBase == "first");
                if (!leadForced || throwToFirst) continue;
                drawn[i]++;
                if (tl.Judgements.Any(j => j.Label == "打" && j.Kind == JudgementKind.Out)) batterCalledOut[i]++;
            }
        });
        Assert.True(counted.Sum() > 50, $"先の走者だけ封殺が少なすぎる（{counted.Sum()}件）");
        Assert.Equal(counted.Sum(), drawn.Sum());
        Assert.Equal(0, batterCalledOut.Sum());   // 打者は一塁に残る＝アウトの判定を出さない
    }
}
