using System;
using System.Collections.Generic;

namespace KokoSim.Engine.Career.Draft;

/// <summary>
/// 指名確定1件の永続記録（設計書20 §6「指名実績」＝キャリアの勲章・issue #586）。
///
/// <para><b>指名漏れは残さない。</b> 教え子名鑑は勲章の一覧であって落選簿ではない
/// （落選は指名週の通知フィードにだけ出て消える）。</para>
///
/// <para>暦年 <paramref name="Year"/> を年度から導かずそのまま焼くのは、暦年の起点
/// （<c>SeasonClock.SeasonBaseYear</c>）が Shell 側の定数で engine が知らないため。導出にすると
/// 起点を動かした瞬間に過去の勲章の年が全部ずれる。</para>
/// </summary>
/// <param name="Year">指名を受けた暦年（表示用）。</param>
/// <param name="YearIndex">指名を受けた年度（1始まり）。</param>
/// <param name="PlayerId">育成選手ID（卒業生の名前解決キー）。</param>
/// <param name="Name">指名当時の氏名。</param>
/// <param name="UniformNumber">指名当時の背番号（0＝背番号なし）。</param>
/// <param name="Round">指名順位（1以上）。</param>
/// <param name="Band">確定時の予想バンド。</param>
/// <param name="Notability">確定時の注目度。</param>
public sealed record DraftRecord(
    int Year,
    int YearIndex,
    int PlayerId,
    string Name,
    int UniformNumber,
    int Round,
    DraftRankBand Band,
    double Notability);

/// <summary>
/// 教え子名鑑（プロ入りOB）の台帳（issue #586・設計書06 §3.8）。監督のキャリアに紐づく横断状態で、
/// 卒業してロスターを外れた後も残る＝<see cref="Saves.SaveState"/> に載る（セーブ v7）。
///
/// <para>乱数を消費しない（不変条件#2）。指名そのものの抽選は <see cref="DraftEngine.RunNomination"/> 側で
/// 独立Forkして済んでおり、ここは確定した結果を積むだけ。</para>
/// </summary>
public sealed class DraftHistory
{
    private readonly List<DraftRecord> _records = new();

    /// <summary>積んだ順（古い年が先）。表示側で並べ替える。</summary>
    public IReadOnlyList<DraftRecord> Records => _records;

    /// <summary>通算の指名人数（キャリアの勲章の数）。</summary>
    public int Count => _records.Count;

    public void Add(DraftRecord record) => _records.Add(record);

    /// <summary>セーブ復元・新規ゲーム用の全消去。</summary>
    public void Clear() => _records.Clear();

    /// <summary>
    /// 指名結果のうち<b>指名された者だけ</b>を台帳へ積み、積んだぶんを返す（通知フィード用）。
    ///
    /// <para>背番号は選手本体からは引けない＝<b>夏の引退で返上済み</b>（issue #531）で、指名は10月。
    /// 当時の番号の引き方（大会別アーカイブを遡る）は呼び出し側の関数
    /// <paramref name="uniformNumberOf"/> に委ねる（0＝背番号なし）。</para>
    /// </summary>
    public IReadOnlyList<DraftRecord> AddFrom(
        DraftResult result, int calendarYear, Func<int, int> uniformNumberOf)
    {
        var added = new List<DraftRecord>();
        foreach (var pick in result.Nominated)
        {
            var number = uniformNumberOf != null ? uniformNumberOf(pick.PlayerId) : 0;
            var record = new DraftRecord(
                calendarYear, result.Year, pick.PlayerId, pick.Name, number,
                pick.Round, pick.Band, pick.Notability);
            _records.Add(record);
            added.Add(record);
        }
        return added;
    }
}
