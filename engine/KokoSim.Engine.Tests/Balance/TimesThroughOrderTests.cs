using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Balance;

/// <summary>
/// 打者の「慣れ」の統計（issue times-through-order-familiarity）: 先発投手に対する 1巡目 vs 3巡目の被OPS。
/// 実測（帯値はオーナー承認待ち＝ここでは「正で、控えめ」だけを固定する）。
/// </summary>
[Trait("Category", "Heavy")]   // 3000試合のフルエンジン
public sealed class TimesThroughOrderTests
{
    private readonly ITestOutputHelper _out;
    public TimesThroughOrderTests(ITestOutputHelper output) => _out = output;

    private sealed class Acc
    {
        public int Pa, Ab, H, Tb, Bb, So;
        public double Obp => Pa > 0 ? (double)(H + Bb) / Pa : 0;
        public double Slg => Ab > 0 ? (double)Tb / Ab : 0;
        public double Ops => Obp + Slg;
        public double K => Pa > 0 ? (double)So / Pa : 0;
    }

    [Fact]
    public void ThirdTimeThrough_OpsExceedsFirst_Modestly()
    {
        // ⚠ 600シードでは足りない（実測 2026-08-30: 送球逸れの回収レース導入でrng列がずれ delta=-.003 に逆転。
        // 1200シードでも±.000で拮抗）。慣れの効果自体が小さく、乱数列がずれると試合ごとの揺れに埋もれる
        // ＝母数で殺す（3000シード）。
        var byPass = new[] { new Acc(), new Acc(), new Acc(), new Acc() };   // 添字＝巡目−1（4巡目以降は3へ畳む）
        for (ulong seed = 1; seed <= 3000; seed++)
        {
            var away = StrengthTeamFactory.Create(50, "A" + seed, new Xoshiro256Random(seed * 3));
            var home = StrengthTeamFactory.Create(50, "H" + seed, new Xoshiro256Random(seed * 3 + 1));
            var r = GameEngine.Play(away, home, new GameContext(), new Xoshiro256Random(seed));
            var seen = new Dictionary<(string Batter, string Pitcher), int>();
            foreach (var e in r.Log)
            {
                var key = (e.BatterName + "|" + (e.IsTop ? "T" : "B"), e.PitcherName ?? "");
                var n = seen.TryGetValue(key, out var v) ? v : 0;
                seen[key] = n + 1;
                var acc = byPass[System.Math.Min(n, 3)];
                acc.Pa++;
                if (e.Result.IsAtBat()) acc.Ab++;
                if (e.Result.IsHit()) acc.H++;
                acc.Tb += e.Result.TotalBases();
                if (e.Result is PlateAppearanceResult.Walk or PlateAppearanceResult.HitByPitch) acc.Bb++;
                if (e.Result == PlateAppearanceResult.Strikeout) acc.So++;
            }
        }
        for (var i = 0; i < 4; i++)
            _out.WriteLine($"{i + 1}巡目{(i == 3 ? "以降" : "")}: PA {byPass[i].Pa} OBP {byPass[i].Obp:.000} SLG {byPass[i].Slg:.000} OPS {byPass[i].Ops:.000} K% {byPass[i].K:P1}");

        var first = byPass[0]; var third = byPass[2];
        var delta = third.Ops - first.Ops;
        _out.WriteLine($"3巡目 − 1巡目 OPS = {delta:+.000;-.000}");
        Assert.True(third.Pa > 5000, "3巡目の母集団が薄い");
        Assert.True(delta > 0, $"3巡目の被OPSが1巡目を上回らない（{delta:+.000;-.000}）");
        Assert.True(delta < 0.120, $"慣れが効きすぎ（{delta:+.000;-.000}）");
    }
}
