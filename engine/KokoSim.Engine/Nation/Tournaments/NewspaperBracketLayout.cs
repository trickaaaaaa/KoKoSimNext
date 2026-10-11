namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>新聞型トーナメント表の寸法（描画側の単位＝ゲーム内は px、HTML は SVG ユーザー単位）。</summary>
public sealed record NewspaperBracketMetrics(
    double NameWidth = 150,     // 1回戦の端の校名欄の幅
    double RoundWidth = 44,     // 1段ぶんの横幅（線の長さ）
    double RowPitch = 24,       // 端（校名）1行の縦ピッチ
    double HeaderHeight = 34,   // 上端の段名帯の高さ（座標の原点をこの下にずらす）
    double ChampionGap = 40);   // 決勝の合流点から優勝校名までの縦の距離

public readonly record struct BracketPoint(double X, double Y);

/// <summary>左右どちらのブロックか（右は左右反転＝決勝側が左）。中央は決勝・優勝欄。</summary>
public enum BracketSide { Left, Right, Center }

/// <summary>線の状態。勝った側＝太線、負けた側と未消化＝細線。</summary>
public enum BracketLineState { Pending, Won, Lost }

/// <summary>
/// 1回戦の端に置く校名。<see cref="LineStart"/> は線の始点＝校名はその外側（左ブロックは左、右ブロックは右）に寄せて書く。
/// </summary>
public sealed record BracketLeaf(string Name, bool IsManager, bool IsEliminated, BracketSide Side, BracketPoint LineStart);

/// <summary>1校が1試合へ入る線（折れ線）。勝ち上がりの経路は試合ごとのこの線の連なり。</summary>
public sealed record BracketPath(IReadOnlyList<BracketPoint> Points, BracketLineState State, bool IsManager);

/// <summary>
/// 試合の線の脇に添えるスコア。<see cref="Anchor"/> から <see cref="Side"/>=Left なら右へ、Right なら左へ書く
/// （どちらも決勝側＝線の進む向き）。<see cref="Mercy"/> はコールドゲームの勝者側にだけ立つ。
/// </summary>
public sealed record BracketScoreMark(string Text, BracketPoint Anchor, BracketSide Side, bool IsWinner, bool IsManager, bool Mercy);

/// <summary>段名（「1回戦」「準決勝」…）。X は段の中心。</summary>
public sealed record BracketRoundHead(string Name, double X);

/// <summary>新聞型トーナメント表の描画データ一式。ゲーム内（Painter2D）と HTML（SVG）が同じものを描く。</summary>
public sealed record NewspaperBracket(
    double Width,
    double Height,
    IReadOnlyList<BracketLeaf> Leaves,
    IReadOnlyList<BracketPath> Paths,
    IReadOnlyList<BracketScoreMark> Scores,
    IReadOnlyList<BracketRoundHead> Heads,
    BracketPoint ChampionAnchor,
    string? ChampionName,
    bool ManagerIsChampion,
    BracketPoint? ManagerFocus)
{
    public static readonly NewspaperBracket Empty = new(0, 0, [], [], [], [], default, null, false, null);
}

/// <summary>
/// エンジンの樹形図（<see cref="TournamentBracketView.Rounds"/>）を新聞型トーナメント表の座標へ写す（issue #645）。
/// 校名は1回戦の端（葉）にだけ書き、2回戦以降は線だけで勝ち上がりを表す。シード校（不戦勝）は
/// 1回戦の端に1行だけ置き、空き枠は描かない（#188）。甲子園ブラケット風に左右2ブロックへ割り、
/// 決勝を中央で向き合わせる。表示専用・RNG 非消費。
/// </summary>
public static class NewspaperBracketLayout
{
    /// <summary>合流点の縦線からスコアまでの隙間（太線に数字が触れない幅）。</summary>
    public const double ScoreGap = 6;

