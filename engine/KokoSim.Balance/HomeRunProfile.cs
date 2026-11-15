using System.Globalization;
using System.Text;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Balance;

/// <summary>
/// 本塁打の出方の集計（issue #1014・#976 の前段の実測）。**観測だけ**＝乱数を引かず、試合結果も変えない。
///
/// <para>帯（<c>home_run_rate</c>）は打席あたりの平均しか見ていないので、「1試合で両軍10本」のような外れの多さが
/// 測れない。ここでは次の3つを数える:</para>
/// <list type="number">
///   <item>1試合（両軍計）の本塁打数の分布（0〜<see cref="MaxPerGame"/>本・最大値）とチーム得点の分布（0〜9点・10点以上）</item>
///   <item>本塁打の打球の上側の内訳: 初速[km/h]・打球角度[度]の分布</item>
///   <item>フェンス上の余裕（フェンス上の通過高さ − フェンス高）の分布＝「フェンス際」の本塁打の割合</item>
/// </list>
/// すべて整数カウンタ＝並列マージの順に依らず決定論（<see cref="Merge"/>）。
/// </summary>
public sealed class HomeRunProfile
{
    /// <summary>1試合の本塁打数のヒストグラムの上限（これ以上はこの箱にまとめる）。</summary>
    public const int MaxPerGame = 20;

    /// <summary>チーム得点の分布の上限（10点以上はこの箱）。</summary>
    public const int RunsCap = 10;

    /// <summary>本塁打の初速の箱: 下端[km/h]と幅。0番＝下端未満、最後＝上端以上。</summary>
    public const double EvLowKmh = 140.0, EvStepKmh = 5.0;
    public const int EvBins = 14;   // <140, 140-145, …, 200-205, ≥205 を 14 箱に（0=未満、13=以上）

    /// <summary>本塁打の打球角度の箱: 下端[度]と幅。</summary>
    public const double LaLowDeg = 15.0, LaStepDeg = 5.0;
    public const int LaBins = 10;   // <15, 15-20, …, 50-55, ≥55

    /// <summary>フェンス上の余裕[m]の箱の境界（[0,1) [1,2) [2,4) [4,6) [6,10) [10,∞)。負は来ない＝本塁打の定義）。</summary>
    public static readonly double[] ClearanceEdgesM = { 1.0, 2.0, 4.0, 6.0, 10.0 };

    /// <summary>「フェンス際」の上限[m]（issue #1014: フェンス上の高さ−フェンス高 が 0〜2m）。</summary>
    public const double FenceEdgeM = 2.0;

    public long Games { get; private set; }
    public long PlateAppearances { get; private set; }
    public long HomeRuns { get; private set; }
    public long Doubles { get; private set; }
    public long Triples { get; private set; }
    /// <summary>安打（単打〜本塁打）と打数（四死球を除く打席。犠打・犠飛は区別せず含める＝近似）。打率の比較用（issue #976）。</summary>
    public long Hits { get; private set; }
    public long AtBats { get; private set; }
    public long TeamRuns { get; private set; }
    public int MaxHomeRunsInGame { get; private set; }

    /// <summary>添字＝1試合（両軍計）の本塁打数（<see cref="MaxPerGame"/> 以上はまとめる）。</summary>
    public long[] PerGame { get; } = new long[MaxPerGame + 1];

    /// <summary>添字＝チーム得点（<see cref="RunsCap"/> 以上はまとめる）。1試合で2件（両軍）入る。</summary>
    public long[] RunsPerTeam { get; } = new long[RunsCap + 1];

    public long[] EvHist { get; } = new long[EvBins];
    public long[] LaHist { get; } = new long[LaBins];
    public long[] ClearanceHist { get; } = new long[ClearanceEdgesM.Length + 1];

    /// <summary>初速・角度を拾えた本塁打（打球の物理層を持つもの）。</summary>
    public long WithBall { get; private set; }
    /// <summary>フェンス上の通過高さを拾えた本塁打（柵越え）。</summary>
    public long WithClearance { get; private set; }
    /// <summary>初速の合計（平均用。0.1km/h 単位の整数で積む＝マージ順に依らない）。</summary>
    public long EvSumDeci { get; private set; }
    public long LaSumDeci { get; private set; }

