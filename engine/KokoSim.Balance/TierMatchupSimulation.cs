using System.Linq;
using System.Globalization;
using System.Text;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Balance;

/// <summary>
/// 低ティア帯・非対称マッチの得点環境の**計測**（issue #536 Phase 1）。係数も帯も動かさない。
///
/// <para>既存の統計帯（<c>games_10k</c> 系）は <see cref="GameSimulation.GenerateTeam"/> の
/// 全能力 N(50,12) クランプ[20,88] という**平均的なチーム同士**しか測っていない（帯のコメント自身が
/// 「標準的な試合(C50同士)」と書いている）。ティアも非対称も入力に無いので、
/// 「開始直後のFランクがEランク投手に完封される」は帯が全部緑のまま起きる。</para>
///
/// <para>ここで測るのは3つ:</para>
/// <list type="number">
///   <item><b>ティア格子</b>: <see cref="StrengthTeamFactory"/> で strength を指定した総当たり</item>
///   <item><b>実プレイの組み合わせ</b>: 自校（<see cref="ProspectGenerator"/> 才能中心32）vs 相手校（strength 指定）。
///         自校と相手校は**別々の生成器**なので、合成チーム同士の格子とはズレうる＝そのズレ自体が原因の一部</item>
///   <item><b>3つのランク算出式の食い違い</b>: 同じロスターを School.Strength 相当 /
///         <see cref="TeamStrengthProfile"/> / <c>AverageLevel</c> 平均 で評価したときの差</item>
/// </list>
/// </summary>
public static class TierMatchupSimulation
{
    /// <summary>格子の1マス（away強度 × home強度）の実測。</summary>
    public sealed record Cell(
        string AwayLabel, string HomeLabel, int Games,
        double AwayRunsPerGame, double HomeRunsPerGame,
        double AwayShutoutRate, double HomeShutoutRate,
        double AwayHitsPerGame, double HomeHitsPerGame,
        double StrikeoutRate, double WalkRate, double HomeWinRate);

    private static Cell Measure(string awayLabel, string homeLabel, int games, ulong seed,
        string? coefficientsPath, Func<IRandomSource, string, bool, Team> factory,
        AbilityEffectCoefficients? effect = null)
    {
        var s = GameSimulation.Run(games, seed, coefficientsPath, teamFactory: factory,
            contextTransform: effect is null ? null : ctx => ctx.WithAbilityEffect(effect));
        return new Cell(
            awayLabel, homeLabel, games,
            (double)s.TotalAwayTeamRuns / games,
            (double)s.TotalHomeTeamRuns / games,
            (double)s.AwayShutoutGames / games,
            (double)s.HomeShutoutGames / games,
            (double)s.TotalAwayHits / games,
            (double)s.TotalHomeHits / games,
            s.StrikeoutRate, s.WalkRate, s.HomeWinRate);
    }

    // ── チーム生成の差し替え口 ──

    /// <summary>
    /// 強度指定のAI校（実ゲームの相手校と同じ生成器）。<paramref name="rng"/> は試合ローカルなので
    /// 試合ごとに違うロスターになる＝1つの強度の中の個体差もならされる。
    /// </summary>
    private static Team StrengthTeam(IRandomSource rng, string name, double strength, RosterCoefficients coeff)
        => StrengthTeamFactory.Create(strength, name, rng, rosterCoeff: coeff);

    /// <summary>
    /// 実プレイと同じ自校（<see cref="ProspectGenerator"/>＝才能中心 <c>TalentCenterDefault</c> 32.0 の
    /// 1〜3年）を <see cref="RosterTeamBuilder"/> で組む。**ゲーム開始直後の自校**そのもの。
    /// </summary>
    private static Team ManagerTeam(IRandomSource rng, string name, RosterCoefficients coeff)
        => RosterTeamBuilder.Build(ManagerRoster(rng, coeff), name);

    /// <summary>ゲーム開始直後の自校ロスター（1〜3年の入学コホート3本）。</summary>
    private static List<DevelopingPlayer> ManagerRoster(IRandomSource rng, RosterCoefficients coeff)
    {
        var list = new List<DevelopingPlayer>();
        for (var grade = 1; grade <= 3; grade++)
        {
            foreach (var p in ProspectGenerator.Intake(grade, coeff, rng))
            {
                p.Grade = grade;
                list.Add(p);
            }
        }
        return list;
    }

