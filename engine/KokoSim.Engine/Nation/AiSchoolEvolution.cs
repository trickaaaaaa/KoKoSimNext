using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation.Tournaments;

namespace KokoSim.Engine.Nation;

/// <summary>
/// 実ゲーム（Unity）用の AI校年次進化の入口（issue wire-ai-school-evolve-into-game・設計書05 §2.3）。
///
/// <para>シム（<see cref="NationEngine"/>／<see cref="Career.CareerEngine"/>）は毎年 <see cref="AiSchoolModel.Evolve"/> を
/// 呼んでいたが、Unity 側から呼ぶ箇所が無く、学校の Strength/Fame がキャリア全体で生成値に固定されていた
/// （宮崎で同じ校が23大会中22大会優勝＝係数どおりの帰結）。ここは「年間勝数の台帳」と「年度末に1回の
/// 進化＋保存用スナップショット」だけを提供し、判定式は <see cref="AiSchoolModel"/> に置いたまま触らない。</para>
///
/// <para>決定論（不変条件#2）: 進化の乱数は <b>母種×終了年度</b> から導く専用ストリームで、試合・生成・
/// 抽選の他の列を1ビットも動かさない。同じ母種・同じ勝数なら同じ勢力図になる。</para>
/// </summary>
public static class AiSchoolEvolution
{
    /// <summary>進化ストリームの識別子（他の Fork キーと衝突しないよう固定）。</summary>
    private const ulong StreamKey = 0xE7_01_5C_00_A1_00_00_00UL;

    /// <summary>
    /// 終了した年度 <paramref name="endedYearIndex"/> ぶんの進化を全 AI校へ適用する。自校は
    /// <see cref="Nation.Schools"/> に含まれない（専用ID）ので対象外＝ここで除外処理は要らない。
    /// <paramref name="championId"/> は甲子園王者（実ゲームは本戦未実装＝-1）。
    /// </summary>
    public static void ApplyYear(
        Nation nation, IReadOnlyDictionary<int, int> winsBySchool, int championId,
        NationCoefficients coeff, ulong masterSeed, int endedYearIndex)
    {
        var rng = new Xoshiro256Random(masterSeed ^ StreamKey ^ ((ulong)(uint)endedYearIndex * 0x9E37_79B9_7F4A_7C15UL));
        AiSchoolModel.Evolve(nation, winsBySchool, championId, coeff, rng);
    }

    /// <summary>セーブ用: 全校の (Id, Strength, Fame) を校ID順で写す。</summary>
    public static List<SchoolEvolutionState> Snapshot(Nation nation)
    {
        var list = new List<SchoolEvolutionState>(nation.Schools.Count);
        foreach (var s in nation.Schools) list.Add(new SchoolEvolutionState(s.Id, s.Strength, s.Fame));
        list.Sort((a, b) => a.SchoolId.CompareTo(b.SchoolId));
        return list;
    }

    /// <summary>
    /// セーブ復元用: 決定論再生成した <paramref name="nation"/> へ保存値を上書きする。<b>同一の School インスタンス</b>を
    /// 書き換えるので、校IDで引く側（ロスターの成長ターゲット・大会の出場校）は自動で追従する。
    /// 見つからない校IDは黙って捨てる（生成校数が変わることは無いが、壊れたセーブで例外にしない）。
    /// </summary>
    public static int Restore(Nation nation, IEnumerable<SchoolEvolutionState> states)
    {
        var byId = new Dictionary<int, School>(nation.Schools.Count);
        foreach (var s in nation.Schools) byId[s.Id] = s;
        var applied = 0;
        foreach (var st in states)
        {
            if (!byId.TryGetValue(st.SchoolId, out var school)) continue;
            school.Strength = st.Strength;
            school.Fame = st.Fame;
            applied++;
        }
        return applied;
    }
}

/// <summary>1校ぶんの進化状態（セーブに載る最小の姿。名前・県は決定論再生成で戻る）。</summary>
public readonly record struct SchoolEvolutionState(int SchoolId, double Strength, double Fame);

/// <summary>
/// 年間の校別勝数台帳（issue wire-ai-school-evolve-into-game）。<see cref="AiSchoolModel.Evolve"/> の
/// winsBySchool を実ゲームで組むための器。自県の大会（ブラケット全カード）と他県の裏試合
/// （<see cref="PrefectureResult.WinsBySchool"/>）の両方から積み、年度末に読み出して空にする。
/// 背景スレッド（全国裏試合）からも積むのでロックで守る。乱数は使わない。
/// </summary>
public sealed class AiSchoolYearLedger
{
    private readonly object _gate = new();
    private readonly Dictionary<int, int> _wins = new();

    /// <summary>ブラケットの全カードから勝者に1勝ずつ積む。</summary>
    public void AddMatches(IEnumerable<BracketMatch> matches)
    {
        lock (_gate)
            foreach (var m in matches) Bump(m.WinnerId, 1);
    }

    /// <summary>校別勝数をまとめて積む（他県の裏試合の結果）。</summary>
    public void AddWins(IReadOnlyDictionary<int, int> wins)
    {
        lock (_gate)
            foreach (var kv in wins) Bump(kv.Key, kv.Value);
    }

    /// <summary>1校に n 勝を積む（セーブ復元も同じ入口）。</summary>
    public void Add(int schoolId, int wins)
    {
        lock (_gate) Bump(schoolId, wins);
    }

    private void Bump(int schoolId, int wins)
        => _wins[schoolId] = (_wins.TryGetValue(schoolId, out var v) ? v : 0) + wins;

    public int WinsOf(int schoolId)
    {
        lock (_gate) return _wins.TryGetValue(schoolId, out var v) ? v : 0;
    }

    /// <summary>今年度ぶんの写し（校ID→勝数）。</summary>
    public IReadOnlyDictionary<int, int> Snapshot()
    {
        lock (_gate) return new Dictionary<int, int>(_wins);
    }

    public int Count { get { lock (_gate) return _wins.Count; } }

    public void Clear()
    {
        lock (_gate) _wins.Clear();
    }
}
