using System;
using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using KokoSim.Unity.Match;    // MatchLiveStatsProvider（守備位置・投打の表記を1箇所に集約）
using KokoSim.Unity.Players;  // RadarAxis を共用
using KokoSim.Unity.Shell;    // GameSession / NationService / PlayerMatchResolver

namespace KokoSim.Unity.MatchPreview
{
    // ── View DTO（コントローラは描画するだけ・UnityEngine 非依存） ──

    /// <summary>スタメン1行（先発投手行は Order 空）。</summary>
    public sealed class MatchPreviewSlotView
    {
        public string Order = "";     // 打順 "1"〜"9"（打順外は空）
        public string PosKanji = "";  // 守備位置漢字（DH は「指」）
        public string Name = "";
        public string Meta = "";      // 学年・投打
        public string Grade = "C";    // 総合ランク（S〜G）
    }

    /// <summary>片側（自校 or 相手校）の対戦カード。</summary>
    public sealed class MatchPreviewSideView
    {
        public string SideCaption = "";        // 「自校」「相手」
        public string TeamName = "";
        public string OrderCaption = "";       // 「先攻」「後攻」
        public string OverallGrade = "C";
        public int OverallValue;
        public List<RadarAxis> Radar = new List<RadarAxis>();
        public List<MatchPreviewSlotView> Lineup = new List<MatchPreviewSlotView>();
        public MatchPreviewSlotView StartingPitcher;   // DH制で打順外の先発。非DHは null（打順内に投がいる）
    }

    /// <summary>試合開始前画面に表示する一式（スナップショット）。</summary>
    public sealed class MatchPreviewView
    {
        public string MatchLine = "";   // 大会名＋ラウンド＋対戦
        public bool Ready;              // 対戦相手を解決できたか（false＝プレースホルダ表示）
        public MatchPreviewSideView Own = new MatchPreviewSideView();
        public MatchPreviewSideView Opponent = new MatchPreviewSideView();
    }

    /// <summary>
    /// 試合開始前画面（対戦カード）の状態。スタメン設定OKの直後に、これから始まる一戦の両校を並べて見せる。
    /// 表示専用＝乱数を一切引かず、大会の進行にも成績にも触れない（帯不変・決定論に影響なし）。
    ///
    /// 両校のラインナップは実試合と同じ入口（<see cref="PlayerMatchResolver.BuildManagerTeam"/> /
    /// <see cref="PlayerMatchResolver.BuildOpponentTeam"/>）から組むので、ここに出た9人がそのまま出場する。
    /// 6指標は自校＝<see cref="TeamStrengthProfile"/>（チーム総合力パネルと同値）、相手＝
    /// <see cref="ScoutedTeamProfile"/>（練習試合・大会展望と同値）で、軸の意味とスケールが一致する。
    /// </summary>
    public sealed class MatchPreviewState
    {
        private static readonly TeamStrengthCoefficients Coeff = TeamStrengthCoeff.Default;

        // レーダーの頂点順（上から時計回り）＝チーム総合力パネルと同一。並べて比較できるよう揃える。
        private static readonly string[] FactorLabels =
            { "打撃力", "投手力", "守備力", "機動力", "選手層", "精神力" };

