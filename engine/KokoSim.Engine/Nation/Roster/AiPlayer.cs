using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Nation.Roster;

/// <summary>
/// AI校の永続ロスターに在籍する1選手（設計書 OPEN-QUESTIONS Q19 / #80）。入学〜引退の3年間、
/// <see cref="Id"/> を変えず在籍し、節目成長で <see cref="Snapshot"/>（試合出場用の能力スナップショット）が伸びる。
/// 使い捨て生成（<see cref="StrengthTeamFactory.ForSchool"/>）を廃し、選手個人の継続を持たせる中核。
/// </summary>
public sealed class AiPlayer
{
    /// <summary>校内で安定な選手ID（成績帰属キー・在籍中不変）。全校横断集計は (校ID, この) で貫通する。</summary>
    public int Id { get; }

    /// <summary>入学年度（yearIndex, 1始まり）。生成シードと3年連続性の基準。</summary>
    public int EnrollmentYearIndex { get; }

    /// <summary>入学時に確定した守備位置（投手含む）。代替わりでも本人の守備位置は不変。</summary>
    public FieldPosition Position { get; }

    /// <summary>投手か。</summary>
    public bool IsPitcher => Position == FieldPosition.Pitcher;

    /// <summary>怪物種別（隠しフラグ, Q20）。生成時に確定し不変。</summary>
    public PhenomType Phenom { get; }

    /// <summary>学年（1〜3）。代替わり（4月）で +1、夏大会後に3年が引退（ロスターから除去）。</summary>
    public int Grade { get; internal set; }

    /// <summary>現在の能力スナップショット（試合投影用の <see cref="Player"/>）。節目成長で差し替える。</summary>
    public Player Snapshot { get; internal set; }

    /// <summary>逆算配分の未適用成長予算（BeginYear で設定・各節目で消費）。怪物は常に0（予算外, Q20 §1）。</summary>
    internal double PendingBudget { get; set; }

    public AiPlayer(int id, int enrollmentYearIndex, FieldPosition position, PhenomType phenom, int grade, Player snapshot)
    {
        Id = id;
        EnrollmentYearIndex = enrollmentYearIndex;
        Position = position;
        Phenom = phenom;
        Grade = grade;
        Snapshot = snapshot;
    }
}
