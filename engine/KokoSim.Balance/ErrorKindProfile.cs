using System.Globalization;
using System.Text;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Timeline;

namespace KokoSim.Balance;

public static partial class GameSimulation
{
    /// <summary>
    /// 失策の種類別の出現数（issue #812。エラー表現4本の共通の物差し）。実試合（GameEngine）の打席ログから数える。
    /// お手玉・捕球・トンネル・悪送球・フライ落球と、悪送球を拾い直してアウトにした打席、悪送球の逸れ幅分布と逸れ方の内訳。
    /// </summary>
    public static string RunErrorKinds(int games, ulong seed)
    {
        var ctx = BuildContext(null, null) with { Weather = null };
        var root = new Xoshiro256Random(seed);
        var forks = new IRandomSource[games];
        for (var i = 0; i < games; i++) forks[i] = root.Fork((ulong)i);

        var counts = new long[8];   // Bobble, Catch, Tunnel, Throw, FlyDrop, Other, ThrowRecovered, PA
        var styleCounts = new long[3];
        var scatterEdges = new[] { 0.0, 3, 5, 7, 10, 15, 20, 999 };
        var scatterHist = new long[scatterEdges.Length - 1];
        var flyByPos = new long[9];
        var flyChancesByPos = new long[9];
        var gate = new object();
        Parallel.For(0, games, i =>
        {
            var g = forks[i];
            var away = GenerateTeam(g, "遠征校");
            var home = GenerateTeam(g, "地元校");
            var r = GameEngine.Play(away, home, ctx, g);
            var local = new long[8];
            var ls = new long[3];
            var lh = new long[scatterHist.Length];
            var lf = new long[9];
            var lfc = new long[9];
            foreach (var e in r.Log)
            {
                local[7]++;
                if (e.BattedIsFly && e.FieldedBy is { } fpos && (int)fpos < 9
                    && e.Result is Engine.Match.AtBat.PlateAppearanceResult.InPlayOut or Engine.Match.AtBat.PlateAppearanceResult.ReachedOnError)
                {
                    lfc[(int)fpos]++;
                    if (e.Result == Engine.Match.AtBat.PlateAppearanceResult.ReachedOnError) lf[(int)fpos]++;
                }
                if (e.ThrowRecovered) local[6]++;
                if (e.Result != Engine.Match.AtBat.PlateAppearanceResult.ReachedOnError) continue;
                var k = e.ErrorKind switch
                {
                    FieldingErrorKind.Bobble => 0,
                    FieldingErrorKind.Catch when e.BattedIsFly => 4,   // フライ落球は捕球エラーの空中版
                    FieldingErrorKind.Catch => 1,
                    FieldingErrorKind.Tunnel => 2,
                    FieldingErrorKind.Throw => 3,
                    _ => 5,
                };
                local[k]++;
                if (e.WildThrowScatterM is { } sc)
                {
                    ls[(int)WildThrowStyles.Classify(sc)]++;
                    for (var b = 0; b < lh.Length; b++)
                        if (sc >= scatterEdges[b] && sc < scatterEdges[b + 1]) { lh[b]++; break; }
                }
            }
            lock (gate)
            {
                for (var k = 0; k < counts.Length; k++) counts[k] += local[k];
                for (var k = 0; k < 3; k++) styleCounts[k] += ls[k];
                for (var k = 0; k < lh.Length; k++) scatterHist[k] += lh[k];
                for (var k = 0; k < 9; k++) { flyByPos[k] += lf[k]; flyChancesByPos[k] += lfc[k]; }
            }
        });

        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        var errors = counts[0] + counts[1] + counts[2] + counts[3] + counts[4] + counts[5];
        sb.AppendLine(c, $"# 失策の種類別（seed {seed}・{games}試合・{counts[7]}打席）");
        sb.AppendLine();
        sb.AppendLine(c, $"失策出塁 {errors}（1試合 {(double)errors / games:F2}）");
        sb.AppendLine();
        sb.AppendLine("| 種類 | 件数 | 1試合あたり | 失策に占める |");
        sb.AppendLine("|---|---:|---:|---:|");
        string[] names = { "お手玉（もたつき）", "捕球（ゴロを弾く）", "トンネル", "悪送球", "フライ落球", "その他（種類なし）" };
        for (var k = 0; k < 6; k++)
            sb.AppendLine(c, $"| {names[k]} | {counts[k]} | {(double)counts[k] / games:F3} | {(errors == 0 ? 0 : (double)counts[k] / errors):P1} |");
        sb.AppendLine(c, $"| （参考）悪送球を拾い直してアウト | {counts[6]} | {(double)counts[6] / games:F3} | — |");
        sb.AppendLine();
        sb.AppendLine("## フライ落球の守備位置別（空中で処理した打球＝捕球アウト＋落球）");
        sb.AppendLine();
        sb.AppendLine("| 位置 | 空中処理 | 落球 | 落球率 | 1試合あたり |");
        sb.AppendLine("|---|---:|---:|---:|---:|");
        for (var k = 0; k < 9; k++)
            sb.AppendLine(c, $"| {(Engine.Match.Field.FieldPosition)k} | {flyChancesByPos[k]} | {flyByPos[k]} | {(flyChancesByPos[k] == 0 ? 0 : (double)flyByPos[k] / flyChancesByPos[k]):P2} | {(double)flyByPos[k] / games:F3} |");
        sb.AppendLine();
        sb.AppendLine("## 悪送球の逸れ幅");
        sb.AppendLine();
        sb.AppendLine("| 逸れ幅[m] | 件数 |");
        sb.AppendLine("|---|---:|");
        for (var b = 0; b < scatterHist.Length; b++)
            sb.AppendLine(c, $"| {scatterEdges[b]:0}–{(scatterEdges[b + 1] > 900 ? "" : scatterEdges[b + 1].ToString("0", c))} | {scatterHist[b]} |");
        sb.AppendLine();
        sb.AppendLine("## 悪送球の逸れ方（WildThrowStyles）");
        sb.AppendLine();
        var tot = styleCounts.Sum();
        string[] sn = { "手前で弾む", "横へ逸れる", "高く抜ける" };
        for (var k = 0; k < 3; k++)
            sb.AppendLine(c, $"- {sn[k]}: {styleCounts[k]}（{(tot == 0 ? 0 : (double)styleCounts[k] / tot):P1}）");
        return sb.ToString();
    }
}
