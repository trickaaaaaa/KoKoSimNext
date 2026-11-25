using System;
using System.Collections.Generic;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>裏試合（自校非関与）1試合ぶんの記録。ランナーが記録した瞬間に新聞の材料として渡す。</summary>
/// <param name="RoundsRemaining">決勝=1・準決勝=2・準々決勝=3…（<see cref="TournamentRecorder.RoundsRemaining"/> と同じ数え方）。</param>
/// <param name="ThirdPlace">3位決定戦か（新聞には載せない）。</param>
public sealed record BackgroundGameReport(
    int RoundsRemaining, bool ThirdPlace, School Away, School Home, GameResult Result);

public enum NewspaperKind { Result, Upset, Phenom }

/// <param name="SchoolId">見出しの主役の学校ID。</param>
public sealed record NewspaperLine(NewspaperKind Kind, string Text, int SchoolId);

/// <summary>
/// 地方大会新聞（設計書05 §1.4・issue #1485）。裏試合の結果から、自県大会の準決勝以降の結果・番狂わせ・
/// 怪物（Phenom）の活躍を1行ずつのダイジェストにする。
///
/// <para>読み取り専用の純関数＝乱数に触れない（決定論・帯は不変）。怪物かどうかは学校ID×校内選手IDで
/// 引く注入関数が決める（engine はロスターの持ち方に依存しない）。</para>
/// </summary>
public static class TournamentNewspaper
{
    /// <summary>番狂わせとみなすティア差（勝った側が何段下か）。</summary>
    public const int UpsetTierGap = 2;

    /// <summary>1回の取りまとめで載せる番狂わせ＋怪物の行数の上限（結果の行は削らない）。</summary>
    public const int MaxExtraLines = 3;

    /// <summary>怪物の好投: 6回以上を1失点以内、または奪三振がこの数以上。</summary>
    public const int PhenomPitcherMinOuts = 18;
    public const int PhenomPitcherMaxRuns = 1;
    public const int PhenomPitcherStrikeOuts = 12;

    /// <summary>怪物の猛打: 本塁打がこの数以上、または安打がこの数以上。</summary>
    public const int PhenomHomeRuns = 2;
    public const int PhenomHits = 4;

    /// <param name="phenomOf">(学校ID, 校内選手ID) → 怪物種別。怪物でなければ <see cref="PhenomType.None"/>。</param>
    public static IReadOnlyList<NewspaperLine> Compose(
        IEnumerable<BackgroundGameReport> reports, Func<int, int, PhenomType> phenomOf)
    {
        var results = new List<NewspaperLine>();
        var extras = new List<NewspaperLine>();
        foreach (var g in reports)
        {
            if (g.ThirdPlace || g.Result.Tied) continue;
            var awayWon = g.Result.AwayRuns > g.Result.HomeRuns;
            var winner = awayWon ? g.Away : g.Home;
            var loser = awayWon ? g.Home : g.Away;
            var ws = Math.Max(g.Result.AwayRuns, g.Result.HomeRuns);
            var ls = Math.Min(g.Result.AwayRuns, g.Result.HomeRuns);
            var round = RoundLabel(g.RoundsRemaining);

            var upset = (int)loser.Tier - (int)winner.Tier >= UpsetTierGap;
            if (upset)
            {
                extras.Add(new NewspaperLine(NewspaperKind.Upset,
                    round + "番狂わせ　" + winner.Name + "が格上の" + loser.Name + "を " + ws + "-" + ls + " で破る", winner.Id));
            }
            else if (g.RoundsRemaining <= 2)
            {
                var tail = g.RoundsRemaining == 1 ? winner.Name + "が優勝" : winner.Name + "が決勝へ";
                results.Add(new NewspaperLine(NewspaperKind.Result,
                    round + "　" + winner.Name + " " + ws + "-" + ls + " " + loser.Name + "　" + tail, winner.Id));
            }

            AddPhenomLines(extras, g, phenomOf);
        }

        if (extras.Count > MaxExtraLines) extras.RemoveRange(MaxExtraLines, extras.Count - MaxExtraLines);
        results.AddRange(extras);
        return results;
    }

    private static string RoundLabel(int roundsRemaining) => roundsRemaining switch
    {
        1 => "決勝",
        2 => "準決勝",
        3 => "準々決勝",
        _ => "",
    };

    private static void AddPhenomLines(
        List<NewspaperLine> lines, BackgroundGameReport g, Func<int, int, PhenomType> phenomOf)
    {
        Add(lines, g, g.Away, g.Result.AwayBatting, g.Result.AwayPitching, phenomOf);
        Add(lines, g, g.Home, g.Result.HomeBatting, g.Result.HomePitching, phenomOf);

        static void Add(List<NewspaperLine> lines, BackgroundGameReport g, School school,
            IReadOnlyList<BattingLine> batting, IReadOnlyList<PitchingLine> pitching,
            Func<int, int, PhenomType> phenomOf)
        {
            var prefix = RoundLabel(g.RoundsRemaining);
            prefix = prefix.Length > 0 ? prefix + "　" : "";
            foreach (var p in pitching)
            {
                if (p.SourceId is not int id || !phenomOf(school.Id, id).IsPhenom()) continue;
                var gem = p.Outs >= PhenomPitcherMinOuts && p.Runs <= PhenomPitcherMaxRuns;
                if (!gem && p.StrikeOuts < PhenomPitcherStrikeOuts) continue;
                lines.Add(new NewspaperLine(NewspaperKind.Phenom,
                    prefix + "怪物のうわさ　" + school.Name + "の" + p.Name + "が" + p.InningsText + "回 "
                    + p.Runs + "失点・" + p.StrikeOuts + "奪三振", school.Id));
            }
            foreach (var b in batting)
            {
                if (b.SourceId is not int id || !phenomOf(school.Id, id).IsPhenom()) continue;
                string? feat = b.HomeRuns >= PhenomHomeRuns ? b.HomeRuns + "本塁打"
                    : b.Hits >= PhenomHits ? b.Hits + "安打" : null;
                if (feat is null) continue;
                lines.Add(new NewspaperLine(NewspaperKind.Phenom,
                    prefix + "怪物のうわさ　" + school.Name + "の" + b.Name + "が" + feat, school.Id));
            }
        }
    }
}
