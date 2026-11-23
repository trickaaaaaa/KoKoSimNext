using System.Globalization;
using System.Text;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;

namespace KokoSim.Balance;

public static partial class GameSimulation
{
    /// <summary>
    /// 走者二塁だけ（一塁・三塁は空き）のゴロ凡打で、二塁走者が三塁へ走った割合と三塁での憤死率（issue #1418）。
    /// 打球を処理した野手（打球の方向の代わり）×打席前のアウト数で表にする。実試合（GameEngine）の打席ログから数える。
    /// 走った＝打席後に二塁が空いた。憤死＝この打席で2アウト増えた（打者の一塁アウト＋三塁の走塁死）。
    /// </summary>
    public static string RunGrounderAdvance(int games, ulong seed)
    {
        var ctx = BuildContext(null, null) with { Weather = null };
        var root = new Xoshiro256Random(seed);
        var forks = new IRandomSource[games];
        for (var i = 0; i < games; i++) forks[i] = root.Fork((ulong)i);

        // [方向][アウト][0=場面, 1=走った, 2=三塁憤死]
        var dirs = new[] { "一塁方向（一）", "二塁方向（二）", "遊撃方向（遊）", "三塁方向（三・捕）", "投手ゴロ（投）" };
        var counts = new long[dirs.Length, 2, 3];
        var gate = new object();
        Parallel.For(0, games, i =>
        {
            var g = forks[i];
            var away = GenerateTeam(g, "遠征校");
            var home = GenerateTeam(g, "地元校");
            var r = GameEngine.Play(away, home, ctx, g);
            var local = new long[dirs.Length, 2, 3];
            foreach (var e in r.Log)
            {
                if (e.Result != PlateAppearanceResult.InPlayOut || e.BattedIsFly) continue;
                if (!e.BaseSecondBefore || e.BaseFirstBefore || e.BaseThirdBefore || e.OutsBefore > 1) continue;
                var d = e.FieldedBy switch
                {
                    FieldPosition.FirstBase => 0,
                    FieldPosition.Pitcher => 4,
                    FieldPosition.SecondBase => 1,
                    FieldPosition.Shortstop => 2,
                    FieldPosition.ThirdBase or FieldPosition.Catcher => 3,
                    _ => -1,
                };
                if (d < 0) continue;
                // 打者は一塁アウト（OutsThisPa≥1）。二塁が空いた＝三塁へ走った（セーフ or 憤死）。
                var ran = e.OutsThisPa >= 2 || (e.BaseThirdAfter && !e.BaseSecondAfter);
                var stayed = e.OutsThisPa == 1 && e.BaseSecondAfter;
                if (!ran && !stayed) continue;   // 野選・打者生存などこの場面でない記録
                local[d, e.OutsBefore, 0]++;
                if (ran) local[d, e.OutsBefore, 1]++;
                if (e.OutsThisPa >= 2) local[d, e.OutsBefore, 2]++;
            }
            lock (gate)
            {
                for (var a = 0; a < dirs.Length; a++)
                    for (var o = 0; o < 2; o++)
                        for (var k = 0; k < 3; k++) counts[a, o, k] += local[a, o, k];
            }
        });

        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(c, $"# 走者二塁のゴロ凡打：二塁走者の三塁進塁（seed {seed}・{games}試合）");
        sb.AppendLine();
        sb.AppendLine("| 打球の方向 | アウト | 場面 | 走った | 走った割合 | 三塁憤死 | 走った中の憤死率 | 場面あたり憤死 |");
        sb.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");
        for (var a = 0; a < dirs.Length; a++)
            for (var o = 0; o < 2; o++)
            {
                var n = counts[a, o, 0];
                var ran = counts[a, o, 1];
                var outs = counts[a, o, 2];
                sb.AppendLine(c, $"| {dirs[a]} | {o} | {n} | {ran} | {Rate(ran, n):P1} | {outs} | {Rate(outs, ran):P1} | {Rate(outs, n):P1} |");
            }
        return sb.ToString();

        static double Rate(long x, long n) => n == 0 ? 0 : (double)x / n;
    }
}
