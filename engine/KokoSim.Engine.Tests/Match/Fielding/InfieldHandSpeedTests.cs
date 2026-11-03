using System.Globalization;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// 内野手の手さばき（捕ってから離すまで）の守備によるメリハリ（issue #765「内野守備がもっさり」）。
/// 外野返球と同じ傾き 0.004 だった頃は、守備90と20で平均約0.15秒しか違わず、上手い内野手も
/// 守備50と同じように球を持っていた。実ゲームと同じ経路（表示の守備→効き→解決器）で測る。
/// </summary>
public sealed class InfieldHandSpeedTests
{
    private readonly ITestOutputHelper _out;
    public InfieldHandSpeedTests(ITestOutputHelper output) => _out = output;

    private static readonly AbilityEffectCoefficients Effect = new();
    private static readonly FieldGeometry Field = new();
    private static readonly Aerodynamics Aero = new();
    // 失策ロールを切る（握り替え＋もたつきの秒だけを見る）。お手玉の失策も内野安打へ戻す。
    private static readonly FieldingCoefficients Coeff = new()
    {
        ErrorBaseProb = 0, ThrowErrorBaseProb = 0, BobbleErrorMinJitterSeconds = 0,
    };

    private static FielderAttributes Fielder(int fielding)
        => new Player { Name = "x", Position = FieldPosition.Shortstop, Fielding = fielding }.ToFielder(Effect);

    private static FieldingPlay Grounder(int fielding, double ev, double bearing, ulong seed)
        => FieldingResolver.ResolveDetailed(
            new BattedBall { ExitVelocityMps = ev, LaunchAngleDeg = 4.0, BearingDeg = bearing },
            Field, Aero, BatterAttributes.LeagueAverage, Field.StandardAlignment(Fielder(fielding)), Coeff,
            new Xoshiro256Random(seed * 7919));

    /// <summary>捕ってから離すまで[s]＝送球の到達 − 捕球 − 送球の飛行時間（前進捕球の罰を含む）。</summary>
    private static double MeanHold(int fielding)
    {
        const int seeds = 2000;
        var sum = 0.0;
        var n = 0;
        for (ulong s = 1; s <= seeds; s++)
        {
            // 遊撃手正面の強いゴロ＝前進しない・一塁手の自重踏みも起きない
            var p = Grounder(fielding, ev: 34.0, bearing: -18.0, seed: s);
            if (p.FielderRole != FieldPosition.Shortstop || p.UnassistedPutout) continue;
            var dist = System.Math.Sqrt(
                (Field.FirstBase.X - p.FieldedX!.Value) * (Field.FirstBase.X - p.FieldedX.Value)
                + (Field.FirstBase.Z - p.FieldedZ!.Value) * (Field.FirstBase.Z - p.FieldedZ.Value));
            sum += p.ThrowArriveSeconds!.Value - p.FieldedAtSeconds!.Value - dist / p.FielderThrowSpeedMps!.Value;
            n++;
        }
        Assert.True(n > seeds / 2, $"テスト前提: 遊撃手の送球が {n}/{seeds} しかない");
        return sum / n;
    }

    [Fact]
    public void FieldingFifty_IsTheFixedPoint()
    {
        // リーグ平均の野手（統計帯の母集団）は握り替え・もたつきとも×1.0＝従来と同じ秒。
        var f = Fielder(50);
        Assert.Equal(1.0, f.TransferFactor(Coeff.InfieldTransferFieldingSlope, Coeff.InfieldTransferFactorMin), 12);
        Assert.Equal(1.0, f.TransferFactor(Coeff.InfieldHandleJitterFieldingSlope, Coeff.InfieldHandleJitterFactorMin), 12);
    }

    [Fact]
    public void GoodInfielders_ReleaseClearlyFaster_PoorOnesSlower()
    {
        var h20 = MeanHold(20);
        var h50 = MeanHold(50);
        var h90 = MeanHold(90);
        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "捕ってから離すまで（平均）: 守備20 {0:F2}s / 守備50 {1:F2}s / 守備90 {2:F2}s", h20, h50, h90));

        // 名手は実物の内野手（0.6〜0.9秒）の域で離す。
        Assert.InRange(h90, 0.60, 0.90);
        // 守備50は従来どおり（構え直し0.10＋握り替え0.70＋もたつき平均0.49≈1.29。上限で切るぶん少し短い）。
        Assert.InRange(h50, 1.15, 1.35);
        // 下手な野手ははっきり遅い。
        Assert.True(h20 >= 1.50, $"守備20 の保持 {h20:F2}s が遅くない");
        // メリハリ: 守備90と20で0.7秒以上違う（旧モデルは約0.15秒）。
        Assert.True(h20 - h90 >= 0.70, $"守備20と90の差 {h20 - h90:F2}s が小さい");
    }

    [Fact]
    public void GoodInfielders_ThrowOutMoreBorderlineGrounders()
    {
        // 三遊間寄りの際どいゴロ。内野全員の守備だけを変え、間に合わなかった率を比べる。
        double SafeRate(int fielding)
        {
            const int seeds = 600;
            var safe = 0;
            for (ulong s = 1; s <= seeds; s++)
                if (Grounder(fielding, ev: 27.0, bearing: -28.0, seed: s).Result == BattedBallResult.Single) safe++;
            return (double)safe / seeds;
        }
        var s20 = SafeRate(20);
        var s50 = SafeRate(50);
        var s90 = SafeRate(90);
        _out.WriteLine(string.Format(CultureInfo.InvariantCulture,
            "際どいゴロの内野安打率: 守備20 {0:P1} / 守備50 {1:P1} / 守備90 {2:P1}", s20, s50, s90));
        Assert.True(s90 < s50 && s50 < s20, "守備が高いほど内野安打が減っていない");
    }
}
