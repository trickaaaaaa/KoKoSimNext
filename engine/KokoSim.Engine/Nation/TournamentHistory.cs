using System.Collections.Generic;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Nation;

/// <summary>
/// 歴代大会結果の1行（issue #600・設計書06 §3.9）。
///
/// <para><b>年は年度（YearIndex・1始まり）で持つ</b>。暦年の文字列化は Unity 側の <c>SeasonClock</c> の
/// 担当で、engine には暦の起点（2026年）を持ち込まない（不変条件#3）。
/// <see cref="SchoolRecord"/> の <c>LastSummerYear</c> 等と同じ尺度＝同じ値を渡すこと。</para>
///
/// <para><see cref="ReachedRoundName"/> は自校が<b>最後に戦ったカードのラウンド名</b>（"3回戦"/"決勝"…）。
/// 優勝でも決勝敗退でも "決勝" が入るので、表示文言は <see cref="TournamentResultLabel"/> が
/// <see cref="ManagerIsChampion"/> と併せて決める。自校が1試合も関与しなかった大会は空文字。</para>
/// </summary>
public sealed record TournamentHistoryEntry(
    int Year,
    TournamentKind Kind,
    string ReachedRoundName,
    bool ManagerIsChampion,
    int ChampionId,
    string ChampionName);

/// <summary>
/// 歴代大会結果の「成績」表示文言を組み立てる純関数（issue #600）。UI側で文字列を組み立てない
/// （<see cref="AppearanceLabel"/> と同じ役割）。
/// </summary>
public static class TournamentResultLabel
{
    public static string For(TournamentHistoryEntry e) => For(e.ReachedRoundName, e.ManagerIsChampion);

    public static string For(string reachedRoundName, bool isChampion)
    {
        if (isChampion) return "優勝";
        if (string.IsNullOrEmpty(reachedRoundName)) return "不参加";
        // 決勝まで行って優勝していない＝準優勝。それ以外は「そのラウンドで敗退」。
        return reachedRoundName == "決勝" ? "準優勝" : reachedRoundName + "敗退";
    }
}

/// <summary>
/// 自校の歴代大会結果を年度順に積む台帳（issue #600・設計書06 §3.9）。
///
/// <para><see cref="SchoolRecordBook"/> が<b>累計へ畳んで年度を捨てる</b>のに対し、こちらは
/// 「2027年 夏 は3回戦敗退」という年度ごとの1行を残す＝記録タブの「歴代大会結果」の源。
/// 乱数を消費しない（不変条件#2・統計帯は不変）。</para>
///
/// <para>主語は<b>自校だけ</b>（issue #600 確定事項2）。全国4000校ぶんの年度別履歴は持たない。</para>
/// </summary>
public sealed class TournamentHistory
{
    private readonly List<TournamentHistoryEntry> _entries = new();

    /// <summary>積んだ順（＝古い順）。新しい順に見せるかは表示側の判断。</summary>
    public IReadOnlyList<TournamentHistoryEntry> Entries => _entries;

    public int Count => _entries.Count;

    public void Add(TournamentHistoryEntry entry) => _entries.Add(entry);

    /// <summary>セーブ復元専用（<see cref="Saves.TournamentHistorySaves"/> だけが使う）。全消去。</summary>
    public void Clear() => _entries.Clear();

    /// <summary>
    /// 大会1回ぶんのブラケット全試合から1行を作って積む（大会終了時に1回だけ呼ぶ）。
    ///
    /// <para>優勝校は<b>呼び出し側が渡す</b>＝<see cref="SchoolRecordBook.FoldTournament"/> が既に
    /// 走査している決勝（RoundsRemaining==1）の勝者を使い回す（同じ走査を2箇所に書かない）。</para>
    /// </summary>
    public TournamentHistoryEntry Fold(
        IReadOnlyList<BracketMatch> matches, TournamentKind kind, int year,
        int managerSchoolId, TournamentChampion? champion)
    {
        // 自校が関与した最後のカードのラウンド名＝到達ラウンド。ブラケットは進行順に積まれる。
        var reached = "";
        foreach (var m in matches)
            if (m.ManagerInvolved) reached = m.RoundName;

        var isChampion = champion is { } c && c.Id == managerSchoolId;
        var entry = new TournamentHistoryEntry(
            year, kind, reached, isChampion,
            champion?.Id ?? 0, champion?.Name ?? "");
        _entries.Add(entry);
        return entry;
    }
}
