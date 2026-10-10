using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 走者保存則の回帰テスト（issue #534）: どの打席でも
/// 「打席前の走者数 + 打者1 = 打席後の走者数 + 得点 + このプレーのアウト数」が成立することを検証する。
/// バント・スクイズ（<see cref="GameEngine"/> の FinishPlateAppearance 引数漏れ）と
/// エンドラン空振り三振（塁状況の観測点が bases 書き換え後だった）で崩れていた（修正前は赤になることを確認済み）。
/// 400試合規模の全打席走査＝Category=Heavy。各試合は独立シードなので Parallel.For で並列化する
/// （GameSimulation.Run と同じ「試合ローカルの乱数・状態で決定論を保ったまま並列化」の作法）。
/// </summary>
[Trait("Category", "Heavy")]
public sealed class RunnerConservationTests
{
    private const int Games = 400;

    [Fact]
    public void EveryPlateAppearance_PreservesRunnerCount()
    {
        var violationsByGame = new ConcurrentBag<string>[Games];
        var paCounts = new int[Games];

        Parallel.For(0, Games, i =>
        {
            var violations = new List<string>();
            var ctx = new GameContext();
            var rng = new Xoshiro256Random(9000UL + (ulong)i);
            var away = StrengthTeamFactory.Create(60, "A", rng) with { Tactics = new StandardTacticsBrain() };
            var home = StrengthTeamFactory.Create(60, "B", rng) with { Tactics = new StandardTacticsBrain() };
            var result = GameEngine.Play(away, home, ctx, rng);

            paCounts[i] = result.Log.Count;
            for (var k = 0; k < result.Log.Count; k++)
            {
                var e = result.Log[k];
                var preRunners = (e.BaseFirstBefore ? 1 : 0) + (e.BaseSecondBefore ? 1 : 0) + (e.BaseThirdBefore ? 1 : 0);
                var postRunners = (e.BaseFirstAfter ? 1 : 0) + (e.BaseSecondAfter ? 1 : 0) + (e.BaseThirdAfter ? 1 : 0);
                // サヨナラの打ち切り（issue #642）: 勝ち越し点より後に本塁へ還った走者は得点にならず、
                // 試合終了で塁上からも消える＝保存則の右辺から抜ける。試合最後の打席・本塁打以外・
                // 1点差で後攻勝ち、のときだけ「右辺が足りない」側の不一致を許す。
                var walkoffTruncated = k == result.Log.Count - 1 && !e.IsTop
                    && e.Inning >= ctx.RegulationInnings && e.Result != PlateAppearanceResult.HomeRun
                    && result.HomeRuns == result.AwayRuns + 1
                    && preRunners + 1 > postRunners + e.RunsScored + e.OutsThisPa;
                if (!walkoffTruncated && preRunners + 1 != postRunners + e.RunsScored + e.OutsThisPa)
                {
                    violations.Add(
                        $"seed={i} inning={e.Inning} top={e.IsTop} batter={e.BatterName} result={e.Result} " +
                        $"pre=({e.BaseFirstBefore},{e.BaseSecondBefore},{e.BaseThirdBefore}) " +
                        $"post=({e.BaseFirstAfter},{e.BaseSecondAfter},{e.BaseThirdAfter}) " +
                        $"runs={e.RunsScored} outs={e.OutsThisPa} isSacBunt={e.IsSacBunt} isSacFly={e.IsSacFly}");
                }
            }
            violationsByGame[i] = new ConcurrentBag<string>(violations);
        });

        var totalPa = paCounts.Sum();
        var allViolations = violationsByGame.SelectMany(b => b).ToList();

        Assert.True(totalPa > 10000, $"打席数が少なすぎる（{totalPa}）＝テスト前提が崩れている");
        Assert.True(allViolations.Count == 0,
            $"走者保存則違反 {allViolations.Count}件（{Games}試合・{totalPa}打席中）:\n" +
            string.Join("\n", allViolations.Take(20)));
    }
}
