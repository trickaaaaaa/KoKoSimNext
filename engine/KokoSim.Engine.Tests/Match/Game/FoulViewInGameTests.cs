using System.Linq;
using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Nation;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 守備ビューで見せるファウル（issue #1074）を実際の試合で確かめる。
/// 捕れなかったファウルフライも打球と「一番早く追える野手」を持ち、(a)(b) の両方が試合の中で出ること。
/// </summary>
public sealed class FoulViewInGameTests
{
    private readonly ITestOutputHelper _out;
    public FoulViewInGameTests(ITestOutputHelper output) => _out = output;

    [Fact]
    public void UncaughtFoulFlies_CarryTheBall_AndBothFieldViewKindsAppear()
    {
        const int games = 40;
        var fouls = new int[games];
        var withBall = new int[games];
        var chased = new int[games];
        var big = new int[games];
        var chaseOutsideFoulGround = new int[games];
        Parallel.For(0, games, i =>
        {
            var rng = new Xoshiro256Random(10740UL + (ulong)i);
            var away = StrengthTeamFactory.Create(55, "A", rng);
            var home = StrengthTeamFactory.Create(55, "B", rng);
            var ctx = new GameContext { CaptureTimelines = true };
            var r = GameEngine.Play(away, home, ctx, rng);
            foreach (var e in r.Log)
            {
                if (e.PitchLog is not { } log) continue;
                foreach (var p in log)
                {
                    if (p.Kind != PitchKind.Foul) continue;
                    fouls[i]++;
                    if (p.BattedBall is null) continue;
                    withBall[i]++;
                    var tok = PitchTokenFactory.From(p, ctx, false, false, false);
                    if (tok.FoulFlight is not { } play) continue;
                    if (play.FoulView == FoulViewKind.ChasedFly)
                    {
                        chased[i]++;
                        if (p.FoulChase is null) chaseOutsideFoulGround[i]++;
                    }
                    if (play.FoulView == FoulViewKind.BigFoul) big[i]++;
                }
            }
        });

        _out.WriteLine($"{games}試合: ファウル {fouls.Sum()} 球 / 打球あり {withBall.Sum()} / "
                       + $"(a)あと一歩のフライ {chased.Sum()}（1試合 {chased.Sum() / (double)games:F2}）/ "
                       + $"(b)ポール際の大ファウル {big.Sum()}（1試合 {big.Sum() / (double)games:F2}）");

        Assert.True(withBall.Sum() > 0);
        Assert.True(chased.Sum() > 0, "(a) あと一歩届かなかったファウルフライが1つも出ない");
        Assert.True(big.Sum() > 0, "(b) ポール際の大ファウルが1つも出ない");
        Assert.Equal(0, chaseOutsideFoulGround.Sum());
        // 守備ビューへ移すのは「見どころのある」ファウルだけ＝ファウル全体のごく一部
        Assert.True(chased.Sum() + big.Sum() < fouls.Sum() / 5,
            $"守備ビューへ移すファウルが多すぎる（{chased.Sum() + big.Sum()} / {fouls.Sum()}）");
    }

    /// <summary>観戦しない試合（統計シム・裏試合）では追う野手を計算しない＝ゼロコスト。</summary>
    [Fact]
    public void WithoutCapture_NoChaseIsComputed()
    {
        var rng = new Xoshiro256Random(10741UL);
        var away = StrengthTeamFactory.Create(55, "A", rng);
        var home = StrengthTeamFactory.Create(55, "B", rng);
        var r = GameEngine.Play(away, home, new GameContext(), rng);
        Assert.All(r.Log.Where(e => e.PitchLog != null).SelectMany(e => e.PitchLog!),
            p => Assert.Null(p.FoulChase));
    }
}
