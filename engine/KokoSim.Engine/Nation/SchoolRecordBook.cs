using System.Collections.Generic;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Nation;

/// <summary>甲子園本戦での最高成績（設計書05 §2.3, issue #84）。優勝が最上位。</summary>
public enum BestResult { None, Appearance, RoundOf8, RoundOf4, RunnerUp, Champion }

/// <summary>甲子園の季節区分（夏/春）。夏地方大会優勝＝夏出場、春はセンバツ選考接続待ち（issue #84）。</summary>
public enum KoshienKind { Summer, Spring }

/// <summary>
/// 「何年ぶり何回目」の文言を組み立てる純関数（issue #84）。UI側で文字列を組み立てない。
/// priorAppearances/priorLastYear は今回の出場を数える<b>前</b>の記録（0回・null＝初出場）。
/// </summary>
public static class AppearanceLabel
{
    public static string For(int priorAppearances, int? priorLastYear, int currentYear)
    {
        if (priorAppearances <= 0 || priorLastYear is null) return "初出場";

        var count = priorAppearances + 1;
        if (priorLastYear.Value == currentYear - 1) return $"2年連続{count}回目";
        return $"{currentYear - priorLastYear.Value}年ぶり{count}回目";
    }
}

/// <summary>
/// 1校ぶんの通算戦績（issue #84）。School 本体は不変に保ち、記録は別ストアに外置きする。
/// </summary>
public sealed class SchoolRecord
{
    /// <summary>公式戦（練習試合を除く）の通算勝敗。</summary>
    public int OfficialWins { get; internal set; }
    public int OfficialLosses { get; internal set; }

    public int SummerAppearances { get; internal set; }
    public int SpringAppearances { get; internal set; }
    public int TotalAppearances => SummerAppearances + SpringAppearances;

    /// <summary>直近出場の暦年。null＝未出場。</summary>
    public int? LastSummerYear { get; internal set; }
    public int? LastSpringYear { get; internal set; }

    /// <summary>直近出場時点の「何年ぶり何回目」表示（初出場含む）。未出場は空文字。</summary>
    public string SummerAppearanceLabel { get; internal set; } = "";
    public string SpringAppearanceLabel { get; internal set; } = "";

    /// <summary>甲子園本戦の通算勝敗（#65で甲子園本戦が実装されるまでは常に0）。</summary>
    public int KoshienWins { get; internal set; }
    public int KoshienLosses { get; internal set; }

    public BestResult BestResult { get; internal set; } = BestResult.None;
}

/// <summary>
/// 学校ごとの通算戦績を積む集計器（issue #84, design-05 §2.3「生きた勢力図」）。
/// School.Id → SchoolRecord。乱数を消費しない（不変条件#2・統計帯は不変）。
/// GameSession が Stats と同様に横断状態として保持する（School 本体は生成物・記録は進行状態）。
/// </summary>
public sealed class SchoolRecordBook
{
    private readonly Dictionary<int, SchoolRecord> _records = new();

    public IReadOnlyDictionary<int, SchoolRecord> Records => _records;

    public SchoolRecord For(int schoolId)
    {
        if (!_records.TryGetValue(schoolId, out var r))
        {
            r = new SchoolRecord();
            _records[schoolId] = r;
        }
        return r;
    }

    /// <summary>公式戦1試合ぶんの勝敗を積む（練習試合は呼ばないこと）。</summary>
    public void RecordOfficialMatch(int winnerId, int loserId)
    {
        For(winnerId).OfficialWins++;
        For(loserId).OfficialLosses++;
    }

    /// <summary>
    /// 甲子園出場を1回積む。当面は「夏の県大会優勝＝夏の甲子園出場」としてのみ呼ばれる（issue #84）。
    /// 春（センバツ）は選考が実ゲームフローへ接続されたときに配線する（器のみ）。
    /// </summary>
    public void RecordKoshienAppearance(int schoolId, KoshienKind kind, int currentYear)
    {
        var r = For(schoolId);
        if (kind == KoshienKind.Summer)
        {
            r.SummerAppearanceLabel = AppearanceLabel.For(r.SummerAppearances, r.LastSummerYear, currentYear);
            r.SummerAppearances++;
            r.LastSummerYear = currentYear;
        }
        else
        {
            r.SpringAppearanceLabel = AppearanceLabel.For(r.SpringAppearances, r.LastSpringYear, currentYear);
            r.SpringAppearances++;
            r.LastSpringYear = currentYear;
        }
        if (r.BestResult == BestResult.None) r.BestResult = BestResult.Appearance;
    }

    /// <summary>甲子園本戦1試合ぶんの勝敗（#65で甲子園本戦が実装されたら配線。現状は未使用の器）。</summary>
    public void RecordKoshienMatch(int winnerId, int loserId)
    {
        For(winnerId).KoshienWins++;
        For(loserId).KoshienLosses++;
    }

    /// <summary>甲子園本戦の最高成績を現状より良ければ更新する（#65で配線予定。現状は未使用の器）。</summary>
    public void UpdateBestResult(int schoolId, BestResult result)
    {
        var r = For(schoolId);
        if (result > r.BestResult) r.BestResult = result;
    }

    /// <summary>
    /// 大会1回ぶんのブラケット全試合（自校戦＋裏試合とも）を通算戦績へ畳み込む。
    /// 決勝（RoundsRemaining==1）の勝者は、夏の大会なら甲子園出場として記録する（issue #84 当面の定義）。
    /// </summary>
    public void FoldTournament(IReadOnlyList<BracketMatch> matches, TournamentKind kind, int currentYear)
    {
        int? championId = null;
        foreach (var m in matches)
        {
            RecordOfficialMatch(m.WinnerId, m.LoserId);
            if (m.RoundsRemaining == 1) championId = m.WinnerId;
        }
        if (kind == TournamentKind.Summer && championId is { } id)
        {
            RecordKoshienAppearance(id, KoshienKind.Summer, currentYear);
        }
    }
}
