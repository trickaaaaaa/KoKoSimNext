namespace KokoSim.Engine.Match.AtBat;

/// <summary>1球の種別。中継風カウント表示・実データで共通の分類。</summary>
public enum PitchKind
{
    Ball,
    CalledStrike,
    SwingingStrike,
    Foul,
    InPlay,
    HitByPitch,
}

/// <summary>
/// <see cref="AtBatSession"/> が実際に解いた1球の記録（設計書15 §4）。
/// 解決済みの値の写しのみを持ち、新たな抽選はしない（観測データ＝試合結果に影響しない）。
/// <see cref="Trajectory"/> は観測専用（設計書15 §0.1 Q12-5）で、Phase B〜D は判定に使わない。
/// <see cref="BattedBall"/> も同様に観測専用（Issue #313）。コンタクトが実際に物理層（初速・角度・方向）を
/// 生成した球だけ非null。ファウルのうち <see cref="Batting.BattingCoefficients.FoulShare"/> の確率だけで決まる
/// 「当たったが物理を生成しない」経路（<see cref="Batting.ContactModel.Resolve"/> の <c>(Foul, null)</c>）や
/// バント/スクイズのファウルは、そもそも解決済みの値が存在しないため null のまま（観測データは
/// 「既に解決済みの値の写し」のみ＝ここで新たに生成しない）。
/// <see cref="AimX"/>/<see cref="AimY"/>（Issue #396）は <see cref="Pitching.PitchPlan.AimX"/>/
/// <see cref="Pitching.PitchPlan.AimY"/> の写し（狙い位置。捕手が投球前から知っている値）。
/// <see cref="LocationX"/>/<see cref="LocationY"/>（コントロール散布込みの実着弾位置）とは別物で、
/// 判定・帯には一切関与しない表示専用の付帯情報。
/// <see cref="Sign"/>（issue #490）はこの球に出ていた打者への1球指示（<see cref="Tactics.PitchBattingOverride"/>
/// の写し）。バント/セーフティバント/スクイズの構えを対決ビューに出すための表示専用データで、
/// 判定・帯には一切関与しない（バント/スクイズ判定自体は従来どおり <see cref="AtBatSession"/> 内で完結）。
/// </summary>
public readonly record struct PitchRecord(
    PitchKind Kind,
    int BallsAfter,
    int StrikesAfter,
    Players.PitchType PitchType,
    double LocationX,
    double LocationY,
    double VelocityKmh,
    double AimX,
    double AimY,
    Pitching.PitchTrajectory? Trajectory = null,
    Batting.BattedBall? BattedBall = null,
    Tactics.PitchBattingOverride? Sign = null);
