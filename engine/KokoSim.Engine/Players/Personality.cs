namespace KokoSim.Engine.Players;

/// <summary>
/// 性格タイプ（設計書23 §1.3・設計書01 §1.1）。性格は4軸（①統率傾向 <c>Leadership</c>／②素直さ <c>Receptiveness</c>／
/// ③勤勉さ <c>Diligence</c>／④試合での姿勢 <c>Selflessness</c>）の値で持ち、<b>タイプはその導出ラベル</b>
/// （各軸が50以上か未満かの2値×4＝16タイプ。<see cref="Personalities.Classify"/>）。タイプを別に保存しない。
/// <see cref="Normal"/> は②③④がすべて50ちょうど（＝効きが完全に無補正）の安全弁。手組みの Player/DevelopingPlayer が
/// 既定値のまま従来挙動（無補正）を保つためのもので、生成では出ない（σ12 の連続値で3軸が揃って50になることは事実上ない）。
/// 列挙は末尾追加のみ（セーブは名前で書くが、並びに依存する箇所を作らないため）。
/// </summary>
public enum Personality
{
    Normal,        // ふつう（②③④が全部50＝無補正の安全弁）
    HotBlood,      // 熱血漢: ↑統率 ↓素直 ↑勤勉 献身
    Hardworker,    // 努力家: ↓統率 ↑素直 ↑勤勉 献身
    Genius,        // 天才肌: ↓統率 ↓素直 ↑勤勉 目立ちたがり
    HonorStudent,  // 優等生: ↑統率 ↑素直 ↑勤勉 献身
    MyPace,        // マイペース: ↓統率 ↓素直 ↓勤勉 献身
    MoodMaker,     // ムードメーカー: ↓統率 ↑素直 ↓勤勉 目立ちたがり
    LoneWolf,      // 一匹狼: ↓統率 ↓素直 ↓勤勉 目立ちたがり
    Introvert,     // 内向的: ↓統率 ↑素直 ↓勤勉 献身
    // ---- 設計書23 §1.3 で増えた8タイプ（既存タイプの少し外れた形として生成の揺らぎから出る） ----
    Star,          // 花形: ↑統率 ↑素直 ↑勤勉 目立ちたがり
    Caretaker,     // 世話焼き: ↑統率 ↑素直 ↓勤勉 献身
    PartyAnimal,   // お祭り男: ↑統率 ↑素直 ↓勤勉 目立ちたがり
    GangLeader,    // ガキ大将: ↑統率 ↓素直 ↑勤勉 目立ちたがり
    BigBrother,    // 兄貴分: ↑統率 ↓素直 ↓勤勉 献身
    Rowdy,         // 暴れん坊: ↑統率 ↓素直 ↓勤勉 目立ちたがり
    Ambitious,     // 野心家: ↓統率 ↑素直 ↑勤勉 目立ちたがり
    Craftsman,     // 職人肌: ↓統率 ↓素直 ↑勤勉 献身
}

/// <summary>性格の4軸（設計書23 §1.1）。見極め（<c>InsightModel</c>）で軸ごとに開示するときの名指しに使う。</summary>
public enum PersonalityAxis
{
    Leadership,     // ①統率傾向（引っ張る／ついていく）
    Receptiveness,  // ②素直さ（素直／頑固）
    Diligence,      // ③勤勉さ（コツコツ／気分屋）
    Selflessness,   // ④試合での姿勢（献身／目立ちたがり）
}

/// <summary>性格タイプの導出・表示名・列挙ヘルパー（表示はデータ非依存で固定）。</summary>
public static class Personalities
{
    /// <summary>軸の「高い側」の境目（50以上＝高い側。設計書23 §1.3）。</summary>
    public const int AxisThreshold = 50;

    /// <summary>軸の中立値（1〜99 の真ん中）。</summary>
    public const int AxisNeutral = 50;

