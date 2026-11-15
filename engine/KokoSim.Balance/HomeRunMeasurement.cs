using System.Globalization;
using System.IO.Compression;
using System.Text;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Balance;

/// <summary>
/// 本塁打の実測（issue #1014・#976 の前段）。**係数・帯・解決式は一切変えない**計測専用。
///
/// <para>帯（games_10k 系）は「平均的な2校・既定球場（両翼95m/中堅118m/フェンス4m）・天候なし」しか測らない。
/// 実ゲームでは ①県大会の序盤は市営級の狭い球場（両翼91〜93m・フェンス2m）②試合日の月の気温（#971）
/// ③力の差がある組み合わせ ④敵AIの采配・コールド が乗る。ここではそれを1つずつ足した条件を並べ、
/// 本塁打率・本塁打/試合（両軍計）・外れ（5本以上／10本以上）・得点分布を同じ表で比べる。</para>
///
/// <para>実ゲームの条件の根拠（コードの出典）はレポートの「条件の根拠」に書く。乱数は条件ごとに
/// シードを分け、試合ごとに事前 Fork（<see cref="GameSimulation"/> と同じ作法）＝同シード同結果。</para>
/// </summary>
public static class HomeRunMeasurement
{
    public sealed record Options
    {
        public int Games { get; init; } = 2000;
        public ulong Seed { get; init; } = 42;
        /// <summary>実プレーのセーブ（manualN.sav・gzip JSON）。指定すると「監督の試合」を保存された両校で再現する。</summary>
        public string? SavePath { get; init; }
        /// <summary>回す条件の id（null＝全部）。</summary>
        public HashSet<string>? Only { get; init; }
        /// <summary>係数YAML（null＝C#既定＝実ゲームと同じ）。校正中の試し打ち用（issue #976）。</summary>
        public string? CoefficientsPath { get; init; }
    }

    /// <summary>1条件の実測。</summary>
    public sealed record Row(string Id, string Label, string Detail, HomeRunProfile Profile);

    /// <summary>実ゲームの全国生成と同じ母種（Unity NationService.GeneratedNation＝seed 2026）。</summary>
    private const ulong NationSeed = 2026;

    /// <summary>実ゲームの暦年の起点（Unity SeasonClock.SeasonBaseYear）。年度 YearIndex の暦年＝2026+(YearIndex−1)。</summary>
    private const int SeasonBaseYear = 2026;

    /// <summary>球場抽選の専用ストリーム（Unity PlayerMatchResolver.StadiumStreamId と同じ番号）。</summary>
    private const ulong StadiumStreamId = 13;

    /// <summary>この計測で使う、組み合わせ・ラウンドの抽選用の専用ストリーム（試合本体の乱数を進めない）。</summary>
    private const ulong PairingStreamId = 0x1014_0001UL;

    /// <summary>神奈川の県ID（engine SummerRegions.KanagawaId と同じ値。internal なのでここで持つ）。</summary>
    private const int KanagawaId = 13;

