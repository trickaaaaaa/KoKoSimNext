using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Batting;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Fielding;

/// <summary>打球の守備解決結果。</summary>
public enum BattedBallResult
{
    Foul,
    Out,
    Error,
    Single,
    Double,
    Triple,
    HomeRun,
}

/// <summary>
/// 守備解決の詳細（結果＋幾何・時間情報, CHANGELOG 32）。
/// 判定に使った着地点・滞空・捕球/送球時刻を捨てずに保持し、タイムライン出力の素材にする。
/// 時刻はすべて「接触（打った瞬間）＝0秒」基準。
/// </summary>
public sealed record FieldingPlay
{
    public required BattedBallResult Result { get; init; }
    /// <summary>着地点（X=+一塁側, Z=センター方向）[m]。</summary>
    public required double LandingX { get; init; }
    public required double LandingZ { get; init; }
    public required double HangTimeSeconds { get; init; }
    public required double ApexHeightM { get; init; }
    public required double RangeM { get; init; }
    public required double BearingDeg { get; init; }
    /// <summary>
    /// 打球の回収点（着地後の転がりの終端。X=+一塁側, Z=センター方向）[m]。
    /// 転がりを解かない経路（ファウル・本塁打・空中捕球・内野ゴロ）では着地点と同値。
    /// </summary>
    public double FieldedX { get; init; }
    public double FieldedZ { get; init; }
    /// <summary>処理した野手（フライ捕球/ゴロ処理）。ファウル・本塁打では null。</summary>
    public FieldPosition? FielderRole { get; init; }
    /// <summary>野手が打球に到達（捕球/処理）した時刻[s]。</summary>
    public double? FieldedAtSeconds { get; init; }
    /// <summary>一塁送球が到達した時刻[s]（内野ゴロのみ）。</summary>
    public double? ThrowArriveSeconds { get; init; }
    /// <summary>打者走者の一塁到達時刻[s]。</summary>
    public required double BatterToFirstSeconds { get; init; }
    public bool IsFly { get; init; }
    /// <summary>処理野手の送球速度[m/s]（本塁クロスプレーの中継起点, 設計書12 §3 F2）。処理野手なしでは null。</summary>
    public double? FielderThrowSpeedMps { get; init; }
}

/// <summary>
/// 守備解決（設計書01 §2⑥）。打球を弾道積分し、着地点・滞空時間と守備位置の幾何から
/// 捕球可否・送球 vs 走者を判定して安打/凡打/本塁打を導く。
/// </summary>
public static class FieldingResolver
{
    /// <summary>互換API（結果のみ）。</summary>
    public static BattedBallResult Resolve(
        BattedBall ball,
        FieldGeometry field,
        Aerodynamics aero,
        BatterAttributes batter,
        IReadOnlyList<Fielder> fielders,
        FieldingCoefficients coeff,
        IRandomSource rng)
        => ResolveDetailed(ball, field, aero, batter, fielders, coeff, rng).Result;

