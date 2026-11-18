using System.Linq;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 決定論ベースラインが伝令役の人選（issue #716）を見張れていること（issue #721）。
/// 「messenger」カードで伝令役の係数を中立（常に1.0）に固定すると、少なくとも1シードの digest が変わる
/// ＝AI の伝令で選んだ控えの効き目が試合結果に届いている。他のカードは控えが中立で、この差が出ない。
/// </summary>
public sealed class MessengerDeterminismWatchTests
{
    [Fact]
    public void MessengerCard_DigestDependsOnMessengerFactor()
    {
        var neutral = new GameContext
        {
            Tactics = new TacticsCoefficients { MessengerFactorMin = 1.0, MessengerFactorMax = 1.0 },
        };
        // 決定論ゲート共通の50シード（DeterminismCards.Seeds）だけに固定すると、伝令役と無関係な
        // エンジン変更（打球判定の校正等）が試合全体のRNG消費列をずらすだけで「たまたまこの50本には
        // 伝令役が利く分岐点が無い」という偽陰性になりうる（issue #1135対応時に実際に発生・幅を広げた
        // 500シードでは34本が分岐=見張り自体は健全）。見張りの対象はこのテスト専用に広く取る。
        var changed = Enumerable.Range(1, 500)
            .Select(i => (ulong)i)
            .AsParallel()
            .Count(seed => GameResultDigest.Sha256Of(DeterminismCards.RunMessenger(seed, new GameContext()))
                        != GameResultDigest.Sha256Of(DeterminismCards.RunMessenger(seed, neutral)));
        Assert.True(changed > 0, "伝令役の係数を中立にしても digest が1つも変わらない＝人選が試合に届いていない（見張りが効かない）");
    }

    [Fact]
    public void AvgCard_ReservesAreNeutral_SoMessengerFactorIsAlwaysOne()
    {
        // 既存カードの控えは中立＝伝令役の係数は誰でも1.0（だから messenger カードが要る）。
        var t = DeterminismCards.Team("A");
        var c = new TacticsCoefficients();
        var pc = new KokoSim.Engine.Players.PersonalityCoefficients();
        foreach (var r in t.Reserves)
            Assert.Equal(1.0, TimeoutMessenger.Factor(r, t.BattingOrder[8], toPitcher: true, c, pc), 10);
    }
}
