using System.Globalization;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 塁上の走者とこぼれ球の勝負（issue #816）。捕球ミス（お手玉。<see cref="FieldingErrorKind.Catch"/>）が
/// 回収レース（issue #817）でも拾い直せず失策のまま確定したとき、処理野手が再び送球できるのは
/// 「弾いた瞬間」（<see cref="FieldingPlay.FieldedAtSeconds"/>）ではなく「拾い直しを終えた瞬間」
/// （<see cref="FieldingPlay.CatchErrorReadyAtSeconds"/>）であること、そしてその遅れを本塁クロスプレー
/// （<see cref="HomePlayResolver"/>）へ渡すと、塁上の走者が次塁を狙いやすくなる・狙えば本当に勝負になる
/// （生還もアウトも両方起きる）ことを確認する。<see cref="ErrorTunnelTests.SecondRunner_ScoringDependsOnWhereTheBallWent"/>
/// と同じ形（1つの打球を1000回の走者勝負に通す）。
/// </summary>
public sealed class CatchErrorRunnerRaceTests
{
    private readonly ITestOutputHelper _out;
    public CatchErrorRunnerRaceTests(ITestOutputHelper output) => _out = output;

    private static readonly Aerodynamics Aero = new();
    private static readonly FieldGeometry Field = new();

    /// <summary>捕球エラーを必ず起こす（トンネル・送球エラーは止める）。こぼれ幅は既定値のまま＝
    /// 回収レース（issue #817）で「拾い直せる打席」と「拾い直せない打席」が両方出る。</summary>
    private static readonly FieldingCoefficients AlwaysCatchError = new()
    {
        ErrorBaseProb = 1.0,
        ErrorMaxProb = 1.0,
        ThrowErrorBaseProb = 0.0,
        TunnelShareProb = 0.0,
        OutfieldFumbleBaseProb = 0.0,
    };

    // 守備側は弱肩（ArmStrength低）＝本塁への送球がもともと際どい勝負になる組み合わせ。走者は快速
    // （Speed/Baserunning高）＝拾い直しの遅れが「送るかどうか」の判断を実際に動かす範囲に margin を置く。
    private static FieldingPlay Grounder(FieldingCoefficients coeff, ulong seed)
        => FieldingResolver.ResolveDetailed(
            new BattedBall { ExitVelocityMps = 30.0, LaunchAngleDeg = 3.0, BearingDeg = -18.0 },
            Field, Aero, BatterAttributes.LeagueAverage,
            Field.StandardAlignment(new FielderAttributes { Fielding = 50, ArmStrength = 20 }),
            coeff, new Xoshiro256Random(seed));

    private static Player FastRunner() => new() { Speed = 80, Baserunning = 70 };

    /// <summary>拾い直せず失策のまま確定した捕球ミスを1件見つける（決定論の総当たり）。</summary>
    private static FieldingPlay FindUnrecoveredCatchError()
    {
        for (ulong seed = 1; seed <= 5000; seed++)
        {
            var play = Grounder(AlwaysCatchError, seed);
            if (play.Result == BattedBallResult.Error && play.ErrorKind == FieldingErrorKind.Catch
                && play.CatchErrorReadyAtSeconds is not null)
                return play;
        }
        throw new Xunit.Sdk.XunitException("拾い直せない捕球ミスが1件も見つからなかった");
    }

    /// <summary>塁上の走者の判断（送るか否か）が実際に動く範囲の「大きくこぼれて拾い直しに手こずった」
    /// 捕球ミスを1件見つける（issue #816: ちょっとした遅れでは本塁が遠い限り送らない。大きな
    /// こぼれ幅のときだけ勝負になる、という本issueの前提そのものを実測で確かめる）。</summary>
    private static FieldingPlay FindBigUnrecoveredCatchError(double minScatterM)
    {
        var coeff = AlwaysCatchError with { CatchFumbleScatterSigmaM = 10.0 };
        for (ulong seed = 1; seed <= 20000; seed++)
        {
            var play = Grounder(coeff, seed);
            if (play.Result == BattedBallResult.Error && play.ErrorKind == FieldingErrorKind.Catch
                && play.CatchErrorScatterM is { } s && s >= minScatterM)
                return play;
        }
        throw new Xunit.Sdk.XunitException($"こぼれ幅{minScatterM}m以上の捕球ミスが1件も見つからなかった");
    }