    /// <summary>詳細解決。判定ロジック・乱数消費順は Resolve と完全に同一（決定論の後方互換）。</summary>
    public static FieldingPlay ResolveDetailed(
        BattedBall ball,
        FieldGeometry field,
        Aerodynamics aero,
        BatterAttributes batter,
        IReadOnlyList<Fielder> fielders,
        FieldingCoefficients coeff,
        IRandomSource rng)
    {
        // 本塁→一塁の実戦タイム（設計書02 §4.1b）: 天井速度での距離÷速度＋実戦オフセット−左打者短縮。
        var handednessBonus = batter.Bats is Handedness.Left or Handedness.Switch
            ? coeff.LeftBatterFirstStepBonusSeconds : 0.0;
        var runnerTime = field.BaseDistanceM / batter.SpeedToFirstMps()
                         + coeff.RunnerReactionSeconds - handednessBonus;

        // フェア/ファウル: 方位角が±45°を越えたらファウル。
        if (Math.Abs(ball.BearingDeg) > 45.0)
        {
            return new FieldingPlay
            {
                Result = BattedBallResult.Foul,
                LandingX = 0, LandingZ = 0, HangTimeSeconds = 0, ApexHeightM = 0,
                RangeM = 0, BearingDeg = ball.BearingDeg, BatterToFirstSeconds = runnerTime,
            };
        }

        var ground = BallisticIntegrator.IntegrateToGround(
            ball.ContactPoint(), ball.InitialVelocity(), ball.SpinVector(), aero);

        var landing = ground.LandingPosition;
        var bearingRad = ball.BearingDeg * Math.PI / 180.0;
        var range = Math.Sqrt(landing.X * landing.X + landing.Z * landing.Z);
        var fenceDist = field.FenceDistance(bearingRad);

        FieldingPlay Base(BattedBallResult result) => new()
        {
            Result = result,
            LandingX = landing.X,
            LandingZ = landing.Z,
            FieldedX = landing.X,
            FieldedZ = landing.Z,
            HangTimeSeconds = ground.HangTimeSeconds,
            ApexHeightM = ground.ApexHeightM,
            RangeM = range,
            BearingDeg = ball.BearingDeg,
            BatterToFirstSeconds = runnerTime,
        };

        // 本塁打: フェンス距離を越えて着地し、かつ壁を越える高さで飛んでいる。
        if (range >= fenceDist && ground.ApexHeightM >= field.FenceHeightM)
        {
            return Base(BattedBallResult.HomeRun);
        }

        var isFly = ground.ApexHeightM >= coeff.FlyApexThresholdM;

        // 空中捕球判定（フライ／ライナー）。
        if (isFly)
        {
            var (catcher, reach) = NearestReach(fielders, landing);
            if (reach <= CatchTimeBudget(ground.HangTimeSeconds, catcher, coeff))
            {
                var result = MaybeError(fielders, landing, coeff, rng, BattedBallResult.Out);
                return Base(result) with
                {
                    IsFly = true,
                    FielderRole = catcher.Position,
                    FieldedAtSeconds = ground.HangTimeSeconds,
                    FielderThrowSpeedMps = catcher.Attributes.ThrowSpeedMps,
                };
            }
            // 捕れないフライ → 安打。着地後の転がりと幾何・走力で塁打数を決める。
            return Hit(Base, ground, landing, fenceDist, field, batter, fielders, coeff, runnerTime) with { IsFly = true };
        }

        // ゴロ/低いライナー。
        if (range <= coeff.InfieldDepthM)
        {
            // 内野が処理 → 一塁送球 vs 打者走者。
            var infielder = NearestInfielder(fielders, landing);
            var fieldTime = ReachTime(infielder, landing);
            var throwDist = (field.FirstBase - landing).Length;
            var defenseTime = fieldTime + coeff.InfieldPlayOverheadSeconds
                              + coeff.ThrowTransferSeconds + throwDist / infielder.Attributes.ThrowSpeedMps;

            if (defenseTime + coeff.ForceOutMarginSeconds <= runnerTime)
            {
                var result = MaybeError(fielders, landing, coeff, rng, BattedBallResult.Out);
                return Base(result) with
                {
                    FielderRole = infielder.Position,
                    FieldedAtSeconds = fieldTime,
                    ThrowArriveSeconds = defenseTime,
                    FielderThrowSpeedMps = infielder.Attributes.ThrowSpeedMps,
                };
            }
            // 間に合わず内野安打。
            return Base(BattedBallResult.Single) with
            {
                FielderRole = infielder.Position,
                FieldedAtSeconds = fieldTime,
                ThrowArriveSeconds = defenseTime,
                FielderThrowSpeedMps = infielder.Attributes.ThrowSpeedMps,
            };
        }

        // 内野を抜けた（外野への低い打球）→ 安打。転がりと幾何・走力で塁打数を決める。
        return Hit(Base, ground, landing, fenceDist, field, batter, fielders, coeff, runnerTime);
    }

