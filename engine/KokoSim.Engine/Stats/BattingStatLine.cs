using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Stats;

/// <summary>
/// 個人打撃の累積成績（複数試合の集計）。1試合ぶんの <see cref="BattingLine"/> を <see cref="Add"/> で畳み込む。
/// 生カウンタのみ保持し、打率・出塁率・長打率は派生プロパティで算出（純データ・決定論）。
/// 犠飛は現エンジン未対応のため出塁率は (安打+四球+死球)/(打数+四球+死球) の近似（犠飛のみ分母から欠落）。
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
    public int HitByPitches { get; private set; }
    public int StrikeOuts { get; private set; }
    public int StolenBases { get; private set; }
    public int CaughtStealing { get; private set; }
    /// <summary>得点（個人の生還数, issue #77）。</summary>
    public int Runs { get; private set; }

    /// <summary>単打＝安打−（二塁打＋三塁打＋本塁打）。</summary>
    public int Singles => Hits - Doubles - Triples - HomeRuns;

    /// <summary>塁打数＝単打+2×二塁打+3×三塁打+4×本塁打。</summary>
    public int TotalBases => Singles + 2 * Doubles + 3 * Triples + 4 * HomeRuns;

    /// <summary>打率（打数0なら0）。</summary>
    public double Average => AtBats > 0 ? (double)Hits / AtBats : 0.0;

    /// <summary>出塁率＝(安打+四球+死球)/(打数+四球+死球)（犠飛のみ未対応の近似, 分母0なら0）。</summary>
    public double Obp => (AtBats + Walks + HitByPitches) > 0
        ? (double)(Hits + Walks + HitByPitches) / (AtBats + Walks + HitByPitches) : 0.0;

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
        HitByPitches += l.HitByPitches;
        StrikeOuts += l.StrikeOuts;
        StolenBases += l.StolenBases;
        CaughtStealing += l.CaughtStealing;
        Runs += l.Runs;
    }

    /// <summary>別の累積打撃成績を合算する（大会別アーカイブの秋合算＝県/地区/神宮, issue #77）。</summary>
    public void Merge(BattingStatLine o)
    {
        Games += o.Games;
        PlateAppearances += o.PlateAppearances;
        AtBats += o.AtBats;
        Hits += o.Hits;
        Doubles += o.Doubles;
        Triples += o.Triples;
        HomeRuns += o.HomeRuns;
        Rbi += o.Rbi;
        Walks += o.Walks;
        HitByPitches += o.HitByPitches;
        StrikeOuts += o.StrikeOuts;
        StolenBases += o.StolenBases;
        CaughtStealing += o.CaughtStealing;
        Runs += o.Runs;
    }

    /// <summary>盗塁成功率（企図0なら0）。企図＝盗塁＋盗塁死。</summary>
    public double StolenBaseRate => (StolenBases + CaughtStealing) > 0
        ? (double)StolenBases / (StolenBases + CaughtStealing) : 0.0;
}
