using System.Globalization;
using System.Text;
using KokoSim.Config;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Players;

namespace KokoSim.Balance;

/// <summary>
/// Step 4 バランス校正の計測（設計書02 §4.1b/§1.1b/§4.2-4.3）。
/// 走塁・送球の秒軸、盗塁・バント成功率、球速ヒストグラムを実測してレポートする。
/// 係数は data/coefficients.yaml 駆動。数値を目標帯と突き合わせて調整する。
/// </summary>
public static class Step4Calibration
{
    public static string Report(ulong seed, string? coefficientsPath)
    {
        var bundle = coefficientsPath is null ? null : CoefficientsLoader.LoadFromFile(coefficientsPath);
        var field = new FieldGeometry();
        var baserun = bundle?.Baserunning ?? new BaserunningCoefficients();
        var fielding = bundle?.Fielding ?? new KokoSim.Engine.Match.Fielding.FieldingCoefficients();
        var pitching = bundle?.Pitching ?? new PitchingCoefficients();
        var batting = bundle?.Batting ?? new KokoSim.Engine.Match.Batting.BattingCoefficients();

        var sb = new StringBuilder();
        sb.AppendLine("# Step 4 バランス校正レポート");
        sb.AppendLine($"- seed: {seed} / 係数: {coefficientsPath ?? "(既定)"}");
        sb.AppendLine();

        // === 1. 本塁→一塁タイム（設計書02 §4.1b: 右4.2-4.3 / 左4.0-4.2 / 俊足3.8-3.9 / 鈍足4.5） ===
        sb.AppendLine("## 1. 本塁→一塁タイム [s]（目標: 右4.2-4.3 / 左4.0-4.2 / 俊足3.8-3.9 / 鈍足4.5）");
        sb.AppendLine("| 走力 | 右打者 | 左打者 |");
        sb.AppendLine("|---|---|---|");
        foreach (var (label, sp) in new[] { ("鈍足20", 20), ("平均50", 50), ("俊足80", 80), ("快足95", 95) })
        {
            var b = new BatterAttributes { Speed = sp };
            var right = field.BaseDistanceM / b.SpeedToFirstMps() + fielding.RunnerReactionSeconds;
            var left = right - fielding.LeftBatterFirstStepBonusSeconds;
            sb.AppendLine($"| {label} | {right:F2} | {left:F2} |");
        }
        sb.AppendLine();

        // === 2. 塁間タイム（盗塁の走者側, §4.2: 速い選手で約3.4s） ===
        sb.AppendLine("## 2. 盗塁 走者側タイム [s]（リード後23.5m, 目標: 速い選手 約3.4s）");
        sb.AppendLine("| 走力/盗塁 | タイム |");
        sb.AppendLine("|---|---|");
        foreach (var (label, sp, st) in new[] { ("平均50/50", 50, 50), ("俊足75/75", 75, 75), ("快足90/90", 90, 90) })
        {
            var runner = new Player { Speed = sp, Steal = st };
            sb.AppendLine($"| {label} | {StealResolver.RunnerTimeSeconds(runner, baserun):F2} |");
        }
        sb.AppendLine();

        // === 3. 肩の秒軸: 捕手二塁送球（守備側タイム）と 三遊間深部→一塁 ===
        sb.AppendLine("## 3. 肩の秒軸 [s]（§4.1b 逆算）");
        sb.AppendLine("| 項目 | 肩50 | 肩70 | 肩90 |");
        sb.AppendLine("|---|---|---|---|");
        string DefRow(string label, System.Func<int, double> f) => $"| {label} | {f(50):F2} | {f(70):F2} | {f(90):F2} |";
        sb.AppendLine(DefRow("捕手 二塁送球 総所要", arm =>
            StealResolver.DefenseTimeSeconds(new Player { Position = FieldPosition.Catcher, ArmStrength = arm }, baserun)));
        // 三遊間深部(約40m)→一塁(約38m送球)を肩別に。処理0.9s+持ち替え+送球。
        sb.AppendLine(DefRow("三遊間深部→一塁 送球到達", arm =>
        {
            var throwSpeed = new FielderAttributes { ArmStrength = arm }.ThrowSpeedMps;
            return 0.9 + fielding.InfieldPlayOverheadSeconds + fielding.ThrowTransferSeconds + 38.0 / throwSpeed;
        }));
        sb.AppendLine();

        // === 4. 盗塁成功率グリッド（§4.2） ===
        sb.AppendLine("## 4. 盗塁成功率 [%]（走者 vs 捕手肩。目安: 俊足で65-85%）");
        sb.AppendLine("| 走者(走力/盗塁) \\ 捕手肩 | 40 | 55 | 70 |");
        sb.AppendLine("|---|---|---|---|");
        foreach (var (label, sp, st) in new[] { ("平均50/50", 50, 50), ("俊足75/75", 75, 75), ("快足90/90", 90, 90) })
        {
            var runner = new Player { Speed = sp, Steal = st };
            var row = new StringBuilder($"| {label} ");
            foreach (var arm in new[] { 40, 55, 70 })
            {
                var catcher = new Player { Position = FieldPosition.Catcher, ArmStrength = arm };
                row.Append($"| {StealResolver.SuccessProbability(runner, catcher, baserun) * 100:F0} ");
            }
            sb.AppendLine(row.Append('|').ToString());
        }
        sb.AppendLine();

        // === 5. バント結果分布（§4.3, N=20000 per level） ===
        sb.AppendLine("## 5. 送りバント結果分布 [%]（N=20000。目安: 犠打成功 60-80%）");
        sb.AppendLine("| バント | 犠打成功 | 内野安打 | 小フライ | ファウル | 空振り |");
        sb.AppendLine("|---|---|---|---|---|---|");
        var pitcher = new PitcherAttributes { MaxVelocityKmh = 135 };
        foreach (var (label, bunt, speed) in new[]
                 { ("下手30", 30, 50), ("平均50", 50, 50), ("上手75", 75, 50), ("快足×上手75", 75, 90) })
        {
            var batter = new Player { Bunt = bunt, Speed = speed };
            int sac = 0, ih = 0, pop = 0, foul = 0, miss = 0;
            var rng = new Xoshiro256Random(seed ^ (ulong)bunt);
            for (var i = 0; i < 20000; i++)
            {
                switch (BuntResolver.Resolve(batter, pitcher, safety: false, baserun, rng))
                {
                    case BuntResult.SacrificeSuccess: sac++; break;
                    case BuntResult.InfieldHit: ih++; break;
                    case BuntResult.PopOut: pop++; break;
                    case BuntResult.Foul: foul++; break;
                    default: miss++; break;
                }
            }
            double P(int x) => x / 200.0;
            sb.AppendLine($"| {label} | {P(sac):F0} | {P(ih):F0} | {P(pop):F0} | {P(foul):F0} | {P(miss):F0} |");
        }
        sb.AppendLine();

        // === 6. 球速ヒストグラム（§1.1b/§1.1d: 常時 最速−3〜5km/h, たまに最速付近） ===
        sb.AppendLine("## 6. 球速ヒストグラム（最速145km/h投手, N=50000。目標: 中央値≈最速−4, 最速付近は稀）");
        var ace = new PitcherAttributes { MaxVelocityKmh = 145 };
        var samples = new List<double>(50000);
        var vrng = new Xoshiro256Random(seed ^ 0x00E1_0000UL);
        for (var i = 0; i < 50000; i++)
        {
            samples.Add(PitchSelection.Select(ace, new StrikeZone(), batting, pitching, vrng).VelocityKmh);
        }
        samples.Sort();
        double Pct(double p) => samples[(int)(p * (samples.Count - 1))];
        sb.AppendLine($"- 最速(カタログ): 145 / 実測max: {samples[^1]:F1} / 中央値: {Pct(0.5):F1} / 平均: {samples.Average():F1}");
        sb.AppendLine($"- p10: {Pct(0.10):F1} / p90: {Pct(0.90):F1} / ≥143km/h の割合: {samples.Count(v => v >= 143) * 100.0 / samples.Count:F1}%");
        sb.AppendLine();

        return sb.ToString();
    }
}