    /// <summary>
    /// 実プレイと同じ組み合わせ（自校＝<see cref="ProspectGenerator"/> vs 相手校＝<see cref="StrengthTeamFactory"/>）を
    /// 強度の梯子で測る（issue #536 ②）。レポートと**低ティア帯の統計回帰（issue #578 Step 4）が同じ経路**を
    /// 通るよう公開している＝帯が守っているものとレポートが見せているものを一致させる。
    /// 自校は先攻に置く（<c>AwayRunsPerGame</c>／<c>AwayShutoutRate</c> が自校の得点・完封され率）。
    /// </summary>
    /// <param name="effect">表示→物理の効き（設計書21）を係数ファイルの値から差し替える（null＝係数どおり）。
    /// 帯の意味を固定する回帰（恒等＝寝かせない世界なら赤くなる）が使う。</param>
    public static IReadOnlyList<Cell> MeasureManagerLadder(
        int games, ulong seed, string? coefficientsPath, RosterCoefficients coeff, IReadOnlyList<double> strengths,
        AbilityEffectCoefficients? effect = null)
    {
        var cells = new List<Cell>(strengths.Count);
        foreach (var h in strengths)
        {
            var opp = h;
            cells.Add(Measure("自校", Label(h), games, seed + 9000 + (ulong)h, coefficientsPath,
                (rng, name, isAway) => isAway ? ManagerTeam(rng, name, coeff) : StrengthTeam(rng, name, opp, coeff),
                effect));
        }
        return cells;
    }

    // ── レポート ──

    public static string Report(int games, ulong seed, string? coefficientsPath, RosterCoefficients? roster = null)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        // ティア圧縮（issue #578）の掃引用に外から差し込めるようにする。既定は C#既定＝実ゲームと同じ。
        var coeff = roster ?? new RosterCoefficients();
        // 30〜60 を5刻み。F=30–40 / E=40–50 / D=50–60（Tier.cs の境界は10刻み）。
        var strengths = new[] { 30.0, 35.0, 40.0, 45.0, 50.0, 55.0, 60.0 };

