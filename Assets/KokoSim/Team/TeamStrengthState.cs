using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Nation;
using KokoSim.Unity.Players; // AbilityBar / RadarAxis を共用
using KokoSim.Unity.Shell;   // RosterService（共有ロスター）/ RankPalette（ランク色の単一ソース）

namespace KokoSim.Unity.Squad
{
    /// <summary>チーム総合力パネルに表示する一式（スナップショット）。</summary>
    public sealed class TeamStrengthView
    {
        public string OverallGrade = "D";
        public int OverallValue;
        public List<AbilityBar> Factors = new List<AbilityBar>();
        public List<RadarAxis> Radar = new List<RadarAxis>();
        public string AnalysisWeak = "";   // 強調表示部（弱点: X・Y）
        public string AnalysisAdvice = ""; // 通常表示部（◯強化が課題）
    }

    /// <summary>
    /// チーム総合力（6指標）の状態（設計決定 2026-07-18）。共有ロスター（RosterService）を
    /// エンジンの <see cref="TeamStrengthProfile"/> に通して 6指標＋総合ランクへ整形するだけ。
    /// 表示専用＝ゲームバランスには影響しない（総合を加重平均へ切替える③は別ステップ）。
    /// 重みは既定係数（data/coefficients.yaml team_strength: と同値）を使う。
    /// バー色はランク連動（RankPalette）。数値の色は画面側で1色統一する。
    /// </summary>
    public sealed class TeamStrengthState
    {
        private static readonly TeamStrengthCoefficients Coeff = new TeamStrengthCoefficients();

        public TeamStrengthView BuildView()
        {
            var s = TeamStrengthProfile.Compute(RosterService.Roster, Coeff);
            var v = new TeamStrengthView
            {
                OverallValue = (int)Math.Round(s.Overall),
                OverallGrade = Tiers.FromStrength(s.Overall).ToString(),
            };

            // レーダーの頂点順（上から時計回り）＝右の指標バー順。両者を1対1に揃える。
            Add(v, "打撃力", s.Batting);
            Add(v, "投手力", s.Pitching);
            Add(v, "守備力", s.Defense);
            Add(v, "機動力", s.Mobility);
            Add(v, "選手層", s.Depth);
            Add(v, "精神力", s.Mental);

            BuildAnalysis(v);
            return v;
        }

        private static void Add(TeamStrengthView v, string label, double value)
        {
            var iv = (int)Math.Round(value);
            var pct = Clamp01((float)(value / 100.0));
            var grade = Tiers.FromStrength(value).ToString();
            v.Factors.Add(new AbilityBar
            {
                Label = label,
                Value = iv,
                Grade = grade,
                Pct = pct,
                BarColorHex = RankPalette.Hex(grade), // バー色＝ランク連動（単一ソース）
            });
            v.Radar.Add(new RadarAxis { Label = label, Value01 = pct });
        }

        // 最も低い2指標を弱点として指摘する自動コメント（強調部と助言部に分割）。
        private static void BuildAnalysis(TeamStrengthView v)
        {
            var weak = v.Factors.OrderBy(f => f.Value).Take(2).ToList();
            if (weak.Count < 2) return;
            var theme = weak[0].Label.Replace("力", ""); // 「打撃力」→「打撃」
            v.AnalysisWeak = $"弱点: {weak[0].Label}・{weak[1].Label}";
            v.AnalysisAdvice = $"{theme}強化が課題";
        }

        private static float Clamp01(float x) => Math.Max(0f, Math.Min(1f, x));
    }
}
