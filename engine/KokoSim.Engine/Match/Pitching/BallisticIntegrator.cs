using KokoSim.Engine.Core;

namespace KokoSim.Engine.Match.Pitching;

/// <summary>
/// 重力＋空気抵抗＋マグヌス力の数値積分（RK4）。設計書01 §2⑥・§4 の物理層計算の中核。
/// スピンは短い飛行中は一定とみなす。純関数（IO・乱数なし）で決定論。
/// </summary>
public static class BallisticIntegrator
{
    /// <summary>積分の1点。位置と速度。</summary>
    public readonly struct State
    {
        public readonly Vector3D Position;
        public readonly Vector3D Velocity;
        public State(Vector3D position, Vector3D velocity)
        {
            Position = position;
            Velocity = velocity;
        }
    }

    /// <summary>本塁面(Z = plateDistance)まで積分した結果。</summary>
    public readonly struct Result
    {
        public readonly double FlightTimeSeconds;
        public readonly Vector3D CrossingPosition;
        public readonly Vector3D FinalVelocity;
        public Result(double flightTime, Vector3D crossing, Vector3D finalVelocity)
        {
            FlightTimeSeconds = flightTime;
            CrossingPosition = crossing;
            FinalVelocity = finalVelocity;
        }
    }

    /// <summary>
    /// リリース点から Z = plateDistance に到達するまで積分する。
    /// </summary>
    /// <param name="release">リリース位置[m]</param>
    /// <param name="initialVelocity">初速度[m/s]</param>
    /// <param name="spin">角速度[rad/s]（一定）</param>
    /// <param name="aero">空力係数</param>
    /// <param name="plateDistanceM">リリース点から本塁面までの Z 距離[m]</param>
    /// <param name="dt">積分刻み[s]</param>
    public static Result IntegrateToPlate(
        Vector3D release,
        Vector3D initialVelocity,
        Vector3D spin,
        Aerodynamics aero,
        double plateDistanceM,
        double dt = 0.0005)
    {
        var targetZ = release.Z + plateDistanceM;
        var state = new State(release, initialVelocity);
        var time = 0.0;

        // 前進が保証される限りループ（数値異常時の暴走を防ぐため上限も設ける）。
        const int maxSteps = 200_000;
        for (var step = 0; step < maxSteps; step++)
        {
            // 投球には風を効かせない（issue #1614）: 常に無風で積分する。
            var next = Rk4Step(state, spin, aero, dt, Vector3D.Zero);

            if (next.Position.Z >= targetZ)
            {
                // 直近ステップ内を線形補間して本塁面ちょうどの通過を求める。
                var z0 = state.Position.Z;
                var z1 = next.Position.Z;
                var frac = (targetZ - z0) / (z1 - z0);
                var crossing = state.Position + (next.Position - state.Position) * frac;
                var vel = state.Velocity + (next.Velocity - state.Velocity) * frac;
                var t = time + dt * frac;
                return new Result(t, crossing, vel);
            }

            state = next;
            time += dt;
        }

        throw new InvalidOperationException("本塁面に到達しませんでした（初期条件が異常です）。");
    }

    /// <summary>着地（Y=0 割り込み）まで積分した結果。打球の滞空時間・着地点・最高到達点を返す。</summary>
    public readonly struct GroundResult
    {
        public readonly double HangTimeSeconds;
        public readonly Vector3D LandingPosition;
        public readonly Vector3D LandingVelocity;
        public readonly double ApexHeightM;
        /// <summary>
        /// 空中でフェンス弧（水平距離 <c>fenceDistanceM</c>）を横切ったか（issue homerun-rate-explodes-at-high-power）。
        /// 横切ったときだけ <see cref="FenceCrossSeconds"/>／<see cref="FenceCrossPosition"/>（Y＝**フェンス上での高さ**）／
        /// <see cref="FenceCrossVelocity"/> が入る。本塁打は「着地距離≥フェンス距離」ではなく
        /// **フェンス上での高さ≥フェンス高**で決める（頂点だけ高い低い当たりは柵の手前でフェンスに当たる）。
        /// </summary>
        public readonly bool CrossedFence;
        public readonly double FenceCrossSeconds;
        public readonly Vector3D FenceCrossPosition;
        public readonly Vector3D FenceCrossVelocity;
        public GroundResult(double hangTime, Vector3D landing, Vector3D landingVelocity, double apex)
            : this(hangTime, landing, landingVelocity, apex, false, 0.0, Vector3D.Zero, Vector3D.Zero) { }
        public GroundResult(double hangTime, Vector3D landing, Vector3D landingVelocity, double apex,
            bool crossedFence, double fenceCrossSeconds, Vector3D fenceCrossPosition, Vector3D fenceCrossVelocity)
        {
            HangTimeSeconds = hangTime;
            LandingPosition = landing;
            LandingVelocity = landingVelocity;
            ApexHeightM = apex;
            CrossedFence = crossedFence;
            FenceCrossSeconds = fenceCrossSeconds;
            FenceCrossPosition = fenceCrossPosition;
            FenceCrossVelocity = fenceCrossVelocity;
        }
    }

