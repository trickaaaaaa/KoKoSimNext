using System.Globalization;
using System.Text;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;

namespace KokoSim.Balance;

/// <summary>
/// 走力・守備の能力差が守備範囲と失点にどれだけ効くかの**計測**（issue #832）。係数も帯も動かさない。
///
/// <para>平均的なチーム同士（<see cref="GameSimulation.GenerateTeam"/>）の試合で、**守る側（後攻）の野手だけ**
/// 能力を固定値に差し替え、攻める側（先攻）の成績を測る。後攻の走力は後攻の走塁にも効くが、
/// 測るのは先攻の攻撃だけなので守備以外の経路は混ざらない。</para>
///
/// <para>同じシードの全条件でロスターは同一（差し替えは生成後）＝条件間の差はロスターの引きでなく
/// 能力の差し替えだけから来る。試合の乱数列は条件で分岐する（守備範囲が変われば消費が変わる）ので、
/// 差には標準誤差を添える。</para>
/// </summary>
public static class DefenseSensitivitySimulation
{
    public enum Attr { Speed, Fielding, Contact, Power }

    public enum Scope { All, Infield, Outfield }

    /// <summary>1条件。Speed/Fielding なら後攻（守備側）、Contact/Power なら先攻（攻撃側）の野手8人を差し替える。</summary>
    public sealed record Condition(Attr Attr, Scope Scope, int Level)
    {
        public bool IsDefense => Attr is Attr.Speed or Attr.Fielding;

        public string Label => $"{AttrName(Attr)} {Level}（{ScopeName(Scope)}）";
    }

    /// <summary>条件1つの実測。値はすべて整数集計（並列マージ順に依らず決定論）。</summary>
    public sealed record Cell(
        Condition Condition, int Games,
        long RunsAllowed, long RunsAllowedSquares,
        long InPlay, long InPlayHits,
        long Grounders, long GrounderOuts,
        long InfieldGrounders, long InfieldGrounderOuts,
        long Flies, long FlyOuts,
        long OutfieldFlies, long OutfieldFlyOuts)
    {
        public double RunsPerGame => (double)RunsAllowed / Games;

        /// <summary>1試合あたり得点の標準誤差（試合間の分散から）。</summary>
        public double RunsStdErr
        {
            get
            {
                if (Games < 2) return 0;
                var mean = RunsPerGame;
                var variance = ((double)RunsAllowedSquares / Games - mean * mean) * Games / (Games - 1);
                return Math.Sqrt(Math.Max(0, variance) / Games);
            }
        }

        public double Babip => Rate(InPlayHits, InPlay);
        public double GrounderOutRate => Rate(GrounderOuts, Grounders);
        public double InfieldGrounderOutRate => Rate(InfieldGrounderOuts, InfieldGrounders);
        public double FlyOutRate => Rate(FlyOuts, Flies);
        public double OutfieldFlyOutRate => Rate(OutfieldFlyOuts, OutfieldFlies);

        private static double Rate(long a, long b) => b > 0 ? (double)a / b : 0;
    }

    public const int DefaultGames = 10000;

    public static readonly int[] DefenseLevels = { 20, 50, 80, 100 };
    public static readonly int[] OffenseLevels = { 20, 50, 80 };

    private static readonly FieldPosition[] InfieldSlots =
    {
        FieldPosition.FirstBase, FieldPosition.SecondBase, FieldPosition.ThirdBase, FieldPosition.Shortstop,
    };

    private static readonly FieldPosition[] OutfieldSlots =
    {
        FieldPosition.LeftField, FieldPosition.CenterField, FieldPosition.RightField,
    };

    private static bool IsInfielder(FieldPosition pos) => Array.IndexOf(InfieldSlots, pos) >= 0;

    private static bool IsOutfielder(FieldPosition pos) => Array.IndexOf(OutfieldSlots, pos) >= 0;

    private static bool InScope(Scope scope, FieldPosition pos) => scope switch
    {
        Scope.All => pos != FieldPosition.Pitcher,
        Scope.Infield => IsInfielder(pos),
        _ => IsOutfielder(pos),
    };

