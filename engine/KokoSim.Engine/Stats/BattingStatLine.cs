using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Stats;

/// <summary>
/// 個人打撃の累積成績（複数試合の集計）。1試合ぶんの <see cref="BattingLine"/> を <see cref="Add"/> で畳み込む。
/// 生カウンタのみ保持し、打率・出塁率・長打率は派生プロパティで算出（純データ・決定論）。
/// 死球/犠飛は現エンジン未対応のため出塁率は (安打+四球)/(打数+四球) の近似（OPEN-QUESTIONS）。
/// </summary>
public sealed class BattingStatLine
{
    public int Games { get; private set; }
    public int PlateAppearances { get; private set; }
    public int AtBats { get; private set; }
    public int Hits { get; private set; }
    public int Doubles { get; private set; }
    public int Triples { get; private set; }
    public int HomeRuns { get; private set; }
    public int Rbi { get; private set; }
    public int Walks { get; private set; }
    public int StrikeOuts { get; private set; }

    /// <summary>単打＝安打−（二塁打＋三塁打＋本塁打）。</summary>
    public int Singles => Hits - Doubles - Triples - HomeRuns;

    /// <summary>塁打数＝単打+2×二塁打+3×三塁打+4×本塁打。</summary>
    public int TotalBases => Singles + 2 * Doubles + 3 * Triples + 4 * HomeRuns;

    /// <summary>打率（打数0なら0）。</summary>
    public double Average => AtBats > 0 ? (double)Hits / AtBats : 0.0;

    /// <summary>出塁率＝(安打+四球)/(打数+四球)（死球/犠飛は未対応の近似, 分母0なら0）。</summary>
    public double Obp => (AtBats + Walks) > 0 ? (double)(Hits + Walks) / (AtBats + Walks) : 0.0;

    /// <summary>長打率＝塁打/打数（打数0なら0）。</summary>
    public double Slg => AtBats > 0 ? (double)TotalBases / AtBats : 0.0;

    /// <summary>OPS＝出塁率＋長打率。</summary>
    public double Ops => Obp + Slg;

    /// <summary>1試合ぶんを畳み込む。打席が立っていれば試合数を1加算。</summary>
    public void Add(BattingLine l)
    {
        if (l.PlateAppearances > 0) Games++;
        PlateAppearances += l.PlateAppearances;
        AtBats += l.AtBats;
        Hits += l.Hits;
        Doubles += l.Doubles;
        Triples += l.Triples;
        HomeRuns += l.HomeRuns;
        Rbi += l.Rbi;
        Walks += l.Walks;
        StrikeOuts += l.StrikeOuts;
    }
}
