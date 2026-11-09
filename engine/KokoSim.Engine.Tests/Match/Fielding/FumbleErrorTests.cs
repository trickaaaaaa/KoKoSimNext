using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Debugging;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Timeline;
using KokoSim.Engine.Players;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 外野手のファンブル（issue #811 / design-14 P1-6 追記）。安打の回収（フライが落ちて安打になった球／
/// 内野を抜けたゴロ）そのものを外野手が弾く・後逸する失策。内野の3段階（捕球/送球/受け手）とは違い、
/// 安打の記録（Single/Double/Triple）は変えず、球が逸れた分だけ回収が遅れた時刻で塁打数を解き直す
/// （一律の追加進塁にしない）。失策だけ外野手に付く＝「安打＋失策」。
/// </summary>
public sealed class FumbleErrorTests
{
    private readonly ITestOutputHelper _out;
    public FumbleErrorTests(ITestOutputHelper output) => _out = output;

    private static readonly Aerodynamics Aero = new();
    private static readonly FieldGeometry Field = new();

    // 左翼手の正面へ抜ける強い当たり（ExtraBaseResolverTests と同じ「標準守備位置の正面＝単打」の当たり）。
    private const double AtLeftFielderDeg = -28.0;

    private static FieldingPlay OutfieldSingle(
        FieldingCoefficients coeff, int leftFieldCatching, ulong seed,
        double exitMps = 43.0, double launchDeg = 8.0)
        => FieldingResolver.ResolveDetailed(
            new BattedBall { ExitVelocityMps = exitMps, LaunchAngleDeg = launchDeg, BearingDeg = AtLeftFielderDeg },
            Field, Aero, BatterAttributes.LeagueAverage,
            Field.AlignmentFrom(new Dictionary<FieldPosition, FielderAttributes>
            {
                [FieldPosition.LeftField] = new FielderAttributes { Catching = leftFieldCatching },
            }),
            coeff, new Xoshiro256Random(seed));

    /// <summary>ファンブルだけを引かせる（他の失策ロールは既定のまま＝外野の安打処理には無関係）。</summary>
    private static FieldingCoefficients FumbleOnly(
        double baseProb, double speedRefMps = 10.0, double delayMeanSeconds = 1.2, double maxProb = 0.30)
        => new()
        {
            OutfieldFumbleBaseProb = baseProb,
            OutfieldFumbleSpeedRefMps = speedRefMps,
            OutfieldFumbleDelayMeanSeconds = delayMeanSeconds,
            ErrorMinProb = 0,
            ErrorMaxProb = maxProb,
        };

    private static double FumbleRate(FieldingCoefficients coeff, int leftFieldCatching, double exitMps, int trials = 4000)
    {
        var fumbles = 0;
        for (ulong seed = 1; seed <= (ulong)trials; seed++)
            if (OutfieldSingle(coeff, leftFieldCatching, seed, exitMps).ErrorKind == FieldingErrorKind.Fumble) fumbles++;
        return (double)fumbles / trials;
    }

    // ===== ① 既定0で恒等・正値で内訳ロール1本 =====

    [Fact]
    public void FumbleDisabled_IsIdenticalAcrossOutfieldCatching()
    {
        var coeff = FumbleOnly(0.0);
        for (ulong seed = 1; seed <= 400; seed++)
        {
            var a = OutfieldSingle(coeff, leftFieldCatching: 10, seed: seed);
            var b = OutfieldSingle(coeff, leftFieldCatching: 95, seed: seed);
            Assert.Equal(a.Result, b.Result);
            Assert.Null(a.ErrorKind);
            Assert.Null(b.ErrorKind);
            Assert.Equal(a.FieldedAtSeconds, b.FieldedAtSeconds);
            Assert.Equal(a.FielderRole, b.FielderRole);
        }
    }

    [Fact]
    public void ZeroBase_DrawsNoExtraRandom_ComparedToPositiveBase()
    {
        var zero = new CountingRandomSource(new Xoshiro256Random(777));
        var full = new CountingRandomSource(new Xoshiro256Random(777));
        var alignment = Field.AlignmentFrom(new Dictionary<FieldPosition, FielderAttributes>
        {
            [FieldPosition.LeftField] = new FielderAttributes { Catching = 10 },
        });
        var ball = new BattedBall { ExitVelocityMps = 43.0, LaunchAngleDeg = 8.0, BearingDeg = AtLeftFielderDeg };

        var disabled = FieldingResolver.ResolveDetailed(
            ball, Field, Aero, BatterAttributes.LeagueAverage, alignment, FumbleOnly(0.0), zero);
        var enabled = FieldingResolver.ResolveDetailed(
            ball, Field, Aero, BatterAttributes.LeagueAverage, alignment, FumbleOnly(1.0, maxProb: 1.0), full);

        _out.WriteLine($"消費: 無効={zero.Stats.Draws} / 有効={full.Stats.Draws}"
                       + $" 種別: {disabled.ErrorKind} → {enabled.ErrorKind}");

        Assert.Null(disabled.ErrorKind);
        Assert.Equal(FieldingErrorKind.Fumble, enabled.ErrorKind);
        // ファンブル確定後に遅れ時間をもう1本引く＝無効と比べて+2本（判定ロール＋遅れロール）。
        Assert.Equal(zero.Stats.Draws + 2, full.Stats.Draws);
    }

    // ===== ② 回収点でのボール速度・守備力に連動（速い球・低い捕球力ほど増える） =====

