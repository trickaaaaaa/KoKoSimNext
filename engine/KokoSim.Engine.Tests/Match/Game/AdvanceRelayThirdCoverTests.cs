using System;
using System.Linq;
using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// ゴロの進塁打で二塁走者を「一塁→三塁」の転送で刺す絵（issue #801）に、三塁へ入る野手の動きがあること（issue #1416）。
/// 動きが無いと三塁手が守備位置に留まったまま、球だけが無人の三塁へ着いてアウトになる。
/// </summary>
public sealed class AdvanceRelayThirdCoverTests
{
    private static readonly FieldGeometry Field = new();

    [Fact]
    public void RelayToThird_HasAFielderCoveringTheBaseBeforeTheThrowArrives()
    {
        const int games = 600;
        var relays = new int[games];
        var failures = new System.Collections.Concurrent.ConcurrentBag<string>();
        var third = new TimelinePoint(Field.ThirdBase.X, Field.ThirdBase.Z);
        var first = new TimelinePoint(Field.FirstBase.X, Field.FirstBase.Z);
        Parallel.For(0, games, i =>
        {
            var seed = (ulong)(i + 1);
            var away = StrengthTeamFactory.Create(55, "先攻", new Xoshiro256Random(seed * 2));
            var home = StrengthTeamFactory.Create(55, "後攻", new Xoshiro256Random(seed * 2 + 1));
            var r = GameEngine.Play(away, home, new GameContext { CaptureTimelines = true }, new Xoshiro256Random(seed));
            foreach (var e in r.Log)
            {
                if (e.Timeline is not { } tl) continue;
                foreach (var seg in tl.Ball)
                {
                    if (seg.Kind != BallSegmentKind.Throw || seg.ToBase != "third") continue;
                    if (Dist(seg.From, first) > 2.0) continue;   // 一塁からの転送だけ
                    relays[i]++;
                    var role = seg.ToRole;
                    var cover = tl.Moves.FirstOrDefault(m => m.Role == role && m.Task == FielderTask.CoverBase
                        && Dist(m.To, third) < 1.0);
                    if (cover is null) failures.Add($"seed={seed} 三塁へ入る動きが無い: 受け手={role}");
                    else if (cover.T1 > seg.T1 + 1e-6) failures.Add($"seed={seed} 球より遅れて着く: 野手{cover.T1:F2}s 球{seg.T1:F2}s");
                }
            }
        });
        Assert.True(relays.Sum() >= 5, $"一塁→三塁の転送が少なすぎる（{relays.Sum()}件）");
        Assert.True(failures.IsEmpty, string.Join("\n", failures.Take(10)));
    }

    private static double Dist(TimelinePoint a, TimelinePoint b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Z - b.Z) * (a.Z - b.Z));
}
