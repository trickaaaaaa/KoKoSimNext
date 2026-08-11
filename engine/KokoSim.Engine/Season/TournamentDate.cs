using System;
using KokoSim.Engine.Nation.Tournaments;

namespace KokoSim.Engine.Season;

/// <summary>
/// 大会期間だけに持たせる実月日（issue #583）。
///
/// <para><b>週暦（<see cref="SeasonCalendar"/>）は触らない。</b> ゲーム内暦は 50週×7日＝350日で実暦365日と
/// 一致せず、<c>MonthOrder</c>／<c>WeeksPerMonth</c> は<b>週配分の表示用</b>で月の実日数と対応していない
/// （例: 7月は5週配分＝35日ぶん）。「週→実月日」を素朴に作ると月末を突き抜けるので、
/// <b>大会種別ごとの実開幕月日＋大会内経過日</b>だけを実カレンダーで数える。
/// これなら成長段階・合宿・引退週の判定に一切触らない＝帯へ波及しない。</para>
///
/// <para>閏年は扱わない（大会は7月／9月開幕で2月を跨がない）。年跨ぎも起きない。</para>
/// </summary>
public static class TournamentDate
{
    private static readonly int[] DaysInMonth = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };

    /// <summary>その月の日数（1〜12月。範囲外は31を返す＝落とさない）。</summary>
    public static int DaysIn(int month)
        => month >= 1 && month <= 12 ? DaysInMonth[month - 1] : 31;

    /// <summary>
    /// 開幕月日から <paramref name="dayOffset"/> 日後の月日（月跨ぎを繰り上げる）。
    /// <paramref name="dayOffset"/> が0なら開幕日そのもの。12月を越えたら1月へ折り返す。
    /// </summary>
    public static (int Month, int Day) AddDays(int openMonth, int openDay, int dayOffset)
    {
        var month = Math.Clamp(openMonth, 1, 12);
        var day = Math.Max(1, openDay) + Math.Max(0, dayOffset);
        while (day > DaysIn(month))
        {
            day -= DaysIn(month);
            month = month == 12 ? 1 : month + 1;
        }
        return (month, day);
    }

    /// <summary>大会種別ごとの実開幕月日（<see cref="TournamentSchedule"/> ＝ YAML 駆動）。</summary>
    public static (int Month, int Day) OpeningOf(TournamentSchedule schedule, TournamentKind kind)
        => kind == TournamentKind.Summer
            ? (schedule.SummerOpenMonth, schedule.SummerOpenDay)
            : (schedule.AutumnOpenMonth, schedule.AutumnOpenDay);

    /// <summary>大会内経過日（0＝開幕日）の実月日。</summary>
    public static (int Month, int Day) DateOf(TournamentSchedule schedule, TournamentKind kind, int tournamentDay)
    {
        var (m, d) = OpeningOf(schedule, kind);
        return AddDays(m, d, tournamentDay);
    }

    /// <summary>表示文字列（"7月13日"）。書式を画面ごとに作らないための単一ソース。</summary>
    public static string Label(TournamentSchedule schedule, TournamentKind kind, int tournamentDay)
    {
        var (m, d) = DateOf(schedule, kind, tournamentDay);
        return m + "月" + d + "日";
    }
}
