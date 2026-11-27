using System.Collections.Generic;
using System.Text;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;
using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Match
{
    /// <summary>交代の説明文の部品の種類。Player＝背番号と名前（太字の墨）・Pos＝守備位置のチップ・Muted＝補足（淡色）。</summary>
    public enum NotePartKind { Text, Player, Pos, Muted }

    /// <summary>交代の説明文の部品1つ。</summary>
    public readonly struct NotePart
    {
        public readonly NotePartKind Kind;
        public readonly string Text;
        public NotePart(NotePartKind kind, string text) { Kind = kind; Text = text; }
    }

    /// <summary>
    /// 選手交代のポップアップの表示用の決まり（View から切り出した純粋な部分・EditMode で検査する）。
    /// 下の説明文を部品列で組む（選手は太字・守備位置はチップ・注記は淡色）のと、比較の表の太字の決め方。
    /// 文の中身（誰が退場するか・再出場できるか）は従来のフッター文と同じ。
    /// </summary>
    public static class SubstitutionDisplay
    {
        /// <summary>「10 桑原 陸」（背番号の無い選手は名前だけ）。</summary>
        public static string Label(Player p)
            => p == null ? "—" : p.UniformNumber > 0 ? p.UniformNumber + " " + p.Name : p.Name;

        /// <summary>背番号の箱の文字（無ければ「—」）。</summary>
        public static string Number(Player p) => p != null && p.UniformNumber > 0 ? p.UniformNumber.ToString() : "—";

        /// <summary>カードの1行目の「3年・右投右打」（学年の無い選手は投打だけ）。</summary>
        public static string GradeAndHand(Player p)
        {
            if (p == null) return "";
            var hand = HandednessLabels.Combined(p.Throws, p.Bats);
            return p.Grade > 0 ? p.Grade + "年・" + hand : hand;
        }

        /// <summary>
        /// 比較の表の太字：高いほうを太字、低いほうを細字（色は変えない）。同じ値なら両方太字。
        /// 片側しか値が無い（選手を片方しか選んでいない）ときは比べないので両方細字。
        /// </summary>
        public static (bool BoldA, bool BoldB) Weights(int a, int b)
        {
            if (a < 0 || b < 0) return (false, false);
            return (a >= b, b >= a);
        }

        /// <summary>部品列を1本の文にする（確定した交代の一覧・撮影の検査用）。</summary>
        public static string Plain(IEnumerable<NotePart> parts)
        {
            var sb = new StringBuilder();
            foreach (var p in parts)
            {
                if (sb.Length > 0 && (p.Kind == NotePartKind.Player || p.Kind == NotePartKind.Pos)) sb.Append(' ');
                sb.Append(p.Text);
                if (p.Kind == NotePartKind.Player || p.Kind == NotePartKind.Pos) sb.Append(' ');
            }
            return sb.ToString().Replace("  ", " ").Trim();
        }

        // ── 文の組み立て（操作ごと） ──

        private static NotePart T(string s) => new NotePart(NotePartKind.Text, s);
        private static NotePart P(Player p) => new NotePart(NotePartKind.Player, Label(p));
        private static NotePart Pos(FieldPosition pos) => new NotePart(NotePartKind.Pos, FieldPositionNames.Abbr(pos));
        private static NotePart M(string s) => new NotePart(NotePartKind.Muted, s);

        /// <summary>守備固め：入る選手がその位置に就き、退く選手は退場。</summary>
        public static List<NotePart> Assign(Player incoming, FieldPosition pos, Player outgoing)
            => new List<NotePart> { P(incoming), T("が"), Pos(pos), T("に就き、"), P(outgoing), T("は退場"), M("（再出場できない）") };

        /// <summary>継投：入る投手が登板し、今の投手は退場。</summary>
        public static List<NotePart> Relieve(Player incoming, Player current)
            => new List<NotePart> { P(incoming), T("が登板し、"), P(current), T("は退場"), M("（再登板できない）") };

        /// <summary>元の投手を守備に回す継投。DH制なら DH も解除される。</summary>
        public static List<NotePart> Convert(Player incoming, Player current, FieldPosition pos, Player outgoing, Player dhOrNull)
        {
            var list = new List<NotePart>
            {
                P(incoming), T("が登板し、"), P(current), T("は"), Pos(pos), T("を守る"), M("（後で再登板できる）"),
                T("。"), Pos(pos), T("の"), P(outgoing), T("は退場"),
            };
            if (dhOrNull != null) { list.Add(T("。DHは解除され、")); list.Add(P(dhOrNull)); list.Add(T("も退場")); list.Add(M("（以降DHなし）")); }
            return list;
        }

        /// <summary>投手と野手の入れ替え（非DH＝誰も退場しない）。</summary>
        public static List<NotePart> PromoteFielder(Player incoming, Player current, FieldPosition pos)
            => new List<NotePart> { P(incoming), T("がマウンドへ、"), P(current), T("は"), Pos(pos), T("を守る"), M("（誰も退場しない・後で再登板できる）") };

        /// <summary>投手と野手の入れ替え（DH制＝元のDHが守備に就き、今の投手は退場）。</summary>
        public static List<NotePart> PromoteFielderDh(Player incoming, Player dh, FieldPosition pos, Player current)
            => new List<NotePart> { P(incoming), T("がマウンドへ。DHの"), P(dh), T("が"), Pos(pos), T("を守り、"), P(current), T("は退場"), M("（以降DHなし）") };

        /// <summary>守備位置の入れ替え（誰も退場しない）。</summary>
        public static List<NotePart> FielderSwap(Player a, FieldPosition posA, Player b, FieldPosition posB)
            => new List<NotePart> { P(a), T("が"), Pos(posB), T("へ、"), P(b), T("が"), Pos(posA), T("へ移る"), M("（誰も退場しない）") };

        /// <summary>DH解除（DHがその守備位置に就き、その位置の選手は退場）。</summary>
        public static List<NotePart> DhToPos(Player dh, FieldPosition pos, Player outgoing)
            => new List<NotePart> { T("DHの"), P(dh), T("が"), Pos(pos), T("を守り、"), P(outgoing), T("は退場。投手が打順に入る"), M("（以降DHなし）") };

        /// <summary>DH解除（DHをそのまま退かせる）。</summary>
        public static List<NotePart> DhWithdraw(Player dh)
            => new List<NotePart> { T("DHの"), P(dh), T("を退かせ、投手が打順に入る"), M("（以降DHなし）") };

        /// <summary>DHの登板（issue #1725）。DHが自分の打順のままマウンドへ、今の投手は退場。</summary>
        public static List<NotePart> DhToMound(Player dh, Player current)
            => new List<NotePart> { T("DHの"), P(dh), T("が登板し、"), P(current), T("は退場"), M("（再登板できない・以降DHなし）") };

        /// <summary>代打。</summary>
        public static List<NotePart> PinchHit(Player incoming, Player outgoing)
            => new List<NotePart> { P(incoming), T("が代打に出て、"), P(outgoing), T("は退場"), M("（再出場できない）") };

        /// <summary>代走。</summary>
        public static List<NotePart> PinchRun(Player incoming, string baseName, Player outgoing)
            => new List<NotePart> { P(incoming), T("が" + baseName + "の代走に出て、"), P(outgoing), T("は退場"), M("（再出場できない）") };

        /// <summary>まだ確定できないときの短い案内（淡色の1部品）。</summary>
        public static List<NotePart> Hint(string text) => new List<NotePart> { M(text) };
    }
}
