namespace KokoSim.Engine.Match.AtBat;

/// <summary>打席（打席完了=Plate Appearance）の結果。</summary>
public enum PlateAppearanceResult
{
    Strikeout,
    Walk,
    Single,
    Double,
    Triple,
    HomeRun,
    InPlayOut,
    ReachedOnError,
}

public static class PlateAppearanceResultExtensions
{
    /// <summary>安打か（単打〜本塁打）。</summary>
    public static bool IsHit(this PlateAppearanceResult r)
        => r is PlateAppearanceResult.Single or PlateAppearanceResult.Double
            or PlateAppearanceResult.Triple or PlateAppearanceResult.HomeRun;

    /// <summary>公式打数に数えるか（四球は打数に含めない）。</summary>
    public static bool IsAtBat(this PlateAppearanceResult r)
        => r is not PlateAppearanceResult.Walk;

    /// <summary>塁打数（長打率用）。</summary>
    public static int TotalBases(this PlateAppearanceResult r) => r switch
    {
        PlateAppearanceResult.Single => 1,
        PlateAppearanceResult.Double => 2,
        PlateAppearanceResult.Triple => 3,
        PlateAppearanceResult.HomeRun => 4,
        _ => 0,
    };
}
