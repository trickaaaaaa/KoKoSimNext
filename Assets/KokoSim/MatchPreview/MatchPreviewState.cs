using System;
using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Practice;
using KokoSim.Engine.Season;
using KokoSim.Unity.Match;    // MatchLiveStatsProvider（守備位置・投打の表記を1箇所に集約）
using KokoSim.Unity.Shell;    // GameSession / NationService / PlayerMatchResolver

namespace KokoSim.Unity.MatchPreview
{
    // ── View DTO（コントローラは描画するだけ） ──

    /// <summary>スタメン1行（先発投手行は Order 空）。</summary>
    public sealed class MatchPreviewSlotView
    {
        public string Order = "";     // 打順 "1"〜"9"（打順外は空）
        public string PosKanji = "";  // 守備位置漢字（DH は「指」）
        public string Name = "";
        public int Number;            // 背番号（0＝未割当＝箱を空にする）
        public string Meta = "";      // 「2年 右投左打」
        // 選手個人の総合ランクは持たない（Issue #30 で廃止・#270 で本画面からも撤去）。
    }

    /// <summary>片側（自校 or 相手校）の対戦カード。</summary>
    public sealed class MatchPreviewSideView
    {
        public string TeamName = "";
        public string OrderCaption = "";       // 「先攻」「後攻」
        public string OverallGrade = "C";
        public int OverallValue;
        /// <summary>戦力6項目（打撃力・投手力・守備力・機動力・選手層・精神力の順。0〜100に丸めた整数）。</summary>
        public int[] Factors = new int[6];
        public bool UsesDh;
        public List<MatchPreviewSlotView> Lineup = new List<MatchPreviewSlotView>();
        /// <summary>先発投手（DH有無に関わらず出す。非DHでは打順内の「投」と同じ選手）。未確定は null。</summary>
        public MatchPreviewSlotView StartingPitcher;
    }

    /// <summary>
    /// 風（issue #1614 で engine に入るまでは常に null＝欄ごと隠す。2026-11-25 オーナー決定）。
    /// 見せ方は「矢印＋m/s」（例: 矢印＋「右翼へ 3m/s」）。
    /// </summary>
    public sealed class MatchPreviewWindView
    {
        /// <summary>矢印の向き[度]。0＝本塁→中堅（打者から見て真っ直ぐ外野へ）、時計回り。</summary>
        public float ArrowDegrees;
        public string Text = "";
    }

    /// <summary>試合開始前画面に表示する一式（スナップショット）。</summary>
    public sealed class MatchPreviewView
    {
        /// <summary>試合の種類の札（「練習試合」「夏の県大会 準々決勝」）。</summary>
        public string KindLabel = "";
        public bool Ready;              // 対戦相手を解決できたか（false＝プレースホルダ表示）
        public MatchPreviewSideView Own = new MatchPreviewSideView();
        public MatchPreviewSideView Opponent = new MatchPreviewSideView();

        /// <summary>球場名（null＝球場の割り当てが無い大会＝欄を出さない）。</summary>
        public string StadiumName;
        /// <summary>天気（null＝天気の欄を出さない）。屋内は <see cref="Indoor"/> が立ち、状態は使わない。</summary>
        public WeatherState? Weather;
        /// <summary>屋内の球場（天気の値を「屋内」にし、気温の欄は出さない）。</summary>
        public bool Indoor;
        /// <summary>気温の表示（「24℃」。空＝欄を出さない）。</summary>
        public string Temperature = "";
        /// <summary>風（null＝欄を出さない）。</summary>
        public MatchPreviewWindView Wind;
    }

    /// <summary>
    /// 試合開始前画面（対戦カード）の状態。スタメン設定OKの直後に、これから始まる一戦の両校を並べて見せる。
    /// 表示専用＝本流の乱数を一切引かず、大会の進行にも成績にも触れない（帯不変・決定論に影響なし）。
    ///
    /// 両校のラインナップは実試合と同じ入口（<see cref="PlayerMatchResolver.BuildManagerTeam"/> /
    /// <see cref="PlayerMatchResolver.BuildOpponentTeam"/>）から組むので、ここに出た9人がそのまま出場する。
    /// 6指標は自校＝<see cref="TeamStrengthProfile"/>（チーム総合力パネルと同値）、相手＝
    /// <see cref="ScoutedTeamProfile"/>（練習試合・大会展望と同値）で、軸の意味とスケールが一致する。
    /// 球場・天気・気温は実際の試合が使う値を先読みする（Fork だけ＝本流を進めない。UIで抽選し直さない）。
    /// </summary>
    public sealed class MatchPreviewState
    {
        private static readonly TeamStrengthCoefficients Coeff = TeamStrengthCoeff.Default;