        public MatchPreviewView BuildView()
        {
            var v = new MatchPreviewView();
            var gs = GameSession.Current;
            var runner = gs.Runner;
            var opponent = runner != null && !runner.Finished ? runner.NextOpponent : null;

            var ownTeam = PlayerMatchResolver.BuildManagerTeam(NationService.ManagerSchoolName);

            if (opponent == null)
            {
                // 相手未確定＝先攻/後攻も未定（HomeAwayAssignment は対戦の組み合わせが要る）。既定表示のみ。
                v.Own = BuildSide("自校", NationService.ManagerSchoolName, "後攻", ownTeam,
                    TeamStrengthProfile.Compute(RosterService.Active, Coeff));
                v.Ready = false;
                v.MatchLine = gs.Title;
                v.Opponent = new MatchPreviewSideView
                {
                    SideCaption = "相手",
                    TeamName = "対戦相手なし",
                    OrderCaption = "先攻",
                };
                return v;
            }

            // 自校の先攻/後攻は対戦の組み合わせ（校ID対＋年度＋週）から決定論で決まる（issue #70）。
            // 実試合（PlayerMatchResolver.Resolve/BeginLive）と同じ入口なので表示と実際が必ず一致する。
            var managerIsAway = PlayerMatchResolver.ManagerIsAway(NationService.ManagerSchool(), opponent);
            v.Own = BuildSide("自校", NationService.ManagerSchoolName, managerIsAway ? "先攻" : "後攻", ownTeam,
                TeamStrengthProfile.Compute(RosterService.Active, Coeff));

            var oppTeam = PlayerMatchResolver.BuildOpponentTeam(opponent);
            v.Opponent = BuildSide("相手", opponent.Name, managerIsAway ? "後攻" : "先攻", oppTeam,
                ScoutedTeamProfile.Compute(oppTeam, Coeff));
            v.Ready = true;
            v.MatchLine = string.Join("　", new[]
            {
                gs.Title,
                runner.RoundName,
                NationService.ManagerSchoolName + " vs " + opponent.Name,
            });
            return v;
        }

        private static MatchPreviewSideView BuildSide(string caption, string teamName, string orderCaption,
            Team team, TeamStrength strength)
        {
            var side = new MatchPreviewSideView
            {
                SideCaption = caption,
                TeamName = teamName,
                OrderCaption = orderCaption,
                OverallValue = (int)Math.Round(strength.Overall),
                OverallGrade = Tiers.FromStrength(strength.Overall).ToString(),
            };

            AddAxis(side, FactorLabels[0], strength.Batting);
            AddAxis(side, FactorLabels[1], strength.Pitching);
            AddAxis(side, FactorLabels[2], strength.Defense);
            AddAxis(side, FactorLabels[3], strength.Mobility);
            AddAxis(side, FactorLabels[4], strength.Depth);
            AddAxis(side, FactorLabels[5], strength.Mental);

            // 個人ランクは試合層 Player を集計用へ逆投影して求める（自校・相手校で同一の尺度）。
            var projected = ScoutedTeamProfile.ToRoster(team);
            for (var i = 0; i < team.BattingOrder.Count; i++)
            {
                var p = team.BattingOrder[i];
                var isDh = team.UsesDh && i == team.DhSlot;
                side.Lineup.Add(new MatchPreviewSlotView
                {
                    Order = (i + 1).ToString(),
                    PosKanji = MatchLiveStatsProvider.PosAbbr(isDh ? FieldPosition.DesignatedHitter : p.Position),
                    Name = p.Name,
                    Meta = Meta(p),
                    Grade = GradeOf(projected, i),
                });
            }

            if (team.UsesDh && team.StartingPitcher != null)
            {
                var sp = team.StartingPitcher;
                side.StartingPitcher = new MatchPreviewSlotView
                {
                    PosKanji = "投",
                    Name = sp.Name,
                    Meta = Meta(sp),
                    Grade = GradeOf(projected, projected.Count - 1),   // ToRoster は先発投手を末尾に積む
                };
            }
            return side;
        }

        private static void AddAxis(MatchPreviewSideView side, string label, double value)
        {
            var pct = (float)Math.Max(0.0, Math.Min(1.0, value / 100.0));
            side.Radar.Add(new RadarAxis
            {
                Label = label,
                Value01 = pct,
                ValueText = ((int)Math.Round(value)).ToString(),
            });
        }

        private static string Meta(Player p)
        {
            var grade = p.Grade > 0 ? p.Grade + "年 " : "";
            return grade + MatchLiveStatsProvider.ThrowsLabel(p.Throws) + MatchLiveStatsProvider.BatsLabel(p.Bats);
        }

        private static string GradeOf(IReadOnlyList<DevelopingPlayer> projected, int index)
            => index >= 0 && index < projected.Count
                ? Tiers.FromStrength(projected[index].AverageLevel()).ToString()
                : "C";
    }
}
