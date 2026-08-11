using System;

namespace KokoSim.Engine.Season;

/// <summary>
/// 練習メニューの分野（設計書03 §3.1 / 04 §1.4 備品）。練習道具はこの分野単位で効く。
/// 施設の効率係数（<see cref="TrainingCoefficients.FacilityCoef"/>）が全メニュー一律なのに対し、
/// 道具は「振っているメニューの分野」にだけ効く＝練習計画と噛み合う支出先になる。
/// </summary>
public enum TrainingField
{
    /// <summary>打撃（ミート打撃・長打打撃・選球練習・筋力トレ）。</summary>
    Batting,
    /// <summary>守備（守備練習・遠投送球・ポジション別守備）。</summary>
    Defense,
    /// <summary>走塁（走塁練習・ランニング・バント練習）。</summary>
    BaseRunning,
    /// <summary>投手（投げ込み・変化球練習・球速強化）。</summary>
    Pitching,
}

/// <summary>練習メニュー → 分野の対応。分野判定はこのクラスにしか置かない（写像の単一ソース）。</summary>
public static class TrainingFields
{
    /// <summary>
    /// メニューの分野。休養は分野を持たないので <c>null</c>。
    /// メニューを追加したらここにも足すこと（<c>TrainingEquipmentTests</c> が取りこぼしを検出する）。
    /// </summary>
    public static TrainingField? OfOrNull(TrainingMenu menu) => menu switch
    {
        TrainingMenu.Batting => TrainingField.Batting,
        TrainingMenu.PowerHitting => TrainingField.Batting,
        TrainingMenu.PlateDiscipline => TrainingField.Batting,
        // 筋力トレは主効果がパワー＝打撃の道具（ティー・マシン）と同じ棚に置く。
        TrainingMenu.Strength => TrainingField.Batting,

        TrainingMenu.Defense => TrainingField.Defense,
        TrainingMenu.Throwing => TrainingField.Defense,
        TrainingMenu.DefenseP => TrainingField.Defense,
        TrainingMenu.DefenseC => TrainingField.Defense,
        TrainingMenu.Defense1B => TrainingField.Defense,
        TrainingMenu.Defense2B => TrainingField.Defense,
        TrainingMenu.Defense3B => TrainingField.Defense,
        TrainingMenu.DefenseSS => TrainingField.Defense,
        TrainingMenu.DefenseLF => TrainingField.Defense,
        TrainingMenu.DefenseCF => TrainingField.Defense,
        TrainingMenu.DefenseRF => TrainingField.Defense,
        TrainingMenu.DefenseInfield => TrainingField.Defense,
        TrainingMenu.DefenseOutfield => TrainingField.Defense,

        TrainingMenu.BaseRunning => TrainingField.BaseRunning,
        TrainingMenu.Running => TrainingField.BaseRunning,
        // バントは小技＝走塁の棚（主効果はバント、副効果でミートも少し伸びる）。
        TrainingMenu.Bunt => TrainingField.BaseRunning,

        TrainingMenu.Pitching => TrainingField.Pitching,
        TrainingMenu.BreakingBall => TrainingField.Pitching,
        TrainingMenu.VelocityTraining => TrainingField.Pitching,

        TrainingMenu.Rest => null,
        _ => null,
    };

    /// <summary>メニューの分野。休養を渡すと例外（呼び出し側で休養を除いてから使う）。</summary>
    public static TrainingField Of(TrainingMenu menu) =>
        OfOrNull(menu) ?? throw new ArgumentOutOfRangeException(
            nameof(menu), menu, "分野を持たないメニュー（休養）");
}

/// <summary>
/// 練習道具による分野別の成長倍率（設計書04 §1.4 備品）。1.0＝未所持。
/// 効き方は「配分した分を倍率ぶん増やしたのと同じ」＝<see cref="DevelopmentModel"/> の
/// 配分スケールと同じ位置に掛かる。全分野1.0（<see cref="None"/>／null）で従来と1ビット一致する（不変条件#2）。
/// </summary>
public sealed record EquipmentBonus
{
    public double Batting { get; init; } = 1.0;
    public double Defense { get; init; } = 1.0;
    public double BaseRunning { get; init; } = 1.0;
    public double Pitching { get; init; } = 1.0;

    /// <summary>道具なし（全分野1.0）。</summary>
    public static EquipmentBonus None { get; } = new();

    /// <summary>分野の倍率。</summary>
    public double For(TrainingField f) => f switch
    {
        TrainingField.Batting => Batting,
        TrainingField.Defense => Defense,
        TrainingField.BaseRunning => BaseRunning,
        TrainingField.Pitching => Pitching,
        _ => 1.0,
    };
}
