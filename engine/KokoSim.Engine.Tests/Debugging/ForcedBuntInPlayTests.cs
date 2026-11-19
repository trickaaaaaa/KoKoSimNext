using System.Collections.Generic;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Match.Timeline.Playback;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>
/// 強制発動 <see cref="ForcedOutcome.BuntInPlay"/>（撮影レシピ用）: 指示したバントが1球目で転がる。
/// 空振り・ファウル・小フライ・ボール球の見送りを0にし、転がしの成功率は式の上限 0.98 に張り付く。
/// </summary>
public sealed class ForcedBuntInPlayTests
{
    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos, Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50, Bunt = 50,
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
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8 };
    }

    /// <summary>
    /// 無死一塁で強制＋バント指示 → 1球目で打席が終わる（ボール球の見送り・空振り・ファウルで打席が続かない）。
    /// 強制した係数が打席の中（AtBatSession→BuntResolver）まで届いていることの確認。
    /// </summary>
    [Theory]
    [InlineData(1UL)]
    [InlineData(7UL)]
    [InlineData(42UL)]
    [InlineData(2026UL)]
    public void ForcedBunt_EndsThePlateAppearanceOnTheFirstPitch(ulong seed)
    {
        var prog = new MatchProgression(Team("A"), Team("H"), new GameContext { CaptureTimelines = true }, seed,
            new ScenarioStart { Inning = 1, IsTop = true, Outs = 0, OnFirst = true });
        prog.ForceNext(ForcedOutcome.BuntInPlay);
        Assert.Equal(AdvancePitchResult.Pitch, prog.AdvancePitch());   // 1球目の手前の采配窓
        prog.SetPitchBattingOverride(offenseIsAway: true, PitchBattingOverride.Bunt);
        Assert.Equal(AdvancePitchResult.PlateAppearance, prog.AdvancePitch());
        Assert.NotNull(prog.Current?.Play);
    }

    /// <summary>強制しなければ、同じ指示でも1球目で打席が終わらない試合がある（＝上の検査が素通りしていない）。</summary>
    [Fact]
    public void WithoutForcing_SomeBuntsDoNotEndThePlateAppearance()
    {
        int continued = 0;
        for (ulong seed = 1; seed <= 30; seed++)
        {
            var prog = new MatchProgression(Team("A"), Team("H"), new GameContext { CaptureTimelines = true }, seed,
                new ScenarioStart { Inning = 1, IsTop = true, Outs = 0, OnFirst = true });
            Assert.Equal(AdvancePitchResult.Pitch, prog.AdvancePitch());
            prog.SetPitchBattingOverride(offenseIsAway: true, PitchBattingOverride.Bunt);
            if (prog.AdvancePitch() == AdvancePitchResult.Pitch) continued++;
        }
        Assert.True(continued > 0, "強制なしでも30試合すべて1球目で終わった（検査の前提が崩れている）");
    }

    /// <summary>強制した係数では、バントの解決が空振り・ファウル・小フライを返さず、ボール球も見送らない。</summary>
    [Fact]
    public void AppliedCoefficients_RemoveMissFoulPopAndTake()
    {
        var c = ForcedOutcomes.Apply(new BaserunningCoefficients(), ForcedOutcome.BuntInPlay);
        var batter = Pos(FieldPosition.LeftField) with { Bunt = 10, Discipline = 99 };
        Assert.Equal(0.0, BuntResolver.TakeProbability(0.3, batter, c));
        Assert.Equal(0.98, BuntResolver.SuccessRate(batter, PitcherAttributes.LeagueAverage, 1.0, c), 6);
        var rng = new KokoSim.Engine.Core.Xoshiro256Random(11);
        var field = new FieldGeometry();
        var fielders = field.StandardAlignment();
        var fieldingCoeff = new KokoSim.Engine.Match.Fielding.FieldingCoefficients();
        int pops = 0;
        for (int i = 0; i < 500; i++)
        {
            var r = BuntResolver.Resolve(batter, PitcherAttributes.LeagueAverage, 1.0, 0.5, false,
                null, null, null, field, fielders, fieldingCoeff, c, rng);
            Assert.NotEqual(BuntResult.MissedBunt, r);
            Assert.NotEqual(BuntResult.Foul, r);
            if (r is BuntResult.PopOut or BuntResult.PopOutDoubledOff) pops++;
        }
        // 小フライは「転がしそこね」（成功率の上限 0.98 の残り）だけ＝約2%
        Assert.True(pops <= 25, $"小フライが {pops}/500");
    }
}
