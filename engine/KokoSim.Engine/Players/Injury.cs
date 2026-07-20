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
