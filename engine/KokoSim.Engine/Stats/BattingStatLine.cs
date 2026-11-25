using KokoSim.Engine.Match.Game;

namespace KokoSim.Engine.Stats;

/// <summary>
/// 個人打撃の累積成績（複数試合の集計）。1試合ぶんの <see cref="BattingLine"/> を <see cref="Add"/> で畳み込む。
/// 生カウンタのみ保持し、打率・出塁率・長打率は派生プロパティで算出（純データ・決定論）。
/// 犠飛（SF, issue #68）は打数から除外し、出塁率の分母にのみ算入する。犠打（SH）は打数にも
/// 出塁率の分母にも入れない（issue sacrifice-bunt-counted-as-at-bat）。
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
    /// <summary>犠飛（issue #68）。打数には含めず、出塁率の分母にのみ算入する。</summary>
    public int SacrificeFlies { get; private set; }
    /// <summary>犠打（issue sacrifice-bunt-counted-as-at-bat）。打数にも出塁率の分母にも含めない。</summary>
    public int SacrificeHits { get; private set; }

    /// <summary>打球データ（設計書06 §3.2b）: 物理を生んだインプレー打球の数。初速・角度の平均の分母。</summary>
    public int BattedBalls { get; private set; }
    /// <summary>インプレー打球の初速の合計[km/h]。</summary>
    public double ExitVelocitySumKmh { get; private set; }
    /// <summary>インプレー打球の打球角度の合計[°]。</summary>
    public double LaunchAngleSumDeg { get; private set; }
    /// <summary>キャリアハイ: 最高初速[km/h]（打球が無ければ0）。</summary>
    public double MaxExitVelocityKmh { get; private set; }
    /// <summary>キャリアハイ: 最長本塁打[m]（本塁打が無ければ0）。</summary>
    public double LongestHomeRunM { get; private set; }
    /// <summary>キャリアハイ: 1試合の最多安打。</summary>
    public int MaxHitsInGame { get; private set; }
    /// <summary>キャリアハイ: 1試合の最多打点。</summary>
    public int MaxRbiInGame { get; private set; }
    /// <summary>キャリアハイ: 1試合の最多本塁打。</summary>
    public int MaxHomeRunsInGame { get; private set; }

    /// <summary>平均打球初速[km/h]（打球0なら0）。</summary>
    public double AverageExitVelocityKmh => BattedBalls > 0 ? ExitVelocitySumKmh / BattedBalls : 0.0;

    /// <summary>平均打球角度[°]（打球0なら0）。</summary>
    public double AverageLaunchAngleDeg => BattedBalls > 0 ? LaunchAngleSumDeg / BattedBalls : 0.0;

    /// <summary>単打＝安打−（二塁打＋三塁打＋本塁打）。</summary>
    public int Singles => Hits - Doubles - Triples - HomeRuns;

    /// <summary>塁打数＝単打+2×二塁打+3×三塁打+4×本塁打。</summary>
    public int TotalBases => Singles + 2 * Doubles + 3 * Triples + 4 * HomeRuns;

    /// <summary>打率（打数0なら0）。</summary>
    public double Average => AtBats > 0 ? (double)Hits / AtBats : 0.0;

    /// <summary>出塁率＝(安打+四球+死球)/(打数+四球+死球+犠飛)（issue #68、分母0なら0）。</summary>
    public double Obp => (AtBats + Walks + HitByPitches + SacrificeFlies) > 0
        ? (double)(Hits + Walks + HitByPitches) / (AtBats + Walks + HitByPitches + SacrificeFlies) : 0.0;

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
        SacrificeFlies += l.SacrificeFlies;
        SacrificeHits += l.SacrificeHits;
        BattedBalls += l.BattedBalls;
        ExitVelocitySumKmh += l.ExitVelocitySumKmh;
        LaunchAngleSumDeg += l.LaunchAngleSumDeg;
        MaxExitVelocityKmh = System.Math.Max(MaxExitVelocityKmh, l.MaxExitVelocityKmh);
        LongestHomeRunM = System.Math.Max(LongestHomeRunM, l.LongestHomeRunM);
        MaxHitsInGame = System.Math.Max(MaxHitsInGame, l.Hits);
        MaxRbiInGame = System.Math.Max(MaxRbiInGame, l.Rbi);
        MaxHomeRunsInGame = System.Math.Max(MaxHomeRunsInGame, l.HomeRuns);
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
        SacrificeFlies += o.SacrificeFlies;
        SacrificeHits += o.SacrificeHits;
        BattedBalls += o.BattedBalls;
        ExitVelocitySumKmh += o.ExitVelocitySumKmh;
        LaunchAngleSumDeg += o.LaunchAngleSumDeg;
        MaxExitVelocityKmh = System.Math.Max(MaxExitVelocityKmh, o.MaxExitVelocityKmh);
        LongestHomeRunM = System.Math.Max(LongestHomeRunM, o.LongestHomeRunM);
        MaxHitsInGame = System.Math.Max(MaxHitsInGame, o.MaxHitsInGame);
        MaxRbiInGame = System.Math.Max(MaxRbiInGame, o.MaxRbiInGame);
        MaxHomeRunsInGame = System.Math.Max(MaxHomeRunsInGame, o.MaxHomeRunsInGame);
    }

    /// <summary>盗塁成功率（企図0なら0）。企図＝盗塁＋盗塁死。</summary>
    public double StolenBaseRate => (StolenBases + CaughtStealing) > 0
        ? (double)StolenBases / (StolenBases + CaughtStealing) : 0.0;

    /// <summary>
    /// セーブ復元専用（issue #507・<see cref="Saves.StatsSaves"/> だけが使う）。生カウンタを直接据える。
    /// private set の集計器を試合再生なしに復元するための internal フック（引数順は書き出しと一致させる）。
    /// </summary>
    internal void Restore(int games, int plateAppearances, int atBats, int hits, int doubles, int triples,
        int homeRuns, int rbi, int walks, int hitByPitches, int strikeOuts, int stolenBases,
        int caughtStealing, int runs, int sacrificeFlies, int sacrificeHits = 0)
    {
        Games = games;
        PlateAppearances = plateAppearances;
        AtBats = atBats;
        Hits = hits;
        Doubles = doubles;
        Triples = triples;
        HomeRuns = homeRuns;
        Rbi = rbi;
        Walks = walks;
        HitByPitches = hitByPitches;
        StrikeOuts = strikeOuts;
        StolenBases = stolenBases;
        CaughtStealing = caughtStealing;
        Runs = runs;
        SacrificeFlies = sacrificeFlies;
        SacrificeHits = sacrificeHits;
    }

    /// <summary>セーブ復元専用: 打球データとキャリアハイ（設計書06 §3.2b）。<see cref="Restore"/> の後に呼ぶ。</summary>
    internal void RestorePhysical(int battedBalls, double exitVelocitySumKmh, double launchAngleSumDeg,
        double maxExitVelocityKmh, double longestHomeRunM, int maxHitsInGame, int maxRbiInGame, int maxHomeRunsInGame)
    {
        BattedBalls = battedBalls;
        ExitVelocitySumKmh = exitVelocitySumKmh;
        LaunchAngleSumDeg = launchAngleSumDeg;
        MaxExitVelocityKmh = maxExitVelocityKmh;
        LongestHomeRunM = longestHomeRunM;
        MaxHitsInGame = maxHitsInGame;
        MaxRbiInGame = maxRbiInGame;
        MaxHomeRunsInGame = maxHomeRunsInGame;
    }
}
