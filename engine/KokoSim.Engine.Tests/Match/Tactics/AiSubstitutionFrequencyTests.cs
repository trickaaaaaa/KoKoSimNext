using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Nation;
using Xunit;
using Xunit.Abstractions;

namespace KokoSim.Engine.Tests.Match.Tactics;

/// <summary>
/// 相手校（敵AI）の交代活動量をティア別に実測する（issue #1046）。両軍とも同ティアの <see cref="EnemyAiFactory"/>
/// 駆動で N 試合回し、1校1試合あたりの「投手交代・代打・代走・守備固め」の回数を集計する。
/// 裏試合フルシムも自校戦も同じ <see cref="AiTacticsBrain"/>（<see cref="EnemyAiBrainFactory"/>）なので、
/// この計測が両経路を代表する。<see cref="StandardTacticsBrain"/> 駆動の統計帯（games_10k）は別経路＝ここでは触れない。
/// </summary>
[Trait("Category", "Heavy")]
public sealed class AiSubstitutionFrequencyTests
{
    private readonly ITestOutputHelper _out;

    public AiSubstitutionFrequencyTests(ITestOutputHelper output) => _out = output;

    // ティアごとの代表強さ（Tiers.FromStrength の帯の中央寄り）。
    private static readonly (string Tier, double Strength)[] TierPoints =
    {
        ("G", 25), ("F", 35), ("E", 45), ("D", 55), ("C", 65), ("B", 75), ("A", 85), ("S", 92),
    };

    private const int GamesPerTier = 200;

    /// <summary>1校1試合あたりの平均交代回数（投手交代/代打/代走/守備固め）。</summary>
    private readonly record struct Freq(double Relief, double PinchHit, double PinchRun, double DefSub);

    /// <summary>
    /// ティア <paramref name="strength"/> の両軍 AI で <paramref name="games"/> 試合回し、1校1試合あたりの
    /// 種別別交代回数を返す（両軍＝2校ぶんを母数にする）。采配能力はティアに比例させ「上級ほど的確」を反映する。
    /// </summary>
    private static Freq Measure(double strength, int tierRank, int games, ulong seed0)
    {
        // CapturePitches=true で交代ログ（観測専用）を埋める。試合結果・乱数消費は不変（GameEngine 束縛窓のコメント）。
        var ctx = new GameContext { CapturePitches = true };
        var sense = (int)MathUtil.Clamp(20 + tierRank * 10, 1, 99); // G=20 〜 S=90
        long relief = 0, pinchHit = 0, pinchRun = 0, defSub = 0;
        var lk = new object();

        Parallel.For(0, games, () => (R: 0L, PH: 0L, PR: 0L, DS: 0L), (g, _, acc) =>
        {
            var rng = new Xoshiro256Random(seed0 + (ulong)g);
            var away = StrengthTeamFactory.Create(strength, "AwayAI", rng) with
            {
                Tactics = EnemyAiFactory.BrainFor(School(strength, tierRank, sense, id: 1)),
            };
            var home = StrengthTeamFactory.Create(strength, "HomeAI", rng) with
            {
                Tactics = EnemyAiFactory.BrainFor(School(strength, tierRank, sense, id: 2)),
            };
            var r = GameEngine.Play(away, home, ctx, rng);
            foreach (var ev in r.SubstitutionLog)
            {
                foreach (var e in ev.Entries)
                {
                    switch (e.Kind)
                    {
                        case SubstitutionEntryKind.Relief: acc.R++; break;
                        case SubstitutionEntryKind.PinchHit: acc.PH++; break;
                        case SubstitutionEntryKind.PinchRun: acc.PR++; break;
                        case SubstitutionEntryKind.DefensiveSub: acc.DS++; break;
                    }
                }
            }
            return acc;
        }, acc =>
        {
            lock (lk) { relief += acc.R; pinchHit += acc.PH; pinchRun += acc.PR; defSub += acc.DS; }
        });

        double teamGames = games * 2.0; // 両軍＝2校ぶん
        return new Freq(relief / teamGames, pinchHit / teamGames, pinchRun / teamGames, defSub / teamGames);
    }