    /// <summary>報告する全条件。基準（同属性・全員50）は各属性に1本だけ持つ。</summary>
    public static IReadOnlyList<Condition> AllConditions()
    {
        var list = new List<Condition>();
        foreach (var attr in new[] { Attr.Speed, Attr.Fielding })
        {
            foreach (var scope in new[] { Scope.All, Scope.Infield, Scope.Outfield })
            {
                foreach (var level in DefenseLevels)
                {
                    if (level == 50 && scope != Scope.All) continue;
                    list.Add(new Condition(attr, scope, level));
                }
            }
        }
        foreach (var attr in new[] { Attr.Contact, Attr.Power })
        {
            foreach (var level in OffenseLevels) list.Add(new Condition(attr, Scope.All, level));
        }
        return list;
    }

    private static Player SetAttr(Player p, Attr attr, int level) => attr switch
    {
        Attr.Speed => p with { Speed = level },
        Attr.Fielding => p with { Fielding = level },
        Attr.Contact => p with { Contact = level },
        _ => p with { Power = level },
    };

    private static Team Apply(Team team, Condition c)
    {
        var order = new Player[team.BattingOrder.Count];
        for (var i = 0; i < order.Length; i++)
        {
            var p = team.BattingOrder[i];
            order[i] = InScope(c.Scope, p.Position) ? SetAttr(p, c.Attr, c.Level) : p;
        }
        return team with { BattingOrder = order };
    }

    private sealed class Accumulator
    {
        public long Runs, RunsSq;
        public long InPlay, InPlayHits;
        public long Grounders, GrounderOuts;
        public long InfieldGrounders, InfieldGrounderOuts;
        public long Flies, FlyOuts;
        public long OutfieldFlies, OutfieldFlyOuts;

        public void Add(GameResult r)
        {
            Runs += r.AwayRuns;
            RunsSq += (long)r.AwayRuns * r.AwayRuns;
            foreach (var e in r.Log)
            {
                if (!e.IsTop) continue;   // 先攻の攻撃＝後攻の守備だけを見る
                if (e.IsSacBunt || e.BattedClass is not { } cls) continue;
                var isHit = e.Result is PlateAppearanceResult.Single or PlateAppearanceResult.Double or PlateAppearanceResult.Triple;
                var isOut = e.Result == PlateAppearanceResult.InPlayOut;
                if (!isHit && !isOut && e.Result != PlateAppearanceResult.ReachedOnError) continue;   // 本塁打・三振・四球は守備の対象外

                InPlay++;
                if (isHit) InPlayHits++;
                if (cls is BattedBallClass.Grounder or BattedBallClass.Bouncer)
                {
                    Grounders++;
                    if (isOut) GrounderOuts++;
                    if (e.FieldedBy is { } gf && IsInfielder(gf))
                    {
                        InfieldGrounders++;
                        if (isOut) InfieldGrounderOuts++;
                    }
                }
                else if (cls == BattedBallClass.Fly)
                {
                    Flies++;
                    if (isOut) FlyOuts++;
                    if (e.FieldedBy is { } ff && IsOutfielder(ff))
                    {
                        OutfieldFlies++;
                        if (isOut) OutfieldFlyOuts++;
                    }
                }
            }
        }

        public void Merge(Accumulator o)
        {
            Runs += o.Runs; RunsSq += o.RunsSq;
            InPlay += o.InPlay; InPlayHits += o.InPlayHits;
            Grounders += o.Grounders; GrounderOuts += o.GrounderOuts;
            InfieldGrounders += o.InfieldGrounders; InfieldGrounderOuts += o.InfieldGrounderOuts;
            Flies += o.Flies; FlyOuts += o.FlyOuts;
            OutfieldFlies += o.OutfieldFlies; OutfieldFlyOuts += o.OutfieldFlyOuts;
        }
    }

