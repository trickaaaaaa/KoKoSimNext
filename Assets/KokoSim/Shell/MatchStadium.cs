using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 大会種別 → その大会を戦う球場の判定寸法（設計書13 §1）。
    ///
    /// <para>甲子園（夏本戦・センバツ）は固有の球場。夏・秋の県大会は汎用ティア方式
    /// （序盤＝市営級／準決勝・決勝＝県営級。<see cref="RegionalStadiumTier"/>）で寸法差を実際の判定へ反映する。
    /// それ以外（地区大会・神宮大会）は既定（従来どおり・未配線のまま）。</para>
    ///
    /// <para>判定に使う寸法をここ1箇所に集約する＝自校戦（<see cref="PlayerMatchResolver"/>）と
    /// 裏試合（<see cref="KoshienEntry"/>）が別々の球場で試合をしないため。</para>
    /// </summary>
    public static class MatchStadium
    {
        /// <summary>
        /// その大会の球場寸法。甲子園以外の全国大会（地区・神宮）は既定（＝これまでと1ビットも変わらない）。
        /// 戻り値が null のときは <c>GameContext</c> の初期値をそのまま使う（＝呼び出し側で分岐しない）。
        /// </summary>
        public static FieldGeometry For(TournamentKind kind)
            => kind is TournamentKind.SummerKoshien or TournamentKind.SenbatsuKoshien ? Koshien : Default;

        /// <summary>
        /// 県大会（夏・秋）向けのラウンド対応版。<paramref name="roundsRemaining"/> と
        /// <paramref name="rng"/> は <see cref="RegionalStadiumTier.For"/> へそのまま渡す
        /// （null可＝ラウンド不明・rng不要な呼び出しは序盤の固定球場へ落ちる）。
        /// 夏・秋以外の種別は<see cref="For(TournamentKind)"/>と同じ結果（甲子園はそのまま／それ以外は既定）。
        /// </summary>
        public static FieldGeometry For(TournamentKind kind, int? roundsRemaining, IRandomSource? rng)
            => kind switch
            {
                TournamentKind.Summer or TournamentKind.Autumn or TournamentKind.AutumnDistrict
                    => RegionalStadiumTier.For(roundsRemaining, rng),
                _ => For(kind),
            };

        /// <summary>甲子園（C#既定＝data/stadiums.yaml の koshien と一致・不変条件#4）。</summary>
        private static readonly FieldGeometry Koshien = Stadium.Koshien.ToFieldGeometry();

        /// <summary>既定（球場割当が未配線の大会＝地区大会・神宮大会。従来どおり）。</summary>
        private static readonly FieldGeometry Default = new FieldGeometry();
    }
}