    /// <summary>
    /// 打球用: リリース（コンタクト点）から着地（Y=0）まで積分する。守備解決の幾何計算に使う（設計書01 §2⑥）。
    /// </summary>
    /// <param name="fenceDistanceM">
    /// フェンス弧の水平距離[m]（本塁中心）。有限値を渡すと、空中でこの距離を横切った瞬間の高さ・速度・時刻を
    /// <see cref="GroundResult"/> に載せる（守備解決の本塁打／フェンス直撃判定用）。既定＝追跡しない。
    /// 追跡は観測だけで積分そのものは変えない＝着地・頂点・滞空時間は引数の有無で1ビットも変わらない。
    /// </param>
    /// <param name="sampleStepSeconds">
    /// 飛行経路の標本を記録する時間刻み[s]（issue #1135。<paramref name="samples"/> と組で使う）。0以下＝記録しない。
    /// </param>
    /// <param name="samples">
    /// 非nullのとき、t=0（接触点）から着地まで <paramref name="sampleStepSeconds"/> おきの (時刻, 位置) を
    /// 時刻順に追加する。積分そのものは1ビットも変えない＝観測の記録だけ（不変条件#1/#2に影響なし）。
    /// </param>
    /// <param name="wind">
    /// 風速ベクトル[m/s]（地面基準、issue #1614）。打球にだけ効かせる軸なので、呼び出し元は
    /// <see cref="Field.FieldGeometry.Wind"/>（試合ごとに <see cref="Game.WindModel.ApplyForMatch"/> が差し替える）を渡す。
    /// 既定ゼロ＝無風＝従来の弾道と1ビットも変わらない。
    /// </param>
    public static GroundResult IntegrateToGround(
        Vector3D contactPoint,
        Vector3D initialVelocity,
        Vector3D spin,
        Aerodynamics aero,
        double dt = 0.001,
        double fenceDistanceM = double.PositiveInfinity,
        double sampleStepSeconds = 0.0,
        List<FlightSample>? samples = null,
        Vector3D wind = default)
    {
        var state = new State(contactPoint, initialVelocity);
        var time = 0.0;
        var apex = contactPoint.Y;
        var trackFence = !double.IsInfinity(fenceDistanceM) && !double.IsNaN(fenceDistanceM) && fenceDistanceM > 0.0;
        var crossed = false;
        var crossAt = 0.0;
        var crossPos = Vector3D.Zero;
        var crossVel = Vector3D.Zero;

        var trackSamples = samples is not null && sampleStepSeconds > 0.0;
        var nextSampleT = 0.0;
        if (trackSamples)
        {
            samples!.Add(new FlightSample(0.0, contactPoint));
            nextSampleT = sampleStepSeconds;
        }

        const int maxSteps = 200_000;
        for (var step = 0; step < maxSteps; step++)
        {
            var next = Rk4Step(state, spin, aero, dt, wind);
            if (next.Position.Y > apex)
            {
                apex = next.Position.Y;
            }

            if (trackSamples)
            {
                // 1歩(dt)の中で複数の標本境界を跨ぐことは無い（sampleStepSeconds≫dt前提）が、
                // 念のため while で跨いだ分すべてを拾う。
                while (nextSampleT <= time + dt + 1e-12)
                {
                    var frac = dt > 0.0 ? Math.Clamp((nextSampleT - time) / dt, 0.0, 1.0) : 1.0;
                    var pos = state.Position + (next.Position - state.Position) * frac;
                    samples!.Add(new FlightSample(nextSampleT, pos));
                    nextSampleT += sampleStepSeconds;
                }
            }

            if (trackFence && !crossed)
            {
                var r0 = HorizontalRange(state.Position);
                var r1 = HorizontalRange(next.Position);
                if (r1 >= fenceDistanceM && r0 < fenceDistanceM)
                {
                    // 横切りの瞬間を1歩の中で線形補間（着地の補間と同じ流儀）。
                    var frac = r1 > r0 ? (fenceDistanceM - r0) / (r1 - r0) : 1.0;
                    crossed = true;
                    crossPos = state.Position + (next.Position - state.Position) * frac;
                    crossVel = state.Velocity + (next.Velocity - state.Velocity) * frac;
                    crossAt = time + dt * frac;
                }
            }

            if (next.Position.Y <= 0.0 && next.Velocity.Y < 0.0)
            {
                var y0 = state.Position.Y;
                var y1 = next.Position.Y;
                var frac = y1 < y0 ? y0 / (y0 - y1) : 1.0;
                var landing = state.Position + (next.Position - state.Position) * frac;
                var vel = state.Velocity + (next.Velocity - state.Velocity) * frac;
                var t = time + dt * frac;
                // 着地の一歩の中でフェンス弧を横切った（着地点がちょうど柵の外）ケースは、横切りが着地より先。
                if (crossed && crossAt > t)
                {
                    crossed = false;
                }
                return new GroundResult(t, landing, vel, apex, crossed, crossAt, crossPos, crossVel);
            }

            state = next;
            time += dt;
        }

        throw new InvalidOperationException("打球が着地しませんでした（初期条件が異常です）。");
    }