    [Fact]
    public void UnrecoveredCatchError_SetsScatterAndReadyTime_LaterThanFieldedAt()
    {
        var play = FindUnrecoveredCatchError();

        Assert.NotNull(play.CatchErrorScatterM);
        Assert.True(play.CatchErrorScatterM >= 0.0);
        Assert.NotNull(play.CatchErrorReadyAtSeconds);
        Assert.NotNull(play.FieldedAtSeconds);
        // 拾い直しには少なくとも反応遅延(0.30s既定)ぶんの時間がかかる＝弾いた瞬間と同時には戻らない。
        Assert.True(play.CatchErrorReadyAtSeconds > play.FieldedAtSeconds,
            $"拾い直し完了({play.CatchErrorReadyAtSeconds})が捕球時刻({play.FieldedAtSeconds})より早いか同時");
        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "弾いた瞬間 {0:F2}s → 拾い直し完了 {1:F2}s（こぼれ幅 {2:F2}m）",
            play.FieldedAtSeconds, play.CatchErrorReadyAtSeconds, play.CatchErrorScatterM));
    }

    [Fact]
    public void RecoveredCatchError_LeavesNewFieldsNull()
    {
        // こぼれ幅を極小に固定＝必ず拾い直してアウトに戻る（CatchFumbleRecoveryTests と同じ手口）。
        var coeff = AlwaysCatchError with { CatchFumbleScatterSigmaM = 0.0001 };
        var play = Grounder(coeff, 1);

        Assert.Equal(BattedBallResult.Out, play.Result);
        Assert.NotNull(play.RecoveredCatchScatterM);
        Assert.Null(play.CatchErrorScatterM);
        Assert.Null(play.CatchErrorReadyAtSeconds);
    }

    [Fact]
    public void OtherErrorKinds_LeaveNewFieldsNull()
    {
        // 送球エラーだけを引かせる（捕球エラーを止める）。
        var coeff = AlwaysCatchError with { ErrorBaseProb = 0.0, ThrowErrorBaseProb = 1.0 };
        var play = Grounder(coeff, 1);

        Assert.Equal(FieldingErrorKind.Throw, play.ErrorKind);
        Assert.Null(play.CatchErrorScatterM);
        Assert.Null(play.CatchErrorReadyAtSeconds);
    }

    /// <summary>
    /// 本丸（issue #816 の主眼）: 同じ「拾い直せなかった捕球ミス」でも、本塁クロスプレーの時計の起点に
    /// 「弾いた瞬間」(FieldedAtSeconds＝#816 以前の誤った前提)を使うか「拾い直し完了」(CatchErrorReadyAtSeconds
    /// ＝GameEngine の実配線)を使うかで、二塁走者の生還しやすさが変わる。拾い直しの遅れを守備の時計に
    /// 乗せた方が走者に有利（送られやすく・勝ちやすい）になり、かつ「勝負」なので生還とアウトの両方が出る。
    /// </summary>
    [Fact]
    public void SecondRunner_UsingRecoveryAwareClock_IsMoreLikelyToScore_ThanTheNaiveClock()
    {
        var play = FindBigUnrecoveredCatchError(minScatterM: 7.0);
        var naive = new HomePlaySituation(
            new Vector3D(play.FieldedX ?? 0, 0, play.FieldedZ ?? 0),
            play.FieldedAtSeconds!.Value,
            play.FielderThrowSpeedMps ?? 30.0,
            play.FielderFielding,
            play.FielderThrowAccuracy);
        var recoveryAware = naive with { BallFieldedAtSeconds = play.CatchErrorReadyAtSeconds!.Value };

        var (naiveScored, naiveOuts) = ScoreSecondRunner(naive);
        var (fixedScored, fixedOuts) = ScoreSecondRunner(recoveryAware);

        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "弾いた瞬間基準: 生還{0}/本塁憤死{1} ／ 拾い直し完了基準: 生還{2}/本塁憤死{3}（各1000回）",
            naiveScored, naiveOuts, fixedScored, fixedOuts));

        Assert.True(fixedScored >= naiveScored,
            $"拾い直しの遅れを乗せたのに生還が増えていない（naive={naiveScored} fixed={fixedScored}）");
        Assert.True(fixedScored + fixedOuts > naiveScored + naiveOuts,
            $"拾い直しの遅れを乗せても勝負そのものが増えていない（naive攻撃={naiveScored + naiveOuts} fixed攻撃={fixedScored + fixedOuts}）");
        // 勝負になっている＝無条件生還でも無条件自重でもない（両方の結末が出る）。
        Assert.True(fixedScored > 0, "拾い直し基準でも一度も生還しない＝走者が送られていない");
        Assert.True(fixedScored < 1000, "拾い直し基準で無条件生還になっている＝時間勝負になっていない");
    }

    private static (int Scored, int HomeOuts) ScoreSecondRunner(HomePlaySituation situation, int trials = 1000)
    {
        var coeff = new BaserunningCoefficients();
        var rng = new Xoshiro256Random(20261112);
        var home = new HomePlayContext(
            Field, situation, new TacticsCoefficients(), 0.5, DefenseDepth.Normal, IsFly: false);

        var scored = 0;
        var homeOuts = 0;
        for (var i = 0; i < trials; i++)
        {
            var bases = new BaseState { Second = FastRunner() };
            var (runs, _, baseOuts, _, _, _) = BaserunningModel.ApplyDetailed(
                bases, PlateAppearanceResult.ReachedOnError, new Player(), 0, coeff, rng,
                collectMoves: true, home, errorKind: FieldingErrorKind.Catch);
            scored += runs;
            homeOuts += baseOuts.Home;
        }
        return (scored, homeOuts);
    }
}