    /// <summary>
    /// 1条件を測る。ロスター生成は <see cref="GameSimulation.GenerateTeam"/>（away→home の順・差し替えは生成後）なので、
    /// 同じシードなら全条件で同じロスターになる。乱数・集計の作法は <see cref="GameSimulation.Run"/> と同じ
    /// （事前 Fork＋整数部分和＝同シード同結果）。
    /// </summary>
    public static Cell Measure(Condition condition, int games, ulong seed, string? coefficientsPath = null)
    {
        var ctx = GameSimulation.BuildContext(coefficientsPath) with { Weather = null };
        var root = new Xoshiro256Random(seed);
        var forks = new IRandomSource[games];
        for (var i = 0; i < games; i++) forks[i] = root.Fork((ulong)i);

        var total = new Accumulator();
        Parallel.For(0, games,
            () => new Accumulator(),
            (i, _, local) =>
            {
                var g = forks[i];
                var away = GameSimulation.GenerateTeam(g, "遠征校");
                var home = GameSimulation.GenerateTeam(g, "地元校");
                if (condition.IsDefense) home = Apply(home, condition);
                else away = Apply(away, condition);
                local.Add(GameEngine.Play(away, home, ctx, g));
                return local;
            },
            local => { lock (total) total.Merge(local); });

        return new Cell(
            condition, games,
            total.Runs, total.RunsSq, total.InPlay, total.InPlayHits,
            total.Grounders, total.GrounderOuts, total.InfieldGrounders, total.InfieldGrounderOuts,
            total.Flies, total.FlyOuts, total.OutfieldFlies, total.OutfieldFlyOuts);
    }

    private static string AttrName(Attr a) => a switch
    {
        Attr.Speed => "走力",
        Attr.Fielding => "守備",
        Attr.Contact => "ミート",
        _ => "パワー",
    };

    private static string ScopeName(Scope s) => s switch
    {
        Scope.All => "全野手",
        Scope.Infield => "内野のみ",
        _ => "外野のみ",
    };

    public static string Report(int games, ulong seed, string? coefficientsPath = null)
        => Run(games, seed, coefficientsPath).Markdown!;

    /// <summary>
    /// 全条件を測ってレポートにする。条件数×試合数が長くなる（1万試合で十数分）ので、測り終えた条件は
    /// <paramref name="cachePath"/> に1行ずつ追記して再開できる。<paramref name="budgetSeconds"/> を過ぎたら
    /// 新しい条件に着手せず止め（各回少なくとも1条件は進む）、<c>Markdown</c> は null・<c>Remaining</c> に残り条件数を返す（もう一度回せば続きから）。
    /// キャッシュは試合数・シード・係数ファイルが一致する行だけを使う。
    /// </summary>
    public static (string? Markdown, int Remaining) Run(
        int games, ulong seed, string? coefficientsPath = null, string? cachePath = null, double? budgetSeconds = null)
    {
        var cacheKey = $"{games}\t{seed}\t{coefficientsPath ?? "-"}";
        var cache = LoadCache(cachePath, cacheKey);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var cells = new Dictionary<Condition, Cell>();
        var remaining = 0;
        var measuredAny = false;
        foreach (var c in AllConditions())
        {
            if (cache.TryGetValue(c, out var hit)) { cells[c] = hit; continue; }
            if (measuredAny && budgetSeconds is { } b && clock.Elapsed.TotalSeconds > b) { remaining++; continue; }
            var cell = Measure(c, games, seed, coefficientsPath);
            measuredAny = true;
            cells[c] = cell;
            if (cachePath is not null) File.AppendAllText(cachePath, cacheKey + "\t" + Serialize(cell) + "\n");
        }
        return remaining > 0 ? (null, remaining) : (ToMarkdown(cells, games, seed), 0);
    }

    private static string Serialize(Cell x) => string.Join('\t',
        (int)x.Condition.Attr, (int)x.Condition.Scope, x.Condition.Level,
        x.RunsAllowed, x.RunsAllowedSquares, x.InPlay, x.InPlayHits,
        x.Grounders, x.GrounderOuts, x.InfieldGrounders, x.InfieldGrounderOuts,
        x.Flies, x.FlyOuts, x.OutfieldFlies, x.OutfieldFlyOuts);

