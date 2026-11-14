using System.Globalization;
using System.Text;
using KokoSim.Config;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.AtBat;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;

namespace KokoSim.Balance;

/// <summary>
/// 能力感度の掃引（issue #840）。**能力を1つだけ振って試合の結果が動くか**を、帯（balance-targets.yaml）とは別の目で見る。
///
/// <para>統計帯は「平均的なチーム同士の平均値」しか測らないので、「設計では効くはずの能力が結果を動かしていない／
/// 逆向きに効いている」は帯が全部緑のまま残る（#835 変化球が効かない／#837 遅い投手ほど打たれない／#838 盗塁AIが損をする）。</para>
///
/// <para>測り方: 全能力50の均一チーム同士（投手はストレート＋変化球2つ・ランク50）。能力を1つだけ 20 / 80 に振り、
/// **片方のチームだけ**変える。変えたチームの得点＝攻撃側の効果、相手の得点＝守備側の効果。
/// 両軍同時に振ると走力・調子・精神力のように攻守両方に効く能力が打ち消し合って測れない。
/// 後攻の9回裏は勝っていると省略されるので、変えたチームの先攻・後攻は試合ごとに入れ替える（偶数番=先攻）。</para>
///
/// <para>決定論: 乱数ストリームは逐次 Fork で事前確定、チームは乱数を使わず試合ローカルな状態は <see cref="GameEngine.Play"/> が作り、
/// 集計は整数加算のみ＝実行順序・スレッド数に依らず同シード同結果。</para>
/// </summary>
public static class AbilitySweep
{
    /// <summary>低い側・高い側の水準（表示能力 Lv）。基準は全員 50。</summary>
    public const int LowLevel = 20;
    public const int HighLevel = 80;
    public const int BaseLevel = 50;

    /// <summary>最速の梯子（km/h）。基準の Lv50 は 132km/h。</summary>
    public static readonly double[] VelocityLadderKmh = { 100, 110, 120, 130, 140, 150 };

    /// <summary>変えたい対象の範囲。</summary>
    public enum Scope { AllPlayers, Catcher, Pitchers, Battery }

    /// <summary>
    /// 1能力の掃引条件。値の単位は能力によって Lv か km/h。<paramref name="Prelude"/> は 20 / 80 を振る前に
    /// 範囲内の選手へ先に当てる下ごしらえ（全員50の世界では起きない状況を作る。例: 盗塁は走力80の走者でないと走る場面が来ない）。
    /// </summary>
    public sealed record Condition(
        string Id, string Label, Scope Scope, double Low, double High, string Unit,
        Func<Player, double, Player> Apply, Func<Player, Player>? Prelude = null);

    // ── 集計 ──

    /// <summary>1チーム分の打撃側集計＋守備側の失策・継投（整数のみ＝並列マージ順に依らず決定論）。</summary>
    public sealed class Totals
    {
        public long Runs, Hits, PlateAppearances, Strikeouts, Walks, HomeRuns;
        public long Errors, StealAttempts, StealSuccesses, SacrificeBunts, PitcherChanges;

        public void Merge(Totals o)
        {
            Runs += o.Runs; Hits += o.Hits; PlateAppearances += o.PlateAppearances;
            Strikeouts += o.Strikeouts; Walks += o.Walks; HomeRuns += o.HomeRuns;
            Errors += o.Errors; StealAttempts += o.StealAttempts; StealSuccesses += o.StealSuccesses;
            SacrificeBunts += o.SacrificeBunts; PitcherChanges += o.PitcherChanges;
        }
    }

