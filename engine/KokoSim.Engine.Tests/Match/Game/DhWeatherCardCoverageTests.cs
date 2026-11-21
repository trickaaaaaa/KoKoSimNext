using System.Linq;
using KokoSim.Engine.Match.Field;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 決定論ゲートの "dh-weather" カード（issue #1232）が、狙った経路を本当に通っているかの見張り。
/// 条件を1つずつ外したとき、5シードのどれかで digest が変わらなければ、その条件はカードに効いていない
/// （＝その経路だけを変える変更が指紋をすり抜ける）。
/// </summary>
public sealed class DhWeatherCardCoverageTests
{
    private static readonly ulong[] Seeds = { 1, 2, 3, 4, 5 };

    private static string[] Digests(KokoSim.Engine.Match.Game.GameContext ctx, bool useDh = true)
        => Seeds.Select(s => GameResultDigest.Sha256Of(DeterminismCards.RunDhWeather(s, ctx, useDh))).ToArray();

    private static void AssertDiffers(string what, string[] baseline, string[] variant)
        => Assert.False(baseline.SequenceEqual(variant), $"{what} を外しても digest が変わらない＝カードがこの経路を見張れていない");

    [Fact]
    public void EachCondition_ChangesDigest()
    {
        var ctx = DeterminismCards.DhWeatherContext;
        var baseline = Digests(ctx);

        AssertDiffers("DH", baseline, Digests(ctx, useDh: false));
        AssertDiffers("天候", baseline, Digests(ctx with { Weather = null }));
        AssertDiffers("左右非対称の球場", baseline, Digests(ctx with { Field = new FieldGeometry() }));
        AssertDiffers("球数上限", baseline, Digests(ctx with { WeeklyPitchLimit = int.MaxValue }));
    }
}
