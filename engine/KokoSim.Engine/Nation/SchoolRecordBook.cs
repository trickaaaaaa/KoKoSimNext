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
        var gap = currentYear - priorLastYear.Value;
        // 同じ年に2度積まれた（＝記録の取り違え）ときに「0年ぶり」「-1年ぶり」を印字しない。
        // 何回目かは分かるのでそこだけ出す＝画面に明らかな嘘を出さない。
        if (gap <= 0) return $"{count}回目";
        if (gap == 1) return $"2年連続{count}回目";
        return $"{gap}年ぶり{count}回目";
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

    /// <summary>甲子園本戦の通算勝敗（<see cref="SchoolRecordBook.FoldTournament"/> が夏の甲子園で積む）。</summary>
    public int KoshienWins { get; internal set; }
    public int KoshienLosses { get; internal set; }

    public BestResult BestResult { get; internal set; } = BestResult.None;
}

/// <summary>大会の優勝校（校ID＋校名, issue #600）。決勝が未消化の大会では存在しない＝null で表す。</summary>
public readonly record struct TournamentChampion(int Id, string Name);

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

    /// <summary>セーブ復元専用（issue #507・<see cref="Saves.RecordBookSaves"/> だけが使う）。全消去。</summary>
    internal void ClearRecords() => _records.Clear();

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

    /// <summary>甲子園本戦1試合ぶんの勝敗。<see cref="FoldTournament"/> が夏の甲子園で呼ぶ。</summary>
    public void RecordKoshienMatch(int winnerId, int loserId)
    {
        For(winnerId).KoshienWins++;
        For(loserId).KoshienLosses++;
    }

    /// <summary>甲子園本戦の最高成績を現状より良ければ更新する。</summary>
    public void UpdateBestResult(int schoolId, BestResult result)
    {
        var r = For(schoolId);
        if (result > r.BestResult) r.BestResult = result;
    }

    /// <summary>
    /// 大会1回ぶんのブラケット全試合（自校戦＋裏試合とも）を通算戦績へ畳み込む。
    /// 決勝（RoundsRemaining==1）の勝者は、夏の県大会なら甲子園出場として記録する（issue #84 当面の定義）。
    ///
    /// <para><b>甲子園本戦（<see cref="TournamentKind.SummerKoshien"/>）では出場を数え直さない。</b>
    /// 出場は県大会優勝の時点で1回積んでいるので、ここで積むと同じ夏を2回出場したことになる。
    /// 代わりに甲子園の勝敗（<see cref="RecordKoshienMatch"/>）と最高成績
    /// （<see cref="UpdateBestResult"/>）を積む。</para>
    ///
    /// <para>戻り値は決勝の勝者（未消化で決勝が無ければ null）。<see cref="TournamentHistory.Fold"/> が
    /// 同じ走査を書き直さずに済むよう、ここで一度だけ拾った結果を渡す（issue #600）。</para>
    /// </summary>
    public TournamentChampion? FoldTournament(IReadOnlyList<BracketMatch> matches, TournamentKind kind, int currentYear)
    {
        var koshien = kind == TournamentKind.SummerKoshien;
        TournamentChampion? champion = null;
        foreach (var m in matches)
        {
            RecordOfficialMatch(m.WinnerId, m.LoserId);
            if (koshien)
            {
                RecordKoshienMatch(m.WinnerId, m.LoserId);
                // 負けた校の最高成績はこのカードで確定する（＝そのラウンドまで到達した）。
                UpdateBestResult(m.LoserId, ReachedBy(m.RoundsRemaining));
            }
            if (m.RoundsRemaining == 1) champion = new TournamentChampion(m.WinnerId, m.WinnerName);
        }
        if (champion is not { } c) return champion;

        if (kind == TournamentKind.Summer) RecordKoshienAppearance(c.Id, KoshienKind.Summer, currentYear);
        else if (koshien) UpdateBestResult(c.Id, BestResult.Champion);
        return champion;
    }

    /// <summary>
    /// 甲子園で「残り <paramref name="roundsRemaining"/> ラウンドのカードに負けた」＝どこまで到達したか。
    /// 1＝決勝で負けた＝準優勝／2＝準決勝敗退＝ベスト4／3＝準々決勝敗退＝ベスト8／それ以前は出場止まり。
    /// </summary>
    private static BestResult ReachedBy(int roundsRemaining) => roundsRemaining switch
    {
        1 => BestResult.RunnerUp,
        2 => BestResult.RoundOf4,
        3 => BestResult.RoundOf8,
        _ => BestResult.Appearance,
    };
}
