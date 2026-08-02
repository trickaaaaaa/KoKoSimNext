using System;
using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Nation.Roster;

/// <summary>
/// AI校の永続ロスターに在籍する1選手（設計書 OPEN-QUESTIONS Q19 / #80）。入学〜引退の3年間、
/// <see cref="Id"/> を変えず在籍し、節目成長で <see cref="Snapshot"/>（試合出場用の能力スナップショット）が伸びる。
/// 使い捨て生成（<see cref="StrengthTeamFactory.ForSchool"/>）を廃し、選手個人の継続を持たせる中核。
///
/// **選手が持つのは能力と適性だけで、配置は持たない**（issue #384 / #366）。旧 <c>Position</c>
/// （入学時に確定して在籍中不変）は撤去した。誰がどこを守り誰がマウンドに立つかは
/// <see cref="AiTeamBuilder"/> が毎回この適性と能力から選抜する＝自校（プレイヤーがスタメンを組む）と同じ形になる。
/// </summary>
public sealed class AiPlayer
{
    // 守備位置適性（1〜99）を (int)FieldPosition で添字付けた配列。AptitudeRoller.Roll の出力をそのまま持つ。
    private readonly int[] _aptitudes;

    /// <summary>校内で安定な選手ID（成績帰属キー・在籍中不変）。全校横断集計は (校ID, この) で貫通する。</summary>
    public int Id { get; }

    /// <summary>入学年度（yearIndex, 1始まり）。生成シードと3年連続性の基準。</summary>
    public int EnrollmentYearIndex { get; }

    /// <summary>
    /// 守備位置適性（issue #384）。自校 <see cref="Season.DevelopingPlayer"/> と同じ
    /// <see cref="Season.AptitudeRoller"/> のロール＝分布が一致する。
    /// <see cref="FieldPosition.DesignatedHitter"/> は守備位置でないのでロールされない（既定50のまま）。
    /// </summary>
    public int Aptitude(FieldPosition pos) => _aptitudes[(int)pos];

    /// <summary>適性配列そのもの（判定ヘルパへ渡す用。読み取り専用）。</summary>
    public IReadOnlyList<int> Aptitudes => _aptitudes;

    /// <summary>怪物種別（隠しフラグ, Q20）。生成時に確定し不変。</summary>
    public PhenomType Phenom { get; }

    /// <summary>学年（1〜3）。代替わり（4月）で +1、夏大会後に3年が引退（ロスターから除去）。</summary>
    public int Grade { get; internal set; }

    /// <summary>現在の能力スナップショット（試合投影用の <see cref="Player"/>）。節目成長で差し替える。</summary>
    public Player Snapshot { get; internal set; }

    /// <summary>逆算配分の未適用成長予算（BeginYear で設定・各節目で消費）。怪物は常に0（予算外, Q20 §1）。</summary>
    internal double PendingBudget { get; set; }

    // 適性配列の長さは FieldPosition 全要素ぶん（自校 DevelopingPlayer と同じ）。AptitudeRoller.Roll が返すのは
    // **実在する9守備位置ぶんだけ**なので、そのまま持つと Aptitude(DesignatedHitter) が範囲外で落ちる。
    // DH は守備位置でなく打順枠なので、自校と同じく既定50を置いて長さを揃える（#377 と同じ罠）。
    private static readonly int PositionCount = Enum.GetValues(typeof(FieldPosition)).Length;

    public AiPlayer(int id, int enrollmentYearIndex, int[] aptitudes, PhenomType phenom, int grade, Player snapshot)
    {
        if (aptitudes is null) throw new ArgumentNullException(nameof(aptitudes));
        Id = id;
        EnrollmentYearIndex = enrollmentYearIndex;
        _aptitudes = new int[PositionCount];
        for (var i = 0; i < _aptitudes.Length; i++)
            _aptitudes[i] = i < aptitudes.Length ? aptitudes[i] : 50;
        Phenom = phenom;
        Grade = grade;
        Snapshot = snapshot;
    }
}
