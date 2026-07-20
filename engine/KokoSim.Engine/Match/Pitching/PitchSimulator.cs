namespace KokoSim.Engine.Match.Pitching;

/// <summary>
/// 1球の弾道を解決する。スピンあり／なしの2本を積分し、差分から誘発変化量（ホップ量など）を求める。
/// </summary>
public static class PitchSimulator
{
    public static PitchTrajectory Simulate(PitchSpec spec, Aerodynamics aero, MoundGeometry geometry)
    {
        var release = spec.ReleasePosition;
        var v0 = spec.InitialVelocity();
        var distance = geometry.ReleaseToPlateDistanceM;

        var spun = BallisticIntegrator.IntegrateToPlate(release, v0, spec.SpinRadPerSec, aero, distance);
        // 同一初速の無回転球（マグヌス力なし）。抗力・重力は共通なので差分がマグヌス寄与＝誘発変化。
        var spinless = BallisticIntegrator.IntegrateToPlate(release, v0, Core.Vector3D.Zero, aero, distance);

        var hop = spun.CrossingPosition.Y - spinless.CrossingPosition.Y;
        var horizontal = spun.CrossingPosition.X - spinless.CrossingPosition.X;

        return new PitchTrajectory
        {
            FlightTimeSeconds = spun.FlightTimeSeconds,
            PlateCrossing = spun.CrossingPosition,
            FinalVelocity = spun.FinalVelocity,
            InducedVerticalBreakM = hop,
            InducedHorizontalBreakM = horizontal,
        };
    }
}
