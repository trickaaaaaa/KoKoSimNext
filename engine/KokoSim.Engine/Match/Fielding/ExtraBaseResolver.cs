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
    /// フェンス直撃の跳ね返り（issue fence-carom-and-over-the-head-cues）。**表示専用**の観測値で、
    /// 判定（回収時刻・塁打数）を決める <see cref="RollPath.PositionAt"/> は一切変えない
    /// ＝帯・決定論ダイジェストは不変。
    /// </summary>
    /// <param name="HitAtSeconds">フェンスに当たる時刻[s]（接触基準）。</param>
    /// <param name="HitPoint">当たった位置。</param>
    /// <param name="Direction">跳ね返る向き（単位ベクトル・フェンス弧の法線で反射）。</param>
    /// <param name="SpeedMps">跳ね返り初速＝到達時の残速 × 反発係数。</param>
    /// <param name="DecelMps2">転がりの減速度（往路と同じ）。</param>
    /// <param name="DistanceM">戻る距離＝v²/(2a)。</param>
    public readonly record struct FenceCarom(
        double HitAtSeconds, Vector3D HitPoint, Vector3D Direction,
        double SpeedMps, double DecelMps2, double DistanceM)
    {
        /// <summary>跳ね返った球が止まる時刻[s]。</summary>
        public double SettleAtSeconds => HitAtSeconds + (DecelMps2 > 0 ? SpeedMps / DecelMps2 : 0.0);

        /// <summary>止まる位置。</summary>
        public Vector3D StopPosition => HitPoint + Direction * DistanceM;

        /// <summary>時刻 t の位置（当たる前は当たった点、止まった後は停止点）。</summary>
        public Vector3D PositionAt(double t)
        {
            var tau = Math.Max(0.0, t - HitAtSeconds);
            var tStop = DecelMps2 > 0 ? SpeedMps / DecelMps2 : 0.0;
            var te = Math.Min(tau, tStop);
            var d = Math.Min(DistanceM, SpeedMps * te - 0.5 * DecelMps2 * te * te);
            return HitPoint + Direction * Math.Max(0.0, d);
        }
    }

    /// <summary>
    /// フェンスに達した転がりの跳ね返りを解く（純関数・乱数不使用）。フェンスに達していなければ null。
    ///
    /// <para>材料はすべて既に <see cref="RollFrom"/> が計算しているもの:
    /// 到達時の残速 <c>√(v0² − 2·a·rollDist)</c>、反発係数
    /// <see cref="FieldingCoefficients.FenceRestitution"/>、向きはフェンス弧の法線での反射
    /// （弧は本塁中心と近似＝法線は本塁向き）。</para>
    ///
    /// <para>⚠ 呼ぶのは**絵を描く側だけ**。判定側の回収時刻（<c>SettleAtSeconds</c> に含まれる
    /// <see cref="FieldingCoefficients.FenceCaromSeconds"/>）は据え置きで、跳ね返りは
    /// 「その予算の中でどこに球があるか」を描き直すだけ。</para>
    /// </summary>
    public static FenceCarom? ComputeFenceCarom(in RollPath path, FieldingCoefficients c)
    {
        if (!path.ReachedFence) return null;
        var decel = Math.Max(0.1, path.DecelMps2);
        // フェンスに当たる時刻＝往路の転がりが rollDist に達する最初の t（RollFrom と同じ式）。
        var disc = Math.Max(0.0, path.InitialSpeedMps * path.InitialSpeedMps - 2.0 * decel * path.RollDistanceM);
        var remaining = Math.Sqrt(disc);
        var hitAt = path.HangTimeSeconds + (path.InitialSpeedMps - remaining) / decel;
        var speed = remaining * Math.Max(0.0, c.FenceRestitution);
        if (speed <= 1e-6) return null;

        var hit = path.StopPosition;
        // フェンス弧の法線（内向き）＝当たった点から本塁へ向かう単位ベクトル。反射 d' = d − 2(d·n)n。
        var r = Math.Sqrt(hit.X * hit.X + hit.Z * hit.Z);
        var n = r > 1e-6 ? new Vector3D(-hit.X / r, 0, -hit.Z / r) : new Vector3D(0, 0, -1);
        var d = path.Direction;
        var dot = d.X * n.X + d.Z * n.Z;
        var refl = new Vector3D(d.X - 2.0 * dot * n.X, 0, d.Z - 2.0 * dot * n.Z);
        // 反射の向きが外向きのまま（浅い角度での数値誤差）なら法線そのもの＝素直に戻す。
        if (refl.X * n.X + refl.Z * n.Z <= 0.0) refl = n;
        var len = Math.Sqrt(refl.X * refl.X + refl.Z * refl.Z);
        if (len <= 1e-6) return null;
        var dir = new Vector3D(refl.X / len, 0, refl.Z / len);

        return new FenceCarom(hitAt, hit, dir, speed, decel, speed * speed / (2.0 * decel));
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
    /// 回収野手が投げ始められる時刻[s]（回収＋拾い上げ＋握り替え）。握り替えは守備力で伸縮
    /// （Issue #36。内野ゴロ→一塁と同じ ThrowTransferSeconds を共有）。
    /// 塁打数判定と表示タイムラインの**単一ソース**（Issue #301③）。
    /// </summary>
    public static double ReadySeconds(double retrieveAtSeconds, int fielding, FieldingCoefficients c)
    {
        var transfer = c.ThrowTransferSeconds
                       * new FielderAttributes { Fielding = fielding }
                           .TransferFactor(c.ThrowTransferFieldingSlope, c.ThrowTransferFactorMin);
        return retrieveAtSeconds + c.OutfieldPickupSeconds + transfer;
    }

    /// <summary>
    /// 回収点から目標塁へ送球が到達する時刻[s]。距離に比例して伸びる失速項（長い送球ほど山なり・
    /// 中継が要る）を掛ける、しきい値なしの連続量。塁打数判定と表示タイムラインの**単一ソース**。
    /// </summary>
    public static double ThrowArriveSeconds(
        double readySeconds, double distanceM, double armMps, FieldingCoefficients c)
        => readySeconds + distanceM / armMps * (1.0 + distanceM * c.ThrowDistanceDragPerM);

    /// <summary>
    /// 打者走者の時計の文脈（Issue #316）。打席から走り出すのでリードは無く、一塁を回った後は
    /// 既に加速しきっているぶん速い（<c>RunningTopSpeedFactor</c>）。代わりにベースを回るたび
    /// 減速・踏み直しで損する（<c>BaseTurnSeconds</c>）。スタート遅延は
    /// <c>batterToFirstSeconds</c> 側に入っているのでここでは 0。
    /// </summary>
    public static RunnerContext BatterContextOf(FieldingCoefficients c)
        => new(
            StartDelaySeconds: 0.0,
            LeadDistanceM: 0.0,
            TopSpeedFactor: c.RunningTopSpeedFactor,
            TurnSecondsPerBase: c.BaseTurnSeconds,
            RoundsStartingBase: true);

    /// <summary>
    /// 回収した野手の送球 vs 打者走者の時間比較で塁打数を決める。
    /// 二塁を狙えなければ単打、三塁まで狙えなければ二塁打。
    /// </summary>
    public static BattedBallResult ResolveBases(
        in RollPath path, Fielder retriever, double retrieveAtSeconds,
        FieldGeometry field, BatterAttributes batter, double batterToFirstSeconds, FieldingCoefficients c)
    {
        var ball = path.PositionAt(retrieveAtSeconds);
        var ready = ReadySeconds(retrieveAtSeconds, retriever.Attributes.Fielding, c);
        var arm = retriever.Attributes.ThrowSpeedMps;

        double ThrowArrivesAt(Vector3D target)
            => ThrowArriveSeconds(ready, (target - ball).Length, arm, c);

        // 打者走者の塁間所要は走者の時計の「打席から」文脈（Issue #316）。一塁到達までは
        // batterToFirstSeconds（打撃動作・左右差込み）なので、そこから1塁ずつ積む。
        var perBase = RunnerClock.SegmentSeconds(
            batter.SpeedToFirstMps(), 1, 2, BatterContextOf(c), field);
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