    private static Dictionary<Condition, Cell> LoadCache(string? path, string key)
    {
        var map = new Dictionary<Condition, Cell>();
        if (path is null || !File.Exists(path)) return map;
        var games = int.Parse(key.Split('\t')[0], CultureInfo.InvariantCulture);
        foreach (var line in File.ReadLines(path))
        {
            if (!line.StartsWith(key + "\t", StringComparison.Ordinal)) continue;
            var f = line[(key.Length + 1)..].Split('\t');
            if (f.Length != 15) continue;
            var cond = new Condition((Attr)int.Parse(f[0], CultureInfo.InvariantCulture),
                (Scope)int.Parse(f[1], CultureInfo.InvariantCulture), int.Parse(f[2], CultureInfo.InvariantCulture));
            long L(int i) => long.Parse(f[i], CultureInfo.InvariantCulture);
            map[cond] = new Cell(cond, games, L(3), L(4), L(5), L(6), L(7), L(8), L(9), L(10), L(11), L(12), L(13), L(14));
        }
        return map;
    }

    /// <summary>測定済みの条件からレポートを組む。</summary>
    internal static string ToMarkdown(IReadOnlyDictionary<Condition, Cell> cells, int games, ulong seed)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# 走力・守備の能力差が守備範囲と失点に効く大きさの計測（issue #832）");
        sb.AppendLine();
        sb.AppendLine(c, $"- 試合数: 条件ごとに {games}／シード: {seed}／係数・帯は変更なし（C#既定）");
        sb.AppendLine("- 母集団: 平均的なチーム同士（全能力 N(50,12) クランプ[20,88] の合成チーム）。采配Brainなし・天候オフ・既定球場");
        sb.AppendLine("- 守備条件: 後攻（地元校）の野手8人（捕手・一二三遊・左中右）の走力／守備を固定値に差し替える。投手は据え置き。"
                      + "「内野のみ」は一二三遊、「外野のみ」は左中右。測るのは先攻（遠征校）の攻撃だけ");
        sb.AppendLine("- 打撃条件: 先攻（遠征校）の野手8人のミート／パワーを固定値に差し替え、先攻の得点を測る（守備条件の「失点」と同じ単位）");
        sb.AppendLine("- 同じシードの全条件でロスターは同一。「基準差」は同じ属性を全野手50にした条件との差（他の能力は生成どおりなので、基準の絶対値は表ごとにわずかに違う）で、±は両者の標準誤差の二乗和平方根"
                      + "（独立とみなした保守的な値）");
        sb.AppendLine("- アウト率＝その打球が InPlayOut になった割合（失策出塁・安打は非アウト）。BABIP＝インプレー打球（本塁打・バント除く）の安打率");
        sb.AppendLine("- 「内野手処理ゴロ」「外野手処理フライ」は処理した野手の位置で分けるため、守備力が変わると母集団が少し動く。"
                      + "「ゴロ全体」「フライ全体」は打球の種類だけで分けた母集団");

