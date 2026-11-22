using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Debugging;

/// <summary>issue #1235: 再現トークン指紋の欠け・乱数消費の数え方・リングバッファの負の件数。</summary>
public sealed class DebugToolDetailTests
{
    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos, Name = pos.ToString(),
    };

    private static Team Team(Func<Player, Player>? edit = null)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            Pos(FieldPosition.Pitcher) with { Pitching = PitcherAttributes.LeagueAverage },
        };
        if (edit is not null) order[0] = edit(order[0]);
        return new Team { Name = "A", BattingOrder = order, PitcherSlot = 8 };
    }

    private static string Fp(Func<Player, Player>? edit = null)
        => ReproToken.Fingerprint(Team(edit), Team(), new GameContext());

    public static IEnumerable<object[]> Edits()
    {
        yield return new object[] { "ThrowAccuracy", (Func<Player, Player>)(p => p with { ThrowAccuracy = 70 }) };
        yield return new object[] { "Lead", (Func<Player, Player>)(p => p with { Lead = 70 }) };
        yield return new object[] { "Bunt", (Func<Player, Player>)(p => p with { Bunt = 70 }) };
        yield return new object[] { "Steal", (Func<Player, Player>)(p => p with { Steal = 70 }) };
        yield return new object[] { "Baserunning", (Func<Player, Player>)(p => p with { Baserunning = 70 }) };
        yield return new object[] { "Throws", (Func<Player, Player>)(p => p with { Throws = Handedness.Left }) };
        yield return new object[] { "Bats", (Func<Player, Player>)(p => p with { Bats = Handedness.Left }) };
        yield return new object[] { "Skills", (Func<Player, Player>)(p => p with { Skills = new SkillSet(new[] { Skill.ClutchHitter }) }) };
        yield return new object[] { "HiddenSkills", (Func<Player, Player>)(p => p with { Skills = new SkillSet(null, new[] { Skill.ClutchHitter }) }) };
        yield return new object[] { "PitchRank", (Func<Player, Player>)(p => p with { Pitching = p.Pitching with { PitchRank = 70 } }) };
        yield return new object[] { "Style", (Func<Player, Player>)(p => p with { Pitching = p.Pitching with { Style = ThrowingStyle.Sidearm } }) };
        yield return new object[] { "Repertoire", (Func<Player, Player>)(p => p with
        {
            Pitching = p.Pitching with
            {
                Repertoire = new[]
                {
                    PitchSlot.FastballOf(50),
                    new PitchSlot { Type = PitchType.Slider, Power = 50, Sharpness = 60 },
                },
            },
        }) };
    }

    [Theory]
    [MemberData(nameof(Edits))]
    public void Fingerprint_ChangesWhenThePlayerFieldChanges(string field, Func<Player, Player> edit)
    {
        Assert.NotEqual(Fp(), Fp(edit));
        Assert.False(string.IsNullOrEmpty(field));
    }

    [Fact]
    public void Fingerprint_RepertoireSharpness_Matters()
    {
        Player Slider(int sharp) => Pos(FieldPosition.Catcher) with
        {
            Pitching = new PitcherAttributes
            {
                Repertoire = new[] { PitchSlot.FastballOf(50), new PitchSlot { Type = PitchType.Slider, Power = 50, Sharpness = sharp } },
            },
        };
        Assert.NotEqual(Fp(_ => Slider(50)), Fp(_ => Slider(51)));
    }

    [Fact]
    public void Fingerprint_SkillOrderAndVisibility_DoNotMatter()
    {
        var a = new SkillSet(new[] { Skill.ClutchHitter, Skill.Durable });
        var b = new SkillSet(new[] { Skill.Durable }, new[] { Skill.ClutchHitter });
        Assert.Equal(Fp(p => p with { Skills = a }), Fp(p => p with { Skills = b }));
    }

    [Fact]
    public void Token_WithOldFormatFingerprint_FailsVerify()
    {
        var token = new ReproToken(new Xoshiro256Random(1UL).CaptureState(), 3, 1, "0badf00d");
        Assert.False(token.Verify(Team(), Team(), new GameContext()));
        var current = new ReproToken(token.RngState, 3, 1, Fp());
        Assert.True(current.Verify(Team(), Team(), new GameContext()));
        // 走塁だけ育った後のロスターには、育つ前のトークンはもう通らない。
        Assert.False(current.Verify(Team(p => p with { Steal = 80 }), Team(), new GameContext()));
    }

    [Fact]
    public void CountingRandomSource_CountsGaussianByActualConsumption()
    {
        var counted = new CountingRandomSource(new Xoshiro256Random(5UL));
        counted.NextGaussian();                   // Box-Muller: 2本引いて予備値が1つ残る
        Assert.Equal(2, counted.Stats.Draws);
        counted.NextGaussian();                   // 予備値を返すだけ
        Assert.Equal(2, counted.Stats.Draws);
        counted.NextGaussian();
        Assert.Equal(4, counted.Stats.Draws);
    }

    [Fact]
    public void CountingRandomSource_GaussianCount_MatchesTheRawStateAdvance()
    {
        var counted = new CountingRandomSource(new Xoshiro256Random(9UL));
        for (var i = 0; i < 7; i++) counted.NextGaussian();
        var raw = new Xoshiro256Random(9UL);
        var drawn = 0;
        // 7本の正規乱数 = Box-Muller 4回 = 8本。同じ本数だけ素で引いて状態が一致すること。
        for (var i = 0; i < counted.Stats.Draws; i++) { raw.NextUInt64(); drawn++; }
        var expect = new Xoshiro256Random(9UL);
        for (var i = 0; i < 7; i++) expect.NextGaussian();
        Assert.Equal(8, drawn);
        Assert.Equal(expect.CaptureState()[..4], raw.CaptureState()[..4]);
    }

    [Fact]
    public void RingBuffer_NegativeCount_ReturnsEmpty()
    {
        var ring = new RingBufferTraceSink(4, 2);
        Assert.Empty(ring.RecentPitches(-1));
        Assert.Empty(ring.RecentPlateAppearances(-5));
        ring.OnPitch(new PitchTrace());
        ring.OnPlateAppearance(new PaTrace());
        Assert.Empty(ring.RecentPitches(-1));
        Assert.Empty(ring.RecentPlateAppearances(-1));
        Assert.Single(ring.RecentPitches(3));
    }
}
