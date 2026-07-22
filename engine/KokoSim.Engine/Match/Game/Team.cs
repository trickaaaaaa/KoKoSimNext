using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// チーム編成（不変ロースター）。打順9人（うち1人が投手スロット）と控え投手（ブルペン）。
/// 試合中の可変状態は <see cref="TeamState"/> が持つ。
/// </summary>
public sealed record Team
{
    public string Name { get; init; } = "チーム";

    /// <summary>打順（9人）。守備位置を各自が持つ。</summary>
    public required IReadOnlyList<Player> BattingOrder { get; init; }

    /// <summary>打順のうち投手が入るスロット番号（0〜8, 慣例で8＝9番）。DH制（DhSlot≧0）では使わない。</summary>
    public int PitcherSlot { get; init; } = 8;

    /// <summary>控え投手。先発が疲労したら順に登板。</summary>
    public IReadOnlyList<Player> Bullpen { get; init; } = Array.Empty<Player>();

    /// <summary>野手の控え（設計書09 §6, 代打・代走・守備交代の供給元）。空＝交代なし（従来挙動と一致）。</summary>
    public IReadOnlyList<Player> Bench { get; init; } = Array.Empty<Player>();

    /// <summary>
    /// 監督采配（設計書09）。null=無指示（従来挙動と完全一致）。
    /// プレイヤーの手動采配・委任・敵AI（設計書11）はすべて同じ ITacticsBrain を差す。
    /// </summary>
    public Tactics.ITacticsBrain? Tactics { get; init; }

    /// <summary>主将（設計書09 §8）。在場時チーム全体のプレッシャー負補正を統率力に応じて緩和。null=不在。</summary>
    public Player? Captain { get; init; }

    /// <summary>
    /// DH制（設計書09 §6, 現代ルールトグル）。−1=DHなし（従来）。0〜8 のとき該当スロットが打撃専門で、
    /// 投手は打順に入らない（StartingPitcher が必須）。
    /// </summary>
    public int DhSlot { get; init; } = -1;

    /// <summary>DH制のときの先発投手（打順外）。DhSlot≧0 なら必須。</summary>
    public Player? StartingPitcher { get; init; }

    /// <summary>
    /// チーム別の投手疲労係数（issue #55, 監督傾向「エース酷使／継投早め」, 決定4: B-1）。
    /// null=既定＝<c>GameContext.Fatigue</c> をそのまま使う（従来挙動・帯不変）。非nullなら
    /// このチームの継投しきい値（<see cref="PitchingFatigue.ShouldRelieve"/>）に適用される。
    /// 疲労の減衰カーブは <c>GameContext.Fatigue</c> と共通＝監督は継投「時期」だけを変える。
    /// </summary>
    public FatigueCoefficients? Fatigue { get; init; }

    /// <summary>DH制か。</summary>
    public bool UsesDh => DhSlot >= 0;
}
