// ViewModel層（設計書06 §1: エンジン→ViewModel→UXML）。UnityEngine 非依存に保つ。
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>主将指名ダイアログの候補1行（可視情報だけ。統率力の生値は出さない, 設計書09 §8）。</summary>
    public sealed class CaptainCandidateRow
    {
        /// <summary><see cref="RosterService.Active"/> 内の index（右ペインの詳細ビューを引くキー）。</summary>
        public int ActiveIndex;
        public string Number = "–";
        public string Name = "";
        public string GradeLabel = "3年";
        public string OverallGrade = "D";
        public int OverallValue;
        public int Mental;
        public string Condition = "普通";
        // 表情顔（ConditionFace）の描画に使う enum（表示文字列は比較に使わない）。
        public KokoSim.Engine.Players.Condition ConditionLevel = KokoSim.Engine.Players.Condition.Normal;
        /// <summary>自動選出された暫定主将か（初期選択に使う）。</summary>
        public bool IsInterim;
    }

    /// <summary>新チーム発足ダイアログに表示する一式（スナップショット）。</summary>
    public sealed class NewTeamView
    {
        public string Headline = "新チーム始動";
        public string Lead = "";
        public string RetiredLabel = "";
        public List<CaptainCandidateRow> Candidates = new List<CaptainCandidateRow>();
    }

    /// <summary>
    /// 新チーム発足（夏の3年引退の翌週）の導線。<see cref="GameClock"/> の週送りフックから開き、
    /// プレイヤーが次期主将を1回だけ指名して閉じる（設計書09 §8）。
    /// 状態はセッション静的（週と同じくシェル共有）。UnityEngine 非依存。
    /// </summary>
    public static class NewTeamService
    {
        private static readonly SeasonCalendar Calendar = new SeasonCalendar();
        private static readonly List<string> RetiredNames = new List<string>();

        /// <summary>主将指名の入力待ちか（ダイアログを出す条件）。</summary>
        public static bool Pending { get; private set; }

        /// <summary>引退週フックから呼ぶ。引退者を控え、指名待ちにする。</summary>
        internal static void Open(NewTeamTransition transition)
        {
            RetiredNames.Clear();
            foreach (var p in transition.Retired) RetiredNames.Add(p.Name);
            Pending = true;
        }

        /// <summary>テスト・リセット用（週を巻き戻したときなど）。</summary>
        public static void Clear()
        {
            RetiredNames.Clear();
            Pending = false;
        }

        public static NewTeamView BuildView()
        {
            var roster = RosterService.Roster;
            var active = RosterService.Active;
            var view = new NewTeamView
            {
                Lead = "3年生が引退しました。新チームの主将を指名してください。",
                RetiredLabel = RetiredNames.Count + " 名が引退",
            };

            foreach (var p in CaptainSelector.DesignationCandidates(roster))
            {
                var overall = (int)System.Math.Round(p.AverageLevel());
                var condition = FormModel.Quantize(p.ConditionValue);
                view.Candidates.Add(new CaptainCandidateRow
                {
                    ActiveIndex = IndexIn(active, p),
                    Number = p.UniformNumber >= 1 ? p.UniformNumber.ToString() : "–",
                    Name = p.Name,
                    GradeLabel = p.Grade + "年",
                    OverallValue = overall,
                    OverallGrade = Tiers.FromStrength(overall).ToString(),
                    Mental = p.Mental,
                    Condition = ConditionLabels.Jp(condition),
                    ConditionLevel = condition,
                    IsInterim = p.IsCaptain,
                });
            }
            return view;
        }

        /// <summary>
        /// 指名を確定して導線を閉じる（設計書09 §8: 指名できるのは新チーム発足時の1回だけ）。
        /// 候補外・期間外なら何もせず false（暫定主将のまま）。
        /// </summary>
        public static bool Confirm(int activeIndex)
        {
            var active = RosterService.Active;
            if (activeIndex < 0 || activeIndex >= active.Count) return false;
            var roster = RosterService.Roster;
            var pick = active[activeIndex];
            if (!CaptainSelector.CanDesignate(roster, pick, GameClock.Week, Calendar)) return false;
            CaptainSelector.Designate(roster, pick);
            Pending = false;
            return true;
        }

        private static int IndexIn(IReadOnlyList<DevelopingPlayer> list, DevelopingPlayer p)
        {
            for (var i = 0; i < list.Count; i++) if (ReferenceEquals(list[i], p)) return i;
            return -1;
        }
    }
}
