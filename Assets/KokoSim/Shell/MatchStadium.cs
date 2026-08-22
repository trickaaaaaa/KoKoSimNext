using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 大会種別 → その大会を戦う球場の判定寸法（設計書13 §1）。
    ///
    /// <para><b>いまは甲子園だけが固有の球場を持つ。</b> 地方大会の球場割当
    /// （序盤＝市営級／準決勝・決勝＝県営級。engine の <c>StadiumPlan</c>）は実ゲームへ未配線で、
    /// <see cref="FieldGeometry"/> の初期値のまま走っている。その初期値は<b>距離が甲子園と同じ</b>
    /// （95/95/118）でフェンスだけ高い（4.0m）ので、甲子園を挿すと効くのは<b>フェンス高 1.4m</b>だけ
    /// ＝「聖地は一発が出る」は出るが「地方は広い」はまだ無い。</para>
    ///
    /// <para>判定に使う寸法をここ1箇所に集約する＝自校戦（<see cref="PlayerMatchResolver"/>）と
    /// 裏試合（<see cref="KoshienEntry"/>）が別々の球場で試合をしないため。</para>
    /// </summary>
    public static class MatchStadium
    {
        /// <summary>
        /// その大会の球場寸法。甲子園以外は既定（＝これまでと1ビットも変わらない）。
        /// 戻り値が null のときは <c>GameContext</c> の初期値をそのまま使う（＝呼び出し側で分岐しない）。
        /// </summary>
        public static FieldGeometry For(TournamentKind kind)
            => kind == TournamentKind.SummerKoshien ? Koshien : Default;

        /// <summary>甲子園（C#既定＝data/stadiums.yaml の koshien と一致・不変条件#4）。</summary>
        private static readonly FieldGeometry Koshien = Stadium.Koshien.ToFieldGeometry();

        /// <summary>既定（球場割当が未配線の大会。従来どおり）。</summary>
        private static readonly FieldGeometry Default = new FieldGeometry();
    }
}
