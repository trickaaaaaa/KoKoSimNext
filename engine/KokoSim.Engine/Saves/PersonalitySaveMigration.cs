using KokoSim.Engine.Players;
using KokoSim.Engine.Saves.Json;

namespace KokoSim.Engine.Saves;

/// <summary>
/// 性格の4軸化（設計書23 §1.5・issue #932・セーブ v27）の読み込み側の移行。乱数は使わない＝ロードのたびに同じ結果。
///
/// <para>v26 までのセーブは性格を8タイプの列挙（<c>personality</c>）で持っていた。軸のキー（<c>receptiveness</c> 等）が
/// 無ければ、旧タイプのマスの<b>代表値</b>（<see cref="PersonalityCoefficients"/> の種の表）へ置き換える。
/// 統率傾向（<c>leadership</c>）は既に個人の値を持っているので触らない。</para>
///
/// <para>自校部員（<see cref="Season.DevelopingPlayer"/>）は旧 <c>personalityFactor</c>（自主成長の個人倍率＝タイプ中心＋σ0.06 の
/// ジッタ）を持っていた。勤勉さは他の軸と同じく<b>旧タイプの代表値</b>に置き、代表値の勤勉さで説明できない残り
/// （<c>personalityFactor − SelfGrowthFactor(代表値)</c>）を<b>隠れた個人差</b>（<c>SelfGrowthJitter</c>・v30・Q47）に入れる。
/// 旧ジッタと新しい個人差は同じもの（タイプとは別の、画面に出ない個人差）なので、これで旧 personalityFactor をそのまま再現する
/// （上下限 0.75〜1.25 も旧と同じ）。勤勉さへ逆算する旧方式（v27〜v29 の実装）は、個人差を勤勉さへ押し込むので
/// 生成では出ない端の値になり、マスをまたぐ個体の効きを保てなかった。</para>
/// </summary>
internal static class PersonalitySaveMigration
{
    private static readonly PersonalityCoefficients Defaults = new();

    /// <summary>試合用 Player（AI校ロスター等）の②③④。</summary>
    public static (int Receptiveness, int Diligence, int Selflessness) Axes(JsonObject o)
    {
        if (o.Has("receptiveness"))
            return (o.Int("receptiveness"), o.Int("diligence"), o.Int("selflessness"));
        var legacy = Legacy(o);
        var pp = Defaults.Profile(legacy);
        return (pp.Receptiveness, pp.Diligence, pp.Selflessness);
    }

    /// <summary>自校部員の②③④（旧セーブは旧タイプの代表値。旧 personalityFactor は個人差の側へ＝<see cref="DevelopingSelfGrowthJitter"/>）。</summary>
    public static (int Receptiveness, int Diligence, int Selflessness) DevelopingAxes(JsonObject o) => Axes(o);

    /// <summary>
    /// 自校部員の自主成長の隠れた個人差（Q47）。v30 以降は保存値。v26 以前（<c>personalityFactor</c> を持つ）は
    /// 旧倍率から代表値の勤勉さの効きを引いた残り＝<c>SelfGrowthFactor(代表値, 残り)</c> が旧倍率と一致する。
    /// 旧タイプが Normal（手組み・未設定）なら 0（無補正の安全弁を保つ）。v27〜v29 は個人差を持たない＝0。
    /// </summary>
    public static double DevelopingSelfGrowthJitter(JsonObject o)
    {
        if (o.Has("growthJitter")) return o.Double("growthJitter");
        if (o.Has("receptiveness") || !o.Has("personalityFactor")) return 0.0;
        var legacy = Legacy(o);
        if (legacy == Personality.Normal) return 0.0;
        return JitterFromLegacyFactor(o.Double("personalityFactor"), Defaults.Profile(legacy).Diligence, Defaults);
    }

    /// <summary>旧 PersonalityFactor − 勤勉さ <paramref name="diligence"/> の効き（上下限で切る前の値）。</summary>
    internal static double JitterFromLegacyFactor(double factor, int diligence, PersonalityCoefficients pc)
        => factor - pc.SelfGrowthFactor(diligence);

    private static Personality Legacy(JsonObject o)
        => o.Has("personality") ? EnumJson.Read<Personality>(o.Str("personality")) : Personality.Normal;
}
