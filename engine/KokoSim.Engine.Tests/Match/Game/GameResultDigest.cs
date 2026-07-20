using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Match.Tactics;

namespace KokoSim.Engine.Tests.Match.Game;

/// <summary>
/// GameResult の「完全一致」ダイジェスト（決定論ゲート用）。スコア・全ログ・全統計・全カウンタを
/// 正規化した文字列へ落とし、SHA256 で固定する。イテレータ化リファクタの前後で1ビットの差も検出する。
/// （設計者Claude指示: 打席単位ステップ化の決定論ゲート。1シードでも不一致なら no-go）
/// </summary>
public static class GameResultDigest
{
    public static string Sha256Of(GameResult r)
    {
        using var sha = SHA256.Create();
        var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(Canonical(r)));
        var sb = new StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    public static string Canonical(GameResult r)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine(string.Create(c, $"{r.AwayName}|{r.HomeName}|A{r.AwayRuns}|H{r.HomeRuns}|IP{r.InningsPlayed}|TP{r.TotalPitches}|PC{r.PitcherChanges}"));
        sb.AppendLine(string.Create(c, $"sub{r.AwaySubstitutions}/{r.HomeSubstitutions} hpo{r.HomePlayOuts} fc{r.FieldersChoiceCount} d3{r.DroppedThirdStrikeCount} eea{r.ErrorExtraAdvanceCount} pk{r.PickoffCount} iw{r.IntentionalWalkCount} ds3{r.DoubleStealThirdBreakCount}"));
        sb.AppendLine(string.Create(c, $"lineA:{string.Join(",", r.AwayLineScore)} lineH:{string.Join(",", r.HomeLineScore)} hits{r.AwayHits}/{r.HomeHits} err{r.AwayErrors}/{r.HomeErrors}"));
        sb.AppendLine(string.Create(c, $"tacA:{r.AwayTactics} tacH:{r.HomeTactics}"));

        foreach (var e in r.Log)
        {
            sb.AppendLine(string.Create(c, $"L {e.Inning}{(e.IsTop ? 'T' : 'B')} {e.BatterName} {e.Result} r{e.RunsScored}"));
            // PitchLog（設計書15 §3.2）: 1球ごとの実データをdigest対象に含める。Trajectoryは観測コスト切替
            // （CaptureTimeline ON/OFF）でハッシュが動かないよう対象外（弾道は観測専用で判定に無関係）。
            if (e.PitchLog is { } log)
                foreach (var p in log)
                    sb.AppendLine(string.Create(c,
                        $"P {p.Kind}{p.BallsAfter}-{p.StrikesAfter} {p.PitchType} v{p.VelocityKmh:F2} x{p.LocationX:F3} y{p.LocationY:F3}"));
        }

        AppendBatting(sb, c, "AB", r.AwayBatting);
        AppendBatting(sb, c, "HB", r.HomeBatting);
        AppendPitching(sb, c, "AP", r.AwayPitching);
        AppendPitching(sb, c, "HP", r.HomePitching);
        return sb.ToString();
    }

    private static void AppendBatting(StringBuilder sb, CultureInfo c, string tag, IReadOnlyList<BattingLine> lines)
    {
        foreach (var b in lines)
            sb.AppendLine(string.Create(c, $"{tag} {b.Order} {b.Position} {b.Name} pa{b.PlateAppearances} ab{b.AtBats} h{b.Hits} 2b{b.Doubles} 3b{b.Triples} hr{b.HomeRuns} rbi{b.Rbi} bb{b.Walks} so{b.StrikeOuts} id{b.SourceId}"));
    }

    private static void AppendPitching(StringBuilder sb, CultureInfo c, string tag, IReadOnlyList<PitchingLine> lines)
    {
        foreach (var p in lines)
            sb.AppendLine(string.Create(c, $"{tag} {p.Name} o{p.Outs} bf{p.BattersFaced} h{p.Hits} r{p.Runs} so{p.StrikeOuts} bb{p.Walks} np{p.Pitches} id{p.SourceId}"));
    }
}

/// <summary>
/// 決定論ゲートの代表対戦カード（数種）＋シード列。イテレータ化の前後で全カード×全シードの
/// GameResult ダイジェストが一致することを保証する（統計テストが触る主要コードパスを網羅）。
/// </summary>
public static class DeterminismCards
{
    /// <summary>最低50シード。</summary>
    public static IEnumerable<ulong> Seeds()
    {
        for (ulong s = 1; s <= 50; s++) yield return s;
    }

    /// <summary>代表カード名。</summary>
    public static readonly string[] CardNames = { "avg", "tactics", "modern", "pitch-tactics" };

    public static GameResult Run(string card, ulong seed)
    {
        var rng = new Xoshiro256Random(seed);
        switch (card)
        {
            case "avg":
                return GameEngine.Play(Team("A"), Team("H"), new GameContext(), rng);
            case "tactics":
                return GameEngine.Play(
                    Team("A") with { Tactics = new StandardTacticsBrain() },
                    Team("H") with { Tactics = new StandardTacticsBrain() },
                    new GameContext(), rng);
            case "modern":
                return GameEngine.Play(Team("A"), Team("H"),
                    new GameContext { TieBreakEnabled = true, TieBreakStartInning = 10, MercyRuleEnabled = true }, rng);
            case "pitch-tactics":
                // 設計書15 Phase C-2: 1球采配（IPitchTacticsBrain）専用の決定論回帰アンカー。
                // ティア/采配能力を高めに設定し、AI三層を通しても1球采配が確実に発動する構成にする。
                var profile = new AiProfile(TacticalSense: 85, TierRank: 6, SchoolStyle.Standard);
                return GameEngine.Play(
                    Team("A") with { Tactics = new AiTacticsBrain(profile) },
                    Team("H") with { Tactics = new AiTacticsBrain(profile) },
                    new GameContext(), rng);
            default:
                throw new System.ArgumentOutOfRangeException(nameof(card), card, "unknown card");
        }
    }

    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 50, ArmStrength = 50, Fielding = 50, Catching = 50,
    };

    private static Team Team(string name)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8,
            Bullpen = new[]
            {
                Pos(FieldPosition.Pitcher) with { Name = name + "R1", Pitching = PitcherAttributes.LeagueAverage },
                Pos(FieldPosition.Pitcher) with { Name = name + "R2", Pitching = PitcherAttributes.LeagueAverage },
            },
            // 交代パスを踏ませるための控え（代打/代走/守備固め）。
            Bench = new[]
            {
                Pos(FieldPosition.FirstBase) with { Name = name + "PH", Contact = 55 },
                Pos(FieldPosition.SecondBase) with { Name = name + "PR", Speed = 70 },
            },
        };
    }
}
