using System.Collections.Generic;

namespace KokoSim.Engine.Season;

/// <summary>
/// 入部予定者の学年1つぶん（3月に確定し、4月に仮入部する集団。設計書04 §2.1・issue #587）。
///
/// <para>従来は「年度替わりで新入生がその場で湧く」だけだったのを、<b>3月確定 → 4月合流</b>の2段に分けた。
/// 確定した時点で選手そのものは出来上がっている（＝4月に引き直したりしない）が、監督に見えるのは
/// <see cref="Reports"/> の見立てだけで、実能力は入部するまで分からない。</para>
///
/// <para>保持するのは1学年ぶんだけで、次の3月に上書きされる。4月に合流したら <see cref="Players"/> を
/// 空にして <see cref="Joined"/> を立てる＝選手の実体はロスター側だけが持つ（セーブに二重に載せない）。
/// 見立て（<see cref="Reports"/>）は残すので、入部後も「事前にどう見えていたか」を突き合わせられる。</para>
/// </summary>
public sealed class IncomingClass
{
    /// <summary>合流する年度（この年度の4月にロスターへ入る＝確定した年度の翌年度）。</summary>
    public int YearIndex { get; set; }

    /// <summary>確定時のスカウト網の事前開示精度 0〜1（<see cref="Career.ContractEffects.ProspectDisclosure"/>）。</summary>
    public double Disclosure { get; set; }

    /// <summary>すでにロスターへ合流したか（true なら <see cref="Players"/> は空で、見立てだけが残る）。</summary>
    public bool Joined { get; set; }

    /// <summary>確定した予定者（生成順＝安定順）。合流時はこの順でロスターへ入り、その後は空になる。</summary>
    public List<DevelopingPlayer> Players { get; set; } = new();

    /// <summary>予定者ごとの事前情報（確定時は <see cref="Players"/> と同じ並び・同じ長さ）。</summary>
    public List<ProspectReport> Reports { get; set; } = new();

    /// <summary>この学年の人数（合流後も見立ての数で分かる）。</summary>
    public int Count => Reports.Count;

    /// <summary>並びのi番目の報告書（長さが食い違っていても落ちない）。</summary>
    public ProspectReport? ReportAt(int index)
        => index >= 0 && index < Reports.Count ? Reports[index] : null;

    /// <summary>
    /// 合流の後始末: 見立てへ安定IDを写し、選手の実体を手放す。<paramref name="joined"/> は
    /// <see cref="Players"/> と同じ並びで渡すこと（＝<see cref="RosterLifecycle.AdvanceYear"/> の Newcomers）。
    /// </summary>
    public void MarkJoined(IReadOnlyList<DevelopingPlayer> joined)
    {
        for (var i = 0; i < Reports.Count && i < joined.Count; i++)
        {
            Reports[i].PlayerId = joined[i].Id;
            Reports[i].Name = joined[i].Name;
        }
        Players = new List<DevelopingPlayer>();
        Joined = true;
    }
}
