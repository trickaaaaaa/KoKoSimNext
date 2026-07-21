namespace KokoSim.Engine.Players;

/// <summary>怪我の段階（設計書03 §3.5）。選手生命に関わる大故障は設けない。常に可視。</summary>
public enum InjurySeverity
{
    None,
    Minor,    // 軽度
    Moderate, // 中度
    Severe,   // 重度
}

/// <summary>部位（常に可視, 設計書03 §3.5）。現フェーズは表示用の箱（部位別の練習制限は後続）。</summary>
public enum InjurySite
{
    Shoulder, // 肩
    Elbow,    // 肘
    Back,     // 腰
    Knee,     // 膝
    Ankle,    // 足首
    Hand,     // 手
}

/// <summary>
/// 傷病の種類（設計書03 §3.5）。engine は id だけを扱い、表示名・取りうる部位・段階分布・
/// 回復週の倍率はすべて data/injuries.yaml（<see cref="InjuryCatalog"/>）が持つ（不変条件#4）。
/// </summary>
public enum InjuryType
{
    /// <summary>種類未設定（カタログ未投入の従来データ）。表示は部位＋段階だけになる。</summary>
    None,
    Sprain,        // 捻挫
    Strain,        // 肉離れ
    Fracture,      // 骨折
    Bruise,        // 打撲
    LigamentTear,  // 靭帯損傷
    Inflammation,  // 疲労性炎症
}

/// <summary>
/// 怪我が発生した場面（設計書03 §3.5）。種類の抽選重みを場面ごとに変えるためのキー。
/// <see cref="Weekly"/> は従来の週次基礎率、それ以外は試合中の場面駆動（設計書12 の詳細プレー由来）。
/// </summary>
public enum InjuryScene
{
    /// <summary>週次の基礎率（練習・日常）。</summary>
    Weekly,
    /// <summary>死球。</summary>
    HitByPitch,
    /// <summary>本塁クロスプレー（走者と捕手の接触）。</summary>
    HomeCollision,
    /// <summary>フェンス激突（フェンス際の深い打球を追った外野手）。</summary>
    FenceCrash,
    /// <summary>全力疾走・スライディング（盗塁の走塁）。</summary>
    Sliding,
    /// <summary>投球過多（当該試合で球数が耐久を大きく超えた投手）。</summary>
    Overuse,
}
