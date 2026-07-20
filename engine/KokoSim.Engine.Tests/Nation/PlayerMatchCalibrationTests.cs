using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using Xunit;

namespace KokoSim.Engine.Tests.Nation;

/// <summary>
/// 未決D（2026-07-18 起票 → 2026-07-20 校正）: 大会の自校カードだけ抽象シム（AggregateMatch）から
/// 実 GameEngine（自校=実ロスター vs 相手=StrengthTeamFactory）へ替えたことによる勝率整合の専用校正。
/// strength差ラダーで非対称マッチ（実ロスター vs 総合力1スカラー）の勝率を実測し、
/// (1) 相手が強いほど勝率が単調に下がる、(2) 等価強さ（勝率50%交点）近傍のロジスティック傾きが
/// 集計モデル AggregateMatch.WinProbability と許容帯内で整合する、ことを固定する。
/// </summary>
public sealed class PlayerMatchCalibrationTests
{
    /// <summary>RosterService.Build と同型の実ロスター（3学年・seed42相当）。</summary>
    private static List<DevelopingPlayer> BuildRoster(ulong seed)
    {
        var rng = new Xoshiro256Random(seed);
        var coefficients = new RosterCoefficients();
        var list = new List<DevelopingPlayer>();
        for (var grade = 1; grade <= 3; grade++)
        {
            foreach (var p in ProspectGenerator.Intake(grade, coefficients, rng))
            {
                p.Grade = grade;
                list.Add(p);
            }
        }
        for (var i = 0; i < list.Count; i++) list[i].Id = i + 1;
        return list;
    }

    [Fact]
    [Trait("Category", "Heavy")] // 実ロスター vs strengthラダーのフルエンジン照合（数十秒）
    public void RosterTeam_VsStrengthLadder_IsMonotonic_AndMatchesAggregateSlope()
    {
        var gameCtx = new GameContext();
        var nation = new NationCoefficients();
        var roster = BuildRoster(42);
        double[] ladder = { 35, 40, 45, 50, 55, 60, 65 };
        const int n = 300;

        var rates = new double[ladder.Length];
        var root = new Xoshiro256Random(7);
        for (var li = 0; li < ladder.Length; li++)
        {
            var wins = 0;
            for (var i = 0; i < n; i++)
            {
                var g = root.Fork((ulong)(li * 1000 + i));
                // 自校=後攻(home)固定（未決I）。相手は総合力1スカラーから決定論生成。
                var away = StrengthTeamFactory.Create(ladder[li], "相手", g);
                var home = RosterTeamBuilder.Build(roster, "自校");
                var r = GameEngine.Play(away, home, gameCtx, g);
                if (r.HomeRuns > r.AwayRuns) wins++;
                else if (r.Tied && g.NextDouble() < 0.5) wins++;
            }
            rates[li] = (double)wins / n;
        }

        // (1) 単調性: 相手が強いほど勝率は下がる（サンプリング誤差の余裕 0.05）。
        for (var i = 1; i < ladder.Length; i++)
        {
            Assert.True(rates[i] <= rates[i - 1] + 0.05,
                $"ラダーが単調でない: s={ladder[i - 1]}→{rates[i - 1]:F3}, s={ladder[i]}→{rates[i]:F3} " +
                $"[{string.Join(", ", rates.Select(r => r.ToString("F3")))}]");
        }

        // (2) 等価強さ（勝率0.5の交点）を線形補間で求め、ラダー各点が集計モデルの予測と整合するか。
        var sEquiv = EstimateEquivalentStrength(ladder, rates);
        Assert.True(sEquiv > ladder[0] && sEquiv < ladder[^1],
            $"等価強さがラダー外: {sEquiv:F1} [{string.Join(", ", rates.Select(r => r.ToString("F3")))}]");

        var maxDiff = 0.0;
        var detail = "";
        for (var i = 0; i < ladder.Length; i++)
        {
            var model = AggregateMatch.WinProbability(sEquiv, ladder[i], nation);
            maxDiff = Math.Max(maxDiff, Math.Abs(model - rates[i]));
            detail += $"s={ladder[i]}: engine={rates[i]:F3} model={model:F3}; ";
        }
        Assert.True(maxDiff <= 0.12,
            $"非対称マッチが集計モデルの傾きから乖離: sEquiv={sEquiv:F1} maxDiff={maxDiff:F3} → {detail}");
    }

    private static double EstimateEquivalentStrength(double[] ladder, double[] rates)
    {
        for (var i = 1; i < ladder.Length; i++)
        {
            if (rates[i - 1] >= 0.5 && rates[i] < 0.5)
            {
                var t = (rates[i - 1] - 0.5) / Math.Max(1e-9, rates[i - 1] - rates[i]);
                return ladder[i - 1] + t * (ladder[i] - ladder[i - 1]);
            }
        }
        // 交点がラダー内に無い場合は端を返す（呼び出し側の範囲アサートで落とす）。
        return rates[0] < 0.5 ? ladder[0] - 1 : ladder[^1] + 1;
    }
}
