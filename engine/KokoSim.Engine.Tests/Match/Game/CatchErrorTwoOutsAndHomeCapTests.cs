using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 捕球ミスの二塁走者（issue #950・#903 の残り・design-14 P1-6）。
/// ①2アウトは打った瞬間に走る（ゴロを読む遅れ0）＝三塁を狙う側も本塁を狙う側も同じ。
/// ②本塁へ回す見込みは、同じ打席の三塁に間に合う見込みを超えない（上限）。
/// </summary>
public sealed class CatchErrorTwoOutsAndHomeCapTests
{
    private static readonly FieldGeometry Field = new();
    private static readonly BaserunningCoefficients Coeff = new();
    private static readonly TacticsCoefficients Tactics = new();

    /// <summary>遊撃手の定位置付近で弾いた球。拾い直し完了時刻 <paramref name="readyAt"/> から投げる。</summary>
    private static HomePlaySituation ShortstopRecovery(double readyAt, double armMps = 30.0)
        => new(new Vector3D(-9.0, 0, 30.0), readyAt, armMps);

    private static HomePlayContext Context(HomePlaySituation s)
        => new(Field, s, Tactics, 0.5, DefenseDepth.Normal, IsFly: false, UnrecoveredCatchError: true);

    [Fact]
    public void TwoOuts_NoReadDelay_OtherwiseGrounderDelay()
    {
        Assert.Equal(Coeff.GrounderAdvanceStartDelaySeconds, BaserunningModel.CatchErrorStartDelaySeconds(0, Coeff), 12);
        Assert.Equal(Coeff.GrounderAdvanceStartDelaySeconds, BaserunningModel.CatchErrorStartDelaySeconds(1, Coeff), 12);
        Assert.Equal(0.0, BaserunningModel.CatchErrorStartDelaySeconds(2, Coeff), 12);
    }

    /// <summary>同じ打球でも2アウトなら走り出しが早い＝三塁・本塁ともセーフの見込みが上がる。</summary>
    [Theory]
    [InlineData(2.5)]
    [InlineData(3.5)]
    [InlineData(4.5)]
    public void TwoOuts_RaisesBothThirdAndHomeChances(double readyAt)
    {
        var runner = new Player();
        var h = Context(ShortstopRecovery(readyAt));

        var third1 = BaserunningModel.CatchErrorThirdProbability(runner, h, 1, Coeff);
        var third2 = BaserunningModel.CatchErrorThirdProbability(runner, h, 2, Coeff);
        Assert.True(third2 > third1, $"三塁: 1アウト {third1:F3} → 2アウト {third2:F3}");

        var home1 = HomePlayResolver.SuccessProbability(runner, 2, h.Situation, Field, Coeff,
            BaserunningModel.CatchErrorStartDelaySeconds(1, Coeff));
        var home2 = HomePlayResolver.SuccessProbability(runner, 2, h.Situation, Field, Coeff,
            BaserunningModel.CatchErrorStartDelaySeconds(2, Coeff));
        Assert.True(home2 > home1, $"本塁: 1アウト {home1:F3} → 2アウト {home2:F3}");
    }