        sb.AppendLine("# 低ティア帯・非対称マッチの得点環境（issue #536 Phase 1・計測のみ）");
        sb.AppendLine();
        sb.AppendLine(c, $"- シード: {seed} ／ 1マスあたり {games} 試合 ／ 係数: {coefficientsPath ?? "C#既定"}");
        sb.AppendLine("- **この計測では係数も帯も生成パラメータも変更していない**（Phase 2 はオーナー判断後に別 issue）");
        sb.AppendLine();

        // ── ① ティア格子（AI校 vs AI校） ──
        sb.AppendLine("## ① ティア格子（StrengthTeamFactory 同士）");
        sb.AppendLine();
        sb.AppendLine("先攻(away)＝行、後攻(home)＝列。セルは **先攻の平均得点 / 先攻が完封された率**。");
        sb.AppendLine("「格下が格上に何点取れるか」を読むので、格下を先攻に置いた行を横に見る。");
        sb.AppendLine();
        sb.Append("| away＼home |");
        foreach (var h in strengths) sb.Append(c, $" {Label(h)} |");
        sb.AppendLine();
        sb.Append("|---|");
        foreach (var _ in strengths) sb.Append("---|");
        sb.AppendLine();

        var cells = new List<Cell>();
        for (var ai = 0; ai < strengths.Length; ai++)
        {
            sb.Append(c, $"| **{Label(strengths[ai])}** |");
            for (var hi = 0; hi < strengths.Length; hi++)
            {
                var a = strengths[ai];
                var h = strengths[hi];
                // マスごとにシードを分ける（同じ乱数列を使い回すと格子の傾きが乱数の癖と混ざる）。
                var cellSeed = seed + (ulong)(ai * 100 + hi);
                var cell = Measure(Label(a), Label(h), games, cellSeed, coefficientsPath,
                    (rng, name, isAway) => StrengthTeam(rng, name, isAway ? a : h, coeff));
                cells.Add(cell);
                sb.Append(c, $" {cell.AwayRunsPerGame:F2} / {cell.AwayShutoutRate:P0} |");
            }
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.AppendLine("### 対角（同格同士）と1ティア差の抜き出し");
        sb.AppendLine();
        sb.AppendLine("| 組み合わせ | 先攻の得点 | 先攻の完封され率 | 先攻の安打 | 三振率 | 四球率 |");
        sb.AppendLine("|---|---|---|---|---|---|");
        foreach (var cell in cells)
        {
            if (cell.AwayLabel != cell.HomeLabel && !IsOneTier(cell)) continue;
            sb.AppendLine(c,
                $"| {cell.AwayLabel} vs {cell.HomeLabel} | {cell.AwayRunsPerGame:F2} | {cell.AwayShutoutRate:P1} "
                + $"| {cell.AwayHitsPerGame:F2} | {cell.StrikeoutRate:P1} | {cell.WalkRate:P1} |");
        }
        sb.AppendLine();

        // ── ② 実プレイと同じ組み合わせ（自校 vs 相手校） ──
        sb.AppendLine("## ② 実プレイの組み合わせ（自校＝ProspectGenerator vs 相手校＝StrengthTeamFactory）");
        sb.AppendLine();
        sb.AppendLine(c,
            $"自校はゲーム開始直後のロスター（才能中心 {coeff.TalentCenterDefault:F1}・1〜3年）。**自校を先攻に置く**。");
        sb.AppendLine();
        sb.AppendLine("| 自校 vs 相手強度 | 自校の得点 | 自校の完封され率 | 相手の得点 | 自校の安打 | 三振率 | 四球率 |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var cell in MeasureManagerLadder(games, seed, coefficientsPath, coeff, strengths))
        {
            sb.AppendLine(c,
                $"| 自校 vs {cell.HomeLabel} | {cell.AwayRunsPerGame:F2} | {cell.AwayShutoutRate:P1} "
                + $"| {cell.HomeRunsPerGame:F2} | {cell.AwayHitsPerGame:F2} | {cell.StrikeoutRate:P1} | {cell.WalkRate:P1} |");
        }
        sb.AppendLine();

        // ── ③ ランク算出式の食い違い ──
        sb.AppendLine(RankFormulaReport(seed, coeff, strengths));

        // ── ④ 実際に当たる相手の分布 ──
        sb.AppendLine(OpponentDistributionReport(seed));
        return sb.ToString();
    }

    /// <summary>
    /// 1年目の夏に**実際に当たる相手**の強度分布（issue #536）。②は強度を等間隔に置いた計測なので、
    /// 「その強度の相手にどれくらい当たるのか」が入っていない。県の学校生成（<see cref="NationGenerator"/>）が
    /// 出す強度をそのまま数える＝格子のどの行を見ればよいかが決まる。
    /// </summary>
    private static string OpponentDistributionReport(ulong seed)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("## ④ 1年目に実際に当たる相手の分布（県内の学校の強度）");
        sb.AppendLine();

        var nation = NationGenerator.Generate(new SchoolNameVocab(), new NationCoefficients(), new Xoshiro256Random(seed));
        var byTier = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var all = 0;
        var sum = 0.0;
        foreach (var s in nation.InPrefecture(13))   // 1県ぶん（県の規模で分布は変わらない＝生成分布は共通）
        {
            var t = Tiers.FromStrength(s.Strength).ToString();
            byTier.TryGetValue(t, out var n);
            byTier[t] = n + 1;
            all++;
            sum += s.Strength;
        }

        sb.AppendLine(c, $"1県（第14県・{all}校）の内訳。平均強度 {sum / all:F1}。");
        sb.AppendLine();
        sb.AppendLine("| 表示ティア | 校数 | 割合 |");
        sb.AppendLine("|---|---|---|");
        foreach (var kv in byTier)
            sb.AppendLine(c, $"| {kv.Key} | {kv.Value} | {(double)kv.Value / all:P1} |");
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>
    /// 3つのランク算出式の食い違い（issue #536 の論点3）。**同じロスター**を評価して並べる。
    /// ティア境界は10刻みなので、式が違えば「Fと表示される自校」と「Eと表示される相手」の
    /// 実能力差が1ティア（10）である保証は無い。
    /// </summary>
    private static string RankFormulaReport(ulong seed, RosterCoefficients coeff, IReadOnlyList<double> strengths)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("## ③ ランク算出式の食い違い（同じロスターを3つの式で評価）");
        sb.AppendLine();
        sb.AppendLine("- **A** = `TeamStrengthProfile.Compute(roster).Overall`（自校の表示・6指標加重平均＋線形較正）");
        sb.AppendLine("- **B** = `roster.Average(AverageLevel)`（メンバー設定の旧表示）");
        sb.AppendLine("- **C** = 生成時に与えた `School.Strength`（AI校の表示の入力そのもの）");
        sb.AppendLine();

        // 自校（ProspectGenerator）を複数シードで作って A / B を出す。C は持たない（生成入力が strength でない）。
        sb.AppendLine("### 自校（ゲーム開始直後・才能中心 " + coeff.TalentCenterDefault.ToString("F1", c) + "）");
        sb.AppendLine();
        sb.AppendLine("| 試行 | A: TeamStrengthProfile | Aのランク | B: AverageLevel平均 | Bのランク | A−B |");
        sb.AppendLine("|---|---|---|---|---|---|");
        var sumA = 0.0;
        var sumB = 0.0;
        const int trials = 20;
        for (var i = 0; i < trials; i++)
        {
            var roster = ManagerRoster(new Xoshiro256Random(seed ^ (0x5361_0000UL + (ulong)i)), coeff);
            var a = TeamStrengthProfile.Compute(roster, StrengthCoeff).Overall;
            var b = roster.Average(p => p.AverageLevel());
            sumA += a;
            sumB += b;
            if (i < 5)
                sb.AppendLine(c,
                    $"| #{i + 1} | {a:F1} | {Tiers.FromStrength(a)} | {b:F1} | {Tiers.FromStrength(b)} | {a - b:+0.0;-0.0;0.0} |");
        }
        sb.AppendLine(c,
            $"| **平均({trials})** | **{sumA / trials:F1}** | **{Tiers.FromStrength(sumA / trials)}** "
            + $"| **{sumB / trials:F1}** | **{Tiers.FromStrength(sumB / trials)}** | **{(sumA - sumB) / trials:+0.0;-0.0;0.0}** |");
        sb.AppendLine();

        // AI校（StrengthTeamFactory）は Team しか返さないので、生成入力 strength と
        // 「その Team を A の式で測り直した値」を比べる。写し戻しはゲーム本体が相手校のレーダーを出すのと
        // 同じ入口（ScoutedTeamProfile）を通す＝ここで独自の逆投影を書かない（適性の置き直しを含むため）。
        sb.AppendLine("### AI校（StrengthTeamFactory に与えた強度 vs 生成物の実力）");
        sb.AppendLine();
        sb.AppendLine("**C（表示に使う強度）と A（実際に出てきた選手の強さ）がズレていれば、表示ランクは実力を表していない。**");
        sb.AppendLine(c,
            $"ティア圧縮（issue #578）は pivot={coeff.TierCompressionPivot:F1} / slope={coeff.TierCompressionSlope:F2}。");
        sb.AppendLine("**格子の外側（70/85）を併記するのは、圧縮が上位校の絶対能力をどれだけ削るかを見るため**"
            + "＝低ティアの一方的さを緩める代償はここに出る。");
        sb.AppendLine();
        sb.AppendLine("| C: 与えた強度 | Cのランク | 圧縮後の中心 | A: 生成物の実力 | Aのランク | A−C |");
        sb.AppendLine("|---|---|---|---|---|---|");
        // 格子（30〜60）に加えて上位帯（B相当70・A相当85）も測る＝圧縮の代償を数値で出す。
        foreach (var s in strengths.Concat(new[] { 70.0, 85.0 }))
        {
            var sumMeasured = 0.0;
            const int n = 20;
            for (var i = 0; i < n; i++)
            {
                var rng = new Xoshiro256Random(seed ^ (0x5A11_0000UL + (ulong)(s * 100 + i)));
                var team = StrengthTeamFactory.Create(s, "計測", rng, rosterCoeff: coeff);
                sumMeasured += ScoutedTeamProfile.Compute(team, StrengthCoeff).Overall;
            }
            var measured = sumMeasured / n;
            sb.AppendLine(c,
                $"| {s:F0} | {Tiers.FromStrength(s)} | {coeff.CompressStrength(s):F1} | {measured:F1} "
                + $"| {Tiers.FromStrength(measured)} | {measured - s:+0.0;-0.0;0.0} |");
        }
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>6指標の係数（既定＝実ゲーム表示と同値。<c>TeamStrengthCoeff.Default</c> と同じもの）。</summary>
    private static readonly TeamStrengthCoefficients StrengthCoeff = new();

    private static string Label(double strength) => $"{Tiers.FromStrength(strength)}({strength:F0})";

    /// <summary>ちょうど1ティア差（表示ランクが隣同士）のマスか。</summary>
    private static bool IsOneTier(Cell cell)
    {
        var a = cell.AwayLabel[0];
        var h = cell.HomeLabel[0];
        var order = "GFEDCBAS";
        var ia = order.IndexOf(a);
        var ih = order.IndexOf(h);
        return ia >= 0 && ih >= 0 && Math.Abs(ia - ih) == 1;
    }
}