    /// <summary>
    /// 生成の「種」になるタイプ（設計書23 §1.4: 種を spawn_weight で引き、その代表値の周りに軸をばらつかせる）。
    /// 既存8タイプに、揺らぎだけではほぼ出ないマスの5タイプを足した（Q44 決着: 16タイプがそれぞれ3%以上出る）。
    /// </summary>
    public static readonly Personality[] Spawnable =
    {
        Personality.HotBlood, Personality.Hardworker, Personality.Genius, Personality.HonorStudent,
        Personality.MyPace, Personality.MoodMaker, Personality.LoneWolf, Personality.Introvert,
        Personality.Star, Personality.Ambitious, Personality.Craftsman, Personality.GangLeader, Personality.Rowdy,
    };

    /// <summary>4軸から導出される16タイプ（Normal を除く）。</summary>
    public static readonly Personality[] All16 =
    {
        Personality.HonorStudent, Personality.Star, Personality.Caretaker, Personality.PartyAnimal,
        Personality.HotBlood, Personality.GangLeader, Personality.BigBrother, Personality.Rowdy,
        Personality.Hardworker, Personality.Ambitious, Personality.Introvert, Personality.MoodMaker,
        Personality.Craftsman, Personality.Genius, Personality.MyPace, Personality.LoneWolf,
    };

    /// <summary>
    /// 4軸→タイプ（設計書23 §1.3 の表）。②③④がすべて50ちょうどなら <see cref="Personality.Normal"/>（無補正の安全弁）。
    /// 統率は Normal の判定に使わない（統率は性格とは別に主将・伝令で効く既存の値で、手組みの選手も自由に置くため）。
    /// </summary>
    public static Personality Classify(int leadership, int receptiveness, int diligence, int selflessness)
    {
        if (receptiveness == AxisNeutral && diligence == AxisNeutral && selflessness == AxisNeutral)
            return Personality.Normal;
        var idx = (High(leadership) ? 0 : 8) + (High(receptiveness) ? 0 : 4) + (High(diligence) ? 0 : 2)
                  + (High(selflessness) ? 0 : 1);
        return All16[idx];
    }

    /// <summary>軸の値が高い側（50以上）か。</summary>
    public static bool High(int axisValue) => axisValue >= AxisThreshold;

    /// <summary>タイプのマスで、その軸が高い側か（Normal は全軸高い側＝50扱い）。<see cref="Classify"/> の逆。</summary>
    public static bool CellHigh(Personality type, PersonalityAxis axis)
    {
        var i = System.Array.IndexOf(All16, type);
        if (i < 0) return true;
        return axis switch
        {
            PersonalityAxis.Leadership => (i & 8) == 0,
            PersonalityAxis.Receptiveness => (i & 4) == 0,
            PersonalityAxis.Diligence => (i & 2) == 0,
            _ => (i & 1) == 0,
        };
    }

    /// <summary>日本語表示名（UI・レポート用。設計書23 §1.3 で確定＝Q34）。</summary>
    public static string DisplayName(Personality p) => p switch
    {
        Personality.HotBlood => "熱血漢",
        Personality.Hardworker => "努力家",
        Personality.Genius => "天才肌",
        Personality.HonorStudent => "優等生",
        Personality.MyPace => "マイペース",
        Personality.MoodMaker => "ムードメーカー",
        Personality.LoneWolf => "一匹狼",
        Personality.Introvert => "内向的",
        Personality.Star => "花形",
        Personality.Caretaker => "世話焼き",
        Personality.PartyAnimal => "お祭り男",
        Personality.GangLeader => "ガキ大将",
        Personality.BigBrother => "兄貴分",
        Personality.Rowdy => "暴れん坊",
        Personality.Ambitious => "野心家",
        Personality.Craftsman => "職人肌",
        _ => "ふつう",
    };

    /// <summary>
    /// 軸の片側の言い方（見極めの「〜な性格かもしれない」の「〜」。設計書23 §1.5「素直な子のようだ」）。
    /// </summary>
    public static string AxisSideName(PersonalityAxis axis, bool high) => axis switch
    {
        PersonalityAxis.Leadership => high ? "人を引っ張る" : "控えめ",
        PersonalityAxis.Receptiveness => high ? "素直" : "頑固",
        PersonalityAxis.Diligence => high ? "コツコツ型" : "気分屋",
        _ => high ? "献身的" : "目立ちたがり",
    };
}