    public static string Run(Options o, Action<string>? log = null)
    {
        log ??= _ => { };
        var rows = new List<Row>();
        var notes = new List<string>();
        bool Want(string id) => o.Only is null || o.Only.Contains(id);
        // 既定は C#既定＝実ゲームと同じ。YAML を渡したときも天候は剥がす（気温は条件ごとに明示で足す）。
        var baseCtx = GameSimulation.BuildContext(o.CoefficientsPath) with { Weather = null };
        var seed = o.Seed;
        ulong Next() => seed += 1000;

        // ── 実ゲームの全国（年度1）。神奈川の学校の強さの分布と、各県の最強校（甲子園の代表の近似）。 ──
        var nation = NationGenerator.Generate(Vocab(), new NationCoefficients(), new Xoshiro256Random(NationSeed));
        var kanagawa = nation.InPrefecture(KanagawaId).ToList();
        var prefTops = nation.Prefectures.Select(p => nation.InPrefecture(p.Id).OrderByDescending(s => s.Strength).First()).ToList();
        var rosters = new NationRosters(new AiRosterDeps());
        var rules = new ModernRules();

        // 夏の県大会（神奈川）: 単純トーナメント n 校＝n−1 試合のうち準決勝・決勝の3試合だけが決勝球場。
        var summerFinalShare = 3.0 / (kanagawa.Count - 1);
        // 秋の県大会（神奈川・data/pref-formats/kanagawa.yaml）: 7地区の4校ブロック総当たり（1ブロック6試合）＋
        // 上位2校＋夏代表の推薦で県大会トーナメント。準決勝・決勝の3試合だけが決勝球場（秋は県営級）。
        var blocks = (int)Math.Ceiling(kanagawa.Count / 4.0);
        var autumnGames = blocks * 6 + (blocks * 2 + 1 - 1);
        var autumnFinalShare = 3.0 / autumnGames;

        // ── ① 帯の条件（simulate-games と同じ） ──
        if (Want("band"))
            rows.Add(Measure("band", "帯の条件", "平均的な2校（全能力N(50,12)）・既定球場・天候なし・采配なし・コールドなし",
                o.Games, Next(), log, (i, g) => (BandTeam(g, "遠征校"), BandTeam(g, "地元校"), baseCtx)));

        // ── ② 球場だけ替える（チーム・天候は帯と同じ） ──
        foreach (var st in new[] { Stadium.MunicipalSmall01, Stadium.MunicipalSmall02, Stadium.PrefLarge, Stadium.Yokohama, Stadium.Koshien })
        {
            var id = "stadium-" + st.Id;
            if (!Want(id)) continue;
            var ctx = baseCtx with { Field = st.ToFieldGeometry() };
            rows.Add(Measure(id, "球場: " + st.Name,
                string.Create(CultureInfo.InvariantCulture, $"帯の2校・両翼{st.LeftM:F0}/{st.RightM:F0}m 中堅{st.CenterM:F0}m フェンス{st.FenceHeightM:F1}m・天候なし"),
                o.Games / 2, Next(), log, (i, g) => (BandTeam(g, "遠征校"), BandTeam(g, "地元校"), ctx)));
        }

        // ── ③ 気温だけ替える（チーム・球場は帯と同じ） ──
        foreach (var month in new[] { 7, 8, 9 })
        {
            var id = "weather-" + month;
            if (!Want(id)) continue;
            var w = WeatherModel.ForMonth(month);
            var ctx = baseCtx with { Weather = w };
            rows.Add(Measure(id, $"気温: {month}月",
                string.Create(CultureInfo.InvariantCulture, $"帯の2校・既定球場・{month}月の気温（平均{w.MeanTemperatureC:F0}℃ σ{w.TemperatureStdDevC:F1}・{w.MinTemperatureC:F0}〜{w.MaxTemperatureC:F0}℃）"),
                o.Games / 2, Next(), log, (i, g) => (BandTeam(g, "遠征校"), BandTeam(g, "地元校"), ctx)));
        }

        // ── ④ 帯の2校のまま、実ゲームの球場・気温・コールドを足す（チームの違いと条件の違いを切り分ける） ──
        if (Want("band-summer-venue"))
            rows.Add(Measure("band-summer-venue", "帯の2校×夏の県大会の球場・気温",
                "帯の2校・神奈川の夏の県大会の球場割当（市営級2種＋準決勝・決勝は横浜スタジアム）・7月の気温・コールドあり",
                o.Games, Next(), log, (i, g) =>
                {
                    var (ctx, _) = PrefCtx(baseCtx, g, summer: true, finalShare: summerFinalShare, month: 7, calendarYear: SeasonBaseYear, rules);
                    return (BandTeam(g, "遠征校"), BandTeam(g, "地元校"), ctx);
                }));

        // ── ⑤ 実ゲーム条件: 神奈川の学校同士（永続ロスターと同じ生成器）・敵AI采配・季節の気温・球場割当 ──
        Team[]? kanaTeams = null;
        Team[] KanaTeams() => kanaTeams ??= BuildTeams(rosters, kanagawa, rules, SeasonBaseYear, log);
        if (Want("real-summer"))
        {
            var teams = KanaTeams();
            rows.Add(Measure("real-summer", "実ゲーム: 夏の県大会（神奈川）",
                string.Create(CultureInfo.InvariantCulture,
                    $"神奈川の{kanagawa.Count}校から2校を一様に抽選・永続ロスター（年度1）＋敵AI・7月の気温・市営級2種（準決勝・決勝 {summerFinalShare:P1} は横浜スタジアム）・コールドあり（決勝以外）"),
                o.Games, Next(), log, (i, g) =>
                {
                    var (a, h) = PickPair(g, teams.Length);
                    var (ctx, _) = PrefCtx(baseCtx, g, summer: true, finalShare: summerFinalShare, month: 7, calendarYear: SeasonBaseYear, rules);
                    return (teams[a], teams[h], ctx);
                }));
        }
        if (Want("real-autumn"))
        {
            var teams = KanaTeams();
            rows.Add(Measure("real-autumn", "実ゲーム: 秋の県大会（神奈川）",
                string.Create(CultureInfo.InvariantCulture,
                    $"同上の2校・9月の気温・市営級2種（準決勝・決勝 {autumnFinalShare:P1} は県営級）・コールドあり（決勝以外）"),
                o.Games, Next(), log, (i, g) =>
                {
                    var (a, h) = PickPair(g, teams.Length);
                    var (ctx, _) = PrefCtx(baseCtx, g, summer: false, finalShare: autumnFinalShare, month: 9, calendarYear: SeasonBaseYear, rules);
                    return (teams[a], teams[h], ctx);
                }));
        }
        if (Want("real-koshien"))
        {
            var teams = BuildTeams(rosters, prefTops, rules, SeasonBaseYear, log);
            var ctxK = rules.ApplyTo(baseCtx with { Field = Stadium.Koshien.ToFieldGeometry(), Weather = WeatherModel.ForMonth(8) }, SeasonBaseYear);
            rows.Add(Measure("real-koshien", "実ゲーム: 夏の甲子園",
                "47都道府県の最強校（代表の近似）から2校を抽選・永続ロスター＋敵AI・甲子園・8月の気温・コールドなし",
                o.Games, Next(), log, (i, g) =>
                {
                    var (a, h) = PickPair(g, teams.Length);
                    return (teams[a], teams[h], ctxK);
                }));
        }

        // ── ⑥ ティア格子（実ゲームの夏の県大会の条件で、強さを指定した2校） ──
        var lattice = new[] { 35.0, 50.0, 65.0, 80.0 };
        for (var x = 0; x < lattice.Length; x++)
        for (var y = x; y < lattice.Length; y++)
        {
            var sa = lattice[x];
            var sh = lattice[y];
            var id = string.Create(CultureInfo.InvariantCulture, $"tier-{sa:F0}-{sh:F0}");
            if (!Want(id) && !Want("tier")) continue;
            var schoolA = StubSchool(-10, sa);
            var schoolH = StubSchool(-11, sh);
            rows.Add(Measure(id, string.Create(CultureInfo.InvariantCulture, $"ティア格子: {Label(sa)} vs {Label(sh)}"),
                "StrengthTeamFactory＋敵AI（校風なし）・夏の県大会の球場割当・7月の気温・コールドあり",
                o.Games / 2, Next(), log, (i, g) =>
                {
                    var away = StrengthTeamFactory.Create(sa, "先攻校", g, modernRules: rules, calendarYear: SeasonBaseYear)
                        with { Tactics = EnemyAiFactory.BrainFor(schoolA) };
                    var home = StrengthTeamFactory.Create(sh, "後攻校", g, modernRules: rules, calendarYear: SeasonBaseYear)
                        with { Tactics = EnemyAiFactory.BrainFor(schoolH) };
                    var (ctx, _) = PrefCtx(baseCtx, g, summer: true, finalShare: summerFinalShare, month: 7, calendarYear: SeasonBaseYear, rules);
                    return (away, home, ctx);
                }));
        }

        // ── ⑦ 監督の試合（宮前商工 vs 秦野農業）──
        double? managerStrength = null;
        double opponentStrength = 70.6;
        if (o.SavePath is not null && (Want("manager-save") || Want("manager-approx")))
        {
            var m = LoadManagerMatch(o.SavePath, baseCtx, notes);
            managerStrength = m.ManagerStrength;
            opponentStrength = m.Opponent.Strength;
            if (Want("manager-save"))
                rows.Add(Measure("manager-save", $"監督の試合（セーブから再現）: {m.ManagerName} vs {m.Opponent.Name}",
                    m.Detail, o.Games * 2, Next(), log, (i, g) =>
                    {
                        var (ctx, _) = PrefCtx(m.Ctx, g, summer: false, finalShare: 0.0, month: 9, calendarYear: m.CalendarYear, rules);
                        return m.ManagerIsAway ? (m.ManagerTeam, m.OpponentTeam, ctx) : (m.OpponentTeam, m.ManagerTeam, ctx);
                    }));
        }
        if (Want("manager-approx"))
        {
            var ms = managerStrength ?? 55.0;
            var os = opponentStrength;
            var schoolM = StubSchool(-12, ms);
            var schoolO = StubSchool(-13, os);
            rows.Add(Measure("manager-approx",
                string.Create(CultureInfo.InvariantCulture, $"監督の試合の近似: 強さ{ms:F0} vs {os:F0}"),
                string.Create(CultureInfo.InvariantCulture,
                    $"StrengthTeamFactory（強さ {ms:F1} vs {os:F1}{(managerStrength is null ? "・自校の強さは仮置き" : "・自校の強さはセーブの部員から TeamStrengthProfile で算出")}）＋敵AI・神奈川の秋の序盤（市営級2種）・9月の気温・コールドあり"),
                o.Games * 2, Next(), log, (i, g) =>
                {
                    var away = StrengthTeamFactory.Create(ms, "自校", g, modernRules: rules, calendarYear: SeasonBaseYear)
                        with { Tactics = EnemyAiFactory.BrainFor(schoolM) };
                    var home = StrengthTeamFactory.Create(os, "相手校", g, modernRules: rules, calendarYear: SeasonBaseYear)
                        with { Tactics = EnemyAiFactory.BrainFor(schoolO) };
                    var (ctx, _) = PrefCtx(baseCtx, g, summer: false, finalShare: 0.0, month: 9, calendarYear: SeasonBaseYear, rules);
                    return (away, home, ctx);
                }));
        }

        return Report(rows, notes, o, kanagawa.Count, summerFinalShare, autumnGames, autumnFinalShare);
    }

