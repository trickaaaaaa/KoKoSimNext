using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// PlayLogEntry の打球観測フィールド（FieldedBy/BattedClass/BattedBearingDeg/OutsThisPa/IsSacFly/IsSacBunt）の配線検証。
/// 試合画面の打者HUD「本日の打撃成績」（右安・二ゴ等のスコアブック略記）が引く観測データで、
/// すべて「既に確定した FieldingPlay / アウト数の写し」＝判定・乱数順・digest には影響しない（帯不変）。
/// </summary>
public sealed class PlayLogBattedObservationTests
{
    /// <summary>固定シード試合の共有キャッシュ（決定論＝毎回同一。複数Factで再シミュしないため）。</summary>
    private static readonly Lazy<List<GameResult>> Games = new(() => RunGames(60, withTactics: false));

    /// <summary>采配Brain付き（送りバント/スクイズが実際に発生する構成）の共有キャッシュ。</summary>
    private static readonly Lazy<List<GameResult>> TacticsGames = new(() => RunGames(40, withTactics: true));

    private static List<GameResult> RunGames(int count, bool withTactics)
    {
        var ctx = new GameContext();
        var results = new List<GameResult>(count);
        for (ulong seed = 0; seed < (ulong)count; seed++)
        {
            var rng = new Xoshiro256Random(5000 + seed);
            var away = StrengthTeamFactory.Create(60, "A", rng);
            var home = StrengthTeamFactory.Create(60, "B", rng);
            if (withTactics)
            {
                away = away with { Tactics = new StandardTacticsBrain() };
                home = home with { Tactics = new StandardTacticsBrain() };
            }
            results.Add(GameEngine.Play(away, home, ctx, rng));
        }
        return results;
    }

    // (a) 打球で決着した打席は、大半で処理野手（FieldedBy）が写っており、写っている打席は
    //     打球分類・打球方向も必ず揃っている（略記「二ゴ」「右安」を組める最低条件）。
    //     バント経路（Play を持たない）と本塁打（FielderRole=null）があるため全件強制にはしない。
    [Fact]
    public void BattedBallEntries_CarryFielderAndClass()
    {
        var battedResults = new[]
        {
            PlateAppearanceResult.InPlayOut, PlateAppearanceResult.Single, PlateAppearanceResult.Double,
            PlateAppearanceResult.Triple, PlateAppearanceResult.ReachedOnError,
        };
        var batted = Games.Value.SelectMany(r => r.Log).Where(e => battedResults.Contains(e.Result)).ToList();
        Assert.NotEmpty(batted);

        var withFielder = batted.Where(e => e.FieldedBy is not null).ToList();
        Assert.True(withFielder.Count > 0, "打球打席で FieldedBy が一度も記録されなかった（配線未達の疑い）");
        Assert.True(withFielder.Count * 2 > batted.Count,
            $"FieldedBy 非null が過半に届かない（{withFielder.Count}/{batted.Count}）");
        Assert.All(withFielder, e => Assert.NotNull(e.BattedClass));
        Assert.All(withFielder, e => Assert.NotNull(e.BattedBearingDeg));
    }

    // (b) 打球が存在しない決着（三振/四球/死球）では打球観測は空のまま。
    [Fact]
    public void NonBattedEntries_HaveNoFielderObservation()
    {
        var nonBatted = Games.Value.SelectMany(r => r.Log).Where(e => e.Result
            is PlateAppearanceResult.Strikeout or PlateAppearanceResult.Walk or PlateAppearanceResult.HitByPitch);
        Assert.All(nonBatted, e =>
        {
            Assert.Null(e.FieldedBy);
            Assert.Null(e.BattedClass);
            Assert.Null(e.BattedBearingDeg);
            Assert.False(e.BattedIsFly);
        });
    }

    // 本塁打は「処理野手なし・打球方向あり」（左/中/右本の導出に BearingDeg を使う）。
    [Fact]
    public void HomeRuns_HaveBearingButNoFielder()
    {
        var homers = Games.Value.SelectMany(r => r.Log)
            .Where(e => e.Result == PlateAppearanceResult.HomeRun).ToList();
        Assert.True(homers.Count > 0, "60試合で本塁打が一度も出なかった（テスト前提が崩れている）");
        Assert.All(homers, e => Assert.Null(e.FieldedBy));
        Assert.True(homers.Any(e => e.BattedBearingDeg is not null),
            "本塁打で BattedBearingDeg が一度も記録されなかった（配線未達の疑い）");
    }

    // (c) 犠飛フラグの整合: IsSacFly の打席は必ずフライ捕球アウト（InPlayOut）かつ得点1以上。
    [Fact]
    public void SacFlies_AreInPlayOutsWithRuns()
    {
        var sacFlies = Games.Value.SelectMany(r => r.Log).Where(e => e.IsSacFly).ToList();
        Assert.True(sacFlies.Count > 0, "60試合で犠飛が一度も記録されなかった（配線未達の疑い）");
        Assert.All(sacFlies, e =>
        {
            Assert.Equal(PlateAppearanceResult.InPlayOut, e.Result);
            Assert.Equal(1, e.RunsScored);
        });
    }

    // 送りバント/スクイズ成功フラグの整合: 成功＝打者アウト1（OutsThisPa>=1）で、バント経路は
    // FieldingPlay を持たないため FieldedBy は null のまま。
    [Fact]
    public void SacBunts_AreFlaggedWithOneOut()
    {
        var sacBunts = TacticsGames.Value.SelectMany(r => r.Log).Where(e => e.IsSacBunt).ToList();
        Assert.True(sacBunts.Count > 0, "40試合（采配あり）で送りバント成功が一度も記録されなかった（配線未達の疑い）");
        Assert.All(sacBunts, e =>
        {
            Assert.True(e.OutsThisPa >= 1, "犠打成功なのに打席アウトが記録されていない");
            Assert.Null(e.FieldedBy);
        });
    }

    // OutsThisPa の値域と併殺の観測可能性（本日成績の「併殺打」検出に使う）。
    [Fact]
    public void OutsThisPa_WithinRange_AndDoublePlaysObservable()
    {
        var log = Games.Value.SelectMany(r => r.Log).ToList();
        Assert.All(log, e => Assert.InRange(e.OutsThisPa, 0, 3));
        Assert.True(log.Any(e => e.OutsThisPa >= 2), "60試合で併殺（OutsThisPa>=2）が一度も観測されなかった");
    }
}