    // ── 1試合ぶん（既知の数から積む＝テストの入口） ──

    /// <summary>1試合ぶんの数を積む。</summary>
    public void AddGame(int homeRunsInGame, int awayRuns, int homeRuns, int plateAppearances,
        int doubles = 0, int triples = 0, int hits = 0, int atBats = 0)
    {
        Games++;
        Hits += hits;
        AtBats += atBats;
        PlateAppearances += plateAppearances;
        HomeRuns += homeRunsInGame;
        Doubles += doubles;
        Triples += triples;
        TeamRuns += awayRuns + homeRuns;
        PerGame[Math.Min(homeRunsInGame, MaxPerGame)]++;
        if (homeRunsInGame > MaxHomeRunsInGame) MaxHomeRunsInGame = homeRunsInGame;
        RunsPerTeam[Math.Min(awayRuns, RunsCap)]++;
        RunsPerTeam[Math.Min(homeRuns, RunsCap)]++;
    }

    /// <summary>本塁打1本の打球を積む（null は拾えなかった値＝その分布には入れない）。</summary>
    public void AddHomeRunBall(double? exitVelocityKmh, double? launchAngleDeg, double? fenceClearanceM)
    {
        if (exitVelocityKmh is { } ev && launchAngleDeg is { } la)
        {
            WithBall++;
            EvHist[Bin(ev, EvLowKmh, EvStepKmh, EvBins)]++;
            LaHist[Bin(la, LaLowDeg, LaStepDeg, LaBins)]++;
            EvSumDeci += (long)Math.Round(ev * 10.0);
            LaSumDeci += (long)Math.Round(la * 10.0);
        }
        if (fenceClearanceM is { } cl)
        {
            WithClearance++;
            var k = 0;
            while (k < ClearanceEdgesM.Length && cl >= ClearanceEdgesM[k]) k++;
            ClearanceHist[k]++;
        }
    }

    /// <summary>
    /// 試合結果から積む（打席ログを読むだけ）。<paramref name="field"/> はその試合の球場（フェンス高の基準）。
    /// 初速・角度は打席の最後の球（インプレー）の <see cref="PitchRecord.BattedBall"/>、
    /// フェンス上の通過高さは <see cref="PlayLogEntry.FenceCrossHeightM"/> から取る。
    /// </summary>
    public void AddGame(GameResult r, FieldGeometry field)
    {
        int hr = 0, doubles = 0, triples = 0, hits = 0, atBats = 0;
        foreach (var e in r.Log)
        {
            if (e.Result.IsHit()) hits++;
            if (e.Result.IsAtBat()) atBats++;
            switch (e.Result)
            {
                case PlateAppearanceResult.Double: doubles++; break;
                case PlateAppearanceResult.Triple: triples++; break;
                case PlateAppearanceResult.HomeRun:
                    hr++;
                    var ball = e.PitchLog is { Count: > 0 } pl ? pl[^1].BattedBall : null;
                    AddHomeRunBall(ball?.ExitVelocityKmh, ball?.LaunchAngleDeg,
                        e.FenceCrossHeightM is { } h ? h - field.FenceHeightM : null);
                    break;
            }
        }
        AddGame(hr, r.AwayRuns, r.HomeRuns, r.Log.Count, doubles, triples, hits, atBats);
    }

    public void Merge(HomeRunProfile o)
    {
        Games += o.Games;
        PlateAppearances += o.PlateAppearances;
        HomeRuns += o.HomeRuns;
        Doubles += o.Doubles;
        Triples += o.Triples;
        Hits += o.Hits;
        AtBats += o.AtBats;
        TeamRuns += o.TeamRuns;
        if (o.MaxHomeRunsInGame > MaxHomeRunsInGame) MaxHomeRunsInGame = o.MaxHomeRunsInGame;
        Add(PerGame, o.PerGame);
        Add(RunsPerTeam, o.RunsPerTeam);
        Add(EvHist, o.EvHist);
        Add(LaHist, o.LaHist);
        Add(ClearanceHist, o.ClearanceHist);
        WithBall += o.WithBall;
        WithClearance += o.WithClearance;
        EvSumDeci += o.EvSumDeci;
        LaSumDeci += o.LaSumDeci;
    }