    // ── 計測の本体 ──

    /// <summary>
    /// <paramref name="games"/> 試合を回して本塁打の出方を集計する。<paramref name="setup"/> は試合ローカルの乱数
    /// （事前 Fork 済み）から (先攻, 後攻, 文脈) を返す。整数集計の並列マージ＝同シード同結果。
    /// </summary>
    public static HomeRunProfile MeasureProfile(int games, ulong seed,
        Func<int, IRandomSource, (Team Away, Team Home, GameContext Ctx)> setup)
    {
        var root = new Xoshiro256Random(seed);
        var forks = new IRandomSource[games];
        for (var i = 0; i < games; i++) forks[i] = root.Fork((ulong)i);
        var total = new HomeRunProfile();
        Parallel.For(0, games,
            () => new HomeRunProfile(),
            (i, _, local) =>
            {
                var g = forks[i];
                var (away, home, ctx) = setup(i, g);
                var r = GameEngine.Play(away, home, ctx, g);
                local.AddGame(r, ctx.Field);
                return local;
            },
            local => { lock (total) total.Merge(local); });
        return total;
    }

    private static Row Measure(string id, string label, string detail, int games, ulong seed, Action<string> log,
        Func<int, IRandomSource, (Team, Team, GameContext)> setup)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var p = MeasureProfile(Math.Max(1, games), seed, setup);
        log(string.Create(CultureInfo.InvariantCulture,
            $"[hr-measure] {id}: {p.Games}試合 HR% {p.HomeRunRate:P2} HR/試合 {p.HomeRunsPerGame:F2} 5本以上 {p.ShareAtLeast(5):P2} 得点 {p.RunsPerTeamAverage:F2}（{sw.Elapsed.TotalSeconds:F0}秒）"));
        return new Row(id, label, detail, p);
    }

    /// <summary>帯と同じ合成チーム（<see cref="GameSimulation.GenerateTeam"/>）。</summary>
    private static Team BandTeam(IRandomSource g, string name) => GameSimulation.GenerateTeam(g, name);

    /// <summary>
    /// 県大会の試合の文脈（実ゲームの <c>PlayerMatchResolver</c> と同じ入口）。ラウンドは専用ストリームで
    /// 「準決勝・決勝か」だけを <paramref name="finalShare"/> で引き、球場は <see cref="RegionalStadiumTier.ForStadium"/>
    /// に試合の乱数の Fork（<see cref="StadiumStreamId"/>）を渡す＝Fork は親を進めないので試合本体の乱数は変わらない。
    /// コールドは決勝以外あり（Q29）。気温はその月の表（#971）。
    /// </summary>
    private static (GameContext Ctx, Stadium Stadium) PrefCtx(GameContext baseCtx, IRandomSource g, bool summer,
        double finalShare, int month, int calendarYear, ModernRules rules)
    {
        var u = g.Fork(PairingStreamId + 1).NextDouble();
        // 準決勝(2)・決勝(1)を finalShare で、それ以外は序盤（5回戦相当＝市営級の抽選）。
        int roundsRemaining = u < finalShare / 3.0 ? 1 : u < finalShare ? 2 : 5;
        var stadium = RegionalStadiumTier.ForStadium(roundsRemaining, g.Fork(StadiumStreamId), KanagawaId, summer);
        var ctx = rules.ApplyTo(baseCtx with
        {
            Field = stadium.ToFieldGeometry(),
            Weather = WeatherModel.ForMonth(month),
            MercyRuleEnabled = roundsRemaining != 1,
        }, calendarYear);
        return (ctx, stadium);
    }

    /// <summary>2校を一様に（別の校同士で）抽選する。専用ストリーム＝試合本体の乱数を進めない。</summary>
    private static (int A, int H) PickPair(IRandomSource g, int n)
    {
        var r = g.Fork(PairingStreamId);
        var a = r.NextInt(0, n);
        var h = r.NextInt(0, n - 1);
        if (h >= a) h++;
        return (a, h);
    }

    /// <summary>
    /// 学校の「いま戦えば出てくるチーム」（実ゲームの <c>PlayerMatchResolver.BuildOpponentTeam</c> と同じ組み方:
    /// 永続ロスター→<see cref="AiTeamBuilder"/>→敵AIの打順の並べ替え→采配ブレイン）。年度1。
    /// </summary>
    private static Team[] BuildTeams(NationRosters rosters, IReadOnlyList<School> schools, ModernRules rules,
        int calendarYear, Action<string> log)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var teams = new Team[schools.Count];
        for (var i = 0; i < schools.Count; i++) teams[i] = OpponentTeam(rosters, schools[i], 1, rules, calendarYear);
        log(string.Create(CultureInfo.InvariantCulture, $"[hr-measure] {schools.Count}校のロスターを生成（{sw.Elapsed.TotalSeconds:F0}秒）"));
        return teams;
    }

    private static Team OpponentTeam(NationRosters rosters, School school, int yearIndex, ModernRules rules, int calendarYear)
    {
        var team = rosters.PeekTeamFor(school, yearIndex, rules, calendarYear);
        var brain = EnemyAiFactory.BrainFor(school);
        var orderRng = AiRosterFactory.CohortSeed(school.Id, yearIndex).Fork(0x0BDE_0000UL);
        return team.WithBattingOrder(brain.ComposeBattingOrder(team.BattingOrder, orderRng)) with { Tactics = brain };
    }

    /// <summary>
    /// 校名語彙（data/school-names.yaml）。校名は独立の抽選列で強さ・校風の生成列に触れない＝無くても同じ全国になるが、
    /// レポートに実ゲームと同じ校名を出すために読む（見つからなければ既定の語彙）。
    /// </summary>
    private static SchoolNameVocab Vocab()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "data", "school-names.yaml");
            if (File.Exists(candidate)) return KokoSim.Config.SchoolNamesLoader.LoadFromFile(candidate);
            dir = dir.Parent;
        }
        return new SchoolNameVocab();
    }

    private static School StubSchool(int id, double strength)
        => new() { Id = id, Name = "計測校", PrefectureId = KanagawaId, Strength = strength };

    private static string Label(double strength)
        => string.Create(CultureInfo.InvariantCulture, $"{Tiers.FromStrength(strength)}({strength:F0})");

    // ── セーブからの再現（監督の試合） ──

    private sealed record ManagerMatch(
        string ManagerName, School Opponent, Team ManagerTeam, Team OpponentTeam, bool ManagerIsAway,
        GameContext Ctx, int CalendarYear, double ManagerStrength, string Detail);

    /// <summary>
    /// セーブ（gzip JSON）から自校と相手校を組む。相手校は実ゲームと同じ入口（全国の決定論再生成＋転任の差分＋
    /// 年次進化の復元→保存された永続ロスター→<see cref="OpponentTeam"/>）。自校は保存された部員から
    /// <see cref="RosterTeamBuilder.Build"/>（自動編成）＝保存されたスタメンの復元は省く（近似）。
    /// 相手は進行中の大会の次の対戦相手（自校＝ID −1 の隣の枠）。
    /// </summary>
    private static ManagerMatch LoadManagerMatch(string path, GameContext baseCtx, List<string> notes)
    {
        string text;
        using (var fs = File.OpenRead(path))
        using (var gz = new GZipStream(fs, CompressionMode.Decompress))
        using (var sr = new StreamReader(gz, Encoding.UTF8))
            text = sr.ReadToEnd();
        if (!KokoSim.Engine.Saves.SaveCodec.TryDeserialize(text, out var s, out var err))
            throw new InvalidOperationException("セーブを読めません: " + err);

        var generated = NationGenerator.Generate(Vocab(), new NationCoefficients(), new Xoshiro256Random(NationSeed));
        var nation = (s.NationOverlay ?? new NationOverlay()).Apply(generated);
        if (s.NationSchools is { Count: > 0 }) AiSchoolEvolution.Restore(nation, s.NationSchools);
        var rosters = NationRosters.FromSaved(new AiRosterDeps(), s.NationRosters);

        // 次の対戦相手: 大会の現在ラウンドの並びで自校（−1）と組む枠。
        var current = s.Tournament?.Runner.Current ?? throw new InvalidOperationException("セーブに進行中の大会がありません");
        var mi = current.IndexOf(-1);
        if (mi < 0) throw new InvalidOperationException("自校が現在のラウンドに居ません（敗退済み）");
        var oppId = current[mi % 2 == 0 ? mi + 1 : mi - 1] ?? throw new InvalidOperationException("次の対戦相手が未定（不戦勝）");
        var opponent = nation.Schools.First(x => x.Id == oppId);

        var calendarYear = SeasonBaseYear + (s.YearIndex - 1);
        var active = RosterLifecycle.Active(s.Roster);
        var managerTeam = RosterTeamBuilder.Build(active, s.SchoolName);
        var opponentTeam = OpponentTeam(rosters, opponent, s.YearIndex, new ModernRules(), calendarYear);
        var managerStrength = TeamStrengthProfile.Compute(active, new TeamStrengthCoefficients()).Overall;
        var managerIsAway = HomeAwayAssignment.ManagerIsAway(-1, opponent.Id, s.YearIndex, s.Week);
        var kind = s.Tournament?.Kind.ToString() ?? "?";

        notes.Add(string.Create(CultureInfo.InvariantCulture,
            $"監督の試合はセーブ（年度{s.YearIndex}・第{s.Week}週・{kind}・{s.Tournament?.Title}）の次の対戦を再現した。"
            + $"自校 {s.SchoolName}（部員 {active.Count} 人・総合 {managerStrength:F1}＝{Tiers.FromStrength(managerStrength)}）vs "
            + $"{opponent.Name}（ID {opponent.Id}・強さ {opponent.Strength:F1}＝{opponent.Tier}・校風 {opponent.Style}）。"
            + $"自校は{(managerIsAway ? "先攻" : "後攻")}（HomeAwayAssignment）。"));
        notes.Add("近似: 自校の打順は保存されたスタメン（LastLineup）ではなく自動編成（RosterTeamBuilder.Build・能力順）。"
            + "前の試合からの投手の疲労・球数の持ち越し（PitchRecovery/PriorWeekPitches）と自校の勢い・伝令の采配能力は入れていない。");

        var detail = string.Create(CultureInfo.InvariantCulture,
            $"セーブの両校（自校＝保存部員の自動編成・相手＝保存された永続ロスター＋敵AI）・{kind}の序盤（市営級2種）・9月の気温・コールドあり・暦年{calendarYear}のルール");
        return new ManagerMatch(s.SchoolName, opponent, managerTeam, opponentTeam, managerIsAway, baseCtx, calendarYear,
            managerStrength, detail);
    }

    // ── レポート ──

    private static string Report(List<Row> rows, List<string> notes, Options o, int kanagawaCount,
        double summerFinalShare, int autumnGames, double autumnFinalShare)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# 本塁打の実測レポート（issue #1014・#976 の前段・計測のみ）");
        sb.AppendLine();
        sb.AppendLine(c, $"- シード: {o.Seed}（条件ごとに +1000）／基本 {o.Games} 試合（球場別・気温別・ティア格子は半分、監督の試合は2倍）");
        sb.AppendLine(o.CoefficientsPath is null
            ? "- 係数: C#既定（実ゲームと同じ）"
            : $"- 係数: {o.CoefficientsPath}（試し打ち。実ゲームは C#既定）");
        sb.AppendLine("- 本塁打/試合・5本以上・10本以上は**両軍計**。得点はチームあたり");
        sb.AppendLine();

        sb.AppendLine("## 条件ごとの要約");
        sb.AppendLine();
        sb.AppendLine("| 条件 | 試合 | 本塁打率（打席あたり） | 本塁打/試合 | 5本以上 | 10本以上 | 最大 | 平均得点/チーム | 二塁打÷本塁打 | 平均初速 | 平均角度 | フェンス際(0〜2m) |");
        sb.AppendLine("|---|---:|---:|---:|---:|---|---:|---:|---:|---:|---:|---:|");
        foreach (var r in rows)
        {
            var p = r.Profile;
            sb.AppendLine(c,
                $"| {r.Label} | {p.Games} | {p.HomeRunRate:P2} | {p.HomeRunsPerGame:F2} | {p.ShareAtLeast(5):P2} | {p.FrequencyLabel(10)} "
                + $"| {p.MaxHomeRunsInGame} | {p.RunsPerTeamAverage:F2} | {p.DoublesPerHomeRun:F2} | {p.MeanEvKmh:F1} | {p.MeanLaDeg:F1}° | {p.FenceEdgeShare:P1} |");
        }
        sb.AppendLine();

        sb.AppendLine("## 1試合（両軍計）の本塁打数の分布");
        sb.AppendLine();
        sb.AppendLine("| 条件 | 0本 | 1本 | 2本 | 3本 | 4本 | 5本以上 | 二項近似の5本以上 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var r in rows)
        {
            var p = r.Profile;
            sb.AppendLine(c,
                $"| {r.Label} | {p.ShareExactly(0):P1} | {p.ShareExactly(1):P1} | {p.ShareExactly(2):P1} | {p.ShareExactly(3):P1} "
                + $"| {p.ShareExactly(4):P1} | {p.ShareAtLeast(5):P2} | {p.BinomialAtLeast(5):P2} |");
        }
        sb.AppendLine();
        sb.AppendLine("二項近似＝その条件の本塁打率と打席数/試合で各打席を独立とみなした値。実測がこれより大きい分は、チーム力・球場・気温のばらつきで裾が太くなった分。");
        sb.AppendLine();

        sb.AppendLine("## チーム得点の分布");
        sb.AppendLine();
        sb.AppendLine("| 条件 | 0 | 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10以上 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var r in rows)
        {
            sb.Append(c, $"| {r.Label} |");
            for (var k = 0; k <= HomeRunProfile.RunsCap; k++) sb.Append(c, $" {r.Profile.RunsShare(k):P1} |");
            sb.AppendLine();
        }
        sb.AppendLine();

        sb.AppendLine("## 本塁打の打球の内訳（主な条件）");
        sb.AppendLine();
        foreach (var r in rows.Where(r => r.Id is "band" or "real-summer" or "real-autumn" or "manager-save"))
        {
            sb.AppendLine(c, $"### {r.Label}");
            sb.AppendLine();
            sb.Append(r.Profile.BattedBallMarkdown());
            sb.AppendLine();
        }

        sb.AppendLine("## 条件の詳細");
        sb.AppendLine();
        sb.AppendLine("| id | 条件 | 中身 |");
        sb.AppendLine("|---|---|---|");
        foreach (var r in rows) sb.AppendLine(c, $"| `{r.Id}` | {r.Label} | {r.Detail} |");
        sb.AppendLine();

        sb.AppendLine("## 条件の根拠（実ゲームのコード）");
        sb.AppendLine();
        sb.AppendLine("- **球場**: 自校戦は `Assets/KokoSim/Shell/PlayerMatchResolver.Stadium` → `MatchStadium.ForStadium` → "
            + "engine `RegionalStadiumTier.ForStadium`。夏・秋の県大会は**準決勝・決勝（残りラウンド≤2）だけ**決勝球場"
            + "（神奈川の夏＝横浜スタジアム、秋＝県営級 `pref_large_01`）、**それ以外は市営級2種（`municipal_small_01`/`02`）を1/2ずつ**抽選"
            + "（乱数は試合の Fork 13）。甲子園は `Stadium.Koshien`。裏試合（`TournamentEntry`）も `useRegionalStadiumTier: true` で同じ判定。");
        sb.AppendLine(c, $"- **球場の比率**: 神奈川は {kanagawaCount} 校（`NationGenerator` の県別校数）。夏は単純トーナメント {kanagawaCount - 1} 試合のうち"
            + $"準決勝・決勝の3試合＝{summerFinalShare:P1} が横浜スタジアム、残りは市営級。秋は `data/pref-formats/kanagawa.yaml`"
            + $"（地区予選＝4校ブロック総当たり＋県大会トーナメント）で約 {autumnGames} 試合のうち3試合＝{autumnFinalShare:P1} が県営級。"
            + $"＝**県大会の試合はほぼ全部が市営級**（大会全体の試合数で重み付け。自校が勝ち進むほど決勝球場の比率は上がる）。");
        sb.AppendLine("- **気温**: `PlayerMatchResolver.CurrentWeatherCoefficients` → `WeatherModel.ForMonth(試合日の月)`（#971）。"
            + "開幕月は `TournamentSchedule`（夏の県大会 7/10・甲子園 8/6・秋季県大会 9/12・地区大会 10/18）。"
            + "夏の県大会＝7月（平均30℃）・甲子園＝8月（32℃）・秋の県大会＝9月（27℃）で測った。");
        sb.AppendLine("- **コールド**: 県大会は決勝以外コールドあり（`mercyRuleEnabled`・Q29）。甲子園はなし。");
        sb.AppendLine("- **チーム**: 実ゲームの相手校は永続ロスター（`NationRosters.TeamFor`→`AiTeamBuilder`）＋敵AI采配（`EnemyAiFactory.BrainFor`）"
            + "＋打順の並べ替え。ここでは全国を実ゲームと同じ母種（2026）で生成し、年度1のロスターを `PeekTeamFor` で組んだ。");
        sb.AppendLine("- **ルール**: `ModernRules.ApplyTo`（タイブレーク・球数制限。DHは各校の判断）。");
        sb.AppendLine();

        sb.AppendLine("## 仮定・近似");
        sb.AppendLine();
        sb.AppendLine("- 県大会の組み合わせは県内の学校から**一様に2校**を引いた（実際は勝ち進んだ強い学校ほど試合数が多い＝上位校同士の試合の重みは実際より小さい）。");
        sb.AppendLine("- 全国は年度1（初期の強さの分布）。年次進化後の勢力図は入れていない（監督の試合を除く）。");
        sb.AppendLine("- 投手の疲労・球数の持ち越し（連戦）は入れていない（全試合、投手は万全）。");
        sb.AppendLine("- ティア格子は `StrengthTeamFactory`（`simulate-tiers` と同じ生成器）＋敵AI（校風なし）。");
        foreach (var n in notes) sb.AppendLine("- " + n);
        sb.AppendLine();
        return sb.ToString();
    }
}
