using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>今日のMVPに選ばれた1人と、その活躍の中身（設計書02 §5.3c・issue #1745）。</summary>
public sealed record MatchMvpPick(
    int PlayerId, string Name, double Score, double BattingScore, double PitchingScore,
    BattingLine? Batting, PitchingLine? Pitching, bool CompleteGame, bool Shutout);

/// <summary>ご褒美が実際に動かしたもの。打撃（ミート/パワー）・投球（制球/キレ/スタミナ）・調子。</summary>
public sealed record MatchMvpReward(bool Batting, bool Pitching, bool Condition);

public sealed record MatchMvpResult(MatchMvpPick Pick, MatchMvpReward Reward);

/// <summary>
/// 今日のMVP（設計書02 §5.3c・issue #1745）。公式戦に勝った試合の自校の出場選手から、いちばん活躍した1人を選び、
/// 活躍した分野の経験値（成功体験ボーナスの <see cref="TrainingCoefficients.MvpRewardMult"/> 倍・同じ経路・同じ大会の格の倍率）と
/// 調子一段アップを与える。選び方は点数式（<see cref="Score"/>）で、同点は打点→投球回→打順→選手Idの順。
/// 乱数も時刻も使わない＝同じ試合なら同じMVP（不変条件#2）。試合結果（判定）には触れない。
/// </summary>
public static class MatchMvp
{
    /// <summary>MVPを選ぶだけ（成長は動かさない）。自校が勝っていない・点数が正の選手がいない・ロスターに載る選手がいないなら null。</summary>
    public static MatchMvpPick? Choose(
        GameResult detail, bool managerIsAway, IReadOnlyList<DevelopingPlayer> roster, TrainingCoefficients c)
    {
        var managerWon = managerIsAway ? !detail.HomeWon && !detail.Tied : detail.HomeWon;
        if (!managerWon) return null;

        var batting = managerIsAway ? detail.AwayBatting : detail.HomeBatting;
        var pitching = managerIsAway ? detail.AwayPitching : detail.HomePitching;
        var opponentRuns = managerIsAway ? detail.HomeRuns : detail.AwayRuns;

        var byId = new Dictionary<int, DevelopingPlayer>(roster.Count);
        foreach (var dp in roster) byId[dp.Id] = dp;

        var cands = new Dictionary<int, Candidate>();
        foreach (var line in batting)
        {
            if (line.SourceId is not { } id || !byId.ContainsKey(id)) continue;
            var cand = cands.TryGetValue(id, out var x) ? x : cands[id] = new Candidate(id);
            cand.Batting = line;
            cand.BattingScore = BattingScore(line, c);
            cand.Order = line.Order;
        }
        var completeGame = pitching.Count == 1;
        foreach (var line in pitching)
        {
            if (line.SourceId is not { } id || !byId.ContainsKey(id)) continue;
            var cand = cands.TryGetValue(id, out var x) ? x : cands[id] = new Candidate(id);
            var isComplete = completeGame && line.Outs > 0;
            cand.Pitching = line;
            cand.CompleteGame = isComplete;
            cand.Shutout = isComplete && opponentRuns == 0;
            cand.PitchingScore = PitchingScore(line, cand.CompleteGame, cand.Shutout, c);
        }

        Candidate? best = null;
        foreach (var cand in cands.Values)
        {
            if (cand.Score <= 0.0) continue;
            if (best == null || Better(cand, best)) best = cand;
        }
        if (best == null) return null;

        return new MatchMvpPick(
            best.Id, byId[best.Id].Name, best.Score, best.BattingScore, best.PitchingScore,
            best.Batting, best.Pitching, best.CompleteGame, best.Shutout);
    }

    /// <summary>
    /// MVPを選び、ご褒美を与える。自校が公式戦に勝った試合だけ呼ぶこと（練習試合・負けは呼び出し側が除く。
    /// 負けならここでも null）。<paramref name="kind"/> は大会の格（成功体験ボーナスと同じ倍率）。
    /// </summary>
    public static MatchMvpResult? Apply(
        GameResult detail, bool managerIsAway, IReadOnlyList<DevelopingPlayer> roster, TournamentKind kind,
        int week, SeasonCalendar calendar, GrowthStageTable stages, TrainingCoefficients c)
    {
        var pick = Choose(detail, managerIsAway, roster, c);
        if (pick == null) return null;

        DevelopingPlayer? dp = null;
        foreach (var p in roster) if (p.Id == pick.PlayerId) { dp = p; break; }
        if (dp == null) return null;

        var scale = MatchGrowthModel.SuccessScale(dp, stages, calendar, week, c.MatchSuccessStageMult(kind));
        var grewBat = pick.BattingScore > 0.0
            && Grow(dp, BatAbilities, c.MatchSuccessBatExp * scale * c.MvpRewardMult, c);
        var grewPitch = pick.PitchingScore > 0.0
            && Grow(dp, PitchAbilities, c.MatchSuccessPitchExp * scale * c.MvpRewardMult, c);

        var before = FormModel.Quantize(dp.ConditionValue);
        dp.ConditionValue = FormModel.RaiseOneTier(dp.ConditionValue);
        var raised = FormModel.Quantize(dp.ConditionValue) != before;

        return new MatchMvpResult(pick, new MatchMvpReward(grewBat, grewPitch, raised));
    }