    // ── 導出値 ──

    public double HomeRunRate => PlateAppearances > 0 ? (double)HomeRuns / PlateAppearances : 0;
    public double HomeRunsPerGame => Games > 0 ? (double)HomeRuns / Games : 0;
    public double RunsPerTeamAverage => Games > 0 ? (double)TeamRuns / (2.0 * Games) : 0;
    public double PlateAppearancesPerGame => Games > 0 ? (double)PlateAppearances / Games : 0;
    public double BattingAverage => AtBats > 0 ? (double)Hits / AtBats : 0;
    public double DoubleRate => PlateAppearances > 0 ? (double)Doubles / PlateAppearances : 0;
    public double TripleRate => PlateAppearances > 0 ? (double)Triples / PlateAppearances : 0;
    public double DoublesPerHomeRun => HomeRuns > 0 ? (double)Doubles / HomeRuns : 0;
    public double MeanEvKmh => WithBall > 0 ? EvSumDeci / 10.0 / WithBall : 0;
    public double MeanLaDeg => WithBall > 0 ? LaSumDeci / 10.0 / WithBall : 0;

    /// <summary>1試合（両軍計）で <paramref name="k"/> 本ちょうどの試合の割合（k=<see cref="MaxPerGame"/> は以上）。</summary>
    public double ShareExactly(int k) => Games > 0 ? (double)PerGame[Math.Min(k, MaxPerGame)] / Games : 0;

    /// <summary>1試合（両軍計）で <paramref name="k"/> 本以上の試合数。</summary>
    public long GamesAtLeast(int k)
    {
        long n = 0;
        for (var i = Math.Min(k, MaxPerGame); i <= MaxPerGame; i++) n += PerGame[i];
        return n;
    }

    public double ShareAtLeast(int k) => Games > 0 ? (double)GamesAtLeast(k) / Games : 0;

    /// <summary>チーム得点が <paramref name="runs"/> 点（<see cref="RunsCap"/> は以上）の割合。</summary>
    public double RunsShare(int runs) => Games > 0 ? (double)RunsPerTeam[Math.Min(runs, RunsCap)] / (2.0 * Games) : 0;

    /// <summary>フェンス際（余裕 0〜<see cref="FenceEdgeM"/>m）の本塁打の割合（柵越えの中で）。</summary>
    public double FenceEdgeShare => WithClearance > 0 ? (double)(ClearanceHist[0] + ClearanceHist[1]) / WithClearance : 0;

    /// <summary>
    /// 独立打席の二項近似（1試合 <see cref="PlateAppearancesPerGame"/> 打席・各打席 <see cref="HomeRunRate"/>）で
    /// <paramref name="k"/> 本以上になる確率。実測との差＝チーム力・球場・気温のばらつきによる「外れの太さ」。
    /// </summary>
    public double BinomialAtLeast(int k)
    {
        var n = (int)Math.Round(PlateAppearancesPerGame);
        var p = HomeRunRate;
        return n <= 0 || p <= 0 ? 0 : AtBatSimulation.BinomialAtLeast(n, p, k);
    }

    // ── 表示 ──

    /// <summary>「N試合に1回」の表記（0件なら 95% 上限＝3/N の目安を添える）。</summary>
    public string FrequencyLabel(int k)
    {
        var c = CultureInfo.InvariantCulture;
        var n = GamesAtLeast(k);
        if (Games == 0) return "—";
        if (n == 0) return string.Create(c, $"0/{Games}（95%上限 {3.0 / Games:P2}）");
        return string.Create(c, $"{n}/{Games}（{(double)Games / n:F0}試合に1回）");
    }