    /// <summary>1条件の実測。<see cref="Focus"/>＝能力を変えたチーム、<see cref="Opponent"/>＝全員50のチーム。</summary>
    public sealed record Cell(string Id, string Label, double Value, int Games,
        Totals Focus, Totals Opponent, long DoublePlays, long WildPitches)
    {
        private double Per(long v) => Games > 0 ? (double)v / Games : 0;
        private static double Rate(long n, long d) => d > 0 ? (double)n / d : 0;

        /// <summary>自軍得点/試合（攻撃側の効果）。</summary>
        public double RunsFor => Per(Focus.Runs);
        /// <summary>相手得点/試合（守備側の効果）。</summary>
        public double RunsAgainst => Per(Opponent.Runs);
        public double HitsFor => Per(Focus.Hits);
        public double HitsAgainst => Per(Opponent.Hits);
        /// <summary>自軍打者の三振率／自軍投手が取った三振率（相手打者の三振率）。</summary>
        public double BatterStrikeoutRate => Rate(Focus.Strikeouts, Focus.PlateAppearances);
        public double PitcherStrikeoutRate => Rate(Opponent.Strikeouts, Opponent.PlateAppearances);
        public double BatterWalkRate => Rate(Focus.Walks, Focus.PlateAppearances);
        public double PitcherWalkRate => Rate(Opponent.Walks, Opponent.PlateAppearances);
        public double BatterHomeRunRate => Rate(Focus.HomeRuns, Focus.PlateAppearances);
        public double PitcherHomeRunRate => Rate(Opponent.HomeRuns, Opponent.PlateAppearances);
        public double ErrorsFor => Per(Focus.Errors);
        public double StealAttemptsFor => Per(Focus.StealAttempts);
        public double StealAttemptsAgainst => Per(Opponent.StealAttempts);
        public double StealSuccessRateFor => Rate(Focus.StealSuccesses, Focus.StealAttempts);
        public double StealSuccessRateAgainst => Rate(Opponent.StealSuccesses, Opponent.StealAttempts);
        public double SacrificeBuntsFor => Per(Focus.SacrificeBunts);
        public double DoublePlaysPerGame => Per(DoublePlays);
        public double WildPitchesPerGame => Per(WildPitches);
        public double PitcherChangesFor => Per(Focus.PitcherChanges);
    }

    /// <summary>掃引の全結果。</summary>
    public sealed record Result(
        int Games, ulong Seed, Cell Baseline,
        IReadOnlyList<(Condition Condition, Cell Low, Cell High)> Abilities,
        IReadOnlyList<Cell> VelocityLadder,
        IReadOnlyList<Cell> Repertoire);

    // ── 条件の定義 ──

    private static Player WithPitching(Player p, Func<PitcherAttributes, PitcherAttributes> f)
        => p with { Pitching = f(p.Pitching) };

    private static IReadOnlyList<PitchSlot> MapRepertoire(PitcherAttributes a, Func<PitchSlot, PitchSlot> f)
        => a.EffectiveRepertoire.Select(f).ToArray();

    /// <summary>性格の効き（C#既定＝実ゲームと同じ）。姿勢の掃引で犠打・チャンスの焼き込み値を作る。</summary>
    private static readonly PersonalityCoefficients PersonalityEffect = new();

    private static Condition C(string id, string label, Scope scope, Func<Player, double, Player> apply,
        double low = LowLevel, double high = HighLevel, string unit = "Lv", Func<Player, Player>? prelude = null)
        => new(id, label, scope, low, high, unit, apply, prelude);

