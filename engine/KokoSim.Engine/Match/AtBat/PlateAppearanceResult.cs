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
    HitByPitch,
}

public static class PlateAppearanceResultExtensions
{
    /// <summary>安打か（単打〜本塁打）。</summary>
    public static bool IsHit(this PlateAppearanceResult r)
        => r is PlateAppearanceResult.Single or PlateAppearanceResult.Double
            or PlateAppearanceResult.Triple or PlateAppearanceResult.HomeRun;

    /// <summary>公式打数に数えるか（四球・死球は打数に含めない）。</summary>
    public static bool IsAtBat(this PlateAppearanceResult r)
        => r is not (PlateAppearanceResult.Walk or PlateAppearanceResult.HitByPitch);

    /// <summary>打席確定球がインプレー/被安打を生んだか（issue #180。三振・四球・死球は対象外）。</summary>
    public static bool IsBattedBall(this PlateAppearanceResult r)
        => r is not (PlateAppearanceResult.Strikeout or PlateAppearanceResult.Walk or PlateAppearanceResult.HitByPitch);

    /// <summary>塁打数（長打率用）。</summary>
    public static int TotalBases(this PlateAppearanceResult r) => r switch
    {
        PlateAppearanceResult.Single => 1,
        PlateAppearanceResult.Double => 2,
        PlateAppearanceResult.Triple => 3,
        PlateAppearanceResult.HomeRun => 4,
        _ => 0,
    };

    /// <summary>見逃し三振か（三振のうち、最終球が振らずに入った CalledStrike のもの）。空振り三振との
    /// 内訳を測る（見逃し三振率の調査）。<paramref name="pitchLog"/> が null/空（バント等 AtBatSession を
    /// 通らない経路）では常に false。判定・帯には関与しない集計専用。</summary>
    public static bool IsLookingStrikeout(this PlateAppearanceResult r, System.Collections.Generic.IReadOnlyList<PitchRecord>? pitchLog)
        => r == PlateAppearanceResult.Strikeout && pitchLog is { Count: > 0 } log && log[^1].Kind == PitchKind.CalledStrike;
}
