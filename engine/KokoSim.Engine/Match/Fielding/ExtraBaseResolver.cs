using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Fielding;

/// <summary>
/// 安打の塁打数（単打/二塁打/三塁打）の決定。
/// 「着地距離のしきい値」ではなく物理層の量から連続的に導く（不変条件#1）:
/// 着地速度 → 転がり（バウンドでの減速＋摩擦）→ 停止点 → 最寄り野手の回収時刻 → 各塁への送球到達 vs 打者走者。
/// これにより「ギャップに落ちて壁際まで転がる＝二塁打／野手の正面に落ちる同距離＝単打」が幾何から創発する。
/// 係数はすべて <see cref="FieldingCoefficients"/> 経由＝data/coefficients.yaml 駆動（不変条件#4）。
/// </summary>
public static class ExtraBaseResolver
{
    /// <summary>
    /// 着地後の打球（転がり）の経路。接触＝0秒基準。
    /// フェンスに到達した場合は停止点＝フェンス（跳ね返りは当面無視し <see cref="FieldingCoefficients.FenceCaromSeconds"/> の遅延で表現）。
    /// </summary>
    public readonly record struct RollPath(
        Vector3D Landing,
        Vector3D Direction,
        double InitialSpeedMps,
        double DecelMps2,
        double RollDistanceM,
        double HangTimeSeconds,
        double SettleAtSeconds,
        bool ReachedFence)
    {
        /// <summary>打球が止まる（またはフェンスに達する）位置。</summary>
        public Vector3D StopPosition => Landing + Direction * RollDistanceM;

        /// <summary>時刻 t における打球の位置（着地前は着地点＝まだ空中なので追いつけない扱い）。</summary>
        public Vector3D PositionAt(double t)
        {
            var tau = Math.Max(0.0, t - HangTimeSeconds);
            var tStop = DecelMps2 > 0 ? InitialSpeedMps / DecelMps2 : 0.0;
            var te = Math.Min(tau, tStop);
            var d = Math.Min(RollDistanceM, InitialSpeedMps * te - 0.5 * DecelMps2 * te * te);
            return Landing + Direction * Math.Max(0.0, d);
        }
    }

    /// <summary>
    /// 着地状態から転がりを解く。前進速度の保持率は落下角で決まる
    /// （水平に近いライナーは滑って伸び、真上から落ちる大飛球は失速して死ぬ）。
    /// </summary>
    public static RollPath ComputeRoll(
        Vector3D landing, Vector3D landingVelocity, double hangTimeSeconds,
        double fenceDistanceM, FieldingCoefficients c)
    {
        var vh = Math.Sqrt(landingVelocity.X * landingVelocity.X + landingVelocity.Z * landingVelocity.Z);
        if (vh <= 1e-6)
        {
            return new RollPath(landing, new Vector3D(0, 0, 1), 0, c.RollDecelMps2, 0, hangTimeSeconds, hangTimeSeconds, false);
        }

        var dir = new Vector3D(landingVelocity.X / vh, 0, landingVelocity.Z / vh);
        var descentRatio = Math.Abs(landingVelocity.Y) / (Math.Abs(landingVelocity.Y) + vh); // 0=水平, 1=垂直
        var retention = MathUtil.Clamp(
            c.RollRetentionFlat + (c.RollRetentionSteep - c.RollRetentionFlat) * descentRatio, 0.0, 1.0);
        return RollFrom(landing, dir, vh * retention, hangTimeSeconds, fenceDistanceM, c);
    }

    /// <summary>
    /// すでに接地して転がっている状態から転がりを解く（接地の速度保持は適用済み）。
    /// バウンドで内野を抜けた打球（Issue #63）を、Issue #24 で校正済みのこの集約モデルへ引き継ぐのに使う。
    /// </summary>
    public static RollPath RollFrom(
        Vector3D start, Vector3D direction, double speedMps, double startSeconds,
        double fenceDistanceM, FieldingCoefficients c)
    {
        var dir = direction;
        var v0 = Math.Max(0.0, speedMps);
        var landing = start;
        var hangTimeSeconds = startSeconds;
        var landingRange = Math.Sqrt(landing.X * landing.X + landing.Z * landing.Z);
        var decel = Math.Max(0.1, c.RollDecelMps2);

        var freeRoll = v0 * v0 / (2.0 * decel);
        var toFence = Math.Max(0.0, fenceDistanceM - landingRange);
        var reachedFence = freeRoll > toFence;
        var rollDist = reachedFence ? toFence : freeRoll;

        double rollTime;
        if (reachedFence)
        {
            // v0·t − ½at² = rollDist を満たす最初の t。
            var disc = Math.Max(0.0, v0 * v0 - 2.0 * decel * rollDist);
            rollTime = (v0 - Math.Sqrt(disc)) / decel + c.FenceCaromSeconds;
        }
        else
        {
            rollTime = v0 / decel;
        }

        return new RollPath(landing, dir, v0, decel, rollDist, hangTimeSeconds, hangTimeSeconds + rollTime, reachedFence);
    }

