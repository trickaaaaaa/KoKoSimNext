using System;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;

namespace KokoSim.Engine.Tests.Match.Fielding;

/// <summary>
/// テスト用の <see cref="FieldingPlay"/> 生成（Issue #315）。<see cref="FieldingResolver"/> の
/// 各経路と**同じ形**を1対1で写す。「同じ形」とは値の一致ではなく、
/// **どの派生メンバーを埋め、どれを未設定のまま残すか**が一致していること。
///
/// 手書きの <c>new FieldingPlay</c> は、リゾルバが必ず埋める派生メンバーを埋め忘れても
/// 既定値がドメイン上ありうる値（座標の 0＝本塁、分類の Grounder）なので誤りが検出されず、
/// **テストが誤った挙動を「正しい」として固定してしまう**。Issue #301 はこれで長く生き残った。
///
/// 個別のズラしは戻り値に <c>with { ... }</c> を掛けて表現し、テストで素の
/// <c>new FieldingPlay</c> は書かないこと。
/// </summary>
internal static class FieldingPlayFixtures
{
    /// <summary>
    /// 全経路の共通部（<c>FieldingResolver.Base</c>）。回収点(FieldedX/Z)は**埋めない**
    /// ＝ null（着地点と同値）。転がりを解く経路だけが上書きする。
    /// 距離・方位は着地点から導出するので、幾何が内部矛盾したフィクスチャを作れない。
    /// </summary>
    private static FieldingPlay Base(
        BattedBallResult result, double landingX, double landingZ,
        double hangTimeSeconds, double apexHeightM, double batterToFirstSeconds, BattedBallClass? ballClass)
        => new()
        {
            Result = result,
            LandingX = landingX,
            LandingZ = landingZ,
            HangTimeSeconds = hangTimeSeconds,
            ApexHeightM = apexHeightM,
            RangeM = Math.Sqrt(landingX * landingX + landingZ * landingZ),
            BearingDeg = Math.Atan2(landingX, landingZ) * 180.0 / Math.PI,
            BatterToFirstSeconds = batterToFirstSeconds,
            Class = ballClass,
        };

    /// <summary>
    /// ファウル（<c>FieldingResolver.ResolveDetailed</c> の方位±45°超え経路）。
    /// 弾道積分の前に返るので幾何も分類も処理野手も持たない。
    /// </summary>
    public static FieldingPlay Foul(double bearingDeg = 60.0, double batterToFirstSeconds = 4.2) => new()
    {
        Result = BattedBallResult.Foul,
        LandingX = 0, LandingZ = 0, HangTimeSeconds = 0, ApexHeightM = 0,
        RangeM = 0, BearingDeg = bearingDeg, BatterToFirstSeconds = batterToFirstSeconds,
    };

    /// <summary>
    /// 本塁打（<c>Base</c> のみを返す経路）。処理野手が居ないので回収点も回収時刻も送球速度も持たない。
    /// <c>IsFly</c> は本塁打経路では立たない（リゾルバの挙動をそのまま写す）。
    /// </summary>
    public static FieldingPlay HomeRun(
        double landingX = 0.0, double landingZ = 130.0,
        double hangTimeSeconds = 4.2, double apexHeightM = 22.0, double batterToFirstSeconds = 4.2)
        => Base(BattedBallResult.HomeRun, landingX, landingZ,
            hangTimeSeconds, apexHeightM, batterToFirstSeconds, BattedBallClass.Fly);

    /// <summary>
    /// 空中捕球（フライ／ライナーのアウト・失策）。回収時刻＝滞空しきった瞬間で、
    /// 回収点は着地点なので**未設定のまま**にする（リゾルバも書き込まない）。
    /// </summary>
    public static FieldingPlay CaughtFly(
        FieldPosition fielderRole, double landingX = 0.0, double landingZ = 88.0,
        double hangTimeSeconds = 3.5, double apexHeightM = 25.0, double batterToFirstSeconds = 4.2,
        BattedBallResult result = BattedBallResult.Out,
        double throwSpeedMps = 31.9, int fielding = 50, int throwAccuracy = 50)
        => Base(result, landingX, landingZ, hangTimeSeconds, apexHeightM, batterToFirstSeconds, BattedBallClass.Fly)
            with
            {
                IsFly = true,
                FielderRole = fielderRole,
                FieldedAtSeconds = hangTimeSeconds,
                FielderThrowSpeedMps = throwSpeedMps,
                FielderFielding = fielding,
                FielderThrowAccuracy = throwAccuracy,
            };

