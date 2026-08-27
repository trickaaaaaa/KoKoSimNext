using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace KokoSim.Config;

/// <summary>統計回帰の許容帯（1指標）。</summary>
public sealed record Band(double Min, double Max)
{
    public bool Contains(double v) => v >= Min && v <= Max;
}

/// <summary>data/balance-targets.yaml の Phase 1（1万打席）許容帯。</summary>
public sealed record BalanceTargets
{
    public required Band BattingAverage { get; init; }
    public required Band StrikeoutRate { get; init; }
    public required Band WalkRate { get; init; }
    public required Band HomeRunRate { get; init; }

    // ===== 長打の量と配分（Issue #24）。塁打数を幾何と走力から決めるようにした結果の帯 =====
    /// <summary>長打率 SLG。</summary>
    public Band Slugging { get; init; } = new(0.340, 0.480);
    /// <summary>二塁打率（打席あたり）。</summary>
    public Band DoubleRate { get; init; } = new(0.035, 0.065);
    /// <summary>三塁打率（打席あたり）。</summary>
    public Band TripleRate { get; init; } = new(0.002, 0.009);
    /// <summary>二塁打÷本塁打。長打の「種類の配分」が壊れていないことを直接見る指標（実野球はおよそ2〜2.5）。</summary>
    public Band DoublesPerHomeRun { get; init; } = new(2.00, 4.50);
}

/// <summary>Phase 2（試合）の許容帯。</summary>
public sealed record GameTargets
{
    public required Band RunsPerTeam { get; init; }
    public required Band PitchesPerGame { get; init; }
    public required Band MinutesPerGame { get; init; }
    public required Band InningsPerGame { get; init; }
    /// <summary>本塁クロスプレー憤死/試合の参考帯（設計書12 §3 F2, Q9）。広め＝得点帯と競合させない warn 相当。</summary>
    public Band HomePlayOutsPerGame { get; init; } = new(0.12, 0.50);
    /// <summary>三塁憤死/試合の参考帯（単打の一塁→三塁レース, Issue #89, 設計書12 §3.5）。広め＝warn 相当。</summary>
    public Band ThirdPlayOutsPerGame { get; init; } = new(0.02, 0.40);
    /// <summary>併殺打/試合の参考帯（両軍計。GIDP, Issue #369。物理レース化後。実測≈1.06）。</summary>
    public Band DoublePlayPerGame { get; init; } = new(0.70, 2.20);

    /// <summary>失策数/試合（両軍計）。乱数生成母平均≈3.4（弱小裾の大量失策込み・中央値C50同士は≈2.4）。issue #123で新設。</summary>
    public Band ErrorsPerGame { get; init; } = new(1.8, 3.6);

    /// <summary>外野失策シェア（外野失策÷全失策。issue #532）。修正前0.393＝フライ捕球に難度が無かった。実野球はおよそ1割。</summary>
    public Band OutfieldErrorShare { get; init; } = new(0.05, 0.20);

    // ===== design-14 第1段（P1）新プレー発生率/試合（両軍計）。采配Brain不要＝無指示でも発生する常時系 =====
    public Band FieldersChoicePerGame { get; init; } = new(0.10, 1.20);
    public Band DroppedThirdStrikePerGame { get; init; } = new(0.02, 0.40);
    /// <summary>失策連鎖/試合。失策総数に比例するため issue #123 で 0.02〜0.40 → 0.10〜0.80 へ拡張。</summary>
    public Band ErrorExtraAdvancePerGame { get; init; } = new(0.10, 0.80);
    /// <summary>暴投・パスボール/試合の参考帯（design-14 P2-8, 設計書15 Phase D-3）。</summary>
    public Band WildPitchPerGame { get; init; } = new(0.15, 1.00);
}

