using System.Collections.Generic;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>約束の決着1件（守り切った／破った）。</summary>
public sealed record ScoutPromiseOutcome(DevelopingPlayer Player, ScoutPromiseKind Kind, bool Kept);

/// <summary>
/// 入部の条件（約束）の判定（設計書04 §2.4）。公式戦1試合の記録・年度替わり・背番号決定・主将指名・入部の
/// 5つの節目で「守り切った／破った」を決め、本人の <see cref="DevelopingPlayer.PromiseStatus"/> を書き換えて返す。
/// 乱数も時刻も使わない純ロジック。信頼度・名声の増減は呼び出し側（結果を1件ずつ受け取って反映する）。
///
/// <para><b>破りは1回だけ</b>＝決着した約束は二度と判定しない（状態が Active のものしか見ない）。
/// 起用の約束（レギュラー・4番・上位打線）は、怪我をしている試合は破りに数えない。</para>
/// </summary>
public static class ScoutPromiseJudge
{
    private const int CleanupOrder = 4;
    private const int TopOrderMax = 5;
    private const int SingleDigitMax = 9;

    /// <summary>
    /// 自校の公式戦1試合の後に呼ぶ。<paramref name="batting"/>・<paramref name="pitching"/> は自校ぶんのボックススコア。
    /// </summary>
    /// <param name="kind">大会の種類（1年目の起用の約束は夏と秋＝県大会・地区予選・地区大会までを見る）。</param>
    /// <param name="pitchDayStamp">登板日の通し番号（同じ大会の翌日が +1 になる値）。連投の判定に使う。</param>
    public static IReadOnlyList<ScoutPromiseOutcome> AfterOfficialGame(
        IReadOnlyList<DevelopingPlayer> roster, IReadOnlyList<BattingLine> batting, IReadOnlyList<PitchingLine> pitching,
        TournamentKind kind, int pitchDayStamp)
    {
        var outcomes = new List<ScoutPromiseOutcome>();
        var isSummer = kind == TournamentKind.Summer || kind == TournamentKind.SummerKoshien;
        var inFirstYearWindow = isSummer || IsAutumn(kind);

        foreach (var p in roster)
        {
            var id = p.Id;
            var pitched = PitchedIn(pitching, id);
            var started = StartedIn(batting, pitching, id);
            var appeared = pitched || AppearedIn(batting, id);

            if (p.PromiseStatus == ScoutPromiseStatus.Active)
            {
                var firstYear = p.Grade == 1;
                var hurt = p.Injury != InjurySeverity.None;
                switch (p.Promise)
                {
                    case ScoutPromiseKind.Regular:
                        if (firstYear && inFirstYearWindow && !hurt && !started) Settle(outcomes, p, kept: false);
                        break;
                    case ScoutPromiseKind.Cleanup:
                        if (firstYear && inFirstYearWindow && !hurt && StartOrder(batting, id) != CleanupOrder)
                            Settle(outcomes, p, kept: false);
                        break;
                    case ScoutPromiseKind.TopOfOrder:
                    {
                        var order = StartOrder(batting, id);
                        if (firstYear && inFirstYearWindow && !hurt && (order < 1 || order > TopOrderMax))
                            Settle(outcomes, p, kept: false);
                        break;
                    }
                    case ScoutPromiseKind.PlayAsFreshman:
                        if (firstYear && appeared) Settle(outcomes, p, kept: true);
                        break;
                    case ScoutPromiseKind.StartingPitcher:
                        if (firstYear && pitching.Count > 0 && pitching[0].SourceId == id) Settle(outcomes, p, kept: true);
                        break;
                    case ScoutPromiseKind.RestFirstSummer:
                        if (firstYear && isSummer && appeared) Settle(outcomes, p, kept: false);
                        else if (firstYear && !isSummer) Settle(outcomes, p, kept: true);   // 夏を越えた
                        break;
                    case ScoutPromiseKind.NoPinchHitter:
                        if (firstYear && inFirstYearWindow && StartOrder(batting, id) > 0 && PinchHitFor(batting, p.Name))
                            Settle(outcomes, p, kept: false);
                        break;
                    case ScoutPromiseKind.NoBunt:
                        if (firstYear && inFirstYearWindow && SacrificeHits(batting, id) > 0) Settle(outcomes, p, kept: false);
                        break;
                    case ScoutPromiseKind.NoConsecutiveDays:
                        if (pitched && p.LastOfficialPitchStamp != 0 && pitchDayStamp == p.LastOfficialPitchStamp + 1)
                            Settle(outcomes, p, kept: false);
                        break;
                }
            }

            // 連投の約束は在学中ずっと続くので、登板日は約束の有無に関わらず全員ぶん覚えておく（次の日の判定の材料）。
            if (pitched) p.LastOfficialPitchStamp = pitchDayStamp;
        }
        return outcomes;
    }

