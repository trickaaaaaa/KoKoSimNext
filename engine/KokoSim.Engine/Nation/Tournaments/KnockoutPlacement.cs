using System;
using System.Collections.Generic;
using System.Linq;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 対話プレイ済み <see cref="TournamentRunner"/> の <see cref="BracketMatch"/> 列から、
/// <see cref="Knockout"/> と同じ規則（優勝→準優勝→ベスト4…、同ラウンド内は強さ降順→Id）で
/// 順位付きプレースメントを組み立てる（設計書05 §1.5b）。
///
/// <para><b>存在理由</b>: <see cref="TournamentRunner"/> は自校の対話プレイ・観戦の両方に対応する
/// カード単位の進行体で、<see cref="Knockout.Run"/>（バッチ計算・順位付きで即返す）とは実装が別。
/// 地区大会の出場枠（<see cref="RegionalFormat.BerthsFor"/>）は「県大会で何位だったか」を要るため、
/// 対話プレイされた県大会から順位を逆算するこのヘルパーが要る。</para>
/// </summary>
public static class KnockoutPlacement
{
    /// <summary>
    /// 解決済みの全カードから順位を組み立てる。<paramref name="matches"/> は決着済みの大会の
    /// <see cref="TournamentBracketView.Matches"/>（空だと空配列を返す＝呼び出し側で大会未決着を弾く）。
    /// </summary>
    public static IReadOnlyList<School> FromMatches(
        IReadOnlyList<BracketMatch> matches, Func<int, School> byId)
    {
        if (matches.Count == 0) return Array.Empty<School>();

        // RoundsRemaining は「決勝=1」から浅い方へ増える。深い敗退（値が小さい）ほど上位。
        var byRound = matches.GroupBy(m => m.RoundsRemaining).OrderBy(g => g.Key).ToList();

        var final = byRound[0];
        var championMatch = final.First();
        var placement = new List<School> { byId(championMatch.WinnerId), byId(championMatch.LoserId) };

        for (var i = 1; i < byRound.Count; i++)
        {
            var losers = byRound[i]
                .Select(m => byId(m.LoserId))
                .OrderByDescending(s => s.Strength)
                .ThenBy(s => s.Id);
            placement.AddRange(losers);
        }

        return placement;
    }
}