    /// <summary>
    /// 転がる打球を回収する野手と回収時刻。停止点まで待つのではなく、転がっている途中で追いつける
    /// 最初の時刻（＝カットオフ）を二分法で求める。乱数を消費しない＝決定論。
    /// </summary>
    public static (Fielder Fielder, double Seconds) Retrieve(
        IReadOnlyList<Fielder> fielders, in RollPath path, FieldingCoefficients c)
    {
        var stop = path.StopPosition;
        var best = fielders[0];
        var bestToStop = double.MaxValue;
        foreach (var f in fielders)
        {
            if (f.Position is FieldPosition.Catcher) continue;
            var t = ReachTime(f, stop);
            if (t < bestToStop) { bestToStop = t; best = f; }
        }

        var lo = path.HangTimeSeconds;
        var hi = Math.Max(Math.Max(bestToStop, path.SettleAtSeconds), lo);
        // g(t) = 「時刻 t の打球位置へ野手が到達する時刻」− t。g(lo)≤0 なら着地と同時に処理できる。
        var chosen = best;
        if (ReachTime(chosen, path.PositionAt(lo)) - lo <= 0) return (chosen, lo);
        for (var i = 0; i < 24; i++)
        {
            var mid = 0.5 * (lo + hi);
            if (ReachTime(chosen, path.PositionAt(mid)) - mid <= 0) hi = mid; else lo = mid;
        }
        return (chosen, hi);
    }

    /// <summary>
    /// 回収した野手の送球 vs 打者走者の時間比較で塁打数を決める。
    /// 二塁を狙えなければ単打、三塁まで狙えなければ二塁打。
    /// </summary>
    public static BattedBallResult ResolveBases(
        in RollPath path, Fielder retriever, double retrieveAtSeconds,
        FieldGeometry field, BatterAttributes batter, double batterToFirstSeconds, FieldingCoefficients c)
    {
        var ball = path.PositionAt(retrieveAtSeconds);
        // 回収野手の握り替えを守備力で伸縮（Issue #36。内野ゴロ→一塁と同じ ThrowTransferSeconds を共有）。
        var transfer = c.ThrowTransferSeconds
                       * retriever.Attributes.TransferFactor(c.ThrowTransferFieldingSlope, c.ThrowTransferFactorMin);
        var ready = retrieveAtSeconds + c.OutfieldPickupSeconds + transfer;
        var arm = retriever.Attributes.ThrowSpeedMps;

        double ThrowArrivesAt(Vector3D target)
        {
            var d = (target - ball).Length;
            // 距離に比例して伸びる失速項（長い送球ほど山なり・中継が要る）。しきい値なしの連続量。
            return ready + d / arm * (1.0 + d * c.ThrowDistanceDragPerM);
        }

        var sprint = batter.SpeedToFirstMps() * c.RunningTopSpeedFactor;
        var perBase = field.BaseDistanceM / sprint + c.BaseTurnSeconds;
        var toSecond = batterToFirstSeconds + perBase;
        var toThird = toSecond + perBase;

        if (toSecond + c.ExtraBaseMarginSeconds > ThrowArrivesAt(field.SecondBase))
        {
            return BattedBallResult.Single;
        }
        if (toThird + c.ExtraBaseMarginSeconds > ThrowArrivesAt(field.ThirdBase))
        {
            return BattedBallResult.Double;
        }
        return BattedBallResult.Triple;
    }

    private static double ReachTime(Fielder f, Vector3D target)
        => (target - f.Location).Length / f.Attributes.SprintSpeedMps + f.Attributes.ReactionDelaySeconds;
}
