using System;
using KokoSim.Engine.Core;

namespace KokoSim.Engine.Match.Fielding;

/// <summary>
/// 送球1本ごとの逸れ（横・高さ）を表す共通モデル（issue #841）。送球精度から決まる散布σ
/// （<see cref="KokoSim.Engine.Players.FielderAttributes.ThrowScatterSigmaCm"/>）を2次元正規分布へ通す。
/// 投球コントロール散布（<see cref="KokoSim.Engine.Match.Pitching.ControlScatter"/>）と同型の幾何モデルで、
/// 設計書02 §1.2 が「発展タスク」として温存していたσの消費先をここで実装する。
///
/// <para><b>最初に通すのは捕手の盗塁送球（<see cref="KokoSim.Engine.Match.Game.StealResolver"/>）だけ</b>
/// （issue #841・2026-11-09 オーナー判断）。内野→一塁・併殺・外野送球/中継・バント処理への展開（既存の
/// 二値の悪送球ロールとの統合も含む）は後続issueに分ける。</para>
/// </summary>
public static class ThrowScatterModel
{
    /// <summary>1本の送球の逸れ[cm]。Lateral=横方向（符号は左右の意味を持たず対称に扱う）。
    /// Vertical=高さ方向（+は浮く・-は低く短くバウンドする側＝ショートバウンド）。</summary>
    public readonly record struct Scatter(double LateralCm, double VerticalCm)
    {
        /// <summary>逸れの大きさ（原点からの距離）[cm]。悪送球判定（<see cref="IsWild"/>）の入力。</summary>
        public double RadialCm => Math.Sqrt(LateralCm * LateralCm + VerticalCm * VerticalCm);
    }

    /// <summary>横・縦それぞれ独立に N(0, σ) をサンプリングする（<see cref="KokoSim.Engine.Match.Pitching.ControlScatter.Sample"/> と同型）。
    /// 乱数は必ず注入された IRandomSource を使う（不変条件#2）。</summary>
    public static Scatter Sample(double sigmaCm, IRandomSource rng)
        => new(rng.NextGaussian(0.0, sigmaCm), rng.NextGaussian(0.0, sigmaCm));

    /// <summary>
    /// 逸れ1本（実際にサンプリングした値）から、受け手がタッチ／ベースを踏むまでに伸びる秒。
    /// 横＝寄る・足が離れる分、浮き＝下ろす分、低い（ショートバウンド側）＝捕り直す分をそれぞれ重みづけて
    /// 加算し、受け手の捕球能力（Catching）で縮める（2026-11-09オーナー判断: 逸れは送球精度だけから引き、
    /// 受け手の捕球能力で遅れが縮む形にする）。
    /// </summary>
    public static double RecoverySeconds(
        Scatter s, int receiverCatching,
        double lateralSecPerCm, double highSecPerCm, double shortHopSecPerCm,
        double catchingReliefSlope, double catchingReliefMinFactor)
    {
        var raw = lateralSecPerCm * Math.Abs(s.LateralCm)
                  + highSecPerCm * Math.Max(0.0, s.VerticalCm)
                  + shortHopSecPerCm * Math.Max(0.0, -s.VerticalCm);
        return raw * CatchingReliefFactor(receiverCatching, catchingReliefSlope, catchingReliefMinFactor);
    }

    /// <summary>
    /// サンプリングせずに求めた期待値（AIの読み・強制発動の絵づくり用）。横・縦それぞれ独立なN(0,σ)の
    /// 半正規分布の期待値（|X|の期待値=σ√(2/π)、max(X,0)の期待値=σ/√(2π)）を使う。
    /// </summary>
    public static double ExpectedRecoverySeconds(
        double sigmaCm, int receiverCatching,
        double lateralSecPerCm, double highSecPerCm, double shortHopSecPerCm,
        double catchingReliefSlope, double catchingReliefMinFactor)
    {
        const double absMeanFactor = 0.7978845608028654;   // sqrt(2/π)
        const double halfMeanFactor = 0.3989422804014327;  // 1/sqrt(2π)
        var raw = lateralSecPerCm * sigmaCm * absMeanFactor
                  + highSecPerCm * sigmaCm * halfMeanFactor
                  + shortHopSecPerCm * sigmaCm * halfMeanFactor;
        return raw * CatchingReliefFactor(receiverCatching, catchingReliefSlope, catchingReliefMinFactor);
    }

    /// <summary>逸れの大きさが閾値以上なら悪送球（ボールが逸れて守備側に正規のプレーがない）。</summary>
    public static bool IsWild(Scatter s, double wildThresholdCm) => s.RadialCm >= wildThresholdCm;

    /// <summary>受け手の捕球能力(Catching)による遅れの縮み率。Catching50で×1.0＝恒等（帯不変, 不変条件#5）。
    /// 高いほど縮む（＜1）、低いほど伸びる（＞1）。下限は<paramref name="minFactor"/>でクランプ（最速側）。</summary>
    private static double CatchingReliefFactor(int receiverCatching, double slope, double minFactor)
        => Math.Max(minFactor, 1.0 - (receiverCatching - 50) * slope);
}
