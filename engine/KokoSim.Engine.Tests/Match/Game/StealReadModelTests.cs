using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// 守備の読み/ピッチアウト（設計書09 §1, 設計書12 §5, G3）。
/// 「捕手リード＋投手センス vs 状況の意外性」の確率化と、読み切り(ピッチアウト)が
/// 盗塁の時間の勝負に与える影響（捕手優位）を検証する。校正値は🟡のため方向・単調性・契約を見る。
/// </summary>
public sealed class StealReadModelTests
{
    private static readonly BaserunningCoefficients C = new();

    private static Player Runner(int steal = 50, int speed = 50) => new() { Steal = steal, Speed = speed };
    private static Player Catcher(int lead = 50, int arm = 50)
        => new() { Position = FieldPosition.Catcher, Lead = lead, ArmStrength = arm };
    private static Player Pitcher(int sense = 50) => new() { Position = FieldPosition.Pitcher, PitcherSense = sense };

    // --- 意外性(Expectedness): 快足盗塁屋のセオリーは高く、意表・ギャンブルは低い ---

    [Fact]
    public void Expectedness_HigherForAccomplishedBaseStealer()
    {
        var burner = StealReadModel.Expectedness(Runner(steal: 90), StartType.Normal, C);
        var plodder = StealReadModel.Expectedness(Runner(steal: 20), StartType.Normal, C);
        Assert.True(burner > plodder, $"盗塁屋の方が予想されやすいはず: burner={burner} plodder={plodder}");
    }

    [Fact]
    public void Expectedness_GambleLowersIt()
    {
        var normal = StealReadModel.Expectedness(Runner(steal: 60), StartType.Normal, C);
        var gamble = StealReadModel.Expectedness(Runner(steal: 60), StartType.Gamble, C);
        Assert.True(gamble < normal, $"ギャンブル始動は意表＝予想度が下がるはず: gamble={gamble} normal={normal}");
    }

    // --- 不変条件1: 生Lvでなく ability_effect（Running / Pitching）を通す（設計書21 §3.1） ---

    private static BaserunningCoefficients WithEffect(double runningScale, double pitchingScale) => new()
    {
        AbilityEffect = AbilityEffectCoefficients.Identity with { RunningScale = runningScale, PitchingScale = pitchingScale },
    };

    [Fact]
    public void Expectedness_GoesThroughRunningEffect()
    {
        double Gap(double scale)
        {
            var c = WithEffect(scale, 1.0);
            return StealReadModel.Expectedness(Runner(steal: 90), StartType.Normal, c)
                 - StealReadModel.Expectedness(Runner(steal: 50), StartType.Normal, c);
        }
        Assert.True(Gap(0.25) < Gap(1.0) - 0.01, $"走力系の効きが小さいほど Steal の差は縮むはず: 0.25={Gap(0.25)} 1.0={Gap(1.0)}");
    }

    [Fact]
    public void Pitchout_GoesThroughPitchingEffectForCatcherLead()
    {
        double Gap(double scale)
        {
            var c = WithEffect(1.0, scale);
            var e = StealReadModel.Expectedness(Runner(steal: 70), StartType.Normal, c);
            return StealReadModel.PitchoutProbability(Catcher(lead: 90), Pitcher(), e, c)
                 - StealReadModel.PitchoutProbability(Catcher(lead: 50), Pitcher(), e, c);
        }
        Assert.True(Gap(0.25) < Gap(1.0) - 0.005, $"投手系の効きが小さいほど捕手リードの差は縮むはず: 0.25={Gap(0.25)} 1.0={Gap(1.0)}");
    }

    // --- ピッチアウト確率: 読み力(捕手リード＋投手センス)×予想度、意表は読み切れない ---

    [Fact]
    public void Pitchout_MoreLikelyWithSharperBattery()
    {
        var e = StealReadModel.Expectedness(Runner(steal: 70), StartType.Normal, C);
        var dull = StealReadModel.PitchoutProbability(Catcher(lead: 20), Pitcher(sense: 30), e, C);
        var sharp = StealReadModel.PitchoutProbability(Catcher(lead: 90), Pitcher(sense: 85), e, C);
        Assert.True(sharp > dull + 0.05, $"鋭いバッテリーほど読むはず: dull={dull} sharp={sharp}");
    }