    /// <summary>初速・角度・フェンス上の余裕の分布表（Markdown）。</summary>
    public string BattedBallMarkdown()
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(c, $"本塁打の打球 {WithBall} 本（柵越えの高さを拾えたもの {WithClearance} 本）。"
            + $"平均初速 {MeanEvKmh:F1} km/h・平均角度 {MeanLaDeg:F1}°・フェンス際（余裕0〜{FenceEdgeM:F0}m）{FenceEdgeShare:P1}");
        sb.AppendLine();
        sb.AppendLine("| 初速[km/h] | 本数 | 割合 |");
        sb.AppendLine("|---|---:|---:|");
        for (var i = 0; i < EvBins; i++)
        {
            if (EvHist[i] == 0) continue;
            sb.AppendLine(c, $"| {BinLabel(i, EvLowKmh, EvStepKmh, EvBins)} | {EvHist[i]} | {Share(EvHist[i], WithBall):P1} |");
        }
        sb.AppendLine();
        sb.AppendLine("| 打球角度[度] | 本数 | 割合 |");
        sb.AppendLine("|---|---:|---:|");
        for (var i = 0; i < LaBins; i++)
        {
            if (LaHist[i] == 0) continue;
            sb.AppendLine(c, $"| {BinLabel(i, LaLowDeg, LaStepDeg, LaBins)} | {LaHist[i]} | {Share(LaHist[i], WithBall):P1} |");
        }
        sb.AppendLine();
        sb.AppendLine("| フェンス上の余裕[m]（通過高さ−フェンス高） | 本数 | 割合 |");
        sb.AppendLine("|---|---:|---:|");
        for (var i = 0; i < ClearanceHist.Length; i++)
        {
            var lo = i == 0 ? 0.0 : ClearanceEdgesM[i - 1];
            var label = i < ClearanceEdgesM.Length
                ? string.Create(c, $"{lo:F0}〜{ClearanceEdgesM[i]:F0}")
                : string.Create(c, $"{lo:F0}以上");
            sb.AppendLine(c, $"| {label} | {ClearanceHist[i]} | {Share(ClearanceHist[i], WithClearance):P1} |");
        }
        return sb.ToString();
    }

    /// <summary>1試合（両軍計）の本塁打数の分布表（Markdown・0〜4本と5本以上・平均・最大）。</summary>
    public string PerGameMarkdown()
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(c, $"{Games} 試合・本塁打 {HomeRuns} 本・本塁打率（打席あたり）{HomeRunRate:P2}・"
            + $"平均 {HomeRunsPerGame:F2} 本/試合（両軍計）・最大 {MaxHomeRunsInGame} 本");
        sb.AppendLine();
        sb.AppendLine("| 1試合の本塁打（両軍計） | 0本 | 1本 | 2本 | 3本 | 4本 | 5本以上 | 10本以上 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        sb.AppendLine(c, $"| 割合 | {ShareExactly(0):P1} | {ShareExactly(1):P1} | {ShareExactly(2):P1} | {ShareExactly(3):P1} "
            + $"| {ShareExactly(4):P1} | {ShareAtLeast(5):P2} | {ShareAtLeast(10):P3} |");
        sb.AppendLine(c, $"| 独立打席の二項近似 | — | — | — | — | — | {BinomialAtLeast(5):P2} | {BinomialAtLeast(10):P4} |");
        return sb.ToString();
    }

    private static int Bin(double v, double low, double step, int bins)
    {
        if (v < low) return 0;
        var k = 1 + (int)Math.Floor((v - low) / step);
        return Math.Min(k, bins - 1);
    }

    private static string BinLabel(int i, double low, double step, int bins)
    {
        var c = CultureInfo.InvariantCulture;
        if (i == 0) return string.Create(c, $"{low:F0}未満");
        var lo = low + (i - 1) * step;
        return i == bins - 1 ? string.Create(c, $"{lo:F0}以上") : string.Create(c, $"{lo:F0}〜{lo + step:F0}");
    }

    private static double Share(long n, long d) => d > 0 ? (double)n / d : 0;

    private static void Add(long[] a, long[] b)
    {
        for (var i = 0; i < a.Length; i++) a[i] += b[i];
    }
}
