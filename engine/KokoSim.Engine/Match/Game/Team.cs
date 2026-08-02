using KokoSim.Engine.Players;

namespace KokoSim.Engine.Match.Game;

/// <summary>
/// チーム編成（不変ロースター）。打順9人（うち1人が投手スロット）と控え（1本の名簿）。
/// 試合中の可変状態は <see cref="TeamState"/> が持つ。
/// </summary>
public sealed record Team
{
    public string Name { get; init; } = "チーム";

    /// <summary>打順（9人）。守備位置を各自が持つ。</summary>
    public required IReadOnlyList<Player> BattingOrder { get; init; }

    /// <summary>打順のうち投手が入るスロット番号（0〜8, 慣例で8＝9番）。DH制（DhSlot≧0）では使わない。</summary>
    public int PitcherSlot { get; init; } = 8;

    /// <summary>
    /// 控え（打順外の全員。継投・代打・代走・守備交代の共通の供給元）。空＝交代なし（従来挙動と一致）。
    ///
    /// **投手控えと野手控えに二分しない**（issue #381）。二分は「投手か否か」という選手の属性を前提にした
    /// 構造で、#366「投手は配置であって属性ではない」と衝突する。継投は投手能力の高い者から選べばよく、
    /// 実際 <c>ITacticsBrain</c> の人選は元々この名簿全体を横断して能力で比較している。
    ///
    /// 並びは<b>背番号順＝プレイヤーが決めた優先度</b>（issue #270）。継投の既定指名（
    /// <see cref="TeamState.TryChangePitcher"/>）はこの並びで最初に見つかる「投手として置かれた控え」
    /// （<c>Position == FieldPosition.Pitcher</c>）を採る＝控えを二分していた頃の投手控え先頭と同じ選手になる。
    /// </summary>
    public IReadOnlyList<Player> Reserves { get; init; } = Array.Empty<Player>();

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

    /// <summary>
    /// 打順だけを差し替えた新しいチームを返す。投手枠・DH枠は<b>差し替え後の並びから参照で引き直す</b>
    /// （<see cref="Season.DefenseAssignment.SlotOf"/> と同じ「配列から引き直す」思想。issue #352）。
    /// 打順の並べ替え（<c>ITacticsBrain.ComposeBattingOrder</c> 等）は必ずこの入口を通す＝
    /// 「投を別の打順へ動かしたのに <see cref="PitcherSlot"/> が古いまま＝別の野手が登板扱いになる」
    /// ズレが起きない形にする。
    /// <paramref name="reordered"/> は <see cref="BattingOrder"/> の順列（同じ Player 参照の並べ替え）であること。
    /// 枠の選手を見つけられなければ黙って -1 にせず例外＝投手を同定できないまま試合を始めさせない。
    /// </summary>
    public Team WithBattingOrder(IReadOnlyList<Player> reordered)
    {
        if (reordered is null) throw new ArgumentNullException(nameof(reordered));
        if (reordered.Count != BattingOrder.Count)
            throw new ArgumentException("打順の差し替えは同じ人数の順列である必要があります。", nameof(reordered));

        // DH制では投手は打順外（StartingPitcher）＝PitcherSlot は打順を指していないので引き直さない。
        var pitcherSlot = UsesDh ? PitcherSlot : SlotAfter(PitcherSlot, nameof(PitcherSlot));
        var dhSlot = UsesDh ? SlotAfter(DhSlot, nameof(DhSlot)) : DhSlot;
        return this with { BattingOrder = reordered, PitcherSlot = pitcherSlot, DhSlot = dhSlot };

        int SlotAfter(int slot, string label)
        {
            if (slot < 0 || slot >= BattingOrder.Count) return slot;   // 未設定（-1）・範囲外はそのまま
            var occupant = BattingOrder[slot];
            for (var i = 0; i < reordered.Count; i++)
                if (ReferenceEquals(reordered[i], occupant)) return i;
            throw new ArgumentException(
                $"{label} の選手が差し替え後の打順に居ません（順列ではありません）。", nameof(reordered));
        }
    }
}