    /// <summary>
    /// 年度替わりの直前に呼ぶ。1年目だけの約束は、ここまで破らなければ守り切った。果たすべき約束
    /// （1年から出場・先発で投げたい）は、果たせていなければ破り。卒業する3年の生きている約束は守り切った。
    /// </summary>
    public static IReadOnlyList<ScoutPromiseOutcome> AtYearEnd(IReadOnlyList<DevelopingPlayer> roster)
    {
        var outcomes = new List<ScoutPromiseOutcome>();
        foreach (var p in roster)
        {
            if (p.PromiseStatus != ScoutPromiseStatus.Active) continue;
            if (p.Grade >= 3) { Settle(outcomes, p, kept: true); continue; }
            if (p.Grade != 1) continue;
            switch (p.Promise)
            {
                case ScoutPromiseKind.PlayAsFreshman:
                case ScoutPromiseKind.StartingPitcher:
                    Settle(outcomes, p, kept: false);
                    break;
                case ScoutPromiseKind.Regular:
                case ScoutPromiseKind.Cleanup:
                case ScoutPromiseKind.TopOfOrder:
                case ScoutPromiseKind.RestFirstSummer:
                case ScoutPromiseKind.NoPinchHitter:
                case ScoutPromiseKind.NoBunt:
                    Settle(outcomes, p, kept: true);
                    break;
            }
        }
        return outcomes;
    }

    /// <summary>新チームの背番号を決めた直後に呼ぶ（1年の秋）。一桁をもらえたか。</summary>
    public static IReadOnlyList<ScoutPromiseOutcome> AfterNumbersDecided(IReadOnlyList<DevelopingPlayer> roster)
    {
        var outcomes = new List<ScoutPromiseOutcome>();
        foreach (var p in roster)
            if (p.PromiseStatus == ScoutPromiseStatus.Active && p.Promise == ScoutPromiseKind.SingleDigitNumber && p.Grade == 1)
                Settle(outcomes, p, kept: p.UniformNumber >= 1 && p.UniformNumber <= SingleDigitMax);
        return outcomes;
    }

    /// <summary>新チームの主将を指名した直後に呼ぶ（本人が2年の秋）。主将に指名したか。</summary>
    public static IReadOnlyList<ScoutPromiseOutcome> AfterCaptainNamed(IReadOnlyList<DevelopingPlayer> roster)
    {
        var outcomes = new List<ScoutPromiseOutcome>();
        foreach (var p in roster)
            if (p.PromiseStatus == ScoutPromiseStatus.Active && p.Promise == ScoutPromiseKind.Captain && p.Grade == 2)
                Settle(outcomes, p, kept: p.IsCaptain);
        return outcomes;
    }

    /// <summary>4月に入部した直後に呼ぶ。寮の約束は、その時点で寮があるか。</summary>
    public static IReadOnlyList<ScoutPromiseOutcome> AtJoin(IReadOnlyList<DevelopingPlayer> newcomers, bool hasDorm)
    {
        var outcomes = new List<ScoutPromiseOutcome>();
        foreach (var p in newcomers)
            if (p.PromiseStatus == ScoutPromiseStatus.Active && p.Promise == ScoutPromiseKind.Dorm)
                Settle(outcomes, p, kept: hasDorm);
        return outcomes;
    }

    /// <summary>
    /// このオーダーで試合に入ると破ることになる約束（オーダー画面の警告・確定時の確認・AIの組み直しに使う）。
    /// <paramref name="order"/> は打順どおりのスタメン9人、<paramref name="startingPitcher"/> は先発投手（DHのとき打順の外に居る）。
    /// 状態は書き換えない。
    /// </summary>
    public static IReadOnlyList<DevelopingPlayer> WouldBreak(
        IReadOnlyList<DevelopingPlayer> roster, IReadOnlyList<DevelopingPlayer> order, DevelopingPlayer? startingPitcher,
        TournamentKind kind, int pitchDayStamp)
    {
        var broken = new List<DevelopingPlayer>();
        var isSummer = kind == TournamentKind.Summer || kind == TournamentKind.SummerKoshien;
        var inFirstYearWindow = isSummer || IsAutumn(kind);
        foreach (var p in roster)
        {
            if (p.PromiseStatus != ScoutPromiseStatus.Active) continue;
            var slot = IndexOf(order, p) + 1;                      // 1〜9。スタメンでなければ 0
            var starts = slot > 0 || ReferenceEquals(startingPitcher, p);
            var firstYear = p.Grade == 1;
            var hurt = p.Injury != InjurySeverity.None;
            var breaks = p.Promise switch
            {
                ScoutPromiseKind.Regular => firstYear && inFirstYearWindow && !hurt && !starts,
                ScoutPromiseKind.Cleanup => firstYear && inFirstYearWindow && !hurt && slot != CleanupOrder,
                ScoutPromiseKind.TopOfOrder => firstYear && inFirstYearWindow && !hurt && (slot < 1 || slot > TopOrderMax),
                ScoutPromiseKind.RestFirstSummer => firstYear && isSummer && starts,
                ScoutPromiseKind.NoConsecutiveDays => ReferenceEquals(startingPitcher, p)
                                                      && p.LastOfficialPitchStamp != 0
                                                      && pitchDayStamp == p.LastOfficialPitchStamp + 1,
                _ => false,
            };
            if (breaks) broken.Add(p);
        }
        return broken;
    }

