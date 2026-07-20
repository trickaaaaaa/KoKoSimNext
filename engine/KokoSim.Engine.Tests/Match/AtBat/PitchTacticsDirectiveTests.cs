using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.AtBat;

/// <summary>
/// 設計書15 Phase C-1 の意味論テスト（§5-3）。<see cref="AtBatSession.ThrowNextPitch"/> に渡す
/// 1球指示（<see cref="PitchBattingOverride"/>／配球ウェイト／ギア）が、その球だけに効き
/// 次球は打席頭の方針に復帰する（Q12-3: 単純上書き・状態を持たない）ことを固定する。
/// </summary>
public sealed class PitchTacticsDirectiveTests
{
    private static readonly FieldGeometry Field = new();

    [Fact]
    public void ForceSwing_OverridesTakeFirstPitchPolicy()
    {
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment(), TakeFirstPitch = true };
        var batter = BatterAttributes.LeagueAverage;
        var pitcher = PitcherAttributes.LeagueAverage;

        for (ulong seed = 1; seed <= 50; seed++)
        {
            // 方針どおり（無指示）なら「待て」の初球は必ずBall/CalledStrike。
            var baseline = AtBatSession.Begin(batter, pitcher, ctx);
            baseline.ThrowNextPitch(new Xoshiro256Random(seed));
            Assert.True(baseline.LastPitchKind is PitchKind.Ball or PitchKind.CalledStrike);

            // 1球指示 ForceSwing は方針を単純上書きする＝Ball/CalledStrikeにはならない（設計書15 §2.3, Q12-3）。
            var overridden = AtBatSession.Begin(batter, pitcher, ctx);
            overridden.ThrowNextPitch(new Xoshiro256Random(seed), battingOverride: PitchBattingOverride.ForceSwing);
            Assert.False(overridden.LastPitchKind is PitchKind.Ball or PitchKind.CalledStrike);
        }
    }

    [Fact]
    public void ForceTake_AlwaysTakesThatPitch_AndNextUnforcedPitchCanSwing()
    {
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment() }; // TakeFirstPitch=false（方針=強攻寄り）
        // 積極的な打者（自然な状況ならほぼ確実に振る）でも、ForceTakeの球は必ずBall/CalledStrikeになる。
        var batter = new BatterAttributes { Contact = 90, Discipline = 10, Power = 60 };
        var pitcher = PitcherAttributes.LeagueAverage;
        var sawSwingOnNextPitch = false;

        for (ulong seed = 1; seed <= 200; seed++)
        {
            var rng = new Xoshiro256Random(seed);
            var session = AtBatSession.Begin(batter, pitcher, ctx);
            session.ThrowNextPitch(rng, battingOverride: PitchBattingOverride.ForceTake);
            Assert.True(session.LastPitchKind is PitchKind.Ball or PitchKind.CalledStrike);

            if (!session.IsComplete)
            {
                session.ThrowNextPitch(rng); // 無指示＝方針（=通常判断）に復帰。次球まで強制Takeが漏れ出ない。
                if (session.LastPitchKind is not (PitchKind.Ball or PitchKind.CalledStrike))
                {
                    sawSwingOnNextPitch = true;
                }
            }
        }

        Assert.True(sawSwingOnNextPitch, "1球Take後、次球は方針（通常判断）に復帰して振ることもあるはず（次球への漏れ出しの疑い）");
    }

    [Fact]
    public void GearOverride_AffectsOnlyThatPitch_VelocityIncreasesWithPush()
    {
        var pitching = new PitchingCoefficients();
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment(), Pitching = pitching };
        var batter = BatterAttributes.LeagueAverage;
        var pitcher = PitcherAttributes.LeagueAverage;

        for (ulong seed = 1; seed <= 30; seed++)
        {
            var baseline = AtBatSession.Begin(batter, pitcher, ctx);
            baseline.ThrowNextPitch(new Xoshiro256Random(seed));
            var baselineVelocity = baseline.PitchLog[0].VelocityKmh;

            var overridden = AtBatSession.Begin(batter, pitcher, ctx);
            overridden.ThrowNextPitch(new Xoshiro256Random(seed), gearOverride: PitcherGear.Push);
            var overriddenVelocity = overridden.PitchLog[0].VelocityKmh;

            Assert.True(overriddenVelocity > baselineVelocity,
                $"seed{seed}: Push指示の球速({overriddenVelocity})がベースライン({baselineVelocity})を上回らない");
        }
    }

    [Fact]
    public void PitchOverride_AffectsOnlyThatPitch_StraightShareShiftsChoice()
    {
        // 配球方針上書き（変化球中心=StraightShareDelta大きく負）を1球だけ与えると、その球の球種選択に効く。
        var ctx = new AtBatContext { Fielders = Field.StandardAlignment() };
        var batter = BatterAttributes.LeagueAverage;
        var pitcher = new PitcherAttributes
        {
            MaxVelocityKmh = 140,
            Control = 50,
            PitchRank = 50,
            Repertoire = new[]
            {
                new PitchSlot { Type = PitchType.Fastball, Power = 60, Sharpness = 60 },
                new PitchSlot { Type = PitchType.Curve, Power = 60, Sharpness = 60 },
            },
        };
        var forceBreaking = new PitchDirective(StraightShareDelta: -0.9, AimXOffsetM: 0, AimYOffsetM: 0, AimSigmaFactor: 1.0);

        var straightCount = 0;
        var breakingCount = 0;
        for (ulong seed = 1; seed <= 100; seed++)
        {
            var session = AtBatSession.Begin(batter, pitcher, ctx);
            session.ThrowNextPitch(new Xoshiro256Random(seed), pitchOverride: forceBreaking);
            if (session.PitchLog[0].PitchType == PitchType.Fastball) straightCount++;
            else breakingCount++;
        }

        Assert.True(breakingCount > straightCount,
            $"変化球中心の1球指示なのに直球が多い（straight={straightCount}, breaking={breakingCount}）");
    }
}
