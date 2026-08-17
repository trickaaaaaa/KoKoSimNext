using System.Linq;
using KokoSim.Engine.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 投手のアウト計上（投球回）の完全性。「9回完投したのに成績画面で8回2/3」の回帰テスト:
/// 打席の外で成立するアウト（盗塁死・牽制死・スクイズ挟殺）は打席確定時の RecordPitching を
/// 通らないため、投手のアウト数に入っていなかった。
/// 表のハーフイニングは必ず3アウトで完了する（サヨナラ・コールドで途切れるのは裏だけ）ので、
/// 「後攻の投手陣のアウト合計 = 3 × 先攻が攻撃したイニング数」が全試合で成り立つ。
/// </summary>
public sealed class PitcherOutsAccountingTests
{
    [Fact]
    public void HomePitcherOuts_EqualThreePerTopHalf_AcrossTacticsGames()
    {
        var awayCaughtStealing = 0;
        var pickoffs = 0;
        // 采配 Brain 付きの2カード（StandardTacticsBrain／AiTacticsBrain 高ティア）を両方回す。
        // 平均能力の "tactics" だけだと盗塁企図が 50 試合で 1 件程度＝乱数列がずれただけで下の
        // 「経路を踏んだ」ガードが空振りする（2026-08-18 実測: 1→0 件）。
        foreach (var card in new[] { "tactics", "pitch-tactics" })
        foreach (var seed in DeterminismCards.Seeds())
        {
            var r = DeterminismCards.Run(card, seed);
            Assert.Equal(3 * r.AwayLineScore.Count, r.HomePitching.Sum(p => p.Outs));
            awayCaughtStealing += r.AwayTactics.StealAttempts - r.AwayTactics.StealSuccesses;
            pickoffs += r.PickoffCount;
        }
        // 対象経路（打席外のアウト）が実際に踏まれたことの確認。1件も無ければ上の等式は
        // バグがあっても成立してしまう＝このテストは何も検証していないことになる。
        Assert.True(awayCaughtStealing + pickoffs > 0,
            $"100試合で盗塁死も牽制死も発生していない（cs={awayCaughtStealing} pk={pickoffs}。係数・采配Brainを確認）");
    }

    [Fact]
    public void RecordRunnerOut_AddsOutsToPitcherLine_WithoutBattersFaced()
    {
        var state = new TeamState(DeterminismCards.Team("A"));
        var pitcher = state.CurrentPitcher;
        state.RecordRunnerOut(pitcher);
        state.RecordRunnerOut(pitcher);
        state.RecordRunnerOut(pitcher, -1); // issue #626: ファウルで無効になった盗塁死の巻き戻し
        var line = Assert.Single(state.BuildPitchingLines());
        Assert.Equal(1, line.Outs);
        Assert.Equal(0, line.BattersFaced);
    }
}
