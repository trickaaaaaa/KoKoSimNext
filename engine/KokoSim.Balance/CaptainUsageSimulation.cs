using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;

namespace KokoSim.Balance;

/// <summary>
/// 主将の起用ジレンマ（issue #860 C）の一度きりの計測ツール。「①主将スタメン」と
/// 「②最良選手スタメン＋主将ベンチ（伝令は主将を走らせる）」の勝率を比較する。
/// 係数・既定帯は動かさない＝Balance CLI <c>captain-usage</c> からだけ叩く分析専用パス
/// （<see cref="GameSimulation.Run"/> の teamFactory 差し替え口を使うだけで、本線の
/// <see cref="GameSimulation.GenerateTeam"/> は変更しない）。
/// </summary>
public static class CaptainUsageSimulation
{
    public sealed record Result(string Scenario, int Games, int Wins, double WinRate);

    /// <summary>
    /// 対戦固定校（平均的な相手校・先攻）に対し、キャッチャー枠を主将本人にした①と、同じ枠を
    /// 「主将より打撃総合+<paramref name="battingGap"/>の控え」にして主将をベンチへ回した②を、
    /// それぞれ <paramref name="games"/> 試合ずつ測る。統率力（leadership×mental/100）は
    /// <paramref name="leadership"/>/<paramref name="mental"/> で固定する（既定: 統率力63.75）。
    /// </summary>
    public static (Result Starter, Result Bench) Run(
        int games, ulong seed, string? coefficientsPath = null,
        int leadership = 85, int mental = 75, int battingGap = 10)
    {
        var starter = RunScenario(games, seed, coefficientsPath, captainStarts: true, leadership, mental, battingGap);
        var bench = RunScenario(games, seed, coefficientsPath, captainStarts: false, leadership, mental, battingGap);
        return (starter, bench);
    }

    private static Result RunScenario(int games, ulong seed, string? coefficientsPath, bool captainStarts,
        int leadership, int mental, int battingGap)
    {
        const int slot = 0; // キャッチャー枠を主将候補の置き場所として固定で使う（①②で揃える）。
        var s = GameSimulation.Run(games, seed, coefficientsPath, useTacticsBrain: true,
            teamFactory: (rng, name, isAway) =>
            {
                // 対戦相手（先攻・固定）は素の平均校。ManagerTacticalSense=50=中立（AI人選・落ち着きの采配係数とも恒等）。
                if (isAway) return GameSimulation.GenerateTeam(rng, name) with { ManagerTacticalSense = 50 };

                var team = GameSimulation.GenerateTeam(rng, name);
                var baseCatcher = team.BattingOrder[slot];
                var captain = baseCatcher with { Name = "主将", Leadership = leadership, Mental = mental };
                var bestPlayer = baseCatcher with
                {
                    Name = "控え最良",
                    Contact = Math.Clamp(baseCatcher.Contact + battingGap, 20, 88),
                    Power = Math.Clamp(baseCatcher.Power + battingGap, 20, 88),
                };

                var order = new List<Player>(team.BattingOrder);
                var reserves = new List<Player>(team.Reserves);
                if (captainStarts) { order[slot] = captain; reserves.Add(bestPlayer); }
                else { order[slot] = bestPlayer; reserves.Add(captain); }

                // 采配能力80: AiMessengerPickMinSense(既定60)以上＝伝令役は係数最大の控えを選ぶ側。
                // ②ではベンチの主将が主将ボーナス（issue #860 B）で係数最大になり、自動で選ばれる。
                return team with
                {
                    BattingOrder = order, Reserves = reserves, Captain = captain, ManagerTacticalSense = 80,
                };
            });

        var wins = s.HomeWins;
        var label = captainStarts ? "①主将スタメン" : "②最良選手スタメン+主将ベンチ(伝令)";
        return new Result(label, games, wins, (double)wins / games);
    }

    public static string Report((Result Starter, Result Bench) r, ulong seed, int leadership, int mental, int battingGap)
    {
        var sb = new StringBuilder();
        var power = leadership * mental / 100.0;
        sb.AppendLine("# 主将の起用ジレンマ: 勝率比較（issue #860 C）");
        sb.AppendLine();
        sb.AppendLine($"- seed={seed}, 統率力={power:F2}（leadership={leadership}, mental={mental}）, 打撃総合差={battingGap}");
        sb.AppendLine("- 対戦相手は固定の平均校（先攻）。実験チーム（後攻）はキャッチャー枠に「主将本人」か");
        sb.AppendLine("  「主将より打撃+battingGap優れる控え」のどちらかを置き、もう一方をベンチへ回す。");
        sb.AppendLine("- 両シナリオとも StandardTacticsBrain・采配能力80（伝令役は係数最大の控えを選ぶ側）。");
        sb.AppendLine();
        sb.AppendLine("| シナリオ | 試合数 | 勝利 | 勝率 |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var x in new[] { r.Starter, r.Bench })
            sb.AppendLine($"| {x.Scenario} | {x.Games} | {x.Wins} | {x.WinRate:P2} |");
        sb.AppendLine();
        var diff = (r.Starter.WinRate - r.Bench.WinRate) * 100.0;
        sb.AppendLine($"- 勝率差（①−②）= {diff:+0.00;-0.00}pt");
        return sb.ToString();
    }
}