    /// <summary>
    /// 内野ゴロ（一塁送球 vs 打者走者で決着する経路）。着地点を省略すると実捕球点と同値にする。
    /// 判定margin を省略すると、リゾルバと同じ式（送球到達 − 走者到達 ＋ ForceOutMargin(既定0)）で導く。
    /// </summary>
    public static FieldingPlay InfieldGrounder(
        FieldPosition fielderRole, double fieldedX = -9.0, double fieldedZ = 30.0,
        double fieldedAtSeconds = 1.4, double throwArriveSeconds = 3.0, double batterToFirstSeconds = 4.2,
        BattedBallResult result = BattedBallResult.Out,
        double? landingX = null, double? landingZ = null,
        double hangTimeSeconds = 0.3, double apexHeightM = 0.4,
        BattedBallClass ballClass = BattedBallClass.Grounder,
        double bounceApexHeightM = 0.3, int bounceCount = 2, bool irregularBounce = false,
        double throwSpeedMps = 34.0, int fielding = 50, int throwAccuracy = 50,
        double? judgementMarginSeconds = null)
        => Base(result, landingX ?? fieldedX, landingZ ?? fieldedZ,
                hangTimeSeconds, apexHeightM, batterToFirstSeconds, ballClass)
            with
            {
                BounceApexHeightM = bounceApexHeightM,
                BounceCount = bounceCount,
                IrregularBounce = irregularBounce,
                FieldedX = fieldedX,
                FieldedZ = fieldedZ,
                FielderRole = fielderRole,
                FieldedAtSeconds = fieldedAtSeconds,
                ThrowArriveSeconds = throwArriveSeconds,
                FielderThrowSpeedMps = throwSpeedMps,
                FielderFielding = fielding,
                FielderThrowAccuracy = throwAccuracy,
                JudgementMarginSeconds =
                    judgementMarginSeconds ?? throwArriveSeconds - batterToFirstSeconds,
            };

    /// <summary>
    /// 外野への安打（着地後の転がりを解いて塁打数を決める経路）。回収点・回収時刻は
    /// 転がり経路から出るので着地点とズレる。一塁送球の勝負はしないので
    /// <c>ThrowArriveSeconds</c> / <c>JudgementMarginSeconds</c> は持たない。
    /// </summary>
    public static FieldingPlay OutfieldHit(
        FieldPosition fielderRole, double landingX, double landingZ,
        double fieldedAtSeconds, double? fieldedX = null, double? fieldedZ = null,
        BattedBallResult result = BattedBallResult.Single,
        double hangTimeSeconds = 1.2, double apexHeightM = 3.0, double batterToFirstSeconds = 4.2,
        bool isFly = false, BattedBallClass? ballClass = null, bool throughInfield = false,
        double bounceApexHeightM = 0.0, int bounceCount = 0,
        double throwSpeedMps = 31.9, int fielding = 50, int throwAccuracy = 50)
        => Base(result, landingX, landingZ, hangTimeSeconds, apexHeightM, batterToFirstSeconds,
                ballClass ?? (isFly ? BattedBallClass.Fly : BattedBallClass.Grounder))
            with
            {
                IsFly = isFly,
                ThroughInfield = throughInfield,
                BounceApexHeightM = bounceApexHeightM,
                BounceCount = bounceCount,
                FieldedX = fieldedX ?? landingX,
                FieldedZ = fieldedZ ?? landingZ,
                FielderRole = fielderRole,
                FieldedAtSeconds = fieldedAtSeconds,
                FielderThrowSpeedMps = throwSpeedMps,
                FielderFielding = fielding,
                FielderThrowAccuracy = throwAccuracy,
            };
}
