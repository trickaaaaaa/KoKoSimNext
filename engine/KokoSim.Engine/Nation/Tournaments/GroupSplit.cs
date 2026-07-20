using KokoSim.Engine.Core;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>
/// 地区ブロック分割（設計書05 §1.5 group_split / CHANGELOG 27-28）。
/// 各ブロックへ地理割 or 抽選で振り分け、ブロック内で子ステージ（knockout/round_robin）を実行し、
/// 上位が次ステージへ進む。敗者復活（loser_bracket）は各ブロックで代表決定戦を追加する。決定論。
/// </summary>
public static class GroupSplit
{
    /// <summary>ブロック分割を実行し、全ブロックの進出校を返す。districtOf は geographic 割当用。</summary>
    public static IReadOnlyList<School> Run(
        IReadOnlyList<School> entrants, StageFormat stage, NationCoefficients coeff, IRandomSource rng,
        System.Func<School, int?>? districtOf = null, TournamentRecorder? recorder = null)
    {
        var groupCount = stage.Groups ?? 1;
        var groups = SplitIntoGroups(entrants, groupCount, stage.Grouping, districtOf, rng);
        var advancePerGroup = stage.AdvancePerGroup ?? 1;
        var loserAdvance = stage.LoserBracket is { Enabled: true } lb ? lb.Advance ?? 1 : 0;

        var advancers = new List<School>();
        foreach (var group in groups)
        {
            if (group.Count == 0) continue;
            var childType = stage.Child?.Type ?? StageType.Knockout;
            if (childType == StageType.RoundRobin)
            {
                var standings = RoundRobin.Run(group, coeff, rng, recorder);
                advancers.AddRange(standings.Take(advancePerGroup).Select(s => s.School));
            }
            else
            {
                var result = Knockout.Run(group, coeff, rng, thirdPlaceMatch: false, recorder);
                advancers.AddRange(result.Top(advancePerGroup));
                // 敗者復活: 進出できなかった中から代表決定戦（設計書05 §1.5, 兵庫型）。
                if (loserAdvance > 0)
                {
                    var revived = result.Placement.Skip(advancePerGroup).ToList();
                    if (revived.Count > 0)
                    {
                        var loserBracket = Knockout.Run(revived, coeff, rng, thirdPlaceMatch: false, recorder);
                        advancers.AddRange(loserBracket.Top(loserAdvance));
                    }
                }
            }
        }
        return advancers;
    }

    private static List<List<School>> SplitIntoGroups(
        IReadOnlyList<School> entrants, int groupCount, GroupingMode grouping,
        System.Func<School, int?>? districtOf, IRandomSource rng)
    {
        var groups = new List<List<School>>(groupCount);
        for (var g = 0; g < groupCount; g++) groups.Add(new List<School>());

        if (grouping == GroupingMode.Geographic && districtOf is not null)
        {
            // 地理固定割: 学校の県内地区でブロックへ。地区未設定・範囲外は Id で均等割へフォールバック。
            for (var i = 0; i < entrants.Count; i++)
            {
                var d = districtOf(entrants[i]);
                var g = d is { } di && di >= 0 && di < groupCount ? di : entrants[i].Id % groupCount;
                groups[g].Add(entrants[i]);
            }
        }
        else
        {
            // 抽選制: 決定論シャッフル後にスネークで均等割（強さ偏りを避ける）。
            var shuffled = Shuffle(entrants, rng);
            for (var i = 0; i < shuffled.Count; i++)
            {
                var cycle = i / groupCount;
                var pos = i % groupCount;
                var g = cycle % 2 == 0 ? pos : groupCount - 1 - pos;
                groups[g].Add(shuffled[i]);
            }
        }
        return groups;
    }

    private static List<School> Shuffle(IReadOnlyList<School> src, IRandomSource rng)
    {
        var list = src.ToList();
        for (var i = list.Count - 1; i > 0; i--)
        {
            var j = rng.NextInt(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
        return list;
    }
}
