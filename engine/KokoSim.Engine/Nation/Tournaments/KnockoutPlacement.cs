using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 準決勝敗退2校の3位決定戦を行う大会の種別（issue #1219）。順位の3位・4位が次の出場枠（秋季地区大会の県内順位・
/// 近畿の決定戦候補・センバツ選考の地区順位）に効く大会だけ＝秋季県大会・秋季地区予選・秋季地区大会。
/// 夏の県大会・甲子園・神宮は勝者だけが意味を持つので行わない。
/// </summary>
public static class ThirdPlaceRule
{
    public static bool AppliesTo(TournamentKind kind)
        => kind is TournamentKind.Autumn or TournamentKind.AutumnDistrict or TournamentKind.Regional;
}

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
    /// 3位決定戦（<see cref="BracketMatch.IsThirdPlace"/>）があれば、準決勝敗退2校はその勝者が3位・敗者が4位（issue #1219）。
    /// </summary>
    public static IReadOnlyList<School> FromMatches(
        IReadOnlyList<BracketMatch> matches, Func<int, School> byId)
    {
        if (matches.Count == 0) return Array.Empty<School>();

        // RoundsRemaining は「決勝=1」から浅い方へ増える。深い敗退（値が小さい）ほど上位。
        var thirdPlace = matches.FirstOrDefault(m => m.IsThirdPlace);
        var byRound = matches.Where(m => !m.IsThirdPlace).GroupBy(m => m.RoundsRemaining).OrderBy(g => g.Key).ToList();

        var final = byRound[0];
        var championMatch = final.First();
        var placement = new List<School> { byId(championMatch.WinnerId), byId(championMatch.LoserId) };

        for (var i = 1; i < byRound.Count; i++)
        {
            if (byRound[i].Key == 2 && thirdPlace is not null && byRound[i].Count() == 2)
            {
                placement.Add(byId(thirdPlace.WinnerId));
                placement.Add(byId(thirdPlace.LoserId));
                continue;
            }
            var losers = byRound[i]
                .Select(m => byId(m.LoserId))
                .OrderByDescending(s => s.Strength)
                .ThenBy(s => s.Id);
            placement.AddRange(losers);
        }

        return placement;
    }

    /// <summary>
    /// <paramref name="matches"/>（<see cref="TournamentRecorder.Records"/>・初戦から決勝への順）から
    /// 指定校が実際に戦った相手の並びを組み立てる（<see cref="NationalTournamentEngine.ChampionPathOf"/>
    /// の一般化＝優勝校に限らず、敗退するまでの並びを返す）。センバツの「選手一人一人の地区大会成績」
    /// 再シム（<see cref="QualifierStatsSim"/>）の入力。乱数不使用。
    /// </summary>
    public static IReadOnlyList<ChampionMatchup> PathOf(IReadOnlyList<TournamentMatchRecord> matches, int schoolId)
    {
        var path = new List<ChampionMatchup>();
        foreach (var m in matches)
        {
            if (m.Winner.Id == schoolId) path.Add(new ChampionMatchup(m.Loser.Id, m.RoundsRemaining));
            else if (m.Loser.Id == schoolId)
            {
                path.Add(new ChampionMatchup(m.Winner.Id, m.RoundsRemaining));
                break;   // 敗退＝この校のブラケットはここで終わる（単一エリミネーション）。
            }
        }
        return path;
    }
}