    public static NewspaperBracket Build(TournamentBracketView? view, NewspaperBracketMetrics? metrics = null)
    {
        if (view?.Rounds is not { Count: > 0 } rounds || rounds[0].Cards.Count == 0) return NewspaperBracket.Empty;
        var m = metrics ?? new NewspaperBracketMetrics();
        var final = rounds.Count - 1;

        var halfWidth = m.NameWidth + final * m.RoundWidth;
        var width = 2 * halfWidth + 2 * m.RoundWidth;
        var centerX = width / 2;
        double Mirror(double x, BracketSide side) => side == BracketSide.Right ? width - x : x;

        // 敗退した校（消化済みカードの負け側）。端の校名をグレーにするのに使う。
        var eliminated = new HashSet<string>();
        foreach (var r in rounds)
            foreach (var c in r.Cards)
                if (c.IsPlayed)
                {
                    var loser = c.Top.IsWinner ? c.Bottom : c.Top;
                    if (loser.TeamName is { } n) eliminated.Add(n);
                }

        var leaves = new List<BracketLeaf>();
        var paths = new List<BracketPath>();
        var scores = new List<BracketScoreMark>();
        BracketPoint? focus = null;

        // 1回戦の端。左右それぞれ上から1行ずつ（空き枠は行を取らない）。
        var leafAt = new Dictionary<(int Card, int Slot), BracketPoint>();
        var round0 = rounds[0].Cards;
        var rows = new[] { 0, 0 };
        for (var i = 0; i < round0.Count; i++)
        {
            var card = round0[i];
            for (var s = 0; s < 2; s++)
            {
                var slot = s == 0 ? card.Top : card.Bottom;
                if (slot.TeamName is null) continue;
                // 決勝しかない（2校）大会は上下をそのまま左右に振る。
                var side = final == 0 ? (s == 0 ? BracketSide.Left : BracketSide.Right)
                    : i < round0.Count / 2 ? BracketSide.Left : BracketSide.Right;
                var row = rows[side == BracketSide.Left ? 0 : 1]++;
                var p = new BracketPoint(Mirror(m.NameWidth, side), m.HeaderHeight + (row + 0.5) * m.RowPitch);
                leafAt[(i, s)] = p;
                leaves.Add(new BracketLeaf(slot.TeamName, slot.IsManager, eliminated.Contains(slot.TeamName), side, p));
            }
        }

        // 各カードの出口（勝者が次段へ伸びる点）。入力が1つも無いカードは null。
        var outAt = new Dictionary<(int Round, int Card), BracketPoint>();
        for (var r = 0; r <= final; r++)
        {
            var cards = rounds[r].Cards;
            for (var i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                var side = r == final ? BracketSide.Center
                    : i < cards.Count / 2 ? BracketSide.Left : BracketSide.Right;
                var jointX = r == final ? centerX : Mirror(m.NameWidth + (r + 1) * m.RoundWidth, side);

                BracketPoint? Source(int s) => r == 0
                    ? leafAt.TryGetValue((i, s), out var lp) ? lp : null
                    : outAt.TryGetValue((r - 1, 2 * i + s), out var op) ? op : null;
                var a = Source(0);
                var b = Source(1);
                if (a is null && b is null) continue;

                if (a is null || b is null)
                {
                    // 不戦勝: 線を次段の合流点までまっすぐ伸ばすだけ（縦の合流もスコアも無い）。
                    // 次の試合に勝っていれば太線で続け、勝ち上がりの経路を途切れさせない。
                    var src = (a ?? b)!.Value;
                    var slot = a is null ? card.Bottom : card.Top;
                    var wonNext = r < final && NextSlot(rounds, r, i).IsWinner;
                    paths.Add(new BracketPath([src, new BracketPoint(jointX, src.Y)],
                        wonNext ? BracketLineState.Won : BracketLineState.Pending, slot.IsManager));
                    var bye = new BracketPoint(jointX, src.Y);
                    outAt[(r, i)] = bye;
                    if (slot.IsManager) focus = bye;
                    continue;
                }

                var midY = (a.Value.Y + b.Value.Y) / 2;
                var joint = new BracketPoint(jointX, midY);
                outAt[(r, i)] = joint;
                AddInput(a.Value, card.Top, isTop: true);
                AddInput(b.Value, card.Bottom, isTop: false);
                if (card.Top.IsManager || card.Bottom.IsManager) focus = joint;

                void AddInput(BracketPoint src, BracketSlot slot, bool isTop)
                {
                    var state = !card.IsPlayed ? BracketLineState.Pending
                        : slot.IsWinner ? BracketLineState.Won : BracketLineState.Lost;
                    paths.Add(new BracketPath(
                        [src, new BracketPoint(jointX, src.Y), new BracketPoint(jointX, midY)], state, slot.IsManager));
                    if (slot.Score is not { } score) return;

                    // 通常の段: 合流点の決勝側、入ってきた線の高さ（そこは線が無い）。
                    // 決勝: 中央で左右の線が向き合うので、それぞれの線の上に置く。
                    BracketPoint anchor;
                    BracketSide textSide;
                    if (r == final)
                    {
                        textSide = isTop ? BracketSide.Right : BracketSide.Left;
                        anchor = new BracketPoint(centerX + (isTop ? -ScoreGap : ScoreGap), src.Y - 0.45 * m.RowPitch);
                    }
                    else
                    {
                        textSide = side;
                        anchor = new BracketPoint(jointX + (side == BracketSide.Left ? ScoreGap : -ScoreGap), src.Y);
                    }
                    scores.Add(new BracketScoreMark(score.ToString(), anchor, textSide,
                        slot.IsWinner, slot.IsManager, card.MercyEnded && slot.IsWinner));
                }
            }
        }

        // 優勝欄: 決勝の合流点から上へ1本。優勝校が決まるまで名前は出さない（「未定」と書かない）。
        var champion = view.ChampionName;
        var finalJoint = outAt.TryGetValue((final, 0), out var fj) ? fj : new BracketPoint(centerX, m.HeaderHeight);
        var champAnchor = new BracketPoint(centerX, finalJoint.Y - m.ChampionGap);
        paths.Add(new BracketPath([finalJoint, champAnchor],
            champion is null ? BracketLineState.Pending : BracketLineState.Won, view.ManagerIsChampion));

        var heads = new List<BracketRoundHead>();
        for (var r = 0; r < final; r++)
        {
            var x = m.NameWidth + (r + 0.5) * m.RoundWidth;
            heads.Add(new BracketRoundHead(rounds[r].RoundName, x));
            heads.Add(new BracketRoundHead(rounds[r].RoundName, width - x));
        }
        heads.Add(new BracketRoundHead(rounds[final].RoundName, centerX));

        var height = m.HeaderHeight + Math.Max(rows[0], rows[1]) * m.RowPitch;

        // 小さな大会では優勝欄が段名帯へ食い込む。全体を下げて逃がす（優勝校名1行ぶんの余白を見る）。
        var shift = Math.Max(0, m.HeaderHeight + m.RowPitch - champAnchor.Y);
        if (shift > 0)
        {
            BracketPoint S(BracketPoint p) => new(p.X, p.Y + shift);
            leaves = leaves.Select(l => l with { LineStart = S(l.LineStart) }).ToList();
            paths = paths.Select(p => p with { Points = p.Points.Select(S).ToList() }).ToList();
            scores = scores.Select(s => s with { Anchor = S(s.Anchor) }).ToList();
            champAnchor = S(champAnchor);
            if (focus is { } f) focus = S(f);
            height += shift;
        }

        return new NewspaperBracket(width, height, leaves, paths, scores, heads,
            champAnchor, champion, view.ManagerIsChampion, focus);
    }

    private static BracketSlot NextSlot(IReadOnlyList<BracketRound> rounds, int r, int i)
    {
        var next = rounds[r + 1].Cards[i / 2];
        return i % 2 == 0 ? next.Top : next.Bottom;
    }
}
