namespace KokoSim.Engine.Players;

/// <summary>
/// 性格タイプ（設計書01 §1.1, CHANGELOG 22b）。内部は4傾向値（統率/素直さ/勤勉さ/自己犠牲⇔目立ちたがり）の
/// 複合だが、表示・生成はこの8タイプのラベルで扱う。各軸の効き先は1つに固定し二重計上しない。
/// <see cref="Normal"/> は未設定既定＝完全中立（生成では選ばれない）。手組みの Player/DevelopingPlayer が
/// 既定値のまま従来挙動（無補正）を保つための安全弁。
/// </summary>
public enum Personality
{
    Normal,        // 中立（既定・生成対象外）
    HotBlood,      // 熱血漢: 主将候補・練習で伸びる・つなぎ強い
    Hardworker,    // 努力家: 最も育てやすい・伸び安定
    Genius,        // 天才肌: 我流で指導は撥ね返すが放置でも才能で伸びる・本番長打上振れ
    HonorStudent,  // 優等生: 個別指導が最も効く・主将向き・犠打忠実
    MyPace,        // マイペース: 指導効きにくいが自分の型・采配無難
    MoodMaker,     // ムードメーカー: チャンス強い・犠打は不本意で成功率↓
    LoneWolf,      // 一匹狼: 主将不向き＋指導拒否が主特徴・我が道
    Introvert,     // 内向的: メンタル連動で本番弱い・指示に忠実・主将不向き
}

/// <summary>性格タイプの表示名・列挙ヘルパー（表示はデータ非依存で固定）。</summary>
public static class Personalities
{
    /// <summary>生成対象の8タイプ（Normal を除く）。</summary>
    public static readonly Personality[] Spawnable =
    {
        Personality.HotBlood, Personality.Hardworker, Personality.Genius, Personality.HonorStudent,
        Personality.MyPace, Personality.MoodMaker, Personality.LoneWolf, Personality.Introvert,
    };

    /// <summary>日本語表示名（UI・レポート用）。</summary>
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
        _ => "ふつう",
    };
}
