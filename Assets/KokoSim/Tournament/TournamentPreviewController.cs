using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Tournament
{
    /// <summary>
    /// 大会プレビューのコントローラ（設計書06 §3.5b）。UIDocument（TournamentPreview.uxml）へ
    /// TournamentPreviewState（ViewModel）をバインドし、優勝争いの構図カードを動的に組む。
    /// 大会モード中は現在進行中の大会概要（自校ハイライト付きの経過）を出力する（要件5）。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class TournamentPreviewController : MonoBehaviour
    {
        private VisualElement _root;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;

            if (GameSession.Current.InTournament)
            {
                RenderBracket(GameSession.Current.Runner.BuildBracketView());
                return;
            }

            var v = new TournamentPreviewState().Build();

            SetText("tp-h2", "優勝争いの構図");
            SetText("tp-title", v.Title);
            SetText("tp-meta", v.Meta);
            SetText("tp-lead", v.Lead);

            var list = _root.Q<VisualElement>("tp-contenders");
            if (list == null) return;
            list.Clear();
            foreach (var c in v.Contenders) list.Add(BuildCard(c));
        }

        // ===== 大会モード: 大会概要（今大会の経過・自校ハイライト） =====

        private void RenderBracket(TournamentBracketView view)
        {
            var status = view.ManagerIsChampion ? "自校 優勝！" : view.ManagerEliminated ? "自校 敗退" : "自校 進行中";
            SetText("tp-h2", "今大会の経過");
            SetText("tp-title", view.Title);
            SetText("tp-meta", status);
            SetText("tp-lead", "優勝校: " + (view.ChampionName ?? "未定") + "　（全 " + view.Matches.Count + " 試合）");

            var list = _root.Q<VisualElement>("tp-contenders");
            if (list == null) return;
            list.Clear();
            foreach (var m in view.Matches) list.Add(BuildMatchRow(m));
        }

        private static VisualElement BuildMatchRow(BracketMatch m)
        {
            var row = new VisualElement();
            row.AddToClassList("tp-team");
            if (m.ManagerInvolved) row.AddToClassList("tp-team--fav");   // 自校の試合を左アクセントで強調

            var body = new VisualElement();
            body.AddToClassList("tp-body");

            var sub = new VisualElement();
            sub.AddToClassList("tp-team-sub");
            var round = new Label(m.RoundName);
            round.AddToClassList("tp-seed");
            sub.Add(round);
            var line = new Label(m.WinnerName + "  " + m.WinnerScore + " － " + m.LoserScore + "  " + m.LoserName);
            line.AddToClassList("tp-team-name");
            line.style.marginLeft = 8;
            sub.Add(line);
            body.Add(sub);

            row.Add(body);
            return row;
        }

        private static VisualElement BuildCard(TournamentPreviewState.ContenderRow c)
        {
            var team = new VisualElement();
            team.AddToClassList("tp-team");
            if (c.IsFavorite) team.AddToClassList("tp-team--fav");

            // 格付けマーク（◎○▲ ＋ ラベル）
            var mark = new VisualElement();
            mark.AddToClassList("tp-mark");
            var sym = new Label(c.MarkSym);
            sym.AddToClassList("tp-mark__sym");
            sym.AddToClassList("tp-mark__sym--" + c.MarkClass);
            mark.Add(sym);
            var mlabel = new Label(c.MarkLabel);
            mlabel.AddToClassList("tp-mark__label");
            mark.Add(mlabel);
            team.Add(mark);

            // 本文
            var body = new VisualElement();
            body.AddToClassList("tp-body");

            var sub = new VisualElement();
            sub.AddToClassList("tp-team-sub");
            var name = new Label(c.Name);
            name.AddToClassList("tp-team-name");
            sub.Add(name);
            var chip = new Label("総合 " + c.TierLetter);
            chip.AddToClassList("grade");
            chip.AddToClassList("grade--" + c.TierLetter);
            chip.style.marginLeft = 8;
            sub.Add(chip);
            var seed = new Label(c.SeedLabel);
            seed.AddToClassList("tp-seed");
            sub.Add(seed);
            body.Add(sub);

            var blurb = new Label(c.Blurb);
            blurb.AddToClassList("tp-blurb");
            body.Add(blurb);

            var bars = new VisualElement();
            bars.AddToClassList("tp-bars");
            bars.Add(Bar("打力", c.Batting, false));
            bars.Add(Bar("投手", c.Pitching, false));
            bars.Add(Bar("守備", c.Defense, true));
            body.Add(bars);

            team.Add(body);
            return team;
        }

        private static VisualElement Bar(string label, int value, bool defense)
        {
            var bar = new VisualElement();
            bar.AddToClassList("tp-bar");

            var top = new VisualElement();
            top.AddToClassList("tp-bar__top");
            var l = new Label(label);
            l.AddToClassList("tp-bar__label");
            var val = new Label(value.ToString());
            val.AddToClassList("tp-bar__val");
            top.Add(l);
            top.Add(val);
            bar.Add(top);

            var track = new VisualElement();
            track.AddToClassList("tp-track");
            var fill = new VisualElement();
            fill.AddToClassList("tp-fill");
            if (defense) fill.AddToClassList("tp-fill--def");
            fill.style.width = Length.Percent(Mathf.Clamp(value, 0, 100));
            track.Add(fill);
            bar.Add(track);

            return bar;
        }

        private void SetText(string name, string text)
        {
            var label = _root.Q<Label>(name);
            if (label != null) label.text = text;
        }
    }
}
