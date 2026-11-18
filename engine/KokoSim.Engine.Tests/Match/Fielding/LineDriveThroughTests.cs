using System.Collections.Generic;
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
/// 通り道のライナー捕球（issue #1135）。空中捕球をこれまで着地点だけで判定していたため、野手正面を通る
/// 強いライナーが、着地点基準では候補に出てこず安打（またはゴロ経路で未判定のまま安打）になっていた。
/// この経路（内野・外野の両方）をノーバウンド捕球でアウトにし、逆に経路の横（グラブが届かない距離）を
/// 抜けるライナーは今どおり抜けることを固定する。
/// </summary>
public sealed class LineDriveThroughTests
{
    private readonly ITestOutputHelper _out;
    public LineDriveThroughTests(ITestOutputHelper output) => _out = output;

    private static readonly Aerodynamics Aero = new();
    private static readonly FieldGeometry Field = new();

    /// <summary>失策乱数を止めて判定（Result=Out/Single等）だけを見る（InfieldChargeTests等と同型）。</summary>
    private static readonly FieldingCoefficients NoError = new()
    {
        ErrorBaseProb = 0, ThrowErrorBaseProb = 0, FlyErrorBaseProb = 0,
        ReceiveErrorBaseProb = 0, OutfieldFumbleBaseProb = 0,
    };

    /// <summary>通り道判定そのものを無効化（従来どおり着地点基準だけの判定に戻す＝回帰確認用）。</summary>
    private static readonly FieldingCoefficients Disabled = NoError with { LineDriveReachHeightM = 0.0 };

    private static IReadOnlyList<Fielder> WithOneFielderAt(FieldPosition pos, Vector3D loc)
        => new[]
        {
            // 捕手は本塁付近に固定（この打球には関係しない）。候補はもう1人の野手だけにする。
            new Fielder(FieldPosition.Catcher, new Vector3D(0, 0, -1.0), new FielderAttributes { Fielding = 50, Catching = 50 }),
            new Fielder(pos, loc, new FielderAttributes { Fielding = 50, Catching = 50 }),
        };

    // ===== ① 内野正面: 遊撃手の胸の高さを通って外野へ落ちるはずの強いゴロ経路ライナー =====

    // 頂点2.30m（<フライ閾値2.5m＝旧コードではゴロ経路。issue #1135の②そのもの）・自然な滞空では
    // 本塁から70m超（遊撃手が捕れなければ長打）運ぶ強い当たり。遊撃手の定位置(40m)の手前（約42m）で
    // 高さ2.2m以下へ下がる＝旧コードはゴロ経路の着地（約70m・約1.6秒後）まで何も走査しておらず、
    // 遊撃手は候補にすら出てこなかった。
    private static readonly BattedBall HardLinerUpTheMiddle =
        new() { ExitVelocityMps = 55.0, LaunchAngleDeg = 4.0, BearingDeg = 0.0 };

    [Fact]
    public void Shortstop_CatchesTheLinerInFront_ThatWouldOtherwiseCarryForAHit()
    {
        var shortstop = new Vector3D(0, 0, 40.0);
        var on = FieldingResolver.ResolveDetailed(
            HardLinerUpTheMiddle, Field, Aero, BatterAttributes.LeagueAverage,
            WithOneFielderAt(FieldPosition.Shortstop, shortstop), NoError, new Xoshiro256Random(1));
        var off = FieldingResolver.ResolveDetailed(
            HardLinerUpTheMiddle, Field, Aero, BatterAttributes.LeagueAverage,
            WithOneFielderAt(FieldPosition.Shortstop, shortstop), Disabled, new Xoshiro256Random(1));

        _out.WriteLine(
            $"on: {on.Result} fielder={on.FielderRole} fieldedAt={on.FieldedAtSeconds:F3}s hang={on.HangTimeSeconds:F3}s range={on.RangeM:F1}m "
            + $"/ off: {off.Result} hang={off.HangTimeSeconds:F3}s range={off.RangeM:F1}m");

        // 機能ON: 遊撃手が通り道でノーバウンド捕球＝アウト。着地点（自然な滞空は約1.6秒・70m超）より
        // 明確に前で判定が終わっている。
        Assert.Equal(BattedBallResult.Out, on.Result);
        Assert.Equal(FieldPosition.Shortstop, on.FielderRole);
        Assert.True(on.IsFly);
        Assert.InRange(on.HangTimeSeconds, 0.5, 1.2);
        Assert.InRange(on.RangeM, 30.0, 50.0);

        // 機能OFF（回帰確認）: 従来どおり着地点基準の判定に戻り、遊撃手は候補に出てこず安打になる。
        Assert.NotEqual(BattedBallResult.Out, off.Result);
        Assert.True(off.HangTimeSeconds > 1.4, "機能を切ったのに着地点まで飛んでいない");
    }

    // ===== ② 外野正面: 前進守備が長打を阻む強いライナー =====

