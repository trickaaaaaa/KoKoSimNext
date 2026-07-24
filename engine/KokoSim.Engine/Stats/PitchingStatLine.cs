using System.Collections.Generic;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Stats;

/// <summary>
/// 個人投手の累積成績（複数試合の集計）。1試合ぶんの <see cref="PitchingLine"/> ＋勝敗フラグを畳み込む。
/// 防御率は自責点ベース（issue #69。失策/失策連鎖で出塁・延命した走者の得点を除外する簡易規則。
/// 詳細規則は docs/design/OPEN-QUESTIONS.md 未決E決定事項を参照）。失点ベースは <see cref="Ra"/> に残す。
/// 純データ・決定論。
/// </summary>
public sealed class PitchingStatLine
{
    public int Games { get; private set; }
    public int GamesStarted { get; private set; }
    public int Wins { get; private set; }
    public int Losses { get; private set; }
    public int Outs { get; private set; }
    public int BattersFaced { get; private set; }
    public int Hits { get; private set; }
    public int Runs { get; private set; }
    /// <summary>自責点（issue #69）。Runs（失点）の内、失策/失策連鎖に起因しない得点。</summary>
    public int EarnedRuns { get; private set; }
    public int StrikeOuts { get; private set; }
    public int Walks { get; private set; }
    public int HitBatters { get; private set; }
    public int Pitches { get; private set; }
    /// <summary>被本塁打（issue #77）。</summary>
    public int HomeRunsAllowed { get; private set; }

    private readonly Dictionary<PitchType, PitchTypeAccum> _byPitch = new();

    /// <summary>球種ごとの被打成績（issue #180）。打席確定球がインプレー/被安打の打席のみ集計対象。</summary>
    public IReadOnlyDictionary<PitchType, PitchTypeBattingLine> BattingAgainstByPitch
        => ToBattingLines(_byPitch);

    /// <summary>投球回テキスト（例: 7回1/3 → "7 1/3"）。</summary>
    public string InningsText => (Outs / 3) + (Outs % 3 == 0 ? "" : " " + (Outs % 3) + "/3");

    /// <summary>防御率＝自責点×27/アウト数（9回換算の自責点, issue #69, アウト0なら0）。</summary>
    public double Era => Outs > 0 ? EarnedRuns * 27.0 / Outs : 0.0;

    /// <summary>失点率（RA）＝失点×27/アウト数（自責点を区別しない参考値, アウト0なら0）。</summary>
    public double Ra => Outs > 0 ? Runs * 27.0 / Outs : 0.0;

    /// <summary>WHIP＝(被安打+与四球)/投球回（アウト0なら0）。</summary>
    public double Whip => Outs > 0 ? (Hits + Walks) * 3.0 / Outs : 0.0;

    /// <summary>奪三振率（9回換算, アウト0なら0）。</summary>
    public double KPer9 => Outs > 0 ? StrikeOuts * 27.0 / Outs : 0.0;

    /// <summary>1試合ぶんを畳み込む（started＝先発, win/loss＝勝敗投手判定の結果）。</summary>
    public void Add(PitchingLine l, bool started, bool win, bool loss)
    {
        Games++;
        if (started) GamesStarted++;
        if (win) Wins++;
        if (loss) Losses++;
        Outs += l.Outs;
        BattersFaced += l.BattersFaced;
        Hits += l.Hits;
        Runs += l.Runs;
        EarnedRuns += l.EarnedRuns;
        StrikeOuts += l.StrikeOuts;
        Walks += l.Walks;
        HitBatters += l.HitBatters;
        Pitches += l.Pitches;
        HomeRunsAllowed += l.HomeRunsAllowed;
        if (l.BattingAgainstByPitch is { } byPitch)
            foreach (var (type, line) in byPitch)
                AddPitchType(type, line.AtBats, line.Hits, line.HomeRuns);
    }

    /// <summary>別の累積投手成績を合算する（大会別アーカイブの秋合算＝県/地区/神宮, issue #77）。</summary>
    public void Merge(PitchingStatLine o)
    {
        Games += o.Games;
        GamesStarted += o.GamesStarted;
        Wins += o.Wins;
        Losses += o.Losses;
        Outs += o.Outs;
        BattersFaced += o.BattersFaced;
        Hits += o.Hits;
        Runs += o.Runs;
        EarnedRuns += o.EarnedRuns;
        StrikeOuts += o.StrikeOuts;
        Walks += o.Walks;
        HitBatters += o.HitBatters;
        Pitches += o.Pitches;
        HomeRunsAllowed += o.HomeRunsAllowed;
        foreach (var (type, line) in o.BattingAgainstByPitch)
            AddPitchType(type, line.AtBats, line.Hits, line.HomeRuns);
    }

    private void AddPitchType(PitchType type, int atBats, int hits, int homeRuns)
    {
        if (!_byPitch.TryGetValue(type, out var a)) { a = new PitchTypeAccum(); _byPitch[type] = a; }
        a.AtBats += atBats;
        a.Hits += hits;
        a.HomeRuns += homeRuns;
    }

    private static IReadOnlyDictionary<PitchType, PitchTypeBattingLine> ToBattingLines(Dictionary<PitchType, PitchTypeAccum> src)
    {
        var result = new Dictionary<PitchType, PitchTypeBattingLine>(src.Count);
        foreach (var (type, a) in src)
            result[type] = new PitchTypeBattingLine(a.AtBats, a.Hits, a.HomeRuns);
        return result;
    }

    private sealed class PitchTypeAccum { public int AtBats, Hits, HomeRuns; }
}
