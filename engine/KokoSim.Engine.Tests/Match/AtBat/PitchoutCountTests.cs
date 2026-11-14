using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.AtBat;

/// <summary>issue #986: 盗塁を読んで外したピッチアウトは、ボール1つとしてカウントに数える。</summary>
public sealed class PitchoutCountTests
{
    private static readonly FieldGeometry Field = new();

    private static AtBatSession NewSession()
        => AtBatSession.Begin(
            BatterAttributes.LeagueAverage, PitcherAttributes.LeagueAverage,
            new AtBatContext { Fielders = Field.StandardAlignment() });

    [Fact]
    public void Pitchout_AddsOneBall_AndIsLoggedAsPitchoutBall()
    {
        var session = NewSession();
        var res = session.ThrowNextPitch(new Xoshiro256Random(1), pitchout: true);

        Assert.False(res.EndsPlateAppearance);
        Assert.Equal(1, session.Balls);
        Assert.Equal(0, session.Strikes);
        Assert.Equal(1, session.PitchCount);
        var rec = Assert.Single(session.PitchLog);
        Assert.Equal(PitchKind.Ball, rec.Kind);
        Assert.True(rec.Pitchout);
        Assert.Equal(1, rec.BallsAfter);
        Assert.False(new StrikeZone().Contains(rec.LocationX, rec.LocationY));
    }

    [Fact]
    public void Pitchout_FromThreeBalls_IsAWalk()
    {
        var session = NewSession();
        var rng = new Xoshiro256Random(2);
        // 3ボールまでを通常の球で作る（ストライクが混ざる球は積み直す）。
        var attempt = 0;
        while (session.Balls < 3 && !session.IsComplete && attempt++ < 200) session.ThrowNextPitch(rng);
        if (session.IsComplete || session.Balls != 3)
        {
            // 通常の球で3ボールに届かなければ、ピッチアウトを3回続けて同じ状態を作る。
            session = NewSession();
            for (var i = 0; i < 3; i++) session.ThrowNextPitch(rng, pitchout: true);
        }
        Assert.Equal(3, session.Balls);
        Assert.False(session.IsComplete);

        var res = session.ThrowNextPitch(rng, pitchout: true);

        Assert.True(res.EndsPlateAppearance);
        Assert.Equal(PlateAppearanceResult.Walk, session.Result.Result);
    }

    [Fact]
    public void FourPitchouts_WalkTheBatter_WithoutAnySwingOrContact()
    {
        var session = NewSession();
        var rng = new Xoshiro256Random(3);
        for (var i = 0; i < 4; i++) session.ThrowNextPitch(rng, pitchout: true);

        Assert.True(session.IsComplete);
        Assert.Equal(PlateAppearanceResult.Walk, session.Result.Result);
        Assert.All(session.PitchLog, r => Assert.True(r.Pitchout && r.Kind == PitchKind.Ball));
    }

    [Theory]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    public void CanPitchout_IsBlockedAtThreeBalls(int balls, bool expected)
        => Assert.Equal(expected, StealReadModel.CanPitchout(balls));
}
