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

    // 引数は今の物差しの値（issue #1080）。選手には表示Lv（−Δ）で載せ、試合の入口が +Δ して戻す。
    private static FielderAttributes Fielder(int fielding)
        => new Player { Name = "x", Position = FieldPosition.Shortstop, Fielding = fielding - AbilityScale.Delta }.ToFielder(Effect);

    private static FieldingPlay Grounder(int fielding, double ev, double bearing, ulong seed)
        => FieldingResolver.ResolveDetailed(
            new BattedBall { ExitVelocityMps = ev, LaunchAngleDeg = 4.0, BearingDeg = bearing },
            Field, Aero, BatterAttributes.LeagueAverage, Field.StandardAlignment(Fielder(fielding)), Coeff,
            new Xoshiro256Random(seed * 7919));

    /// <summary>捕ってから離すまで[s]＝送球の到達 − 捕球 − 送球の飛行時間（前進捕球の罰を含む）。
    /// 飛行時間は判定が使った送球の見かけの速さ（<see cref="FieldingPlay.FirstThrowSpeedMps"/>＝体勢の弱まり＋失速, issue #1685）で割る。</summary>
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
            sum += p.ThrowArriveSeconds!.Value - p.FieldedAtSeconds!.Value - dist / p.FirstThrowSpeedMps!.Value;
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

        // 名手は実物の内野手（0.6〜0.9秒）の域で離す。issue #1789 でもたつき平均 0.12→0.08 にして守備90 が 0.59s に
        // なったので下限を 0.60→0.55 へ（守備20 との差は 0.5秒以上のまま＝能力差は残っている）。
        Assert.InRange(h90, 0.55, 0.85);
        // 守備50＝構え直し0.10＋握り替え0.70＋もたつき平均0.08≈0.88（issue #1685 で 0.49→0.12、#1789 で 0.12→0.08。それまでは≈1.29）。
        Assert.InRange(h50, 0.85, 1.00);
        // 下手な野手ははっきり遅い（それでも実物の遅い高校生の域＝1.3秒前後まで）。
        Assert.InRange(h20, 1.05, 1.35);
        // メリハリ: 守備90と20で0.3秒以上違う（旧モデル #765 以前は約0.15秒）。#765 の 0.7秒差はもたつき平均0.49の
        // 伸縮が作っていた差で、もたつきを実物の長さへ縮めた issue #1685 で握り替え中心の差（傾き0.025で≈0.56秒）になった。
        Assert.True(h20 - h90 >= 0.30, $"守備20と90の差 {h20 - h90:F2}s が小さい");
    }

    [Fact]
    public void GoodInfielders_ThrowOutMoreBorderlineGrounders()
    {
        // 三遊間寄りの際どいゴロ（遊撃が右へ流れながら捕る緩いゴロ。issue #1685 でもたつきを縮めたので、旧来の
        // 初速27m/s・方位−28°は守備によらずアウトになった）。内野全員の守備だけを変え、間に合わなかった率を比べる。
        // issue #1789 で内野の到達に加速を入れ、内野安打の側（もたつき 0.12→0.08 ほか）を締めたので、旧 22m/s・−14°は
        // 守備50でもアウト（守備20 だけセーフ）になった。三遊間の深い所へ流れる 26m/s・−26°へ選び直した（実測 守備20/50/90＝99/66/0%）。
        double SafeRate(int fielding)
        {
            const int seeds = 600;
            var safe = 0;
            for (ulong s = 1; s <= seeds; s++)
                if (Grounder(fielding, ev: 26.0, bearing: -26.0, seed: s).Result == BattedBallResult.Single) safe++;
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