    [Fact]
    public void FasterBallAndLowerCatching_FumblesMore()
    {
        var coeff = FumbleOnly(0.5, speedRefMps: 15.0);
        var slowGoodCatch = FumbleRate(coeff, leftFieldCatching: 95, exitMps: 30.0);
        var fastPoorCatch = FumbleRate(coeff, leftFieldCatching: 10, exitMps: 55.0);

        _out.WriteLine($"遅い球+名手 {slowGoodCatch:F3} / 速い球+下手 {fastPoorCatch:F3}");
        Assert.True(fastPoorCatch > slowGoodCatch,
            $"速い球+下手 {fastPoorCatch:F3} が遅い球+名手 {slowGoodCatch:F3} 以下");
    }

    /// <summary>捕球の効きは基準確率に対する倍率＝基準確率を半分にすれば、捕球の弱い外野手でも半分になる。
    /// 傾きを足し算で使っていた時は、捕球10の外野手で基準と無関係に+26%が乗り、基準を下げても頻度が下がらなかった。</summary>
    [Fact]
    public void LoweringBaseProb_ScalesDownEvenForPoorCatcher()
    {
        var high = FumbleRate(FumbleOnly(0.02), leftFieldCatching: 10, exitMps: 55.0);
        var low = FumbleRate(FumbleOnly(0.01), leftFieldCatching: 10, exitMps: 55.0);

        _out.WriteLine($"捕球10: 基準0.02 {high:F3} / 基準0.01 {low:F3}");
        Assert.InRange(low / high, 0.35, 0.65);
    }

    [Fact]
    public void StoppedBall_AlmostNeverFumbles()
    {
        // 緩い打球（遅い・ギャップでなく正面）はたいてい止まってから回収する＝難度0に近い。
        var coeff = FumbleOnly(0.9, speedRefMps: 10.0, maxProb: 1.0);
        var rate = FumbleRate(coeff, leftFieldCatching: 10, exitMps: 24.0, trials: 1000);
        _out.WriteLine($"緩い当たりのファンブル率 {rate:F3}");
        Assert.True(rate < 0.05, $"緩い当たりで {rate:F3} も発生した（下限クランプ以上に増えている）");
    }

    // ===== ③ 記録は「安打＋失策」＝Result は安打のまま、失策は外野手に付く =====

    [Fact]
    public void Fumble_KeepsTheHitResult_AndAttributesTheErrorToTheOutfielder()
    {
        var coeff = FumbleOnly(1.0, maxProb: 1.0);
        var play = OutfieldSingle(coeff, leftFieldCatching: 10, seed: 1);

        Assert.Equal(FieldingErrorKind.Fumble, play.ErrorKind);
        Assert.Equal(FieldPosition.LeftField, play.ErrorFielderRole);
        Assert.Equal(FieldPosition.LeftField, play.FielderRole);
        Assert.True(play.Result is BattedBallResult.Single or BattedBallResult.Double or BattedBallResult.Triple,
            $"安打のはずが {play.Result}");

        var text = TimelineBuilder.DescribeResult(play);
        _out.WriteLine($"{play.Result}（{play.ErrorKind}） → 「{text}」");
        // ErrorAtSeconds（弾いた瞬間）は実際の回収時刻（FieldedAtSeconds、遅延込み）より前。
        Assert.True(play.ErrorAtSeconds < play.FieldedAtSeconds);
    }

    // ===== ④ 一律の追加進塁ではなく、遅れた回収時刻で塁打数が時間勝負で決まる =====

    [Fact]
    public void Fumble_ExtendsBasesByDelayingRetrieval_NotByAFlatBonus()
    {
        var off = FumbleOnly(0.0);
        var on = FumbleOnly(1.0, delayMeanSeconds: 3.0, maxProb: 1.0); // 大きく後逸させる

        var found = false;
        for (ulong seed = 1; seed <= 500 && !found; seed++)
        {
            var baseline = OutfieldSingle(off, leftFieldCatching: 50, seed);
            var fumbled = OutfieldSingle(on, leftFieldCatching: 50, seed);
            if (fumbled.ErrorKind != FieldingErrorKind.Fumble) continue;
            found = true;
            _out.WriteLine($"seed={seed}: {baseline.Result}({baseline.FieldedAtSeconds:F2}s) "
                           + $"→ {fumbled.Result}({fumbled.FieldedAtSeconds:F2}s)");
            // 回収が遅れた分だけ回収時刻が後ろへ動く。
            Assert.True(fumbled.FieldedAtSeconds > baseline.FieldedAtSeconds);
            // 塁打数は後退しない（遅れれば伸びるか、同じ塁で止まる＝一律加算ではなく時間勝負）。
            Assert.True(Bases(fumbled.Result) >= Bases(baseline.Result));
        }
        Assert.True(found, "500シード内でファンブルが1件も出ない");
    }

    [Fact]
    public void Timeline_MarksErrorAtTheMomentOfTheFumble_WhileKeepingTheHitSegments()
    {
        var coeff = FumbleOnly(1.0, maxProb: 1.0);
        var play = OutfieldSingle(coeff, leftFieldCatching: 10, seed: 1);
        Assert.Equal(FieldingErrorKind.Fumble, play.ErrorKind);

        var tl = TimelineBuilder.BuildBattedBall(
            play, Field.StandardAlignment(), Field, null, runnersOn: false,
            TimelineBuilder.DescribeResult(play));

        // 「エラー」の判定イベントが弾いた瞬間（ErrorAtSeconds）に出る。
        var err = Assert.Single(tl.Judgements, j => j.Kind == JudgementKind.Error);
        Assert.Equal(TimelineBuilder.ContactAt + play.ErrorAtSeconds!.Value, err.T, 6);
    }

    private static int Bases(BattedBallResult r) => r switch
    {
        BattedBallResult.Single => 1,
        BattedBallResult.Double => 2,
        BattedBallResult.Triple => 3,
        _ => 0,
    };
}