    /// <summary>掃引する能力の一覧（Id は balance-targets.yaml の ability_sensitivity.abilities の鍵と一致）。</summary>
    public static readonly IReadOnlyList<Condition> Conditions = new[]
    {
        C("contact", "ミート", Scope.AllPlayers, (p, v) => p with { Contact = (int)v }),
        C("power", "パワー", Scope.AllPlayers, (p, v) => p with { Power = (int)v }),
        C("launch", "弾道", Scope.AllPlayers, (p, v) => p with { LaunchTendency = (int)v }),
        C("discipline", "選球眼", Scope.AllPlayers, (p, v) => p with { Discipline = (int)v }),
        C("speed", "走力", Scope.AllPlayers, (p, v) => p with { Speed = (int)v }),
        C("steal", "盗塁", Scope.AllPlayers, (p, v) => p with { Steal = (int)v }),
        C("steal_fast", "盗塁（走力80の走者）", Scope.AllPlayers, (p, v) => p with { Steal = (int)v },
            prelude: p => p with { Speed = HighLevel }),
        C("baserunning", "走塁", Scope.AllPlayers, (p, v) => p with { Baserunning = (int)v }),
        C("bunt", "バント", Scope.AllPlayers, (p, v) => p with { Bunt = (int)v }),
        C("arm", "肩", Scope.AllPlayers, (p, v) => p with { ArmStrength = (int)v }),
        C("throw_accuracy", "送球精度", Scope.AllPlayers, (p, v) => p with { ThrowAccuracy = (int)v }),
        C("fielding", "守備", Scope.AllPlayers, (p, v) => p with { Fielding = (int)v }),
        C("catching", "捕球", Scope.AllPlayers, (p, v) => p with { Catching = (int)v }),
        C("mental", "精神力", Scope.AllPlayers, (p, v) => p with { Mental = (int)v }),
        C("condition", "調子", Scope.AllPlayers, (p, v) =>
        {
            var cv = (v - BaseLevel) / 30.0;   // 20→-1（絶不調）／80→+1（絶好調）
            return p with { Condition = FormModel.Quantize(cv), ConditionValue = cv };
        }),
        // 性格の4軸（設計書23 §1・issue #932）。統率は主将・伝令でだけ効く既存の値なので掃引しない。
        // 素直さ＝伝令の落ち着き、姿勢＝犠打・チャンスの長打（投影時に焼き込む値も一緒に置く）、勤勉さ＝自主成長だけ（試合では効かない）。
        C("receptiveness", "性格:素直さ", Scope.AllPlayers, (p, v) => p with { Receptiveness = (int)v }),
        C("diligence", "性格:勤勉さ", Scope.AllPlayers, (p, v) => p with { Diligence = (int)v }),
        C("selflessness", "性格:試合での姿勢（献身）", Scope.AllPlayers, (p, v) => p with
        {
            Selflessness = (int)v,
            BuntSuccessBonus = PersonalityEffect.BuntSuccessBonus((int)v),
            ChanceHitFactor = PersonalityEffect.ChanceHitFactor((int)v),
        }),
        C("catcher_lead", "捕手:リード", Scope.Catcher, (p, v) => p with { Lead = (int)v }),
        // バッテリーの相性の出発点（設計書23 §2.2・issue #933）: 捕手の統率と投手の素直さを同時に振る＝S0 だけが動く
        // （均一チームは学年0＝積み重ね B は0）。
        C("battery_affinity", "バッテリー:相性（捕手の統率×投手の素直さ）", Scope.Battery, (p, v) =>
            p.Position == FieldPosition.Catcher ? p with { Leadership = (int)v } : p with { Receptiveness = (int)v }),
        C("catcher_arm", "捕手:肩", Scope.Catcher, (p, v) => p with { ArmStrength = (int)v }),
        C("catcher_catching", "捕手:捕球", Scope.Catcher, (p, v) => p with { Catching = (int)v }),
        C("pitcher_velocity", "投手:最速", Scope.Pitchers,
            (p, v) => WithPitching(p, a => a with { MaxVelocityKmh = v }), low: 110, high: 150, unit: "km/h"),
        C("pitcher_control", "投手:制球", Scope.Pitchers, (p, v) => WithPitching(p, a => a with { Control = (int)v })),
        C("pitcher_stamina", "投手:スタミナ", Scope.Pitchers,
            (p, v) => WithPitching(p, a => a with { StaminaPitches = PitcherAttributes.StaminaPitchesFromLevel((int)v) })),
        C("pitcher_stuff", "投手:球威", Scope.Pitchers, (p, v) => WithPitching(p, a => a with
        {
            Repertoire = MapRepertoire(a, s => s.Type == PitchType.Fastball ? s with { Power = (int)v } : s),
        })),
        C("pitcher_sharpness", "投手:キレ", Scope.Pitchers, (p, v) => WithPitching(p, a => a with
        {
            Repertoire = MapRepertoire(a, s => s.Type != PitchType.Fastball ? s with { Sharpness = (int)v } : s),
        })),
        C("pitcher_fielding", "投手:守備", Scope.Pitchers, (p, v) => p with { Fielding = (int)v }),
    };

    // ── チーム ──