    private static readonly AbilityKind[] BatAbilities = { AbilityKind.Contact, AbilityKind.Power };
    private static readonly AbilityKind[] PitchAbilities = { AbilityKind.Control, AbilityKind.PitchRank, AbilityKind.Stamina };

    /// <summary>各能力へ同じ経験値を足し、どれか1つでも経験値かLvが動いたら true（上限で止まっていれば false）。</summary>
    private static bool Grow(DevelopingPlayer dp, AbilityKind[] kinds, double exp, TrainingCoefficients c)
    {
        var moved = false;
        foreach (var k in kinds)
        {
            var lv = dp.Level(k);
            var e = dp.Exp(k);
            DevelopmentModel.ApplyExp(dp, k, exp, c);
            if (dp.Level(k) != lv || dp.Exp(k) != e) moved = true;
        }
        return moved;
    }

    /// <summary>打者の点数。安打・長打・本塁打・打点・得点・盗塁。</summary>
    public static double BattingScore(BattingLine l, TrainingCoefficients c)
        => l.Hits * c.MvpHit
           + (l.Doubles + l.Triples + l.HomeRuns) * c.MvpExtraBaseHit
           + l.HomeRuns * c.MvpHomeRun
           + l.Rbi * c.MvpRbi
           + l.Runs * c.MvpRun
           + l.StolenBases * c.MvpStolenBase;

    /// <summary>投手の点数。投球回（1/3回は1/3）・奪三振・失点・完投・完封。</summary>
    public static double PitchingScore(PitchingLine l, bool completeGame, bool shutout, TrainingCoefficients c)
        => l.Outs / 3.0 * c.MvpInning
           + l.StrikeOuts * c.MvpStrikeOut
           - l.Runs * c.MvpRunAllowed
           + (completeGame ? c.MvpCompleteGame : 0.0)
           + (shutout ? c.MvpShutout : 0.0);

    private sealed class Candidate
    {
        public Candidate(int id) { Id = id; }
        public int Id { get; }
        public BattingLine? Batting;
        public PitchingLine? Pitching;
        public double BattingScore, PitchingScore;
        public bool CompleteGame, Shutout;
        public int Order = int.MaxValue;   // 投手専任（DHなしの先発の投手以外の打線に載らない）は最後

        /// <summary>浮動小数点の端数で同点が割れないよう丸める。</summary>
        public double Score => Math.Round(BattingScore + PitchingScore, 6);
        public int Rbi => Batting?.Rbi ?? 0;
        public int Outs => Pitching?.Outs ?? 0;
    }

    /// <summary>同点の決め方: 点数 → 打点 → 投球回（アウト数）→ 打順の若い方 → 選手Id。</summary>
    private static bool Better(Candidate a, Candidate b)
    {
        if (a.Score != b.Score) return a.Score > b.Score;
        if (a.Rbi != b.Rbi) return a.Rbi > b.Rbi;
        if (a.Outs != b.Outs) return a.Outs > b.Outs;
        if (a.Order != b.Order) return a.Order < b.Order;
        return a.Id < b.Id;
    }

    /// <summary>活躍の一文（例「4打数3安打、1本塁打、2打点」「9回1失点、10奪三振の完投」）。打者・投手・両方で言い回しを分ける。</summary>
    public static string Describe(MatchMvpPick pick)
    {
        var bat = pick.BattingScore > 0.0 && pick.Batting != null ? DescribeBatting(pick.Batting) : null;
        var pit = pick.PitchingScore > 0.0 && pick.Pitching != null ? DescribePitching(pick.Pitching, pick) : null;
        if (bat != null && pit != null) return pit + "。打っても" + bat;
        return pit ?? bat ?? "";
    }

    private static string DescribeBatting(BattingLine l)
    {
        var s = l.AtBats > 0 ? l.AtBats + "打数" + l.Hits + "安打" : l.PlateAppearances + "打席";
        if (l.HomeRuns > 0) s += "、" + l.HomeRuns + "本塁打";
        if (l.Rbi > 0) s += "、" + l.Rbi + "打点";
        if (l.Runs > 0) s += "、" + l.Runs + "得点";
        if (l.StolenBases > 0) s += "、" + l.StolenBases + "盗塁";
        return s;
    }

    private static string DescribePitching(PitchingLine l, MatchMvpPick pick)
    {
        var whole = l.Outs / 3;
        var frac = l.Outs % 3;
        var inning = frac == 0 ? whole + "回" : whole == 0 ? frac + "/3回" : whole + "回" + frac + "/3";
        var runs = l.Runs == 0 ? "無失点" : l.Runs + "失点";
        var strikeOuts = l.StrikeOuts > 0 ? "、" + l.StrikeOuts + "奪三振" : "";
        if (pick.Shutout) return inning + "を" + runs + strikeOuts + "の完封";
        if (pick.CompleteGame) return inning + runs + strikeOuts + "の完投";
        return inning + "を投げて" + runs + strikeOuts;
    }

    /// <summary>ご褒美の中身の一文（「！」「。」なし）。実際に動いたものだけ書く。何も動かなければ null。</summary>
    public static string? RewardText(MatchMvpReward r)
    {
        var parts = new List<string>();
        if (r.Batting) parts.Add("ミート・パワーに経験値");
        if (r.Pitching) parts.Add("制球・キレ・スタミナに経験値");
        if (r.Condition) parts.Add("調子が上がった");
        return parts.Count == 0 ? null : string.Join("、", parts);
    }
}
