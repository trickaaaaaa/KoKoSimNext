using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Stats;

/// <summary>
/// 個人投手の累積成績（複数試合の集計）。1試合ぶんの <see cref="PitchingLine"/> ＋勝敗フラグを畳み込む。
/// 防御率は失点ベース（自責点は現エンジン未追跡＝RA近似, OPEN-QUESTIONS）。純データ・決定論。
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
    public int StrikeOuts { get; private set; }
    public int Walks { get; private set; }
    public int HitBatters { get; private set; }
    public int Pitches { get; private set; }
    /// <summary>被本塁打（issue #77）。</summary>
    public int HomeRunsAllowed { get; private set; }

    /// <summary>投球回テキスト（例: 7回1/3 → "7 1/3"）。</summary>
    public string InningsText => (Outs / 3) + (Outs % 3 == 0 ? "" : " " + (Outs % 3) + "/3");

    /// <summary>防御率＝失点×27/アウト数（9回換算の失点。自責点未追跡のためRA近似, アウト0なら0）。</summary>
    public double Era => Outs > 0 ? Runs * 27.0 / Outs : 0.0;

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
        StrikeOuts += l.StrikeOuts;
        Walks += l.Walks;
        HitBatters += l.HitBatters;
        Pitches += l.Pitches;
        HomeRunsAllowed += l.HomeRunsAllowed;
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
        StrikeOuts += o.StrikeOuts;
        Walks += o.Walks;
        HitBatters += o.HitBatters;
        Pitches += o.Pitches;
        HomeRunsAllowed += o.HomeRunsAllowed;
    }
}