/// <summary>design-14 第1段（P1）のうち采配Brain（KokoSim.Balance の GameSimulation --tactics）が
/// 盗塁/敬遠を選ばない限り発生しないプレーの許容帯。games_10k_tactics セクション。</summary>
public sealed record GameTacticsTargets
{
    public required Band RunsPerTeam { get; init; }
    public Band PickoffPerGame { get; init; } = new(0.0, 0.05);
    public Band IntentionalWalkPerGame { get; init; } = new(0.0, 0.30);
    public Band DoubleStealThirdBreakPerGame { get; init; } = new(0.0, 0.05);
    /// <summary>単独盗塁（二盗・三盗・本盗計）の企図/試合。issue steal-thresholds-unreachable（2026-08-27）:
    /// 采配閾値が解決式の到達可能上限を超えていて長らく実質0件だった。閾値を実測レンジへ再校正した後の
    /// 実測（3000試合・tactics有効）は 0.24/試合。旧来「0件が正常」だった帯を、この回帰で初めて意味を持つ
    /// 帯として追加する。</summary>
    public Band StealAttemptPerGame { get; init; } = new(0.05, 0.60);
    /// <summary>単独盗塁の成功率。解決式の物理層（<see cref="Match.Game.StealResolver"/>）が返す到達可能
    /// レンジ自体が現実の高校野球（65〜70%）より低い（design-14「リーグ平均を動かすなら jump/pop time を
    /// 動かす」参照、本issueのスコープ外）ため、帯は現行物理層の実測値（3000試合で37.5%）を中心に取る。</summary>
    public Band StealSuccessRate { get; init; } = new(0.20, 0.55);
    // 1球采配（設計書15 Phase C-2）: 追い込まれ矯正/3-0待て/決め球切替の影響を最も受ける指標。
    public Band StrikeoutRate { get; init; } = new(0.16, 0.22);
    public Band WalkRate { get; init; } = new(0.06, 0.10);
    public Band HomeRunRate { get; init; } = new(0.010, 0.022);
}

/// <summary>
/// 低ティア・非対称マッチの許容帯（issue #536 / #578 Step 4・tiers_low セクション）。
///
/// <para>既存4帯は「平均的なチーム同士」だけを測っているので、格差マッチの体験は帯が全部緑のまま壊れる
/// （#536 の実測）。ここが守るのは<b>体験そのもの</b>＝開始直後の自校が格上に完封される率と、
/// 表示1ティアあたりの得点の落ち方。</para>
/// </summary>
/// <summary>
/// 県内の寡占（issue prefecture-dominance・2026-08-18）。集計モデル（ChampionsOnly＝実ゲームの他県裏試合と同じ部品）で
/// 全国4000校×20年を回し、49地区それぞれの「最多優勝校の優勝回数」の分布を固定する。帯4本は大会も勢力図も測らないので、
/// 「同じ校が27大会連続優勝」は帯が全部緑のまま起きた。現実の県（20年で最多校 5〜7回・聖光学院/明徳級で13〜16）に寄せる。
/// </summary>
public sealed record NationDominanceTargets
{
    /// <summary>49地区の最多優勝校の回数（20年）の中央値。校正前 8〜9。</summary>
    public Band MaxChampionCountMedian { get; init; } = new(3, 7);
    /// <summary>最多優勝校が10回以上（20年の半分）の地区数。校正前 15〜17/49。</summary>
    public Band RegionsWithTenPlus { get; init; } = new(0, 8);
    /// <summary>49地区の最多優勝校の回数の最大（一強の尾）。校正前 19〜20（＝20年全部）。</summary>
    public Band MaxChampionCountMax { get; init; } = new(0, 17);
    /// <summary>県1位の圧縮後の強さ vs 2位（初期）の単試合勝率＝集計モデルの平均。校正前 0.85 前後。</summary>
    public Band TopTwoWinProbabilityMean { get; init; } = new(0.55, 0.78);
}

public sealed record LowTierTargets
{
    /// <summary>自校の得点（ほぼ互角の相手＝強度40）。</summary>
    public Band ManagerRunsVsE40 { get; init; } = new(5.5, 8.0);
    /// <summary>自校の得点（1ティア上＝強度50）。</summary>
    public Band ManagerRunsVsD50 { get; init; } = new(3.2, 5.4);
    /// <summary>自校の得点（2ティア上＝強度60）。</summary>
    public Band ManagerRunsVsC60 { get; init; } = new(1.9, 3.6);
    /// <summary>自校が完封される率（1ティア上）。校正前 0.212。</summary>
    public Band ManagerShutoutRateVsD50 { get; init; } = new(0.02, 0.14);
    /// <summary>自校が完封される率（2ティア上）。校正前 0.403。</summary>
    public Band ManagerShutoutRateVsC60 { get; init; } = new(0.08, 0.28);
    /// <summary>表示1ティア（強度10ポイント）ごとの得点比＝勾配。校正前 0.42 / 校正後 0.61–0.67。</summary>
    public Band RunsRatioPerTier { get; init; } = new(0.52, 0.80);
}