        /// <summary>レーダーの頂点・棒グラフの行の並び（真上から時計回り）。</summary>
        public static readonly string[] FactorLabels =
            { "打撃力", "投手力", "守備力", "機動力", "選手層", "精神力" };

        public MatchPreviewView BuildView()
        {
            var v = new MatchPreviewView();
            var gs = GameSession.Current;
            var runner = gs.Runner;
            var practice = gs.PendingMatchIsPractice;
            // 相手校は試合種別で供給元が変わる（issue #269）。練習試合は申込で確定した相手、
            // 大会戦は進行体の次戦相手。以降の組み立ては両者で完全に同じ。
            var opponent = practice
                ? gs.PracticeOpponent
                : runner != null && !runner.Finished ? runner.NextOpponent : null;

            var ownTeam = PlayerMatchResolver.BuildManagerTeam(NationService.ManagerSchoolName);
            v.KindLabel = practice ? "練習試合" : JoinNonEmpty(gs.Title, runner?.RoundName);

            if (opponent == null)
            {
                // 相手未確定＝先攻/後攻も未定（HomeAwayAssignment は対戦の組み合わせが要る）。既定表示のみ。
                v.Own = BuildSide(NationService.ManagerSchoolName, "後攻", ownTeam,
                    TeamStrengthProfile.Compute(RosterService.Active, Coeff));
                v.Ready = false;
                v.Opponent = new MatchPreviewSideView { TeamName = "対戦相手なし", OrderCaption = "先攻" };
                return v;
            }

            // 自校の先攻/後攻は対戦の組み合わせ（校ID対＋年度＋週）から決定論で決まる（issue #70）。
            // 実試合（PlayerMatchResolver.Resolve/BeginLive）と同じ入口なので表示と実際が必ず一致する。
            var managerIsAway = PlayerMatchResolver.ManagerIsAway(NationService.ManagerSchool(), opponent);
            v.Own = BuildSide(NationService.ManagerSchoolName, managerIsAway ? "先攻" : "後攻", ownTeam,
                TeamStrengthProfile.Compute(RosterService.Active, Coeff));

            var oppTeam = PlayerMatchResolver.BuildOpponentTeam(opponent);
            v.Opponent = BuildSide(opponent.Name, managerIsAway ? "後攻" : "先攻", oppTeam,
                ScoutedTeamProfile.Compute(oppTeam, Coeff));
            v.Ready = true;

            if (practice) FillPracticeConditions(v, gs, opponent, managerIsAway);
            else FillTournamentConditions(v, gs);
            return v;
        }

        /// <summary>
        /// 大会戦の球場・天気・気温。球場は実試合と同じ <see cref="PlayerMatchResolver.StadiumFor"/> に、
        /// 次戦の試合 rng（<c>PeekNextPlayerMatchRng</c>）を渡して引く＝表示と実際の会場が一致する。
        /// 球場の割り当てが無い大会（地区大会）は球場の欄を出さない。屋内は天候が効かない＝天気「屋内」・気温なし。
        /// </summary>
        private static void FillTournamentConditions(MatchPreviewView v, GameSession gs)
        {
            var runner = gs.Runner;
            var matchRng = runner.PeekNextPlayerMatchRng();
            var stadium = matchRng == null
                ? null
                : PlayerMatchResolver.StadiumFor(runner.NextMatchRoundsRemaining, matchRng, NationService.PrefectureId);
            v.StadiumName = stadium?.Name;

            if (stadium?.Indoor == true)
            {
                v.Indoor = true;
                return;
            }
            if (runner.PeekNextMatchWeather(PlayerMatchResolver.CurrentWeatherCoefficients()) is { } w)
                SetWeather(v, w);
        }

