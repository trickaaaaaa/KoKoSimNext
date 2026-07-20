namespace KokoSim.Engine.Players;

/// <summary>
/// 野手（打者としての）表示層能力（1〜100）。物理層へはバランス係数経由で変換する（不変条件#1）。
/// </summary>
public sealed record BatterAttributes
{
    /// <summary>ミート: コンタクト確率・芯を捉える精度。</summary>
    public int Contact { get; init; } = 50;

    /// <summary>パワー: 打球初速の上限。</summary>
    public int Power { get; init; } = 50;

    /// <summary>弾道: 打球角度の分布（ゴロ型〜フライ型）。</summary>
    public int LaunchTendency { get; init; } = 50;

    /// <summary>選球眼: ボール球スイング率・見極め。</summary>
    public int Discipline { get; init; } = 50;

    /// <summary>走力: 塁間タイム（内野安打・進塁判定に寄与）。</summary>
    public int Speed { get; init; } = 50;

    /// <summary>打ち手の利き（設計書01 §1.1c）。左打者は一塁が近く駆け抜けが速い（§4.1b）。</summary>
    public Handedness Bats { get; init; } = Handedness.Right;

    /// <summary>リーグ平均的な打者（全能力50, D帯=一般校の主力）。</summary>
    public static BatterAttributes LeagueAverage => new();

    /// <summary>打者走者のスプリント速度[m/s]（設計書02 §1.2: 6.0 + R×0.025, 天井）。</summary>
    public double SpeedToFirstMps() => 6.0 + Speed * 0.025;
}
