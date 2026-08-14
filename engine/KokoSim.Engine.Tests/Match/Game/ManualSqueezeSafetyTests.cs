using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// issue #619: 手動スクイズの予約を消費する時点で三塁走者が消えていても、engine は例外を投げない。
///
/// <para>1球ループは「盗塁・牽制の解決 → 手動1球指示の消費」の順に走る（GameEngine）。本盗・牽制死・
/// 重盗で <c>bases.Third</c> が null になった後にスクイズ予約が消費されると、三塁走者 null のまま
/// <see cref="AtBatSession"/> へ渡って <see cref="System.InvalidOperationException"/> が飛び、
/// ライブ進行に try/catch が無いため試合がその場で強制終了していた。</para>
///
/// <para>本盗の抑止ガードは brain 由来のスクイズ（squeezeSign）しか見ていなかったので、手動スクイズは
/// ガードの外に居た。ここでは手動盗塁（本盗）と手動スクイズを同じ球へ同時に予約して、その競合を固定する。</para>
/// </summary>
public sealed class ManualSqueezeSafetyTests
{
    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos, Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
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
            Reserves = TeamReserves.Merge(
                new[] { Pos(FieldPosition.Pitcher) with { Name = name + "R", Pitching = PitcherAttributes.LeagueAverage } },
                System.Array.Empty<Player>()),
        };
    }

    /// <summary>
    /// 同じ球へ「本盗」と「スクイズ」を予約したら、本盗が抑止されスクイズだけが走る。
    ///
    /// <para>修正前は本盗が先に解決して三塁走者を消費し（<c>bases.Third = null</c>）、その後にスクイズ予約が
    /// 消費されていた。打席頭に三塁走者が居るこの場面では <c>AtBatSession</c> が打席頭スナップショットへ
    /// フォールバックするため例外にはならないが、**もう三塁に居ない走者でスクイズが解決される**
    /// （三塁走者が本盗とスクイズに二重に使われる）。抑止ガードが brain 由来のスクイズしか見て
    /// いなかったのが原因。</para>
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    [InlineData(2026UL)]
    public void ManualSqueezeSuppressesStealHome(ulong seed)
    {
        var prog = NewProgressionWithRunnerOnThird(seed);

        // 采配窓（投球の手前）まで進めてから、同じ球へ本盗とスクイズを予約する。
        Assert.Equal(AdvancePitchResult.Pitch, prog.AdvancePitch());
        prog.SetStealAttempt(offenseIsAway: true, StartType.Normal, StealTarget.Home);
        prog.SetPitchBattingOverride(offenseIsAway: true, PitchBattingOverride.Squeeze);

        var ex = Record.Exception(() => prog.AdvancePitch());
        Assert.Null(ex);

        while (prog.AdvancePitch() != AdvancePitchResult.Finished) { }
        var result = prog.BuildResult();

        // 采配Brain も EnableSmallBall も無いので、盗塁が記録されるならこの本盗しかありえない。
        Assert.Equal(0, result.AwayTactics.StealAttempts);
        Assert.Equal(1, result.AwayTactics.Squeezes);
    }

    /// <summary>三塁走者が居ない場面でスクイズだけを予約しても例外にならない（無指示相当へ落ちる）。</summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    public void ManualSqueezeWithoutThirdRunner_DoesNotThrow(ulong seed)
    {
        var prog = new MatchProgression(Team("A"), Team("H"),
            new GameContext { CaptureTimelines = true }, seed);

        Assert.Equal(AdvancePitchResult.Pitch, prog.AdvancePitch()); // 1回表先頭＝無走者
        prog.SetPitchBattingOverride(offenseIsAway: true, PitchBattingOverride.Squeeze);

        var ex = Record.Exception(() => prog.AdvancePitch());
        Assert.Null(ex);
    }

    /// <summary>
    /// 決定論の担保: スクイズ予約が「三塁走者不在で無効化される」球は、予約しなかった試合と
    /// 完全一致する（フォールバックが余分な乱数を引かない＝統計帯が動かない）。
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    public void VoidedSqueeze_MatchesNoOverrideGame(ulong seed)
    {
        var withVoided = new MatchProgression(Team("A"), Team("H"),
            new GameContext { CaptureTimelines = true }, seed);
        Assert.Equal(AdvancePitchResult.Pitch, withVoided.AdvancePitch());
        withVoided.SetPitchBattingOverride(offenseIsAway: true, PitchBattingOverride.Squeeze); // 無走者＝無効
        while (withVoided.AdvancePitch() != AdvancePitchResult.Finished) { }

        var plain = new MatchProgression(Team("A"), Team("H"),
            new GameContext { CaptureTimelines = true }, seed);
        while (plain.AdvancePitch() != AdvancePitchResult.Finished) { }

        Assert.Equal(GameResultDigest.Sha256Of(plain.BuildResult()),
                     GameResultDigest.Sha256Of(withVoided.BuildResult()));
    }

    private static MatchProgression NewProgressionWithRunnerOnThird(ulong seed) =>
        new(Team("A"), Team("H"), new GameContext { CaptureTimelines = true }, seed,
            new KokoSim.Engine.Debugging.ScenarioStart
            {
                Inning = 1, IsTop = true, Outs = 0,
                OnFirst = false, OnSecond = false, OnThird = true,
            });
}
