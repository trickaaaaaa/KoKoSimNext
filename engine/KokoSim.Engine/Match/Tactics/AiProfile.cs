namespace KokoSim.Engine.Match.Tactics;

/// <summary>
/// 敵AI（および委任采配）の三層プロファイル（設計書11 §0）。
/// これを <see cref="AiTacticsBrain"/> に与えると、共通の采配システム（設計書09）の
/// 「どの選択肢をどう選ぶか」だけがこの三層で変わる（AI専用の裏ルールは作らない）。
/// </summary>
/// <param name="TacticalSense">① 采配能力（1〜100, 設計書04）。高いほど正着を選ぶ＝ミスが減る。</param>
/// <param name="TierRank">② AIティア（0=G 〜 7=S）。使える戦術の引き出しの高度さ。</param>
/// <param name="Style">③ 校風。采配の好み・チームの色。委任采配（自チーム）は Standard。</param>
public readonly record struct AiProfile(int TacticalSense, int TierRank, SchoolStyle Style)
{
    /// <summary>プレイヤー委任采配用（校風なし＝Standard, 賢さはプレイヤー自身の采配能力, 設計書11 §7）。</summary>
    public static AiProfile Delegated(int tacticalSense, int tierRank)
        => new(tacticalSense, tierRank, SchoolStyle.Standard);
}