    // 頂点7.59m（フライ閾値2.5m以上＝旧コードでも空中捕球の対象）。自然な着地点は約87m（阻めば三塁打級）。
    // 外野手を68mに置くと、着地点基準では届かない距離（旧コード=三塁打）だが、通り道（約81mで高さ2.2m以下へ
    // 下がる区間）なら、残り距離がその時点までの滞空時間に見合うぶんだけ早く間に合う＝前進守備が長打を阻む。
    private static readonly BattedBall DeepLinerToTheGap =
        new() { ExitVelocityMps = 42.0, LaunchAngleDeg = 14.0, BearingDeg = 0.0 };

    [Fact]
    public void CenterFielder_PlayingShallow_CatchesTheLinerBeforeItCarriesPastHim()
    {
        var centerField = new Vector3D(0, 0, 68.0);
        var on = FieldingResolver.ResolveDetailed(
            DeepLinerToTheGap, Field, Aero, BatterAttributes.LeagueAverage,
            WithOneFielderAt(FieldPosition.CenterField, centerField), NoError, new Xoshiro256Random(1));
        var off = FieldingResolver.ResolveDetailed(
            DeepLinerToTheGap, Field, Aero, BatterAttributes.LeagueAverage,
            WithOneFielderAt(FieldPosition.CenterField, centerField), Disabled, new Xoshiro256Random(1));

        _out.WriteLine(
            $"on: {on.Result} fielder={on.FielderRole} fieldedAt={on.FieldedAtSeconds:F3}s hang={on.HangTimeSeconds:F3}s range={on.RangeM:F1}m "
            + $"/ off: {off.Result} hang={off.HangTimeSeconds:F3}s range={off.RangeM:F1}m");

        Assert.Equal(BattedBallResult.Out, on.Result);
        Assert.Equal(FieldPosition.CenterField, on.FielderRole);
        Assert.True(on.IsFly);
        // 自然な滞空（約2.86秒・87m）より明確に前で判定が終わっている。
        Assert.True(on.HangTimeSeconds < 2.8, $"自然な着地点まで飛んでから判定している（{on.HangTimeSeconds:F3}s）");
        Assert.True(on.RangeM < 85.0, $"着地点基準の捕球点のまま（{on.RangeM:F1}m）");

        // 機能OFF（回帰確認）: 着地点(約87m)は外野手の立ち位置(80m)より深く、着地点基準では届かない＝安打。
        Assert.NotEqual(BattedBallResult.Out, off.Result);
    }

    // ===== ③ 横を抜けるライナーは今どおり抜ける =====

    // ①と同じ打球だが、遊撃手を大きく横（15m）へ外す＝守備範囲（InfielderFieldingRadiusM=4.2m）も
    // 通り道の届く半径も明確に超える。通り道判定を入れても「グラブが届かない距離」なら捕れないままでいい。
    [Fact]
    public void LinerPassingWideOfTheFielder_StillGetsThrough()
    {
        var farToTheSide = new Vector3D(15.0, 0, 40.0);
        var on = FieldingResolver.ResolveDetailed(
            HardLinerUpTheMiddle, Field, Aero, BatterAttributes.LeagueAverage,
            WithOneFielderAt(FieldPosition.Shortstop, farToTheSide), NoError, new Xoshiro256Random(1));
        var off = FieldingResolver.ResolveDetailed(
            HardLinerUpTheMiddle, Field, Aero, BatterAttributes.LeagueAverage,
            WithOneFielderAt(FieldPosition.Shortstop, farToTheSide), Disabled, new Xoshiro256Random(1));

        _out.WriteLine($"on: {on.Result} hang={on.HangTimeSeconds:F3}s range={on.RangeM:F1}m / off: {off.Result}");

        Assert.NotEqual(BattedBallResult.Out, on.Result);
        Assert.NotEqual(BattedBallResult.Out, off.Result);
        // 通り道判定の有無で結果が変わらない（横に外れた打球には一切効かない）。
        Assert.Equal(off.Result, on.Result);
    }

    // ===== ④ 決定論: 同シード同結果 =====

    [Fact]
    public void SameSeed_GivesBitForBitIdenticalResult()
    {
        var shortstop = new Vector3D(0, 0, 40.0);
        var coeff = new FieldingCoefficients(); // 既定（失策ロールも含めて通常運転）。
        FieldingPlay Resolve() => FieldingResolver.ResolveDetailed(
            HardLinerUpTheMiddle, Field, Aero, BatterAttributes.LeagueAverage,
            WithOneFielderAt(FieldPosition.Shortstop, shortstop), coeff, new Xoshiro256Random(99));

        var a = Resolve();
        var b = Resolve();

        Assert.Equal(a.Result, b.Result);
        Assert.Equal(a.FielderRole, b.FielderRole);
        Assert.Equal(a.FieldedAtSeconds, b.FieldedAtSeconds);
        Assert.Equal(a.HangTimeSeconds, b.HangTimeSeconds);
        Assert.Equal(a.LandingX, b.LandingX);
        Assert.Equal(a.LandingZ, b.LandingZ);
        Assert.Equal(a.ApexHeightM, b.ApexHeightM);
        Assert.Equal(a.CatchMarginSeconds, b.CatchMarginSeconds);
        Assert.Equal(a.ErrorKind, b.ErrorKind);
    }
}