    /// <summary>
    /// 2アウトで実際に三塁へ生きた走者は、遅れ0で走る（判定と同じ秒を絵へ渡す）。
    /// 1アウトでは同じ打球で遅れ0.90秒を引き受ける。
    /// </summary>
    [Fact]
    public void TwoOuts_RunnerMoveCarriesZeroStartDelay()
    {
        // 三塁は送るが本塁は送らない遅さを探す（2アウトの基準で）。
        var runner = new Player();
        HomePlaySituation? found = null;
        for (var t = 1.0; t < 6.0; t += 0.01)
        {
            var s = ShortstopRecovery(t);
            var h = Context(s);
            var third = BaserunningModel.CatchErrorThirdProbability(runner, h, 2, Coeff);
            var home = BaserunningModel.CatchErrorHomeSendProbability(runner, h, 2, Coeff);
            if (HomeSendDecision.ShouldSendThird(third, 2, 0.5, Tactics) && !HomeSendDecision.ShouldSend(home, 2, 0.5, Tactics)
                && third > 0.9)
            { found = s; break; }
        }
        Assert.NotNull(found);

        var rng = new Xoshiro256Random(950);
        var sawSafe = false;
        for (var i = 0; i < 200 && !sawSafe; i++)
        {
            var bases = new BaseState { Second = runner };
            var (_, extraOuts, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.ReachedOnError, new Player(), 2, Coeff, rng,
                collectMoves: true, Context(found!.Value), errorKind: FieldingErrorKind.Catch);
            if (extraOuts != 0) continue;
            var m = Assert.Single(moves, x => ReferenceEquals(x.Runner, runner));
            Assert.Equal((2, 3, false), (m.FromBase, m.ToBase, m.Out));
            Assert.Equal(0.0, m.ExtraStartDelaySeconds, 9);
            sawSafe = true;
        }
        Assert.True(sawSafe);
    }

    /// <summary>
    /// 本塁へ回す見込みは、同じ打席の三塁の見込みを超えない（拾い直しの遅さ・肩・アウト数・走力を振って確かめる）。
    /// 上限が無いと、拾い直しが遅い打席で本塁の方が三塁より安全に見える（遊撃の定位置で4.5秒: 本塁0.98／三塁0.97）。
    /// </summary>
    [Fact]
    public void HomeSendChance_NeverExceedsThirdChance()
    {
        // 既定係数では、読みの遅れを本塁・三塁で揃えた（#950）ことで、生の本塁の見込みは三塁を超えない。
        // 上限が効く形を確かめるため、本塁の係数を甘くした版（本塁バイアス+1秒）でも回す。
        Assert.False(HomeChanceExceedsThirdSomewhere(Coeff, out _), "上限の後でも本塁の見込みが三塁を超えた");
        Assert.False(HomeChanceExceedsThirdSomewhere(Coeff with { HomeSuccessBias = Coeff.HomeSuccessBias + 1.0 }, out var capped),
            "上限の後でも本塁の見込みが三塁を超えた（甘い本塁係数）");
        Assert.True(capped, "甘い本塁係数でも上限が一度も効いていない（＝テストが空回り）");
    }

    private static bool HomeChanceExceedsThirdSomewhere(BaserunningCoefficients coeff, out bool anyCapped)
    {
        anyCapped = false;
        foreach (var legs in new[] { 20, 50, 80, 100 })
            foreach (var arm in new[] { 18.0, 26.0, 34.0, 40.0 })
                foreach (var outs in new[] { 0, 1, 2 })
                    for (var t = 0.0; t <= 9.0; t += 0.25)
                    {
                        var runner = new Player { Speed = legs, Baserunning = legs };
                        var h = Context(ShortstopRecovery(t, arm));
                        var third = BaserunningModel.CatchErrorThirdProbability(runner, h, outs, coeff);
                        var home = BaserunningModel.CatchErrorHomeSendProbability(runner, h, outs, coeff);
                        if (home > third + 1e-12) return true;
                        var raw = HomePlayResolver.SuccessProbability(runner, 2, h.Situation, Field, coeff,
                            BaserunningModel.CatchErrorStartDelaySeconds(outs, coeff));
                        if (raw > third + 1e-9) anyCapped = true;
                    }
        return false;
    }