        foreach (var attr in new[] { Attr.Speed, Attr.Fielding })
        {
            var baseline = cells[new Condition(attr, Scope.All, 50)];
            sb.AppendLine();
            sb.AppendLine(c, $"## {AttrName(attr)}（守備側・後攻の野手）");
            sb.AppendLine();
            sb.AppendLine("| 条件 | 失点/試合 | 基準差 | ± | BABIP | ゴロ全体アウト率 | 内野手処理ゴロ | フライ全体アウト率 | 外野手処理フライ |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
            AppendRow(sb, baseline, baseline, $"基準（{AttrName(attr)}全野手50）");
            foreach (var scope in new[] { Scope.All, Scope.Infield, Scope.Outfield })
            {
                foreach (var level in DefenseLevels)
                {
                    if (level == 50) continue;
                    var cell = cells[new Condition(attr, scope, level)];
                    AppendRow(sb, cell, baseline, cell.Condition.Label);
                }
            }
        }

        foreach (var attr in new[] { Attr.Contact, Attr.Power })
        {
            var baseline = cells[new Condition(attr, Scope.All, 50)];
            sb.AppendLine();
            sb.AppendLine(c, $"## {AttrName(attr)}（攻撃側・先攻の野手）");
            sb.AppendLine();
            sb.AppendLine("| 条件 | 得点/試合 | 基準差 | ± | BABIP | ゴロ全体アウト率 | 内野手処理ゴロ | フライ全体アウト率 | 外野手処理フライ |");
            sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|");
            foreach (var level in OffenseLevels)
            {
                var cell = cells[new Condition(attr, Scope.All, level)];
                AppendRow(sb, cell, baseline, level == 50 ? $"基準（{AttrName(attr)}全野手50）" : cell.Condition.Label);
            }
        }

        sb.AppendLine();
        sb.AppendLine("## 能力10ポイントあたりの得失点（全野手8人を同時に動かした場合）");
        sb.AppendLine();
        sb.AppendLine("各列は「50からその水準へ動かしたときの 得失点の変化[点/試合] ÷ 動かした幅（10pt単位）」。"
                      + "上げ側＝50→80、下げ側＝50→20（能力を10pt下げたときの変化）、100側＝50→100。"
                      + "守備は失点なので**負が良い**、打撃は得点なので**正が良い**。比は「上げ側10ptの効き（絶対値）÷ ミート／パワー10ptの効き」。");
        sb.AppendLine();
        sb.AppendLine("| 能力 | 上げ側 +10pt | 下げ側 −10pt | 100側 +10pt | 対ミート比（上げ側） | 対パワー比（上げ側） |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|");
        var contactUp = PerTen(cells, Attr.Contact, 80);
        var powerUp = PerTen(cells, Attr.Power, 80);
        foreach (var attr in new[] { Attr.Speed, Attr.Fielding, Attr.Contact, Attr.Power })
        {
            var up = PerTen(cells, attr, 80);
            var down = PerTen(cells, attr, 20);
            var hi = attr is Attr.Speed or Attr.Fielding ? PerTen(cells, attr, 100).ToString("+0.000;-0.000", c) : "—";
            sb.AppendLine(c, $"| {AttrName(attr)} | {up:+0.000;-0.000} | {down:+0.000;-0.000} | {hi} | "
                             + $"{Math.Abs(up) / Math.Abs(contactUp):0.00} | {Math.Abs(up) / Math.Abs(powerUp):0.00} |");
        }
        return sb.ToString();
    }

    /// <summary>全野手の能力を 50 から <paramref name="level"/> へ動かしたときの、10ポイントあたりの得失点変化[点/試合]。</summary>
    private static double PerTen(IReadOnlyDictionary<Condition, Cell> cells, Attr attr, int level)
    {
        var baseline = cells[new Condition(attr, Scope.All, 50)];
        var cell = cells[new Condition(attr, Scope.All, level)];
        return (cell.RunsPerGame - baseline.RunsPerGame) / (Math.Abs(level - 50) / 10.0);
    }

    private static void AppendRow(StringBuilder sb, Cell cell, Cell baseline, string label)
    {
        var c = CultureInfo.InvariantCulture;
        var diff = cell.RunsPerGame - baseline.RunsPerGame;
        var se = Math.Sqrt(cell.RunsStdErr * cell.RunsStdErr + baseline.RunsStdErr * baseline.RunsStdErr);
        sb.AppendLine(c, $"| {label} | {cell.RunsPerGame:0.000} | {diff:+0.000;-0.000;0.000} | {se:0.000} | "
                         + $"{cell.Babip:0.0000} | {cell.GrounderOutRate:P1} | {cell.InfieldGrounderOutRate:P1} | "
                         + $"{cell.FlyOutRate:P1} | {cell.OutfieldFlyOutRate:P1} |");
    }
}