/// <summary>
/// 投打差の格子（issue matchup-gap-saturation-k-rate・matchup_grid セクション）。
/// <c>atbat_10k</c> は平均同士（N(50,12)）しか測らず、A/S投手 vs E/F打線の裾（24奪三振試合）が帯を全部緑のまま通る。
/// 格子は Balance の <c>AtBatSimulation.RunMatchupGrid</c>（投手=球速/制球/キレ同レベル・打者=ミート/選球眼同レベル）。
/// </summary>
public sealed record MatchupGridTargets
{
    /// <summary>A投手(80) vs F打者(30) の K%。線形時 65%（歴史的試合が平均になる）→ soft-cap 後 ≈44%。</summary>
    public Band StrikeoutRateP80B30 { get; init; } = new(0.30, 0.48);
    /// <summary>A投手(80) vs E打者(45) の K%。</summary>
    public Band StrikeoutRateP80B45 { get; init; } = new(0.28, 0.46);
    /// <summary>S投手(90) vs F打者(30) の K%（裾の天井）。</summary>
    public Band StrikeoutRateP90B30 { get; init; } = new(0.32, 0.52);
    /// <summary>互角（P50 vs B45）の K%＝飽和が互角近傍を動かしていないこと。</summary>
    public Band StrikeoutRateP50B45 { get; init; } = new(0.17, 0.28);
    /// <summary>F投手(35) vs B打者(75) の被打率（逆側の裾。パワー50固定）。</summary>
    public Band AverageP35B75 { get; init; } = new(0.260, 0.360);
    /// <summary>A投手(80) vs F打者(30) の被打率（格下でも「当たれば飛ぶ」余地）。</summary>
    public Band AverageP80B30 { get; init; } = new(0.120, 0.220);
    /// <summary>A投手(80) vs F打者(30) の BB%（下限＝四球が消えない。issue walk-rate-floor-vs-elite-pitcher）。</summary>
    public Band WalkRateP80B30 { get; init; } = new(0.025, 0.08);
    /// <summary>S投手(90) vs B打者(75) の BB%。</summary>
    public Band WalkRateP90B75 { get; init; } = new(0.03, 0.13);
    /// <summary>互角（P50 vs B45）の BB%＝隅狙いが互角近傍を動かしていないこと。</summary>
    public Band WalkRateP50B45 { get; init; } = new(0.06, 0.10);
}

