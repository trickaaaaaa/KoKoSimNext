using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Components;  // 部品辞書（RankChip / RadarChartView）
using KokoSim.Unity.Shell;       // ScreenRouter / GameSession

namespace KokoSim.Unity.MatchPreview
{
    /// <summary>
    /// 試合開始前（対戦カード）画面のコントローラ（issue #7・A案）。スタメン設定OKの直後に挟まり、
    /// 左＝自校／右＝相手校を同一構成で並べる（先攻後攻・総合ランク・6角形・スタメン一覧）。
    /// 戻る導線は持たない（スタメン決定後は試合をやるのみ）。主要操作は「試合開始」1つだけ（UI原則⑦）で、
    /// 押すとホーム経由でライブ観戦（MatchLive）へ入る＝既存の試合開始フローに合流する。
    /// 表示専用＝ここではエンジンを進めない（帯不変・決定論に影響なし）。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class MatchPreviewController : MonoBehaviour
    {
        private MatchPreviewState _state;
        private VisualElement _root;
        private RadarChartView _leftRadar, _rightRadar;

        private const float RadiusFactor = 0.34f;
        private const float LabelOffset = 1.22f;

        private void OnEnable()
        {
            _state = new MatchPreviewState();
            _root = GetComponent<UIDocument>().rootVisualElement;

            var start = _root.Q<Button>("mp-start");
            if (start != null) start.clicked += OnStart;

            // レーダーは部品辞書の共通部品。軸の並び・塗り色はチーム総合力パネルと同一。
            _leftRadar = new RadarChartView(_root.Q<VisualElement>("mp-left-radar"), RadiusFactor, LabelOffset);
            _rightRadar = new RadarChartView(_root.Q<VisualElement>("mp-right-radar"), RadiusFactor, LabelOffset);

            Render();
        }

        private void Render()
        {
            var v = _state.BuildView();
            SetText("mp-match", v.MatchLine);
            RenderSide("mp-left", v.Own, _leftRadar);
            RenderSide("mp-right", v.Opponent, _rightRadar);
        }

        private void RenderSide(string prefix, MatchPreviewSideView side, RadarChartView radar)
        {
            SetText(prefix + "-cap", side.SideCaption);
            SetText(prefix + "-name", side.TeamName);
            SetText(prefix + "-turn", side.OrderCaption);
            SetText(prefix + "-value", side.Lineup.Count > 0 ? "(" + side.OverallValue + ")" : "");

            var rank = _root.Q<VisualElement>(prefix + "-rank");
            if (rank != null)
            {
                rank.Clear();
                if (side.Lineup.Count > 0) rank.Add(UiComponents.RankChip(side.OverallGrade));
            }

            radar.SetData(side.Radar, side.OverallGrade);

            var host = _root.Q<VisualElement>(prefix + "-lineup");
            if (host == null) return;
            host.Clear();
            foreach (var s in side.Lineup) host.Add(Row(s));
            // DH制のときだけ打順外の先発投手を最下段に足す（非DHは打順内の「投」がそれに当たる）。
            if (side.StartingPitcher != null)
            {
                var head = new Label("先発");
                head.AddToClassList("mlineup-subhead");
                host.Add(head);
                host.Add(Row(side.StartingPitcher));
            }
        }

        // 1行＝部品辞書の MLineupRow（打順・守備位置・氏名・学年投打・総合ランク）。
        private static VisualElement Row(MatchPreviewSlotView s)
        {
            var row = new VisualElement();
            row.AddToClassList("mlineup-row");
            var ord = Text(s.Order, "mlineup-row__ord");
            ord.AddToClassList("f-num");
            row.Add(ord);
            row.Add(Text(s.PosKanji, "mlineup-row__pos"));
            row.Add(Text(s.Name, "mlineup-row__name"));
            row.Add(Text(s.Meta, "mlineup-row__meta"));
            row.Add(UiComponents.RankChip(s.Grade));
            return row;
        }

        private static Label Text(string text, string cls)
        {
            var l = new Label(text);
            l.AddToClassList(cls);
            return l;
        }

        private void SetText(string name, string text)
        {
            var l = _root.Q<Label>(name);
            if (l != null) l.text = text;
        }

        // ホームへ戻すと AwaitingMatchStart を見て自校戦のライブ観戦が始まる（既存フローに合流）。
        // クリック配信中に同期 Show するとUITKのイベント木が壊れるため遅延切替を使う。
        private void OnStart() => ScreenRouter.Instance?.ShowDeferred("HomeDashboard");
    }
}
