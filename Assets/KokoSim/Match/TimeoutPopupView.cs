using KokoSim.Unity.Shell;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// タイム（試合中の中断ポップアップ）の View。UXML（MatchLive.uxml の pause-modal）の骨組みへ
    /// <see cref="TimeoutPopupModel"/> の値を流し込むだけ＝判断・集計を持たない。
    /// ボタンの押下配線（伝令・交代・能力・スキップ・再開）は従来どおり MatchLiveController が name で持つ。
    /// </summary>
    public sealed class TimeoutPopupView
    {
        private readonly Label _phase, _inningN, _inningHalf, _awayScore, _homeScore;
        private readonly VisualElement _out1, _out2, _base1, _base2, _base3;
        private readonly VisualElement _awayNameRow, _homeNameRow;
        private readonly VisualElement _batterRow, _pitcherRow;
        private readonly Button _timeout, _sub, _scout, _skip;
        private readonly Label _curSide, _curText, _othSide, _othText;
        private readonly VisualElement _curDots, _othDots;

        public TimeoutPopupView(VisualElement root)
        {
            _phase = root.Q<Label>("pm-phase");
            _inningN = root.Q<Label>("pm-inning-n");
            _inningHalf = root.Q<Label>("pm-inning-half");
            _out1 = root.Q("pm-out-1");
            _out2 = root.Q("pm-out-2");
            _base1 = root.Q("pm-base-1");
            _base2 = root.Q("pm-base-2");
            _base3 = root.Q("pm-base-3");
            _awayNameRow = root.Q("pm-away-name-row");
            _homeNameRow = root.Q("pm-home-name-row");
            _awayScore = root.Q<Label>("pm-away-score");
            _homeScore = root.Q<Label>("pm-home-score");
            _batterRow = root.Q("pm-batter-row");
            _pitcherRow = root.Q("pm-pitcher-row");
            _timeout = root.Q<Button>("pm-timeout");
            _sub = root.Q<Button>("pm-sub");
            _scout = root.Q<Button>("pm-scout");
            _skip = root.Q<Button>("pm-skip");
            _curSide = root.Q<Label>("pm-to-cur-side");
            _curText = root.Q<Label>("pm-to-cur-text");
            _curDots = root.Q("pm-to-cur-dots");
            _othSide = root.Q<Label>("pm-to-oth-side");
            _othText = root.Q<Label>("pm-to-oth-text");
            _othDots = root.Q("pm-to-oth-dots");

            // アイコン（線画部品）は一度だけ差し込む。色はタイル・ボタンの文字色を継承する。
            AddIcon(root.Q("pm-timeout-icon"), LineIcon.Kind.Megaphone);
            AddIcon(root.Q("pm-sub-icon"), LineIcon.Kind.Swap);
            AddIcon(root.Q("pm-scout-icon"), LineIcon.Kind.Roster);
            AddIcon(root.Q("pm-skip-icon"), LineIcon.Kind.FastForward, fill: true);
            AddIcon(root.Q("pm-resume-icon"), LineIcon.Kind.Play, fill: true);
        }

        private static void AddIcon(VisualElement host, LineIcon.Kind kind, bool fill = false)
        {
            if (host == null) return;
            var icon = new LineIcon(kind);
            if (fill) { icon.style.flexGrow = 1; }
            host.Add(icon);
        }

        public void Render(TimeoutPopupModel m)
        {
            if (m == null) return;

            // 1. ヘッダー
            SetText(_phase, m.ManagerOnOffense ? "攻撃中" : "守備中");

            // 2. 状況欄
            SetText(_inningN, m.Inning.ToString());
            SetText(_inningHalf, m.IsTop ? "回表" : "回裏");
            _out1?.EnableInClassList("tmo-out--on", m.Outs >= 1);
            _out2?.EnableInClassList("tmo-out--on", m.Outs >= 2);
            _base1?.EnableInClassList("tmo-base--on", m.OnFirst);
            _base2?.EnableInClassList("tmo-base--on", m.OnSecond);
            _base3?.EnableInClassList("tmo-base--on", m.OnThird);
            FillTeam(_awayNameRow, _awayScore, m.AwayName, m.AwayScore, own: m.ManagerIsAway);
            FillTeam(_homeNameRow, _homeScore, m.HomeName, m.HomeScore, own: !m.ManagerIsAway);

            // 3. 対戦欄
            FillPlayerRow(_batterRow, "打者", m.Batter);
            FillPlayerRow(_pitcherRow, "投手", m.Pitcher);

            // 4. 伝令（いまの攻守の行が上・もう一方は灰色で「〜時に使用」）
            SetText(_curSide, m.ManagerOnOffense ? "攻撃" : "守備");
            SetText(_curText, "残り" + m.CurrentTimeoutsLeft + "回");
            FillDots(_curDots, m.TimeoutCap, m.CurrentTimeoutsLeft);
            SetText(_othSide, m.ManagerOnOffense ? "守備" : "攻撃");
            SetText(_othText, m.ManagerOnOffense ? "守備時に使用" : "攻撃時に使用");
            FillDots(_othDots, m.TimeoutCap, 0);

            _timeout?.SetEnabled(m.TimeoutEnabled);
            _sub?.SetEnabled(m.ActionsEnabled);
            _scout?.SetEnabled(m.ActionsEnabled);
            _skip?.SetEnabled(m.ActionsEnabled);
        }

        private static void FillTeam(VisualElement nameRow, Label score, string name, int runs, bool own)
        {
            if (nameRow != null)
            {
                nameRow.Clear();
                var n = new Label(name);
                n.AddToClassList("tmo-team__name");
                n.AddToClassList(own ? "f-body-bd" : "f-body");
                if (own) n.AddToClassList("tmo-team__name--own");
                nameRow.Add(n);
                if (own)
                {
                    var badge = new Label("自");
                    badge.AddToClassList("tmo-own-badge");
                    badge.AddToClassList("f-body-bd");
                    nameRow.Add(badge);
                }
            }
            if (score != null)
            {
                score.text = runs.ToString();
                score.EnableInClassList("tmo-team__score--own", own);
            }
        }

        // 丸を cap 個並べ、先頭から filled 個を差し色で塗る（残りは枠だけ）。
        private static void FillDots(VisualElement host, int cap, int filled)
        {
            if (host == null) return;
            host.Clear();
            for (var i = 0; i < cap; i++)
            {
                var d = new VisualElement { pickingMode = PickingMode.Ignore };
                d.AddToClassList("tmo-to__dot");
                if (i < filled) d.AddToClassList("tmo-to__dot--on");
                host.Add(d);
            }
        }

        private static void FillPlayerRow(VisualElement row, string role, TimeoutPopupModel.PlayerLine p)
        {
            if (row == null) return;
            row.Clear();
            row.Add(MakeLabel(role, "tmo-mrow__role", "f-body"));
            if (p == null) return;   // 打席の切れ目で対戦がまだ無い＝ラベルだけ

            var numCell = new VisualElement { pickingMode = PickingMode.Ignore };
            numCell.AddToClassList("tmo-mrow__num");
            if (p.Number.Length > 0) numCell.Add(MakeLabel(p.Number, "sbk-patch", "f-num-bd"));
            row.Add(numCell);

            row.Add(MakeLabel(p.Name, "tmo-mrow__name", "f-display"));
            row.Add(MakeLabel(p.Detail, "tmo-mrow__detail", "f-body"));

            var stats = new VisualElement { pickingMode = PickingMode.Ignore };
            stats.AddToClassList("tmo-mrow__stats");
            foreach (var (label, value) in p.Stats)
            {
                stats.Add(MakeLabel(label, "tmo-stat__label", "f-body"));
                stats.Add(MakeLabel(value, "tmo-stat__value", "f-num-bd"));
            }
            row.Add(stats);

            var condCell = new VisualElement { pickingMode = PickingMode.Ignore };
            condCell.AddToClassList("tmo-mrow__cond");
            var face = new ConditionFace();
            face.Set(p.Condition);
            face.SetEstimated(p.ConditionEstimated);
            condCell.Add(face);
            row.Add(condCell);
        }

        private static Label MakeLabel(string text, params string[] classes)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            foreach (var c in classes) l.AddToClassList(c);
            return l;
        }

        private static void SetText(Label l, string text)
        {
            if (l != null) l.text = text;
        }
    }
}
