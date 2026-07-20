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
            var next = Rk4Step(state, spin, aero, dt);

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
        public GroundResult(double hangTime, Vector3D landing, Vector3D landingVelocity, double apex)
        {
            HangTimeSeconds = hangTime;
            LandingPosition = landing;
            LandingVelocity = landingVelocity;
            ApexHeightM = apex;
        }
    }

    /// <summary>
    /// 打球用: リリース（コンタクト点）から着地（Y=0）まで積分する。守備解決の幾何計算に使う（設計書01 §2⑥）。
    /// </summary>
    public static GroundResult IntegrateToGround(
        Vector3D contactPoint,
        Vector3D initialVelocity,
        Vector3D spin,
        Aerodynamics aero,
        double dt = 0.001)
    {
        var state = new State(contactPoint, initialVelocity);
        var time = 0.0;
        var apex = contactPoint.Y;

        const int maxSteps = 200_000;
        for (var step = 0; step < maxSteps; step++)
        {
            var next = Rk4Step(state, spin, aero, dt);
            if (next.Position.Y > apex)
            {
                apex = next.Position.Y;
            }

            if (next.Position.Y <= 0.0 && next.Velocity.Y < 0.0)
            {
                var y0 = state.Position.Y;
                var y1 = next.Position.Y;
                var frac = y1 < y0 ? y0 / (y0 - y1) : 1.0;
                var landing = state.Position + (next.Position - state.Position) * frac;
                var vel = state.Velocity + (next.Velocity - state.Velocity) * frac;
                var t = time + dt * frac;
                return new GroundResult(t, landing, vel, apex);
            }

            state = next;
            time += dt;
        }

        throw new InvalidOperationException("打球が着地しませんでした（初期条件が異常です）。");
    }

    private static State Rk4Step(State s, Vector3D spin, Aerodynamics aero, double dt)
    {
        var (k1p, k1v) = Derivative(s, spin, aero);
        var s2 = new State(s.Position + k1p * (dt / 2), s.Velocity + k1v * (dt / 2));
        var (k2p, k2v) = Derivative(s2, spin, aero);
        var s3 = new State(s.Position + k2p * (dt / 2), s.Velocity + k2v * (dt / 2));
        var (k3p, k3v) = Derivative(s3, spin, aero);
        var s4 = new State(s.Position + k3p * dt, s.Velocity + k3v * dt);
        var (k4p, k4v) = Derivative(s4, spin, aero);

        var pos = s.Position + (k1p + 2 * k2p + 2 * k3p + k4p) * (dt / 6);
        var vel = s.Velocity + (k1v + 2 * k2v + 2 * k3v + k4v) * (dt / 6);
        return new State(pos, vel);
    }

    /// <summary>状態微分: dPosition = Velocity, dVelocity = 加速度（重力+抗力+マグヌス）。</summary>
    private static (Vector3D dPos, Vector3D dVel) Derivative(State s, Vector3D spin, Aerodynamics aero)
    {
        var v = s.Velocity;
        var speed = v.Length;

        // 重力
        var accel = new Vector3D(0, -aero.Gravity, 0);

        if (speed > 0)
        {
            var area = aero.CrossSectionalArea;

            // 抗力: F = -0.5 ρ Cd A |v| v → a = F/m
            var dragMag = 0.5 * aero.AirDensity * aero.DragCoefficient * area * speed / aero.BallMassKg;
            accel -= v * dragMag;

            // マグヌス力: 方向 = (ω × v) の単位ベクトル, 大きさ = 0.5 ρ Cl A |v|^2
            var cross = Vector3D.Cross(spin, v);
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