    [Fact]
    public void Pitchout_ReadsPitcherSense_NotMental()
    {
        // issue #1472: 投手側の読みは投手センス（隠し能力）。旧実装の代用（精神力）は効かない。
        var e = StealReadModel.Expectedness(Runner(steal: 70), StartType.Normal, C);
        var dull = StealReadModel.PitchoutProbability(Catcher(), Pitcher(sense: 20), e, C);
        var sharp = StealReadModel.PitchoutProbability(Catcher(), Pitcher(sense: 90), e, C);
        Assert.True(sharp > dull, $"投手センスが高いほど読むはず: dull={dull} sharp={sharp}");
        var calm = new Player { Position = FieldPosition.Pitcher, Mental = 95 };
        var nervous = new Player { Position = FieldPosition.Pitcher, Mental = 5 };
        Assert.Equal(StealReadModel.PitchoutProbability(Catcher(), nervous, e, C),
            StealReadModel.PitchoutProbability(Catcher(), calm, e, C), 12);
    }

    [Fact]
    public void Pitchout_SurpriseIsHardToReadEvenForSharpBattery()
    {
        // 意表（予想度をほぼ0にする）は、読み力が高くても掛け算で読み切れない。
        var eObvious = StealReadModel.Expectedness(Runner(steal: 95), StartType.Normal, C);
        var eSurprise = StealReadModel.Expectedness(Runner(steal: 20), StartType.Gamble, C);
        var onObvious = StealReadModel.PitchoutProbability(Catcher(lead: 90), Pitcher(sense: 85), eObvious, C);
        var onSurprise = StealReadModel.PitchoutProbability(Catcher(lead: 90), Pitcher(sense: 85), eSurprise, C);
        Assert.True(onSurprise < onObvious, $"意表の方が読まれにくいはず: surprise={onSurprise} obvious={onObvious}");
    }

    [Fact]
    public void Pitchout_IsClampedToMax()
    {
        // 極端に読みやすい状況でも上限を越えない。
        var e = StealReadModel.Expectedness(Runner(steal: 99), StartType.Normal, C);
        var p = StealReadModel.PitchoutProbability(Catcher(lead: 99), Pitcher(sense: 99), e, C);
        Assert.InRange(p, 0.0, C.MaxPitchoutProb);
    }

    // --- 時間の勝負への影響: ピッチアウトで刺されやすくなる（捕手優位） ---

    [Fact]
    public void Pitchout_LowersStealSuccess()
    {
        var runner = Runner(steal: 60, speed: 65);
        var catcher = Catcher(arm: 55);
        var normal = StealResolver.SuccessProbability(runner, catcher, C);
        var read = StealResolver.SuccessProbability(runner, catcher, C, pitchout: true);
        Assert.True(read < normal - 0.05, $"ピッチアウトで成功率が下がるはず: normal={normal} read={read}");
    }

    [Fact]
    public void GambleStart_ImprovesJump_ButIsMoreExposedOnPitchout()
    {
        var runner = Runner(steal: 55, speed: 60);
        var catcher = Catcher(arm: 55);
        // 読まれなければ好ジャンプで成功率↑。
        var normalNoRead = StealResolver.SuccessProbability(runner, catcher, C, pitchout: false, StartType.Normal);
        var gambleNoRead = StealResolver.SuccessProbability(runner, catcher, C, pitchout: false, StartType.Gamble);
        Assert.True(gambleNoRead > normalNoRead, "ギャンブルは読まれなければ好ジャンプで有利なはず");
        // 読まれると無防備＝通常始動が読まれるより刺されやすい。
        var normalRead = StealResolver.SuccessProbability(runner, catcher, C, pitchout: true, StartType.Normal);
        var gambleRead = StealResolver.SuccessProbability(runner, catcher, C, pitchout: true, StartType.Gamble);
        Assert.True(gambleRead < normalRead, "ギャンブルは読まれると最も無防備なはず");
    }

    [Fact]
    public void RollPitchout_IsDeterministic_ForSameSeed()
    {
        var runner = Runner(steal: 70);
        var catcher = Catcher(lead: 70);
        var pitcher = Pitcher(sense: 60);
        var a = new Xoshiro256Random(9);
        var b = new Xoshiro256Random(9);
        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(
                StealReadModel.RollPitchout(runner, catcher, pitcher, StartType.Normal, C, a),
                StealReadModel.RollPitchout(runner, catcher, pitcher, StartType.Normal, C, b));
        }
    }
}
