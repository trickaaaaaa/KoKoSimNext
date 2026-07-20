using System.Collections.Generic;
using System.Text.Json;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 中断保存の素地（設計者Claude条件3）: GameProgress の保存状態がシリアライズ可能で、
/// 「保存→復元→続行」した最終結果が「中断なしの同シード実行」と完全一致することを保証する。
/// 決定論エンジンなので保存物はシード＋確定打席数（＋采配決定列）だけで足り、復元は再生で状態を再構築する。
/// 打席途中で保存しても enumerator の位置が再生で正しく復元されるため一致する。
/// </summary>
public sealed class GameReplaySaveTests
{
    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Team Team(string name)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Bullpen = new[] { Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage } },
        };
    }

    private static GameResult Uninterrupted(ulong seed)
        => GameEngine.Play(Team("A"), Team("H"), new GameContext(), new Xoshiro256Random(seed));

    [Theory]
    [InlineData(3UL, 1)]    // 1打席目で中断（半頭に近い）
    [InlineData(7UL, 14)]   // イニングを跨いだ打席途中で中断
    [InlineData(42UL, 25)]  // 終盤寄りで中断
    public void SaveRestoreContinue_MatchesUninterrupted(ulong seed, int confirmedPa)
    {
        var full = GameResultDigest.Sha256Of(Uninterrupted(seed));

        // 保存物を作る（シード＋確定打席数）。JSON 往復でシリアライズ可能性を実証。
        var save = new GameSaveState(seed, confirmedPa);
        var json = JsonSerializer.Serialize(save);
        var restored = JsonSerializer.Deserialize<GameSaveState>(json);
        Assert.NotNull(restored);

        // 復元（同シードで confirmedPa ぶん再生して位置を復元）→ 続きを drain。
        var resumed = GameReplay.Restore(Team("A"), Team("H"), new GameContext(), restored!);
        while (resumed.Steps.MoveNext()) { /* 続行 */ }
        var continued = GameResultDigest.Sha256Of(GameEngine.BuildResult(resumed.Progress));

        Assert.Equal(full, continued);
    }

    [Fact]
    public void SaveState_JsonRoundTripsIncludingDecisions()
    {
        var save = new GameSaveState(99UL, 5)
        {
            Decisions = new[] { new GameDecision(3, GameDecisionKind.PinchHit, OffenseIsAway: false, BenchIndex: 0) },
        };
        var json = JsonSerializer.Serialize(save);
        var back = JsonSerializer.Deserialize<GameSaveState>(json);

        Assert.NotNull(back);
        Assert.Equal(99UL, back!.Seed);
        Assert.Equal(5, back.ConfirmedPlateAppearances);
        Assert.Single(back.Decisions);
        Assert.Equal(GameDecisionKind.PinchHit, back.Decisions[0].Kind);
        Assert.Equal(3, back.Decisions[0].AtStep);
    }

    /// <summary>保存ステップ数が試合全打席数を超えても安全（そこで打ち切られ、結果は中断なしと一致）。</summary>
    [Fact]
    public void Restore_BeyondGameEnd_IsSafe()
    {
        var full = GameResultDigest.Sha256Of(Uninterrupted(3UL));
        var resumed = GameReplay.Restore(Team("A"), Team("H"), new GameContext(), new GameSaveState(3UL, 100000));
        while (resumed.Steps.MoveNext()) { }
        Assert.Equal(full, GameResultDigest.Sha256Of(GameEngine.BuildResult(resumed.Progress)));
    }
}