    private static School School(double strength, int tierRank, int sense, int id)
        => new() { Id = id, Name = $"T{tierRank}", PrefectureId = 0, Strength = strength, Style = SchoolStyle.Standard, TacticalSense = sense };

    private static string BuildTable(IReadOnlyList<(string Tier, Freq F)> rows)
    {
        var sb = new StringBuilder();
        sb.AppendLine("| ティア | 投手交代 | 代打 | 代走 | 守備固め |");
        sb.AppendLine("|---|---:|---:|---:|---:|");
        foreach (var (tier, f) in rows)
        {
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture,
                "| {0} | {1:F2} | {2:F2} | {3:F2} | {4:F2} |", tier, f.Relief, f.PinchHit, f.PinchRun, f.DefSub));
        }
        return sb.ToString();
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "data", "coefficients.yaml")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    [Fact]
    public void Measure_And_Report()
    {
        var rows = new List<(string Tier, Freq F)>();
        for (var i = 0; i < TierPoints.Length; i++)
        {
            var (tier, strength) = TierPoints[i];
            rows.Add((tier, Measure(strength, tierRank: i, GamesPerTier, seed0: 0xA1_0000UL + (ulong)i * 1000)));
        }

        var table = BuildTable(rows);
        _out.WriteLine(table);

        // 計測表をファイルへ（PR 貼付用。CI の作業ディレクトリでは失敗しうるので best-effort）。
        try
        {
            var outDir = Path.Combine(RepoRoot(), "out");
            Directory.CreateDirectory(outDir);
            var path = Path.Combine(outDir, "ai-sub-frequency.md");
            File.WriteAllText(path, $"# 敵AI交代頻度（1校1試合あたり・{GamesPerTier}試合×両軍）\n\n{table}\n");
            _out.WriteLine($"written: {path}");
        }
        catch (IOException) { /* 計測成果物の保存失敗はテストの失敗にしない */ }

        // === 目安（issue #1046 のユーザー決定 2026-11-16・現実の高校野球並み） ===
        var by = rows.ToDictionary(r => r.Tier, r => r.F);

        // 投手交代: 全ティア 1〜2回の目安（疲労＋高度トリガーで現実並み）。
        foreach (var (tier, f) in rows)
            Assert.True(f.Relief is >= 0.8 and <= 2.2, $"投手交代が目安(1〜2)外: {tier}={f.Relief:F2}\n{table}");

        // 代打: どのティアも出しすぎない（<=3）。中堅（E/D/C）は現実並みに明確に出す（>=1）。
        foreach (var (tier, f) in rows)
            Assert.True(f.PinchHit <= 3.0, $"代打が多すぎ（目安<=3）: {tier}={f.PinchHit:F2}\n{table}");
        foreach (var tier in new[] { "E", "D", "C" })
            Assert.True(by[tier].PinchHit >= 1.0, $"中堅の代打が少なすぎ（目安>=1）: {tier}={by[tier].PinchHit:F2}\n{table}");

        // 代走: 0〜1の目安（鈍足の走者を終盤に置き換える局面限定）。
        foreach (var (tier, f) in rows)
            Assert.True(f.PinchRun is >= 0.0 and <= 1.0, $"代走が目安(0〜1)外: {tier}={f.PinchRun:F2}\n{table}");

        // ティア差（設計書11 §2 の「引き出し」の差を保つ）:
        // ・高度継投（動揺・僅差終盤）を持つ E は、崩れ火消しだけの F より投手交代が多い。
        Assert.True(by["E"].Relief > by["F"].Relief,
            $"E(高度継投あり)の投手交代が F(崩れのみ)以下＝引き出し差が消えている\n{table}");
        // ・強豪ほど先発が控えを上回り代打の必要が薄い（C > S）。
        Assert.True(by["C"].PinchHit > by["S"].PinchHit,
            $"強豪(S)の代打が中堅(C)以上＝ロスターの厚みが反映されていない\n{table}");
    }
}