public static class BalanceTargetsLoader
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(UnderscoredNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public static BalanceTargets LoadFromFile(string path) => Parse(File.ReadAllText(path));

    public static BalanceTargets Parse(string yaml)
    {
        var a = Root(yaml).Atbat10k ?? throw new InvalidDataException("atbat_10k セクションがありません。");
        return new BalanceTargets
        {
            BattingAverage = a.BattingAverage!.ToBand(),
            StrikeoutRate = a.StrikeoutRate!.ToBand(),
            WalkRate = a.WalkRate!.ToBand(),
            HomeRunRate = a.HomeRunRate!.ToBand(),
            Slugging = a.Slugging?.ToBand() ?? new Band(0.340, 0.480),
            DoubleRate = a.DoubleRate?.ToBand() ?? new Band(0.035, 0.065),
            TripleRate = a.TripleRate?.ToBand() ?? new Band(0.002, 0.009),
            DoublesPerHomeRun = a.DoublesPerHomeRun?.ToBand() ?? new Band(2.00, 4.50),
        };
    }

    public static GameTargets LoadGameFromFile(string path) => ParseGame(File.ReadAllText(path));

    public static GameTargets ParseGame(string yaml)
    {
        var g = Root(yaml).Games10k ?? throw new InvalidDataException("games_10k セクションがありません。");
        return new GameTargets
        {
            RunsPerTeam = g.RunsPerTeam!.ToBand(),
            PitchesPerGame = g.PitchesPerGame!.ToBand(),
            MinutesPerGame = g.MinutesPerGame!.ToBand(),
            InningsPerGame = g.InningsPerGame!.ToBand(),
            HomePlayOutsPerGame = g.HomePlayOutsPerGame?.ToBand() ?? new Band(0.12, 0.50),
            ThirdPlayOutsPerGame = g.ThirdPlayOutsPerGame?.ToBand() ?? new Band(0.02, 0.40),
            DoublePlayPerGame = g.DoublePlayPerGame?.ToBand() ?? new Band(0.70, 2.20),
            ErrorsPerGame = g.ErrorsPerGame?.ToBand() ?? new Band(1.8, 3.6),
            OutfieldErrorShare = g.OutfieldErrorShare?.ToBand() ?? new Band(0.05, 0.20),
            FieldersChoicePerGame = g.FieldersChoicePerGame?.ToBand() ?? new Band(0.10, 1.20),
            DroppedThirdStrikePerGame = g.DroppedThirdStrikePerGame?.ToBand() ?? new Band(0.02, 0.40),
            ErrorExtraAdvancePerGame = g.ErrorExtraAdvancePerGame?.ToBand() ?? new Band(0.10, 0.80),
            WildPitchPerGame = g.WildPitchPerGame?.ToBand() ?? new Band(0.15, 1.00),
        };
    }

    /// <summary>
    /// 夏場の天候（気温）モデル有効時の許容帯（Issue #120, games_10k_summer セクション）。
    /// 一般帯（games_10k）を土台に、games_10k_summer で指定された指標だけ上書きする（未指定は一般帯を流用）。
    /// </summary>
    public static GameTargets LoadGameSummerFromFile(string path) => ParseGameSummer(File.ReadAllText(path));

    public static GameTargets ParseGameSummer(string yaml)
    {
        var baseTargets = ParseGame(yaml);
        var s = Root(yaml).Games10kSummer;
        if (s is null) return baseTargets;
        return baseTargets with
        {
            RunsPerTeam = s.RunsPerTeam?.ToBand() ?? baseTargets.RunsPerTeam,
            PitchesPerGame = s.PitchesPerGame?.ToBand() ?? baseTargets.PitchesPerGame,
            InningsPerGame = s.InningsPerGame?.ToBand() ?? baseTargets.InningsPerGame,
        };
    }

    public static GameTacticsTargets LoadGameTacticsFromFile(string path) => ParseGameTactics(File.ReadAllText(path));

    public static GameTacticsTargets ParseGameTactics(string yaml)
    {
        var g = Root(yaml).Games10kTactics
            ?? throw new InvalidDataException("games_10k_tactics セクションがありません。");
        return new GameTacticsTargets
        {
            RunsPerTeam = g.RunsPerTeam!.ToBand(),
            PickoffPerGame = g.PickoffPerGame?.ToBand() ?? new Band(0.0, 0.05),
            IntentionalWalkPerGame = g.IntentionalWalkPerGame?.ToBand() ?? new Band(0.0, 0.30),
            DoubleStealThirdBreakPerGame = g.DoubleStealThirdBreakPerGame?.ToBand() ?? new Band(0.0, 0.05),
            StealAttemptPerGame = g.StealAttemptPerGame?.ToBand() ?? new Band(0.05, 0.60),
            StealSuccessRate = g.StealSuccessRate?.ToBand() ?? new Band(0.20, 0.55),
            StrikeoutRate = g.StrikeoutRate?.ToBand() ?? new Band(0.16, 0.22),
            WalkRate = g.WalkRate?.ToBand() ?? new Band(0.06, 0.10),
            HomeRunRate = g.HomeRunRate?.ToBand() ?? new Band(0.010, 0.022),
        };
    }

    public static LowTierTargets LoadLowTierFromFile(string path) => ParseLowTier(File.ReadAllText(path));

    public static LowTierTargets ParseLowTier(string yaml)
    {
        var t = Root(yaml).TiersLow;
        if (t is null) return new LowTierTargets();
        var d = new LowTierTargets();
        return new LowTierTargets
        {
            ManagerRunsVsE40 = t.ManagerRunsVsE40?.ToBand() ?? d.ManagerRunsVsE40,
            ManagerRunsVsD50 = t.ManagerRunsVsD50?.ToBand() ?? d.ManagerRunsVsD50,
            ManagerRunsVsC60 = t.ManagerRunsVsC60?.ToBand() ?? d.ManagerRunsVsC60,
            ManagerShutoutRateVsD50 = t.ManagerShutoutRateVsD50?.ToBand() ?? d.ManagerShutoutRateVsD50,
            ManagerShutoutRateVsC60 = t.ManagerShutoutRateVsC60?.ToBand() ?? d.ManagerShutoutRateVsC60,
            RunsRatioPerTier = t.RunsRatioPerTier?.ToBand() ?? d.RunsRatioPerTier,
        };
    }

    public static MatchupGridTargets LoadMatchupGridFromFile(string path) => ParseMatchupGrid(File.ReadAllText(path));

    public static MatchupGridTargets ParseMatchupGrid(string yaml)
    {
        var t = Root(yaml).MatchupGrid;
        if (t is null) return new MatchupGridTargets();
        var d = new MatchupGridTargets();
        return new MatchupGridTargets
        {
            StrikeoutRateP80B30 = t.StrikeoutRateP80B30?.ToBand() ?? d.StrikeoutRateP80B30,
            StrikeoutRateP80B45 = t.StrikeoutRateP80B45?.ToBand() ?? d.StrikeoutRateP80B45,
            StrikeoutRateP90B30 = t.StrikeoutRateP90B30?.ToBand() ?? d.StrikeoutRateP90B30,
            StrikeoutRateP50B45 = t.StrikeoutRateP50B45?.ToBand() ?? d.StrikeoutRateP50B45,
            AverageP35B75 = t.AverageP35B75?.ToBand() ?? d.AverageP35B75,
            AverageP80B30 = t.AverageP80B30?.ToBand() ?? d.AverageP80B30,
            WalkRateP80B30 = t.WalkRateP80B30?.ToBand() ?? d.WalkRateP80B30,
            WalkRateP90B75 = t.WalkRateP90B75?.ToBand() ?? d.WalkRateP90B75,
            WalkRateP50B45 = t.WalkRateP50B45?.ToBand() ?? d.WalkRateP50B45,
        };
    }

    public static NationDominanceTargets LoadNationDominanceFromFile(string path) => ParseNationDominance(File.ReadAllText(path));

    public static NationDominanceTargets ParseNationDominance(string yaml)
    {
        var t = Root(yaml).NationDominance;
        if (t is null) return new NationDominanceTargets();
        var d = new NationDominanceTargets();
        return new NationDominanceTargets
        {
            MaxChampionCountMedian = t.MaxChampionCountMedian?.ToBand() ?? d.MaxChampionCountMedian,
            RegionsWithTenPlus = t.RegionsWithTenPlus?.ToBand() ?? d.RegionsWithTenPlus,
            MaxChampionCountMax = t.MaxChampionCountMax?.ToBand() ?? d.MaxChampionCountMax,
            TopTwoWinProbabilityMean = t.TopTwoWinProbabilityMean?.ToBand() ?? d.TopTwoWinProbabilityMean,
        };
    }

    private static RootDto Root(string yaml)
        => Deserializer.Deserialize<RootDto>(yaml)
            ?? throw new InvalidDataException("balance-targets.yaml が空です。");

    private sealed class RootDto
    {
        [YamlMember(Alias = "atbat_10k")]
        public AtBatDto? Atbat10k { get; set; }

        [YamlMember(Alias = "games_10k")]
        public GamesDto? Games10k { get; set; }

        [YamlMember(Alias = "games_10k_tactics")]
        public GamesTacticsDto? Games10kTactics { get; set; }

        [YamlMember(Alias = "games_10k_summer")]
        public GamesDto? Games10kSummer { get; set; }

        [YamlMember(Alias = "tiers_low")]
        public TiersLowDto? TiersLow { get; set; }

        [YamlMember(Alias = "matchup_grid")]
        public MatchupGridDto? MatchupGrid { get; set; }

        [YamlMember(Alias = "nation_dominance")]
        public NationDominanceDto? NationDominance { get; set; }
    }

    private sealed class NationDominanceDto
    {
        public BandDto? MaxChampionCountMedian { get; set; }
        public BandDto? RegionsWithTenPlus { get; set; }
        public BandDto? MaxChampionCountMax { get; set; }
        public BandDto? TopTwoWinProbabilityMean { get; set; }
    }

    private sealed class MatchupGridDto
    {
        public BandDto? StrikeoutRateP80B30 { get; set; }
        public BandDto? StrikeoutRateP80B45 { get; set; }
        public BandDto? StrikeoutRateP90B30 { get; set; }
        public BandDto? StrikeoutRateP50B45 { get; set; }
        public BandDto? AverageP35B75 { get; set; }
        public BandDto? AverageP80B30 { get; set; }
        public BandDto? WalkRateP80B30 { get; set; }
        public BandDto? WalkRateP90B75 { get; set; }
        public BandDto? WalkRateP50B45 { get; set; }
    }

    private sealed class TiersLowDto
    {
        public BandDto? ManagerRunsVsE40 { get; set; }
        public BandDto? ManagerRunsVsD50 { get; set; }
        public BandDto? ManagerRunsVsC60 { get; set; }
        public BandDto? ManagerShutoutRateVsD50 { get; set; }
        public BandDto? ManagerShutoutRateVsC60 { get; set; }
        public BandDto? RunsRatioPerTier { get; set; }
    }

    private sealed class AtBatDto
    {
        public BandDto? BattingAverage { get; set; }
        public BandDto? StrikeoutRate { get; set; }
        public BandDto? WalkRate { get; set; }
        public BandDto? HomeRunRate { get; set; }
        public BandDto? Slugging { get; set; }
        public BandDto? DoubleRate { get; set; }
        public BandDto? TripleRate { get; set; }
        // YAML 鍵は doubles_per_homerun（UnderscoredNamingConvention だと doubles_per_home_run になり束縛されず、
        // C#既定 1.30–2.60 が黙って使われていた。2026-08-18 に本塁打帯の再校正で発覚）。
        [YamlMember(Alias = "doubles_per_homerun")]
        public BandDto? DoublesPerHomeRun { get; set; }
    }

    private sealed class GamesDto
    {
        public BandDto? RunsPerTeam { get; set; }
        public BandDto? PitchesPerGame { get; set; }
        public BandDto? MinutesPerGame { get; set; }
        public BandDto? InningsPerGame { get; set; }
        public BandDto? HomePlayOutsPerGame { get; set; }
        public BandDto? ThirdPlayOutsPerGame { get; set; }
        public BandDto? DoublePlayPerGame { get; set; }
        public BandDto? ErrorsPerGame { get; set; }
        public BandDto? OutfieldErrorShare { get; set; }
        public BandDto? FieldersChoicePerGame { get; set; }
        public BandDto? DroppedThirdStrikePerGame { get; set; }
        public BandDto? ErrorExtraAdvancePerGame { get; set; }
        public BandDto? WildPitchPerGame { get; set; }
    }

    private sealed class GamesTacticsDto
    {
        public BandDto? RunsPerTeam { get; set; }
        public BandDto? PickoffPerGame { get; set; }
        public BandDto? IntentionalWalkPerGame { get; set; }
        public BandDto? DoubleStealThirdBreakPerGame { get; set; }
        public BandDto? StealAttemptPerGame { get; set; }
        public BandDto? StealSuccessRate { get; set; }
        public BandDto? StrikeoutRate { get; set; }
        public BandDto? WalkRate { get; set; }
        public BandDto? HomeRunRate { get; set; }
    }

    private sealed class BandDto
    {
        public double Min { get; set; }
        public double Max { get; set; }
        public Band ToBand() => new(Min, Max);
    }
}
