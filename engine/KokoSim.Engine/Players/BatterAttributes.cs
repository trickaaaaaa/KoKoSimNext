namespace KokoSim.Engine.Players;

/// <summary>
/// 野手（打者としての）表示層能力（1〜100）。物理層へはバランス係数経由で変換する（不変条件#1）。
///
/// <para>能力5項目は <c>double</c>（issue #1631）: この型は表示値そのものではなく、試合内の実効能力
/// （疲労・調子・スキル・プレッシャー→主将→動揺→伝令→相性→勢い→エンドラン/チャンス/速球打ち・変化球打ち）を
/// 物理層へ渡す直前まで保持する「パイプライン唯一の運び手」でもある。段ごとに int へ丸めると、小さな倍率
/// （例: 制球/ミート50×1.008）が中間段の丸めで消え、かつ適用順で結果が変わってしまう（issue #1241 項目6）。
/// 表示・セーブ側の能力（<see cref="Player.Contact"/> 等）は従来どおり <c>int</c>。この型への変換は
/// <see cref="Player.ToBatter(AbilityEffectCoefficients)"/> の1箇所（不変条件#1）だけで、以降の補正は丸めない。</para>
/// </summary>
public sealed record BatterAttributes
{
    /// <summary>ミート: コンタクト確率・芯を捉える精度。</summary>
    public double Contact { get; init; } = 50;

    /// <summary>パワー: 打球初速の上限。</summary>
    public double Power { get; init; } = 50;

    /// <summary>弾道: 打球角度の分布（ゴロ型〜フライ型）。</summary>
    public double LaunchTendency { get; init; } = 50;

    /// <summary>選球眼: ボール球スイング率・見極め。</summary>
    public double Discipline { get; init; } = 50;

    /// <summary>走力: 塁間タイム（内野安打・進塁判定に寄与）。</summary>
    public double Speed { get; init; } = 50;

    /// <summary>打ち手の利き（設計書01 §1.1c）。左打者は一塁が近く駆け抜けが速い（§4.1b）。</summary>
    public Handedness Bats { get; init; } = Handedness.Right;

    /// <summary>リーグ平均的な打者（全能力50, D帯=一般校の主力）。</summary>
    public static BatterAttributes LeagueAverage => new();

    /// <summary>打者走者のスプリント速度[m/s]（設計書02 §1.2: 6.0 + R×0.025, 天井）。</summary>
    public double SpeedToFirstMps() => 6.0 + Speed * 0.025;
}