    /// <summary>
    /// 空中で捕れなかった打球（＝安打）の解決。着地後の転がり → 回収 → 各塁への送球 vs 打者走者。
    /// 乱数は消費しない（決定論・進塁判断は幾何と時間のみで決まる）。
    /// </summary>
    private static FieldingPlay Hit(
        Func<BattedBallResult, FieldingPlay> baseOf,
        BallisticIntegrator.GroundResult ground,
        Vector3D landing,
        double fenceDistanceM,
        FieldGeometry field,
        BatterAttributes batter,
        IReadOnlyList<Fielder> fielders,
        FieldingCoefficients coeff,
        double runnerTime)
    {
        var roll = ExtraBaseResolver.ComputeRoll(
            landing, ground.LandingVelocity, ground.HangTimeSeconds, fenceDistanceM, coeff);
        var (retriever, retrieveAt) = ExtraBaseResolver.Retrieve(fielders, roll, coeff);
        var result = ExtraBaseResolver.ResolveBases(
            roll, retriever, retrieveAt, field, batter, runnerTime, coeff);
        var fieldedPoint = roll.PositionAt(retrieveAt);
        return baseOf(result) with
        {
            FieldedX = fieldedPoint.X,
            FieldedZ = fieldedPoint.Z,
            FielderRole = retriever.Position,
            FieldedAtSeconds = retrieveAt,
            FielderThrowSpeedMps = retriever.Attributes.ThrowSpeedMps,
        };
    }

    /// <summary>
    /// 空中捕球で走れる時間の上限[s]。滞空時間比例（＝深い飛球ほど守備範囲が広がる）に
    /// 絶対上限を掛け、走路取りの巧拙（守備力）で伸縮させる。守備50・上限未満では従来と同一。
    /// </summary>
    private static double CatchTimeBudget(double hangTime, Fielder fielder, FieldingCoefficients coeff)
    {
        var routeFactor = 1.0 + (fielder.Attributes.Fielding - 50) * coeff.CatchReachFieldingSlope;
        return Math.Min(hangTime * coeff.CatchReachFactor, coeff.CatchReachCapSeconds) * routeFactor;
    }

    private static BattedBallResult MaybeError(
        IReadOnlyList<Fielder> fielders, Vector3D landing, FieldingCoefficients coeff,
        IRandomSource rng, BattedBallResult onSuccess)
    {
        var f = NearestFielder(fielders, landing);
        var errProb = coeff.ErrorBaseProb - (f.Attributes.Catching - 50) * coeff.ErrorCatchingSlope;
        if (MathUtil.Chance(MathUtil.Clamp(errProb, 0.001, 0.2), rng))
        {
            return BattedBallResult.Error;
        }
        return onSuccess;
    }

    private static double ReachTime(Fielder f, Vector3D target)
    {
        var dist = (target - f.Location).Length;
        return dist / f.Attributes.SprintSpeedMps + f.Attributes.ReactionDelaySeconds;
    }

    private static (Fielder Fielder, double Time) NearestReach(IReadOnlyList<Fielder> fielders, Vector3D target)
    {
        Fielder best = fielders[0];
        var bestT = double.MaxValue;
        foreach (var f in fielders)
        {
            if (f.Position is FieldPosition.Catcher) continue;
            var t = ReachTime(f, target);
            if (t < bestT) { bestT = t; best = f; }
        }
        return (best, bestT);
    }

    private static Fielder NearestFielder(IReadOnlyList<Fielder> fielders, Vector3D target)
    {
        Fielder best = fielders[0];
        var bestD = double.MaxValue;
        foreach (var f in fielders)
        {
            var d = (target - f.Location).LengthSquared;
            if (d < bestD) { bestD = d; best = f; }
        }
        return best;
    }

    private static Fielder NearestInfielder(IReadOnlyList<Fielder> fielders, Vector3D target)
    {
        Fielder? best = null;
        var bestD = double.MaxValue;
        foreach (var f in fielders)
        {
            if (f.Position is not (FieldPosition.FirstBase or FieldPosition.SecondBase
                or FieldPosition.ThirdBase or FieldPosition.Shortstop or FieldPosition.Pitcher))
            {
                continue;
            }
            var d = (target - f.Location).LengthSquared;
            if (d < bestD) { bestD = d; best = f; }
        }
        return best ?? fielders[0];
    }
}