        /// <summary>
        /// 練習試合の球場・天気・気温。球場は後攻の学校のグラウンド（表示だけ・2026-11-25 オーナー決定）。
        /// 天気・気温は申込と同じ種から作り直した rng（<see cref="GameSession.PracticeMatchRng"/>）で、実試合と
        /// 同じ Fork 鎖（受諾判定1回→試合 Fork→天候 Fork）を辿って先読みする。
        /// </summary>
        private static void FillPracticeConditions(MatchPreviewView v, GameSession gs, School opponent, bool managerIsAway)
        {
            var homeName = managerIsAway ? opponent.Name : NationService.ManagerSchoolName;
            v.StadiumName = homeName + "グラウンド";

            var coefficients = PlayerMatchResolver.CurrentWeatherCoefficients();
            if (!coefficients.Enabled) return;
            var matchRng = PracticeMatchScheduler.MatchRngAfterAcceptance(
                GameSession.PracticeMatchRng(opponent.Id, gs.PracticeWeek));
            SetWeather(v, WeatherModel.SampleForMatch(matchRng.Fork(2), coefficients));
        }

        private static void SetWeather(MatchPreviewView v, WeatherSample w)
        {
            v.Weather = w.State;
            v.Temperature = Math.Round(w.TemperatureC).ToString() + "℃";
        }

        /// <summary>天気の表示名。屋内は天気を出さず「屋内」。</summary>
        public static string WeatherLabel(MatchPreviewView v)
        {
            if (v.Indoor) return "屋内";
            return v.Weather switch
            {
                WeatherState.Rainy => "雨",
                WeatherState.Cloudy => "曇り",
                WeatherState.Sunny => "晴れ",
                _ => "",
            };
        }

        private static MatchPreviewSideView BuildSide(string teamName, string orderCaption, Team team, TeamStrength strength)
        {
            var side = new MatchPreviewSideView
            {
                TeamName = teamName,
                OrderCaption = orderCaption,
                OverallValue = (int)Math.Round(strength.Overall),
                OverallGrade = Tiers.FromStrength(strength.Overall).ToString(),
                UsesDh = team.UsesDh,
                Factors = new[]
                {
                    Pct(strength.Batting), Pct(strength.Pitching), Pct(strength.Defense),
                    Pct(strength.Mobility), Pct(strength.Depth), Pct(strength.Mental),
                },
            };

            for (var i = 0; i < team.BattingOrder.Count; i++)
            {
                var p = team.BattingOrder[i];
                var isDh = team.UsesDh && i == team.DhSlot;
                side.Lineup.Add(Slot(p, (i + 1).ToString(),
                    MatchLiveStatsProvider.PosAbbr(isDh ? FieldPosition.DesignatedHitter : p.Position)));
            }

            // 先発投手は DH の有無に関わらず最下段に出す（非DHでは打順内の「投」と同じ選手）。
            var sp = team.UsesDh ? team.StartingPitcher : PitcherInOrder(team);
            if (sp != null) side.StartingPitcher = Slot(sp, "", "投");
            return side;
        }

        private static Player PitcherInOrder(Team team)
            => team.PitcherSlot >= 0 && team.PitcherSlot < team.BattingOrder.Count
                ? team.BattingOrder[team.PitcherSlot]
                : team.StartingPitcher;

        private static MatchPreviewSlotView Slot(Player p, string order, string pos) => new MatchPreviewSlotView
        {
            Order = order,
            PosKanji = pos,
            Name = p.Name,
            Number = p.UniformNumber,
            Meta = Meta(p),
        };

        /// <summary>戦力の値を 0〜100 の整数へ（6項目は理屈の上で 100 を少し超え得る＝表示で切り詰める）。</summary>
        private static int Pct(double value) => (int)Math.Round(Math.Max(0.0, Math.Min(100.0, value)));

        private static string Meta(Player p)
        {
            var grade = p.Grade > 0 ? p.Grade + "年 " : "";
            return grade + MatchLiveStatsProvider.ThrowsLabel(p.Throws) + MatchLiveStatsProvider.BatsLabel(p.Bats);
        }

        private static string JoinNonEmpty(params string[] parts)
        {
            var list = new List<string>();
            foreach (var s in parts) if (!string.IsNullOrEmpty(s)) list.Add(s);
            return string.Join(" ", list);
        }
    }
}