    private static readonly FieldPosition[] FieldSlots =
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
        FieldPosition.CenterField, FieldPosition.RightField,
    };

    private static Player UniformPitcher(string name, int breakingBalls) => new()
    {
        Name = name,
        Position = FieldPosition.Pitcher,
        Pitching = new PitcherAttributes
        {
            MaxVelocityKmh = PitcherAttributes.VelocityKmhFromLevel(BaseLevel),
            Control = BaseLevel,
            StaminaPitches = PitcherAttributes.StaminaPitchesFromLevel(BaseLevel),
            PitchRank = BaseLevel,
            Repertoire = Repertoire(breakingBalls),
        },
    };

    private static PitchSlot[] Repertoire(int breakingBalls)
    {
        var all = new[]
        {
            PitchSlot.FastballOf(BaseLevel),
            new PitchSlot { Type = PitchType.Slider, Power = BaseLevel, Sharpness = BaseLevel },
            new PitchSlot { Type = PitchType.Fork, Power = BaseLevel, Sharpness = BaseLevel },
        };
        return all.Take(1 + breakingBalls).ToArray();
    }

    /// <summary>全能力50の均一チーム（先発＋控え投手2人＝合成チームと同じ人数）。乱数を使わない。</summary>
    public static Team UniformTeam(string name, int breakingBalls = 2)
    {
        var order = new List<Player>(9);
        foreach (var pos in FieldSlots) order.Add(new Player { Name = pos.ToString(), Position = pos });
        order.Add(UniformPitcher(name + "先発", breakingBalls));
        var bullpen = new List<Player>
        {
            UniformPitcher(name + "中継ぎ", breakingBalls),
            UniformPitcher(name + "抑え", breakingBalls),
        };
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8, Reserves = bullpen };
    }

    private static bool InScope(Scope scope, Player p) => scope switch
    {
        Scope.AllPlayers => true,
        Scope.Catcher => p.Position == FieldPosition.Catcher,
        Scope.Pitchers => p.Position == FieldPosition.Pitcher,
        Scope.Battery => p.Position is FieldPosition.Pitcher or FieldPosition.Catcher,
        _ => false,
    };

    /// <summary>範囲内の選手だけに <paramref name="apply"/> を適用したチームを返す（打順・控えの両方）。</summary>
    public static Team Mutate(Team t, Scope scope, Func<Player, Player> apply)
    {
        Player Map(Player p) => InScope(scope, p) ? apply(p) : p;
        return t with
        {
            BattingOrder = t.BattingOrder.Select(Map).ToArray(),
            Reserves = t.Reserves.Select(Map).ToArray(),
        };
    }

    // ── 計測 ──

    /// <summary>
    /// 変えたチーム（<paramref name="focus"/>）と全員50のチーム（<paramref name="opponent"/>）を
    /// <paramref name="games"/> 試合戦わせる。先攻・後攻は試合ごとに入れ替える。
    /// </summary>
    public static Cell Measure(string id, string label, double value, Team focus, Team opponent,
        int games, ulong seed, string? coefficientsPath = null)
    {
        var ctx = GameSimulation.BuildContext(coefficientsPath) with { Weather = null };
        var brain = new StandardTacticsBrain(ctx.Tactics, ctx.Baserunning);
        var f = focus with { Tactics = brain };
        var o = opponent with { Tactics = brain };

        var root = new Xoshiro256Random(seed);
        var forks = new IRandomSource[games];
        for (var i = 0; i < games; i++) forks[i] = root.Fork((ulong)i);

        var focusTotal = new Totals();
        var oppTotal = new Totals();
        long dp = 0, wp = 0;
        Parallel.For(0, games,
            () => (F: new Totals(), O: new Totals(), Dp: 0L, Wp: 0L),
            (i, _, local) =>
            {
                var focusAway = i % 2 == 0;
                var r = focusAway
                    ? GameEngine.Play(f, o, ctx, forks[i])
                    : GameEngine.Play(o, f, ctx, forks[i]);
                AddSide(local.F, r, away: focusAway);
                AddSide(local.O, r, away: !focusAway);
                return (local.F, local.O, local.Dp + r.DoublePlayCount, local.Wp + r.WildPitchCount);
            },
            local =>
            {
                lock (focusTotal)
                {
                    focusTotal.Merge(local.F);
                    oppTotal.Merge(local.O);
                    dp += local.Dp;
                    wp += local.Wp;
                }
            });

        return new Cell(id, label, value, games, focusTotal, oppTotal, dp, wp);
    }

    private static void AddSide(Totals t, GameResult r, bool away)
    {
        t.Runs += away ? r.AwayRuns : r.HomeRuns;
        t.Hits += away ? r.AwayHits : r.HomeHits;
        t.Errors += away ? r.AwayErrors : r.HomeErrors;
        foreach (var b in away ? r.AwayBatting : r.HomeBatting)
        {
            t.PlateAppearances += b.PlateAppearances;
            t.Strikeouts += b.StrikeOuts;
            t.Walks += b.Walks;
            t.HomeRuns += b.HomeRuns;
        }
        var tally = away ? r.AwayTactics : r.HomeTactics;
        t.StealAttempts += tally.StealAttempts;
        t.StealSuccesses += tally.StealSuccesses;
        t.SacrificeBunts += tally.SacrificeBunts;
        foreach (var e in r.PitchingChangeLog)
        {
            if (e.DefenseIsAway == away) t.PitcherChanges++;
        }
    }

    /// <summary>
    /// 全条件を測る。条件ごとのシードは同じ値を使う（共通乱数＝基準との差から試合ごとの運を一部取り除く）。
    /// <paramref name="only"/> を渡すとその Id の能力だけ測る（回帰テストは期待のある能力だけで足りる）。
    /// 最速の梯子は能力表の 110 / 150km/h と同じ条件を再利用する。
    /// </summary>
    public static Result Run(int games, ulong seed, string? coefficientsPath = null, IReadOnlySet<string>? only = null)
    {
        var opp = UniformTeam("相手校");
        var baseline = Measure("baseline", "基準（全員50）", BaseLevel, UniformTeam("自校"), opp, games, seed, coefficientsPath);

        var abilities = new List<(Condition, Cell, Cell)>(Conditions.Count);
        foreach (var c in Conditions)
        {
            if (only is not null && !only.Contains(c.Id)) continue;
            Cell At(double v) => Measure(c.Id, c.Label, v,
                Mutate(UniformTeam("自校"), c.Scope, p => c.Apply(c.Prelude?.Invoke(p) ?? p, v)),
                opp, games, seed, coefficientsPath);
            abilities.Add((c, At(c.Low), At(c.High)));
        }

        var velocity = Conditions.First(c => c.Id == "pitcher_velocity");
        var known = abilities.FirstOrDefault(a => a.Item1.Id == velocity.Id);
        var ladder = new List<Cell>(VelocityLadderKmh.Length);
        foreach (var kmh in VelocityLadderKmh)
        {
            if (known.Item1 is not null && kmh == velocity.Low) ladder.Add(known.Item2);
            else if (known.Item1 is not null && kmh == velocity.High) ladder.Add(known.Item3);
            else
            {
                ladder.Add(Measure("velocity_ladder", "最速", kmh,
                    Mutate(UniformTeam("自校"), velocity.Scope, p => velocity.Apply(p, kmh)), opp, games, seed, coefficientsPath));
            }
        }

        var repertoire = new List<Cell>(3);
        for (var breaking = 0; breaking <= 2; breaking++)
        {
            if (breaking == 2)
            {
                repertoire.Add(baseline with { Id = "repertoire", Label = "変化球の本数", Value = 2 });
                continue;
            }
            repertoire.Add(Measure("repertoire", "変化球の本数", breaking,
                UniformTeam("自校", breaking), opp, games, seed, coefficientsPath));
        }

        return new Result(games, seed, baseline, abilities, ladder, repertoire);
    }

    // ── 判定 ──

    /// <summary>1つの期待に対する判定。</summary>
    public sealed record Verdict(string Ability, string Side, double Effect, double MinRuns, bool Pass, int? KnownIssue);

    /// <summary>
    /// 期待（balance-targets.yaml）に照らして判定する。<c>Effect</c> は「期待する向きに動いた量」＝
    /// 攻撃は (80の自軍得点−20の自軍得点)、守備は (20の相手得点−80の相手得点) を向きで符号合わせしたもの。
    /// </summary>
    public static IReadOnlyList<Verdict> Evaluate(Result result, AbilitySensitivityTargets targets)
    {
        var verdicts = new List<Verdict>();
        foreach (var (cond, low, high) in result.Abilities)
        {
            if (!targets.Abilities.TryGetValue(cond.Id, out var exp)) continue;
            if (exp.Offense is { } off)
            {
                verdicts.Add(Judge(targets, cond.Id, "offense", off, high.RunsFor - low.RunsFor));
            }
            if (exp.Defense is { } def)
            {
                verdicts.Add(Judge(targets, cond.Id, "defense", def, high.RunsAgainst - low.RunsAgainst));
            }
        }
        return verdicts;
    }

    private static Verdict Judge(AbilitySensitivityTargets t, string ability, string side,
        SideExpectation exp, double rawDelta)
    {
        var effect = exp.Expect == SensitivityDirection.Up ? rawDelta : -rawDelta;
        var known = t.KnownDefects.FirstOrDefault(d => d.Ability == ability && d.Side == side);
        return new Verdict(ability, side, effect, exp.MinRuns, effect >= exp.MinRuns, known?.Issue);
    }

    /// <summary>
    /// 最速の梯子の失敗理由（空＝合格）。①1段上げて相手得点が雑音幅を超えて増える段（逆転）②最速の両端で
    /// 相手得点が <see cref="AbilitySensitivityTargets.VelocityLadderMinDrop"/> だけ下がらない。
    /// </summary>
    public static IReadOnlyList<string> VelocityLadderFailures(IReadOnlyList<Cell> ladder, AbilitySensitivityTargets t)
    {
        var bad = new List<string>();
        for (var i = 1; i < ladder.Count; i++)
        {
            var rise = ladder[i].RunsAgainst - ladder[i - 1].RunsAgainst;
            if (rise > t.NoiseRuns)
            {
                bad.Add($"{ladder[i - 1].Value:F0}→{ladder[i].Value:F0}km/h で相手得点 +{rise:F2}");
            }
        }
        var drop = ladder[0].RunsAgainst - ladder[^1].RunsAgainst;
        if (drop < t.VelocityLadderMinDrop)
        {
            bad.Add($"{ladder[0].Value:F0}→{ladder[^1].Value:F0}km/h で相手得点が {drop:F2} しか下がらない（最低 {t.VelocityLadderMinDrop:F2}）");
        }
        return bad;
    }

    /// <summary>球種構成の失敗理由（空＝合格）。変化球なし→2本で相手得点が <see cref="AbilitySensitivityTargets.RepertoireMinDrop"/> 以上下がること。</summary>
    public static IReadOnlyList<string> RepertoireFailures(IReadOnlyList<Cell> repertoire, AbilitySensitivityTargets t)
    {
        var drop = repertoire[0].RunsAgainst - repertoire[^1].RunsAgainst;
        return drop >= t.RepertoireMinDrop
            ? Array.Empty<string>()
            : new[] { $"ストレートのみ→変化球2本で相手得点が {drop:F2} しか下がらない（最低 {t.RepertoireMinDrop:F2}）" };
    }

    // ── レポート ──

    public static string Report(Result r, AbilitySensitivityTargets? targets = null)
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.AppendLine("# 能力感度の掃引（issue #840）");
        sb.AppendLine();
        sb.AppendLine(c, $"- シード: {r.Seed} / 1条件あたり {r.Games} 試合 / 得点の誤差はおよそ ±{3.2 / Math.Sqrt(r.Games):F2}（1σ）");
        sb.AppendLine("- 全能力50の均一チーム同士（投手はストレート＋変化球2つ・ランク50）。采配Brainあり。能力を1つだけ変え、**片方のチームだけ**に当てる");
        sb.AppendLine("- 自軍得点＝攻撃側の効果 / 相手得点＝守備側の効果。変えたチームの先攻・後攻は試合ごとに入れ替え（後攻の9回裏省略を避ける）");
        sb.AppendLine("- 併殺・暴投は両軍計（どちらの側のものか記録に無い）");
        sb.AppendLine();

        sb.AppendLine("## 能力 20 / 80（片側だけ変更）");
        sb.AppendLine();
        AppendHeader(sb);
        AppendRow(sb, r.Baseline, "基準（全員50）");
        foreach (var (cond, low, high) in r.Abilities)
        {
            AppendRow(sb, low, $"{cond.Label} {Fmt(cond.Low)}{UnitSuffix(cond)}");
            AppendRow(sb, high, $"{cond.Label} {Fmt(cond.High)}{UnitSuffix(cond)}");
        }
        sb.AppendLine();

        sb.AppendLine("## 20 → 80 の差（自軍得点・相手得点）");
        sb.AppendLine();
        sb.AppendLine("| 能力 | 自軍得点Δ | 相手得点Δ |");
        sb.AppendLine("|---|---|---|");
        foreach (var (cond, low, high) in r.Abilities)
        {
            sb.AppendLine(c, $"| {cond.Label} | {high.RunsFor - low.RunsFor:+0.00;-0.00;0.00} | {high.RunsAgainst - low.RunsAgainst:+0.00;-0.00;0.00} |");
        }
        sb.AppendLine();

        sb.AppendLine("## 最速の梯子（投手だけ）");
        sb.AppendLine();
        AppendHeader(sb);
        foreach (var cell in r.VelocityLadder) AppendRow(sb, cell, $"最速 {cell.Value:F0}km/h");
        sb.AppendLine();

        sb.AppendLine("## 球種構成（投手だけ）");
        sb.AppendLine();
        AppendHeader(sb);
        foreach (var cell in r.Repertoire)
        {
            AppendRow(sb, cell, cell.Value == 0 ? "ストレートのみ" : $"ストレート＋変化球{cell.Value:F0}");
        }
        sb.AppendLine();

        if (targets is not null)
        {
            sb.AppendLine("## 期待（data/balance-targets.yaml ability_sensitivity）との照合");
            sb.AppendLine();
            sb.AppendLine("| 能力 | 側 | 期待する向きに動いた量 | 最小の効き | 判定 |");
            sb.AppendLine("|---|---|---|---|---|");
            foreach (var v in Evaluate(r, targets))
            {
                var mark = v.Pass
                    ? (v.KnownIssue is { } fixedIssue ? $"OK（既知 #{fixedIssue} は直った？除外から外す）" : "OK")
                    : v.KnownIssue is { } n ? $"NG（既知 #{n}）" : "**NG（新しい穴）**";
                sb.AppendLine(c, $"| {v.Ability} | {(v.Side == "offense" ? "攻撃" : "守備")} | {v.Effect:+0.00;-0.00;0.00} | {v.MinRuns:0.00} | {mark} |");
            }
            sb.AppendLine();
            AppendSeriesVerdict(sb, "最速の梯子", VelocityLadderFailures(r.VelocityLadder, targets), "velocity_ladder", targets);
            AppendSeriesVerdict(sb, "球種構成", RepertoireFailures(r.Repertoire, targets), "repertoire", targets);
        }
        return sb.ToString();
    }

    private static void AppendSeriesVerdict(StringBuilder sb, string title, IReadOnlyList<string> failures,
        string key, AbilitySensitivityTargets t)
    {
        if (failures.Count == 0) { sb.AppendLine($"- {title}: OK"); return; }
        var known = t.KnownDefects.FirstOrDefault(d => d.Ability == key);
        var tag = known is null ? "**NG（新しい穴）**" : $"NG（既知 #{known.Issue}）";
        sb.AppendLine($"- {title}: {tag} {string.Join(" / ", failures)}");
    }

    private static string UnitSuffix(Condition cond) => cond.Unit == "Lv" ? "" : cond.Unit;

    private static string Fmt(double v) => v.ToString("0", CultureInfo.InvariantCulture);

    private static void AppendHeader(StringBuilder sb)
    {
        sb.AppendLine("| 条件 | 自軍得点 | 相手得点 | 自軍安打 | 相手安打 | 打者K% | 投手K% | 打者BB% | 投手BB% | 打者HR% | 被HR% | 失策 | 盗塁企図 自/相 | 盗塁成功率 自/相 | 犠打 | 併殺 | 暴投 | 継投 |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|");
    }

    private static void AppendRow(StringBuilder sb, Cell x, string label)
    {
        var c = CultureInfo.InvariantCulture;
        sb.AppendLine(c,
            $"| {label} | {x.RunsFor:F2} | {x.RunsAgainst:F2} | {x.HitsFor:F2} | {x.HitsAgainst:F2}"
            + $" | {x.BatterStrikeoutRate:P1} | {x.PitcherStrikeoutRate:P1} | {x.BatterWalkRate:P1} | {x.PitcherWalkRate:P1}"
            + $" | {x.BatterHomeRunRate:P2} | {x.PitcherHomeRunRate:P2} | {x.ErrorsFor:F2}"
            + $" | {x.StealAttemptsFor:F2}/{x.StealAttemptsAgainst:F2} | {x.StealSuccessRateFor:P0}/{x.StealSuccessRateAgainst:P0}"
            + $" | {x.SacrificeBuntsFor:F2} | {x.DoublePlaysPerGame:F2} | {x.WildPitchesPerGame:F2} | {x.PitcherChangesFor:F2} |");
    }
}
