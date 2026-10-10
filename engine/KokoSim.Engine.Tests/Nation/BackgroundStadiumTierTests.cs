using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// issue #636: 県大会の裏試合（AI校同士）も、自校の試合と同じ汎用ティア球場（設計書13 §1）で解く。
/// 自校側（Shell の PlayerMatchResolver → MatchStadium）は <c>RegionalStadiumTier.For(残りラウンド, rng.Fork(13))</c>
/// で球場を決める。裏試合も同じ入力・同じ式で決まっていることを、「その球場を ctx に直接置いた解決」との
/// 完全一致で固定する（球場は GameResult に載らないので、結果の一致で配線を確かめる）。
/// </summary>
public sealed class BackgroundStadiumTierTests
{
    private static readonly School Away = new() { Id = 901, Name = "先攻", PrefectureId = 13, Strength = 55 };
    private static readonly School Home = new() { Id = 902, Name = "後攻", PrefectureId = 13, Strength = 58 };

    private static string Resolve(GameContext ctx, bool useTier, int roundsRemaining, ulong seed)
    {
        var bg = new BackgroundMatchResolver(new NationRosters(new AiRosterDeps()), ctx, yearIndex: 1,
            useRegionalStadiumTier: useTier);
        var context = new TournamentMatchContext(roundsRemaining, MatchDay: 1, new TournamentPitchLedger());
        return GameResultDigest.Sha256Of(bg.Resolve(Away, Home, new Xoshiro256Random(seed), context));
    }

    /// <summary>決勝・準決勝は県営級、それより前は市営級2種（試合の乱数の専用 Fork で抽選）。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(6)]
    public void TierOn_UsesTheSameStadiumAsThePlayerSide(int roundsRemaining)
    {
        for (ulong seed = 1; seed <= 6; seed++)
        {
            var expectedField = RegionalStadiumTier.For(roundsRemaining, new Xoshiro256Random(seed).Fork(13));
            var expected = Resolve(new GameContext { Field = expectedField }, useTier: false, roundsRemaining, seed);
            Assert.Equal(expected, Resolve(new GameContext(), useTier: true, roundsRemaining, seed));
        }
    }

    /// <summary>
    /// 既定（フラグなし）は ctx の Field のまま＝甲子園・地区大会など県大会以外の呼び出しは従来どおり。
    /// 甲子園の寸法を渡したとき、フラグありは決勝を県営級へ差し替える＝少なくとも1シードで結果が変わる
    /// （フラグなしが甲子園で解いていることの裏返し。球場が効いていなければ全シード一致する）。
    /// </summary>
    [Fact]
    public void TierOff_KeepsContextField()
    {
        var koshien = new GameContext { Field = Stadium.Koshien.ToFieldGeometry() };
        var differs = false;
        for (ulong seed = 1; seed <= 10 && !differs; seed++)
            differs = Resolve(koshien, useTier: false, 1, seed) != Resolve(koshien, useTier: true, 1, seed);
        Assert.True(differs, "県営級と甲子園で10シードとも結果が同じ＝球場が効いていない疑い");
    }
}