    /// <summary>
    /// 飛行経路の1標本（issue #1135）。打球が着地するまでの通り道を、野手が正面でノーバウンド捕球できるかの
    /// 走査（<see cref="Fielding.FieldingResolver"/>）に使う。<see cref="IntegrateToGround"/> が1回の積分の
    /// ついでに記録するだけ＝再積分なし（決定論・性能とも既存の着地判定と同コスト）。
    /// </summary>
    public readonly record struct FlightSample(double TimeSeconds, Vector3D Position);

    private static double HorizontalRange(Vector3D p) => Math.Sqrt(p.X * p.X + p.Z * p.Z);

    private static State Rk4Step(State s, Vector3D spin, Aerodynamics aero, double dt, Vector3D wind)
    {
        var (k1p, k1v) = Derivative(s, spin, aero, wind);
        var s2 = new State(s.Position + k1p * (dt / 2), s.Velocity + k1v * (dt / 2));
        var (k2p, k2v) = Derivative(s2, spin, aero, wind);
        var s3 = new State(s.Position + k2p * (dt / 2), s.Velocity + k2v * (dt / 2));
        var (k3p, k3v) = Derivative(s3, spin, aero, wind);
        var s4 = new State(s.Position + k3p * dt, s.Velocity + k3v * dt);
        var (k4p, k4v) = Derivative(s4, spin, aero, wind);

        var pos = s.Position + (k1p + 2 * k2p + 2 * k3p + k4p) * (dt / 6);
        var vel = s.Velocity + (k1v + 2 * k2v + 2 * k3v + k4v) * (dt / 6);
        return new State(pos, vel);
    }

    /// <summary>
    /// 状態微分: dPosition = Velocity, dVelocity = 加速度（重力+抗力+マグヌス）。
    /// <paramref name="wind"/> は風速ベクトル[m/s]（地面基準・Y成分は常に0想定、issue #1614）。
    /// 抗力・マグヌス力は「空気に対する相対速度」v_rel = v − wind で計算する（風に押されるのは空気との
    /// 相対運動そのものなので）。位置の微分（dPos）は地面基準の速度 v のまま＝実際に飛ぶ経路は v が決める。
    /// 重力は風と無関係。<see cref="IntegrateToPlate"/>（投球）は常に wind=Vector3D.Zero を渡す
    /// （issue #1614: 風は打球の飛びだけに効かせる・投球には効かせない）。
    /// </summary>
    private static (Vector3D dPos, Vector3D dVel) Derivative(State s, Vector3D spin, Aerodynamics aero, Vector3D wind)
    {
        var v = s.Velocity;
        var vRel = v - wind;
        var speed = vRel.Length;

        // 重力
        var accel = new Vector3D(0, -aero.Gravity, 0);

        if (speed > 0)
        {
            var area = aero.CrossSectionalArea;

            // 抗力: F = -0.5 ρ Cd A |v_rel| v_rel → a = F/m
            var dragMag = 0.5 * aero.AirDensity * aero.DragCoefficient * area * speed / aero.BallMassKg;
            accel -= vRel * dragMag;

            // マグヌス力: 方向 = (ω × v_rel) の単位ベクトル, 大きさ = 0.5 ρ Cl A |v_rel|^2
            var cross = Vector3D.Cross(spin, vRel);
            var crossLen = cross.Length;
            if (crossLen > 0)
            {
                var spinFactor = aero.BallRadiusM * spin.Length / speed;
                var cl = aero.LiftCoefficient(spinFactor);
                var magnusMag = 0.5 * aero.AirDensity * cl * area * speed * speed / aero.BallMassKg;
                accel += (cross / crossLen) * magnusMag;
            }
        }

        return (v, accel);
    }
}
