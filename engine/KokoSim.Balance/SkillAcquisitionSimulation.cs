using System.Globalization;
using System.Text;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Balance;

/// <summary>
/// 特殊能力の後天習得・克服・開示の頻度の実測（issue #1471）。自校を名声（＝新入生の質）別に N 校ずつ回し、
/// 3年間在籍して卒業した選手1人あたり、何個 身につけた／克服した／気づきで開示されたかを数える。
/// <para>実ゲーム（Unity の週送り）と同じ部品を同じ順で呼ぶ: 練習→調子→成長イベント（＋覚醒のスキル）→気づき、
/// 年度替わりの克服、公式戦の後の実戦経験。公式戦は夏（第13週〜）と秋（第23週〜）に強さ別の試合数を、
/// 名声50の相手校（同じく練習を回す）と戦う＝大会の勝ち上がりは再現しない（試合数で近似）。監督の育成眼は 50。</para>
/// </summary>
public static class SkillAcquisitionSimulation
{
    public sealed record Tier(string Label, double Fame, int SummerGames, int AutumnGames);

    public static readonly Tier[] Tiers =
    {
        new("弱（名声10）", 10, 2, 1),
        new("中（名声50）", 50, 3, 3),
        new("強（名声90）", 90, 6, 4),
    };

    public sealed record Row(Tier Tier, int Players, double Acquired, double Manifested, double OvercomeMatch,
        double OvercomeGrade, double Revealed, double HiddenAtEntry, double HiddenLeft,
        double AwakeningGain, double MatchGain, double ShareWithAny);

    private sealed class Tally
    {
        public int Acquired, Manifested, OvercomeMatch, OvercomeGrade, Revealed, AwakeningGain, MatchGain;
        public int HiddenAtEntry;
    }

    public static IReadOnlyList<Row> Run(int schools, ulong seed, double talentEye = 50)
    {
        var rows = new List<Row>();
        foreach (var tier in Tiers)
        {
            var results = new (int Players, Tally Sum, int HiddenLeft, int WithAny)[schools];
            System.Threading.Tasks.Parallel.For(0, schools, i =>
                results[i] = RunSchool(tier, seed ^ ((ulong)i * 0x9E37_79B9_7F4A_7C15UL) ^ (ulong)(tier.Fame * 1000), talentEye));
            var n = results.Sum(r => r.Players);
            double Per(Func<Tally, int> f) => n == 0 ? 0 : results.Sum(r => f(r.Sum)) / (double)n;
            rows.Add(new Row(tier, n,
                Per(t => t.Acquired), Per(t => t.Manifested), Per(t => t.OvercomeMatch), Per(t => t.OvercomeGrade),
                Per(t => t.Revealed), Per(t => t.HiddenAtEntry),
                n == 0 ? 0 : results.Sum(r => r.HiddenLeft) / (double)n,
                Per(t => t.AwakeningGain), Per(t => t.MatchGain),
                n == 0 ? 0 : results.Sum(r => r.WithAny) / (double)n));
        }
        return rows;
    }

    private const int Years = 6;

