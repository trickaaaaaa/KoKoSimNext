using System;
using System.Collections.Generic;
using KokoSim.Engine.Nation.Tournaments;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Tournament
{
    /// <summary>
    /// 新聞型トーナメント表（issue #645）。座標は engine の <see cref="NewspaperBracketLayout"/> が決め、
    /// ここは線（Painter2D）と校名・スコア（Label）を置くだけ。HTML 書き出し（<see cref="TournamentPamphletHtml"/>）も
    /// 同じ座標を SVG に描く＝画面と紙面の形が食い違わない。
    /// 校名は1回戦の端にだけ書き、勝った側の線を太線で次段へ伸ばす。自校の経路は自校色（盤面の状態色扱い）。
    /// </summary>
    public sealed class NewspaperBracketElement : VisualElement
    {
        /// <summary>
        /// 画面用の寸法。校名欄は旧カード幅（--brk-card-w 176px）、1段は段名「準々決勝」（--fs-th 18px×4字）が収まる幅、
        /// 行ピッチは --fs-label 19px の校名が詰まって読める高さ。段名帯は ScrollView 側の帯（--h-thead）が持つので0。
        /// </summary>
        public static readonly NewspaperBracketMetrics Metrics =
            new NewspaperBracketMetrics(NameWidth: 176, RoundWidth: 72, RowPitch: 28, HeaderHeight: 0, ChampionGap: 56);

        private const float ThinWidth = 1.5f;
        private const float WonWidth = 3.5f;

        private static readonly CustomStyleProperty<Color> LineProp = new CustomStyleProperty<Color>("--brk-line");
        private static readonly CustomStyleProperty<Color> WinProp = new CustomStyleProperty<Color>("--brk-line-win");
        private static readonly CustomStyleProperty<Color> FavProp = new CustomStyleProperty<Color>("--brk-line-fav");

        // tokens.uss の値の写し（USS が解決されるまでの初期値）。正は tokens.uss。
        private Color _line = new Color(0.541f, 0.533f, 0.482f);   // #8A887B = --tx-3
        private Color _win = new Color(0.075f, 0.082f, 0.071f);    // #131512 = --ink
        private Color _fav = new Color(0.243f, 0.557f, 0.816f);    // #3E8ED0 = --sky

        private readonly NewspaperBracket _b;

        /// <summary>自校の最新の試合の位置に置いた目印（初期スクロール先）。自校が居なければ null。</summary>
        public VisualElement FocusMarker { get; }

        public NewspaperBracketElement(NewspaperBracket bracket, Action<string> onSchoolClick)
        {
            _b = bracket;
            AddToClassList("brk-paper");
            style.width = (float)bracket.Width;
            style.height = (float)bracket.Height;
            generateVisualContent += OnGenerate;
            RegisterCallback<CustomStyleResolvedEvent>(OnStyles);

            foreach (var leaf in bracket.Leaves) Add(Leaf(leaf, onSchoolClick));
            foreach (var s in bracket.Scores) Add(Score(s));
            Add(Champion(bracket));

            if (bracket.ManagerFocus is { } f)
            {
                FocusMarker = new VisualElement { pickingMode = PickingMode.Ignore };
                FocusMarker.style.position = Position.Absolute;
                FocusMarker.style.left = (float)f.X;
                FocusMarker.style.top = (float)f.Y;
                FocusMarker.style.width = 1;
                FocusMarker.style.height = 1;
                Add(FocusMarker);
            }
        }

        /// <summary>段名帯（縦スクロールに追従する帯の中身）。列の X は樹形図と同じ座標から取る。</summary>
        public static VisualElement BuildHead(NewspaperBracket bracket)
        {
            var head = new VisualElement();
            head.AddToClassList("brk-head__row");
            head.style.width = (float)bracket.Width;
            var w = (float)Metrics.RoundWidth;
            foreach (var h in bracket.Heads)
            {
                var l = new Label(h.Name);
                l.AddToClassList("brk-col__name");
                l.style.position = Position.Absolute;
                l.style.left = (float)h.X - w / 2;
                l.style.width = w;
                head.Add(l);
            }
            return head;
        }

        private void OnStyles(CustomStyleResolvedEvent e)
        {
            var cs = e.customStyle;
            if (cs.TryGetValue(LineProp, out var l)) _line = l;
            if (cs.TryGetValue(WinProp, out var w)) _win = w;
            if (cs.TryGetValue(FavProp, out var f)) _fav = f;
            MarkDirtyRepaint();
        }

        private void OnGenerate(MeshGenerationContext mgc)
        {
            var p = mgc.painter2D;
            p.lineJoin = LineJoin.Miter;
            p.lineCap = LineCap.Butt;
            // 細線→太線→自校の順に重ねる（決勝の中央で左右の縦線が重なっても勝者と自校が上に来る）。
            foreach (var pass in new[] { 0, 1, 2 })
                foreach (var path in _b.Paths)
                {
                    var won = path.State == BracketLineState.Won;
                    var order = path.IsManager ? 2 : won ? 1 : 0;
                    if (order != pass) continue;
                    p.strokeColor = path.IsManager ? _fav : won ? _win : _line;
                    p.lineWidth = won ? WonWidth : ThinWidth;
                    p.BeginPath();
                    p.MoveTo(V(path.Points[0]));
                    for (var i = 1; i < path.Points.Count; i++) p.LineTo(V(path.Points[i]));
                    p.Stroke();
                }
        }

        private static Vector2 V(BracketPoint pt) => new Vector2((float)pt.X, (float)pt.Y);

        private static VisualElement Leaf(BracketLeaf leaf, Action<string> onSchoolClick)
        {
            var m = Metrics;
            var l = new Label(leaf.Name);
            l.AddToClassList("brk-leaf");
            l.AddToClassList("f-display");   // 校名は明朝（書体3役）
            l.AddToClassList(leaf.Side == BracketSide.Right ? "brk-leaf--right" : "brk-leaf--left");
            if (leaf.IsManager) l.AddToClassList("brk-leaf--fav");
            else if (leaf.IsEliminated) l.AddToClassList("brk-leaf--out");
            var w = (float)m.NameWidth - 8;
            l.style.left = leaf.Side == BracketSide.Right ? (float)leaf.LineStart.X + 8 : (float)leaf.LineStart.X - 8 - w;
            l.style.top = (float)(leaf.LineStart.Y - m.RowPitch / 2);
            l.style.width = w;
            l.style.height = (float)m.RowPitch;
            // 校別詳細（#189）は1回戦の端の校名から開く（段ごとの校名枠は無くなった）。
            var name = leaf.Name;
            l.RegisterCallback<ClickEvent>(_ => onSchoolClick(name));
            return l;
        }

        private static VisualElement Score(BracketScoreMark s)
        {
            var m = Metrics;
            var box = new VisualElement { pickingMode = PickingMode.Ignore };
            box.AddToClassList("brk-scorebox");
            var w = (float)m.RoundWidth - 6;
            var toLeft = s.Side == BracketSide.Right;   // 右ブロック／決勝の左側は合流点から左へ書く
            box.style.left = toLeft ? (float)s.Anchor.X - w : (float)s.Anchor.X;
            box.style.top = (float)(s.Anchor.Y - m.RowPitch / 2);
            box.style.width = w;
            box.style.height = (float)m.RowPitch;
            box.style.justifyContent = toLeft ? Justify.FlexEnd : Justify.FlexStart;

            var num = new Label(s.Text);
            num.AddToClassList("brk-score");
            num.AddToClassList("f-num");   // スコアは純数値＝コンデンス体
            if (s.IsManager) num.AddToClassList("brk-score--fav");
            else if (!s.IsWinner) num.AddToClassList("brk-score--out");
            box.Add(num);
            if (s.Mercy)
            {
                // コールド勝ちの印。数字と和文は Label を分ける（Oswald は和文グリフを持たない）。
                var mercy = new Label("コ");
                mercy.AddToClassList("brk-mercy");
                box.Add(mercy);
            }
            return box;
        }

        private static VisualElement Champion(NewspaperBracket b)
        {
            var m = Metrics;
            var box = new VisualElement { pickingMode = PickingMode.Ignore };
            box.AddToClassList("brk-champ");
            var w = (float)m.NameWidth * 1.5f;
            box.style.left = (float)b.ChampionAnchor.X - w / 2;
            box.style.top = (float)(b.ChampionAnchor.Y - m.RowPitch);
            box.style.width = w;
            box.style.height = (float)m.RowPitch;

            var cap = new Label("優勝");
            cap.AddToClassList("brk-champ__cap");
            box.Add(cap);
            // 優勝校が決まるまで名前は出さない（「未定」と書かない＝issue #645）。
            if (b.ChampionName != null)
            {
                var name = new Label(b.ChampionName);
                name.AddToClassList("brk-champ__name");
                name.AddToClassList("f-display");
                if (b.ManagerIsChampion) name.AddToClassList("brk-champ__name--fav");
                box.Add(name);
            }
            return box;
        }
    }
}