    /// <summary>
    /// 上限は実際の送り判断に効く: 生の本塁の見込みは送り閾値を超えるが三塁の見込みが届かない打席では、
    /// 二塁走者を本塁へ送らない。
    /// </summary>
    [Fact]
    public void CappedHomeChance_IsWhatTheSendDecisionUses()
    {
        // 既定係数では上限が効く打席が無い（上のテスト）ので、本塁の係数をうんと甘くした版（本塁バイアス+3秒）で確かめる。
        var coeff = Coeff with { HomeSuccessBias = Coeff.HomeSuccessBias + 3.0 };
        var runner = new Player();
        var sendHome = HomeSendDecision.SendThreshold(0, 0.5, Tactics);
        for (var t = 0.0; t < 8.0; t += 0.01)
        {
            var h = Context(ShortstopRecovery(t));
            var raw = HomePlayResolver.SuccessProbability(runner, 2, h.Situation, Field, coeff,
                BaserunningModel.CatchErrorStartDelaySeconds(0, coeff));
            var capped = BaserunningModel.CatchErrorHomeSendProbability(runner, h, 0, coeff);
            if (raw < sendHome || capped >= sendHome) continue;

            // ここ＝上限が無ければ本塁へ送っていた打席。上限で本塁は送らない（三塁の判断へ進む）。
            var rng = new Xoshiro256Random(9500);
            var bases = new BaseState { Second = runner };
            var (runs, _, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.ReachedOnError, new Player(), 0, coeff, rng,
                collectMoves: true, h, errorKind: FieldingErrorKind.Catch);
            Assert.Equal(0, runs);
            Assert.DoesNotContain(moves, m => ReferenceEquals(m.Runner, runner) && m.ToBase == 4);
            return;
        }
        Assert.Fail("上限が送り判断を変える打席が見つからない（＝テストが空回り）");
    }

    /// <summary>
    /// 一塁走者だけの捕球ミスで三塁を狙う勝負も、二塁走者と同じ読みの遅れ（#950）を引き受ける（issue #1293）。
    /// 1アウト以下は <see cref="BaserunningCoefficients.GrounderAdvanceStartDelaySeconds"/>、2アウトは0。
    /// 見込みも遅れ込みなので、遅れなしの見込みより下がる。
    /// </summary>
    [Theory]
    [InlineData(1, true)]
    [InlineData(2, false)]
    public void FirstRunnerToThird_CarriesTheSameReadDelay(int outs, bool delayed)
    {
        var runner = new Player();
        var third = HomePlayResolver.ThirdParams(Field, Coeff);
        var delay = BaserunningModel.CatchErrorStartDelaySeconds(outs, Coeff);
        Assert.Equal(delayed, delay > 0.0);

        // 遅れ込みの見込みで三塁へ送る打席を探す（拾い直しが遅いほど送りやすい＝送る境目の少し先）
        HomePlaySituation? found = null;
        for (var t = 2.0; t < 12.0; t += 0.01)
        {
            var s = ShortstopRecovery(t);
            var withDelay = HomePlayResolver.SuccessProbability(runner, 1, s, Field, Coeff, third, delay);
            if (HomeSendDecision.ShouldSendThird(withDelay, outs, 0.5, Tactics) && withDelay < 0.97)
            { found = s; break; }
        }
        Assert.NotNull(found);

        var rng = new Xoshiro256Random(1293);
        var sawThirdMove = false;
        for (var i = 0; i < 300 && !sawThirdMove; i++)
        {
            var bases = new BaseState { First = runner };
            var (_, _, _, _, _, moves) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.ReachedOnError, new Player(), outs, Coeff, rng,
                collectMoves: true, Context(found!.Value), errorKind: FieldingErrorKind.Catch);
            var m = moves.FirstOrDefault(x => ReferenceEquals(x.Runner, runner) && x.ToBase == 3);
            if (m is null) continue;
            Assert.True(m.ExtraStartDelaySeconds >= delay - 1e-9,
                $"{outs}アウト: 一塁→三塁の走り出しの遅れ {m.ExtraStartDelaySeconds:F3} < 読みの遅れ {delay:F3}");
            sawThirdMove = true;
        }
        Assert.True(sawThirdMove, "一塁走者が三塁を狙う打席が出ない（＝テストが空回り）");
    }
}