    private static (int Players, Tally Sum, int HiddenLeft, int WithAny) RunSchool(Tier tier, ulong seed, double talentEye)
    {
        var ctx = new SeasonContext();
        var rng = new Xoshiro256Random(seed);
        var oppRng = rng.Fork(0x0990_0000UL);
        var roster = new List<DevelopingPlayer>();
        var opp = new List<DevelopingPlayer>();
        var tallies = new Dictionary<DevelopingPlayer, Tally>();
        var center = ctx.Roster.TalentCenterFromFame(tier.Fame);
        var oppCenter = ctx.Roster.TalentCenterFromFame(50);
        var insight = new InsightCoefficients();
        var acq = new SkillAcquisitionCoefficients();
        var nextId = 1;
        var oppNextId = 100000;
        int players = 0, hiddenLeft = 0, withAny = 0;
        var sum = new Tally();

        Tally T(DevelopingPlayer p) => tallies.TryGetValue(p, out var t) ? t : tallies[p] = new Tally();
        void Count(IEnumerable<SkillChangeNotice> ns)
        {
            foreach (var n in ns)
            {
                var t = T(n.Player);
                switch (n.Kind)
                {
                    case SkillChangeKind.Acquired: t.Acquired++; break;
                    case SkillChangeKind.Manifested: t.Manifested++; break;
                    case SkillChangeKind.Revealed: t.Revealed++; break;
                    default:
                        if (n.Cause == SkillChangeCause.Match) t.OvercomeMatch++; else t.OvercomeGrade++;
                        break;
                }
                if (n.Kind is SkillChangeKind.Acquired or SkillChangeKind.Manifested)
                {
                    if (n.Cause == SkillChangeCause.Awakening) t.AwakeningGain++; else t.MatchGain++;
                }
            }
        }

        for (var year = 1; year <= Years; year++)
        {
            var rollover = RosterLifecycle.AdvanceYear(roster, year, ctx.Roster, rng,
                skills: ctx.Skills, personalities: ctx.Personalities, form: ctx.Form, talentCenter: center, nextId: nextId);
            nextId = rollover.NextId;
            var oppRoll = RosterLifecycle.AdvanceYear(opp, year, ctx.Roster, oppRng,
                skills: ctx.Skills, personalities: ctx.Personalities, form: ctx.Form, talentCenter: oppCenter, nextId: oppNextId);
            oppNextId = oppRoll.NextId;
            foreach (var p in rollover.Newcomers) T(p).HiddenAtEntry = p.Skills.Hidden.Count;
            Count(SkillAcquisitionModel.OnYearAdvanced(roster, rng.Fork(0x5C12_0000UL ^ (ulong)year), acq));

            // 1年目に入った学年（year=1 の新入生）は 4年目の年度替わりで卒業＝3年間そろう。year>=4 の卒業生を数える。
            if (year >= 4)
            {
                foreach (var g in rollover.Graduated)
                {
                    var t = T(g);
                    players++;
                    sum.Acquired += t.Acquired; sum.Manifested += t.Manifested; sum.Revealed += t.Revealed;
                    sum.OvercomeMatch += t.OvercomeMatch; sum.OvercomeGrade += t.OvercomeGrade;
                    sum.AwakeningGain += t.AwakeningGain; sum.MatchGain += t.MatchGain;
                    sum.HiddenAtEntry += t.HiddenAtEntry;
                    hiddenLeft += g.Skills.Hidden.Count;
                    if (t.Acquired + t.Manifested + t.OvercomeMatch + t.OvercomeGrade + t.Revealed > 0) withAny++;
                }
            }

            var (facilityCoef, budget) = ctx.ResolveFacility();
            var training = ctx.Training with { FacilityCoef = facilityCoef };
            for (var week = 0; week < ctx.Calendar.WeeksPerYear; week++)
            {
                RosterLifecycle.OnWeekEntered(roster, week, ctx.Calendar);
                RosterLifecycle.OnWeekEntered(opp, week, ctx.Calendar);
                var campMult = ctx.Calendar.CampMultiplier(week, ctx.Training);
                foreach (var team in new[] { roster, opp })
                foreach (var p in team)
                {
                    if (p.IsRetired || !ctx.Calendar.CanTrain(p.Grade, week)) continue;
                    var alloc = TrainingPresets.Resolve(p.Plan ?? TrainingPlan.Default, budget);
                    DevelopmentModel.TrainWeekPlan(p, alloc, training.ReferenceWeekMinutes,
                        ctx.Calendar.StageIndex(p.Grade, week), campMult, ctx.Stages, training, ctx.Skills,
                        ctx.Personalities, ctx.Coaching, ctx.Equipment, team);
                }

                var active = roster.Where(p => !p.IsRetired).ToList();
                var wk = (ulong)(year * 100 + week);
                var formRng = rng.Fork(0xF0F0_0000UL ^ wk);
                foreach (var p in active) p.ConditionValue = FormModel.NextWeeklyCondition(p.ConditionValue, formRng, ctx.Form);

                var growth = GrowthEventModel.Week(active, rng.Fork(0x6707_0000UL ^ wk), ctx.Growth, p =>
                    ctx.Calendar.CanTrain(p.Grade, week)
                        ? TrainingFields.Trained(TrainingPresets.Resolve(p.Plan ?? TrainingPlan.Default, budget))
                        : Array.Empty<TrainingField>());
                Count(SkillAcquisitionModel.OnGrowthEvents(growth, rng.Fork(0x5C11_0000UL ^ wk), acq));

                foreach (var n in InsightModel.Week(active, talentEye, rng.Fork(0x1E51_0000UL ^ wk), insight))
                    if (n.HiddenSkill is { } hs)
                        Count(new[] { new SkillChangeNotice(n.Player, hs, SkillChangeKind.Revealed, SkillChangeCause.Insight) });

                var games = week == ctx.Calendar.SummerTournamentStartWeek ? tier.SummerGames
                    : week == ctx.Calendar.AutumnTournamentStartWeek ? tier.AutumnGames : 0;
                for (var g = 0; g < games; g++)
                {
                    var home = RosterLifecycle.Active(roster);
                    var away = RosterLifecycle.Active(opp);
                    if (home.Count < 9 || away.Count < 9) break;
                    var gameRng = rng.Fork(0x6A3E_0000UL ^ (wk << 8) ^ (ulong)g);
                    var result = GameEngine.Play(RosterTeamBuilder.Build(away, "相手"), RosterTeamBuilder.Build(home, "自校"),
                        new GameContext(), gameRng);
                    Count(SkillAcquisitionModel.AfterOfficialMatch(result, managerIsAway: false, roster,
                        gameRng.Fork(0x5C13_0000UL), acq));
                }
            }
        }
        return (players, sum, hiddenLeft, withAny);
    }

    public static string Report(IReadOnlyList<Row> rows, int schools, ulong seed)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# 特殊能力の後天習得・克服・開示の頻度（issue #1471）");
        sb.AppendLine();
        sb.AppendLine(c, $"- 強さごとに {schools} 校 × {Years} 年。3年間在籍して卒業した選手（4〜{Years}年目の卒業生）1人あたりの件数");
        sb.AppendLine("- 公式戦は夏・秋に強さ別の試合数を名声50の相手と（勝ち上がりは試合数で近似）。監督の育成眼 50・個別指導なし");
        sb.AppendLine();
        sb.AppendLine("| 強さ | 夏+秋の試合/年 | 卒業生 | 習得 | 発現 | 克服(実戦) | 克服(学年) | 開示(気づき) | うち覚醒で | うち実戦で | 何か起きた割合 | 入部時の隠し | 卒業時に残った隠し |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|");
        foreach (var r in rows)
        {
            sb.AppendLine(c,
                $"| {r.Tier.Label} | {r.Tier.SummerGames + r.Tier.AutumnGames} | {r.Players} | {r.Acquired:F3} | {r.Manifested:F3} | {r.OvercomeMatch:F3} | {r.OvercomeGrade:F3} | {r.Revealed:F3} | {r.AwakeningGain:F3} | {r.MatchGain:F3} | {r.ShareWithAny:P1} | {r.HiddenAtEntry:F3} | {r.HiddenLeft:F3} |");
        }
        sb.AppendLine();
        sb.AppendLine(c, $"- seed {seed}");
        return sb.ToString();
    }
}