    /// <summary>
    /// 試合中の交代で、途中から出す選手が約束を破るか（交代画面の警告に使う。状態は書き換えない）。
    /// 「1年夏は出さない」の子を夏に出す／「連投させない」の子を前日に続けて登板させる、の2つ。
    /// </summary>
    /// <param name="pitches">この交代で登板させるか（継投・投野交代）。</param>
    public static bool BreaksOnEntry(DevelopingPlayer incoming, bool pitches, TournamentKind kind, int pitchDayStamp)
    {
        if (incoming.PromiseStatus != ScoutPromiseStatus.Active) return false;
        var isSummer = kind == TournamentKind.Summer || kind == TournamentKind.SummerKoshien;
        return incoming.Promise switch
        {
            ScoutPromiseKind.RestFirstSummer => incoming.Grade == 1 && isSummer,
            ScoutPromiseKind.NoConsecutiveDays => pitches && incoming.LastOfficialPitchStamp != 0
                                                  && pitchDayStamp == incoming.LastOfficialPitchStamp + 1,
            _ => false,
        };
    }

    /// <summary>代打を送られると「代打を出さないで」を破る打者か（交代画面の警告に使う。状態は書き換えない）。</summary>
    public static bool BreaksWhenPinchHitFor(DevelopingPlayer batter, TournamentKind kind)
    {
        var inFirstYearWindow = kind == TournamentKind.Summer || kind == TournamentKind.SummerKoshien || IsAutumn(kind);
        return batter.PromiseStatus == ScoutPromiseStatus.Active && batter.Promise == ScoutPromiseKind.NoPinchHitter
               && batter.Grade == 1 && inFirstYearWindow;
    }

    /// <summary>登板日の通し番号。同じ年度・同じ大会の中で、翌日が +1 になる（大会をまたぐと連続しない）。</summary>
    public static int PitchDayStamp(int yearIndex, TournamentKind kind, int matchDay)
        => (yearIndex * 16 + (int)kind + 1) * 1000 + matchDay + 1;

    private static bool IsAutumn(TournamentKind kind)
        => kind == TournamentKind.Autumn || kind == TournamentKind.Regional || kind == TournamentKind.AutumnDistrict;

    private static void Settle(List<ScoutPromiseOutcome> outcomes, DevelopingPlayer p, bool kept)
    {
        p.PromiseStatus = kept ? ScoutPromiseStatus.Kept : ScoutPromiseStatus.Broken;
        outcomes.Add(new ScoutPromiseOutcome(p, p.Promise, kept));
    }

    private static int IndexOf(IReadOnlyList<DevelopingPlayer> order, DevelopingPlayer p)
    {
        for (var i = 0; i < order.Count; i++) if (ReferenceEquals(order[i], p)) return i;
        return -1;
    }

    private static bool AppearedIn(IReadOnlyList<BattingLine> batting, int id)
    {
        foreach (var l in batting) if (l.SourceId == id) return true;
        return false;
    }

    private static bool PitchedIn(IReadOnlyList<PitchingLine> pitching, int id)
    {
        foreach (var l in pitching) if (l.SourceId == id) return true;
        return false;
    }

    /// <summary>スタメンの打順（1〜9）。スタメンでなければ 0。</summary>
    private static int StartOrder(IReadOnlyList<BattingLine> batting, int id)
    {
        foreach (var l in batting) if (l.SourceId == id && l.Entry == BoxEntry.Starter) return l.Order;
        return 0;
    }

    /// <summary>スタメンか（打順の9人、またはDHで打順の外に居る先発投手）。</summary>
    private static bool StartedIn(IReadOnlyList<BattingLine> batting, IReadOnlyList<PitchingLine> pitching, int id)
        => StartOrder(batting, id) > 0 || (pitching.Count > 0 && pitching[0].SourceId == id);

    private static bool PinchHitFor(IReadOnlyList<BattingLine> batting, string name)
    {
        foreach (var l in batting) if (l.Entry == BoxEntry.PinchHit && l.EnteredFor == name) return true;
        return false;
    }

    private static int SacrificeHits(IReadOnlyList<BattingLine> batting, int id)
    {
        var n = 0;
        foreach (var l in batting) if (l.SourceId == id) n += l.SacrificeHits;
        return n;
    }
}
