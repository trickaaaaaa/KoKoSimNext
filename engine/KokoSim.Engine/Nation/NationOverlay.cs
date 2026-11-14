using System;
using System.Collections.Generic;
using KokoSim.Engine.Match.Tactics;

namespace KokoSim.Engine.Nation;

/// <summary>
/// 転任で全国に加わった学校の定義（issue #641）。新設校（全国生成 seed=2026 に含まれない自校）から
/// 監督が転任すると、その学校は AI校として全国に残る。再生成では出てこないので定義をセーブに持つ。
/// 強さ・名声は<b>追加した時点の値</b>で、以後の変化は他校と同じく年次進化（<see cref="AiSchoolEvolution"/>）の
/// 保存値が上書きする。
/// </summary>
public sealed record AddedSchool(
    int Id, string Name, int PrefectureId, int? DistrictId, double Strength, double Fame)
{
    public School ToSchool() => new()
    {
        Id = Id,
        Name = Name,
        PrefectureId = PrefectureId,
        DistrictId = DistrictId,
        Establishment = Establishment.Public,
        Tradition = Tradition.Emerging,
        Strength = Strength,
        Fame = Fame,
        Style = SchoolStyle.Standard,
        TacticalSense = 50,
        ManagerTraits = Array.Empty<ManagerTrait>(),
    };
}

/// <summary>
/// 校名の表記の約束（issue #641）。全国生成の校名は「〜高校」込み、自校の校名は「高校」を含まない幹
/// （表示側で付ける）。転任で自校⇔全国を行き来するときはここを通して付け外しする。
/// </summary>
public static class SchoolNames
{
    private const string Suffix = "高校";

    /// <summary>「〜高校」から幹へ（付いていなければそのまま）。</summary>
    public static string Stem(string name)
        => name.EndsWith(Suffix, StringComparison.Ordinal) && name.Length > Suffix.Length
            ? name.Substring(0, name.Length - Suffix.Length)
            : name;

    /// <summary>幹から全国の表記（「〜高校」）へ（既に付いていればそのまま）。</summary>
    public static string Full(string stem)
        => stem.EndsWith(Suffix, StringComparison.Ordinal) ? stem : stem + Suffix;

    /// <summary>
    /// 改名の入力（幹）の文字数の上限（issue #946）。画面で切れない長さとして暫定 10 字（全国生成の校名の幹の最長と
    /// 校名の出る欄の実測で詰める）。
    /// </summary>
    public const int MaxStemLength = 10;

    /// <summary>
    /// 改名の入力を幹へ整える（issue #946）。前後の空白（全角を含む）を落とし、末尾の「高校」は外す。
    /// 空なら null＝改名の取り消し（生成時の名前に戻す）。上限を超えたら false。
    /// </summary>
    public static bool TryNormalizeStem(string? input, out string? stem)
    {
        var t = (input ?? "").Trim().Trim('\u3000').Trim();
        t = Stem(t).Trim().Trim('\u3000').Trim();
        if (t.Length == 0) { stem = null; return true; }
        if (t.Length > MaxStemLength) { stem = null; return false; }
        stem = t;
        return true;
    }
}

/// <summary>
/// 決定論で再生成した全国（<see cref="NationGenerator"/>）に重ねる差分（issue #641）。
/// <list type="bullet">
/// <item><see cref="Added"/>: 転任で AI校として残った新設校。</item>
/// <item><see cref="ManagedSchoolId"/>: 監督がいま率いている<b>生成校</b>の ID（新設校のままなら null）。
/// 自校は全国の外の専用ID（-1）で表すので、その生成校は全国から外す＝大会に二重出場しない。</item>
/// </list>
/// 監督が率いる学校は常に1校なので、外す学校も高々1校。純データ・乱数不使用（不変条件#2）。
/// </summary>
public sealed class NationOverlay
{
    public List<AddedSchool> Added { get; } = new();

    // 追加校の School 実体（ID→実体）。差分を当て直しても同じ実体を返す＝年次進化で動いた強さ・名声が
    // 当て直しで初期値へ戻らない（生成校は生成済みの実体をそのまま使うので同じ性質を持つ）。
    private readonly Dictionary<int, School> _materialized = new();

    public int? ManagedSchoolId { get; set; }

    /// <summary>
    /// プレイヤーの改名（issue #946）: 学校 ID → 幹（「高校」を含まない）。このセーブだけに効く。
    /// 記録（大会結果の校名の文字列）は書き換えず、表示のときに ID から今の名前を引く＝取り消せば元に戻る。
    /// </summary>
    public Dictionary<int, string> Renames { get; } = new();

    public bool IsEmpty => Added.Count == 0 && ManagedSchoolId is null && Renames.Count == 0;

    /// <summary>
    /// 改名を記録して、その学校の実体にも当てる（issue #946）。<paramref name="stem"/> が null なら取り消し。
    /// </summary>
    public void SetRename(School school, string? stem)
    {
        if (string.IsNullOrEmpty(stem)) Renames.Remove(school.Id);
        else Renames[school.Id] = stem!;
        ApplyName(school);
    }

    private void ApplyName(School school)
        => school.Rename(Renames.TryGetValue(school.Id, out var stem) ? SchoolNames.Full(stem) : null);

    /// <summary>生成済みの全国へ差分を当てた全国を返す（生成校の順序を保ち、追加校は末尾に ID 順）。</summary>
    public Nation Apply(Nation generated)
    {
        // 改名は全校へ当て直す（改名の無い校は生成時の名前に戻る＝前に読んだセーブの改名が残らない）。
        foreach (var s in generated.Schools) ApplyName(s);
        if (IsEmpty) return generated;
        var schools = new List<School>(generated.Schools.Count + Added.Count);
        foreach (var s in generated.Schools)
            if (s.Id != ManagedSchoolId) schools.Add(s);
        var added = new List<AddedSchool>(Added);
        added.Sort((a, b) => a.Id.CompareTo(b.Id));
        foreach (var a in added)
        {
            if (a.Id == ManagedSchoolId) continue;
            if (!_materialized.TryGetValue(a.Id, out var school))
                _materialized[a.Id] = school = a.ToSchool();
            ApplyName(school);
            schools.Add(school);
        }
        return generated with { Schools = schools };
    }

    /// <summary>次に追加する学校の ID（生成校・追加校のどれとも衝突しない＝全ての最大ID＋1）。</summary>
    public int NextSchoolId(Nation generated)
    {
        var max = 0;
        foreach (var s in generated.Schools) max = Math.Max(max, s.Id);
        foreach (var a in Added) max = Math.Max(max, a.Id);
        return max + 1;
    }

    /// <summary>
    /// 新設校を AI校として加えるときの初期名声。全国生成と同じ式（強さ×0.5＋新興の減点）＝
    /// 同じ強さの生成校と同じ土俵に置く（<see cref="NationGenerator"/> の CreateSchool）。
    /// </summary>
    public static double InitialFame(double strength, NationCoefficients? coeff = null)
        => Core.MathUtil.Clamp(strength * 0.5 + (coeff ?? new NationCoefficients()).EmergingFamePenalty, 0, 100);
}
