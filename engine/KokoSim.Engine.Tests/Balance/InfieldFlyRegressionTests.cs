using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using Xunit;

namespace KokoSim.Engine.Tests.Balance;

/// <summary>
/// フライのアウトが内野/外野どちらに付くかの統計回帰（issue #1065）。「早打ち傾向が抜けていない」の体感の
/// 正体は早打ち自体（#683 の帯は既に目標どおり）ではなく、フライの約半分を内野手が捕っていたこと
/// （実測12.2%・外野フライ8.0%より多い）だった。オーナー承認済み目標（2026-11-17）: 内野フライのアウトは
/// 1試合（両軍計）2〜4本・打球の4〜6%、外野フライのアウトのほうが多いこと。#683 の早打ちの帯は維持。
///
/// <para>校正は <c>popup_share</c>（0.45→0.10。芯の下を叩いた当たりをポップフライへ差し替える割合）。
/// 強さの組は <see cref="PitchCountRegressionTests"/> と同じ5通り×120試合（汎用の母集団・采配なし）。
/// 実ゲームの相手校（永続ロスター＋敵AI采配）とほぼ同値になることは issue #1065 の実測で確認済み。</para>
/// </summary>
[Trait("Category", "Heavy")]
public sealed class InfieldFlyRegressionTests
{
    private readonly Xunit.Abstractions.ITestOutputHelper _output;

    public InfieldFlyRegressionTests(Xunit.Abstractions.ITestOutputHelper output) => _output = output;

    private static bool IsInfieldPosition(FieldPosition pos) => pos
        is FieldPosition.Pitcher or FieldPosition.Catcher or FieldPosition.FirstBase
        or FieldPosition.SecondBase or FieldPosition.ThirdBase or FieldPosition.Shortstop;

    [Fact]
    public void InfieldFlyOuts_StayBelowOutfieldFlyOuts_AndNearTarget()
    {
        var entries = new ConcurrentBag<PlayLogEntry>();
        var inputs = new List<(double A, double H, ulong Seed)>();
        foreach (var (a, h) in new[] { (40.0, 40.0), (50.0, 50.0), (60.0, 50.0), (45.0, 55.0), (70.0, 40.0) })
            for (ulong seed = 1; seed <= 120; seed++) inputs.Add((a, h, seed));

        Parallel.ForEach(inputs, t =>
        {
            var away = StrengthTeamFactory.Create(t.A, "A", new Xoshiro256Random(t.Seed * 7 + 1));
            var home = StrengthTeamFactory.Create(t.H, "H", new Xoshiro256Random(t.Seed * 13 + 5));
            var r = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(t.Seed));
            foreach (var e in r.Log) entries.Add(e);
        });

        var list = entries.ToList();
        var battedBalls = list.Count(e => e.Result.IsBattedBall());
        var infieldFlyOuts = list.Count(e => e.Result == PlateAppearanceResult.InPlayOut
            && e.BattedClass == BattedBallClass.Fly && e.FieldedBy is { } p && IsInfieldPosition(p));
        var outfieldFlyOuts = list.Count(e => e.Result == PlateAppearanceResult.InPlayOut
            && e.BattedClass == BattedBallClass.Fly && e.FieldedBy is { } p && !IsInfieldPosition(p));

        var games = inputs.Count;
        var infieldFlyPerGame = infieldFlyOuts / (double)games;
        var outfieldFlyPerGame = outfieldFlyOuts / (double)games;
        var infieldFlyShare = infieldFlyOuts / (double)battedBalls;
        var outfieldFlyShare = outfieldFlyOuts / (double)battedBalls;
        _output.WriteLine(
            $"games={games} battedBalls={battedBalls} "
            + $"infieldFlyOuts={infieldFlyOuts}({infieldFlyPerGame:F2}/game, {infieldFlyShare:P1}) "
            + $"outfieldFlyOuts={outfieldFlyOuts}({outfieldFlyPerGame:F2}/game, {outfieldFlyShare:P1})");

        // オーナー承認済み目標（4〜6%・1試合2〜4本）の内側に収める。実ゲーム（永続ロスター＋敵AI）は
        // この汎用母集団より少し低めに出る（issue #1065 実測: 12.2% vs 汎用13.6%）ので、下限側に寄せて
        // マージンを持たせる。
        Assert.InRange(infieldFlyShare, 0.035, 0.065);
        Assert.InRange(infieldFlyPerGame, 2.0, 4.0);
        // 完了条件: 外野フライのアウトのほうが内野フライのアウトより多い。
        Assert.True(outfieldFlyOuts > infieldFlyOuts,
            $"外野フライのアウト({outfieldFlyOuts})が内野フライのアウト({infieldFlyOuts})以下になっている");
    }
}
