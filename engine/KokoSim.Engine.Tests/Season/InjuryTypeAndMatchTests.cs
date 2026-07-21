using System.Collections.Generic;
using System.IO;
using System.Linq;
using KokoSim.Config;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using KokoSim.Engine.Tests.Match.Game;
using Xunit;

namespace KokoSim.Engine.Tests.Season;

/// <summary>
/// 怪我の拡張（issue #29 / 設計書03 §3.5）:
/// A 傷病の種類（種類→部位→段階の一貫抽選・data/injuries.yaml 駆動）
/// B 試合中の場面駆動発生（決定論を壊さない＝Fork 隔離）
/// C 押して出場による全治延長・悪化の試合接続
/// </summary>
public sealed class InjuryTypeAndMatchTests
{
    private static readonly InjuryCoefficients Inj = new();

    private static string DataPath(string file)
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "data"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "data", file);
    }

    // ===== A. 傷病の種類 =====

    [Fact]
    public void Catalog_Yaml_LoadsAllTypes_AndMatchesBuiltInDefault()
    {
        var catalog = InjuryCatalogLoader.LoadFromFile(DataPath("injuries.yaml"));

        // enum の None 以外はすべて data 側に定義がある（表示名は data が単一ソース）。
        foreach (var t in System.Enum.GetValues(typeof(InjuryType)).Cast<InjuryType>())
        {
            if (t == InjuryType.None) continue;
            var entry = catalog.Find(t);
            Assert.True(entry is not null, $"{t} が data/injuries.yaml に無い");
            Assert.False(string.IsNullOrWhiteSpace(entry!.Name), $"{t} の表示名が空");
            Assert.True(entry.Sites.Count > 0, $"{t} の部位が空");
            Assert.True(entry.WeightFor(InjuryScene.Weekly) >= 0.0);
        }

        // 組み込み既定（Unity は data/ を読まないため必要）が YAML と同じ構成であること。
        Assert.Equal(catalog.Types.Count, InjuryCatalog.Default.Types.Count);
        foreach (var e in catalog.Types)
        {
            var d = InjuryCatalog.Default.Find(e.Id);
            Assert.True(d is not null, $"{e.Id} が組み込み既定に無い");
            Assert.Equal(e.Name, d!.Name);
            Assert.Equal(e.RecoveryWeekFactor, d.RecoveryWeekFactor, 6);
            Assert.Equal(e.MinorShare, d.MinorShare, 6);
            Assert.Equal(e.Sites.Count, d.Sites.Count);
        }
    }

    [Fact]
    public void Sample_DrawsTypeThenSite_SiteAlwaysAllowedForThatType()
    {
        var catalog = InjuryCatalogLoader.LoadFromFile(DataPath("injuries.yaml"));
        var rng = new Xoshiro256Random(11);
        var seen = new HashSet<InjuryType>();

        for (var i = 0; i < 5000; i++)
        {
            var d = InjuryModel.Sample(InjuryScene.Weekly, rng, Inj, catalog);
            Assert.NotEqual(InjuryType.None, d.Type);
            var entry = catalog.Find(d.Type)!;
            Assert.Contains(d.Site, entry.Sites.Select(s => s.Site)); // 種類が取りうる部位しか出ない
            Assert.True(d.WeeksRemaining >= 1);
            seen.Add(d.Type);
        }
        // 週次に重みを持つ種類はひととおり出る（重み抽選が機能している）。
        Assert.True(seen.Count >= 5, $"種類の広がりが足りない: {seen.Count}");
    }

    [Fact]
    public void Sample_SceneWeights_RestrictTypes()
    {
        var catalog = InjuryCatalogLoader.LoadFromFile(DataPath("injuries.yaml"));
        var rng = new Xoshiro256Random(23);

        // 死球: data 側で骨折・打撲にしか重みが無い。
        var hbp = new HashSet<InjuryType>();
        for (var i = 0; i < 2000; i++) hbp.Add(InjuryModel.Sample(InjuryScene.HitByPitch, rng, Inj, catalog).Type);
        Assert.Equal(new[] { InjuryType.Bruise, InjuryType.Fracture }.OrderBy(x => x),
            hbp.OrderBy(x => x));

        // 投球過多: 肉離れ・疲労性炎症のみ。かつ部位は肩肘腰に寄る。
        var overuse = new HashSet<InjuryType>();
        for (var i = 0; i < 2000; i++) overuse.Add(InjuryModel.Sample(InjuryScene.Overuse, rng, Inj, catalog).Type);
        Assert.Equal(new[] { InjuryType.Inflammation, InjuryType.Strain }.OrderBy(x => x),
            overuse.OrderBy(x => x));
    }

    [Fact]
    public void RecoveryWeekFactor_MakesFractureLongerThanBruise()
    {
        var catalog = InjuryCatalogLoader.LoadFromFile(DataPath("injuries.yaml"));
        var fracture = InjuryModel.RecoveryWeeks(InjurySeverity.Moderate, Inj,
            catalog.Find(InjuryType.Fracture)!.RecoveryWeekFactor);
        var bruise = InjuryModel.RecoveryWeeks(InjurySeverity.Moderate, Inj,
            catalog.Find(InjuryType.Bruise)!.RecoveryWeekFactor);
        Assert.True(fracture > bruise, $"骨折({fracture}週)が打撲({bruise}週)より長引かない");
    }

    [Fact]
    public void WeeklyCheck_SetsType_AndRecoveryClearsIt()
    {
        var rng = new Xoshiro256Random(3);
        var c = Inj with { WeeklyBaseProb = 1.0 };
        var p = new DevelopingPlayer();
        // 週次発生率は 0.5 で上限クランプされるため、発生するまで数回引く。
        var fired = false;
        for (var i = 0; i < 50 && !fired; i++) fired = InjuryModel.WeeklyCheck(p, rng, c, null, InjuryCatalog.Default);
        Assert.True(fired, "週次発生が起きない");
        Assert.NotEqual(InjuryType.None, p.InjuryType);

        for (var w = 0; w < 200 && p.Injury != InjurySeverity.None; w++)
        {
            InjuryModel.WeeklyRecover(p, c, InjuryCatalog.Default);
        }
        Assert.Equal(InjurySeverity.None, p.Injury);
        Assert.Equal(InjuryType.None, p.InjuryType); // 完治で種類も消える
    }

    // ===== B. 試合中の発生 =====

    [Fact]
    public void MatchInjury_DoesNotChangeGameResult_AnySeed_AnyCoefficients()
    {
        // Fork 隔離（不変条件#2）: 受傷係数をどれだけ振っても試合本体の乱数順・結果は不変。
        var off = new GameContext { MatchInjury = new MatchInjuryCoefficients(), InjuryCatalog = InjuryCatalog.Empty };
        var on = new GameContext
        {
            MatchInjury = new MatchInjuryCoefficients
            {
                HitByPitchProb = 1.0, HomeCollisionProb = 1.0, FenceCrashProb = 1.0,
                FenceCrashMarginM = 40.0, SlidingProb = 1.0, OveruseProb = 1.0, OveruseOverPitches = 0.0,
            },
            InjuryCatalog = InjuryCatalog.Default,
        };

        for (ulong seed = 1; seed <= 25; seed++)
        {
            var a = GameEngine.Play(MatchTeam("A"), MatchTeam("H"), off, new Xoshiro256Random(seed));
            var b = GameEngine.Play(MatchTeam("A"), MatchTeam("H"), on, new Xoshiro256Random(seed));
            Assert.Equal(GameResultDigest.Sha256Of(a), GameResultDigest.Sha256Of(b));
        }
    }

    [Fact]
    public void MatchInjury_Occurs_WhenEnabled_AndNeverWhenDisabled()
    {
        var on = new GameContext
        {
            MatchInjury = new MatchInjuryCoefficients
            {
                HitByPitchProb = 1.0, HomeCollisionProb = 1.0, FenceCrashProb = 1.0,
                FenceCrashMarginM = 40.0, SlidingProb = 1.0, OveruseProb = 1.0, OveruseOverPitches = 0.0,
            },
            InjuryCatalog = InjuryCatalog.Default,
            EnableSmallBall = true,
        };
        var offCtx = new GameContext
        {
            MatchInjury = new MatchInjuryCoefficients
            {
                HitByPitchProb = 0, HomeCollisionProb = 0, FenceCrashProb = 0,
                SlidingProb = 0, OveruseProb = 0,
            },
            EnableSmallBall = true,
        };

        var scenes = new HashSet<InjuryScene>();
        var total = 0;
        for (ulong seed = 1; seed <= 60; seed++)
        {
            var r = GameEngine.Play(MatchTeam("A"), MatchTeam("H"), on, new Xoshiro256Random(seed));
            total += r.Injuries.Count;
            foreach (var e in r.Injuries)
            {
                scenes.Add(e.Scene);
                Assert.NotEqual(InjuryType.None, e.Type);
                Assert.NotEqual(InjurySeverity.None, e.Severity);
            }
            Assert.Empty(GameEngine.Play(MatchTeam("A"), MatchTeam("H"), offCtx, new Xoshiro256Random(seed)).Injuries);
        }
        Assert.True(total > 0, "試合中の受傷が1件も起きていない");
        // 場面駆動が複数種類のフックから出ていること（死球・投球過多は必ず通る）。
        Assert.True(scenes.Count >= 2, $"発生場面の種類が少なすぎる: {string.Join(",", scenes)}");
    }

    [Fact]
    public void MatchInjury_Roll_RespectsResistanceAndSkills()
    {
        var c = new MatchInjuryCoefficients();
        var rng = new Xoshiro256Random(77);

        int Count(Player victim)
        {
            var n = 0;
            for (var i = 0; i < 4000; i++)
            {
                // paIndex を振って独立ストリームを回す。
                if (MatchInjuryModel.Roll(InjuryScene.HitByPitch, 0.2, victim, "T", 1, true, i,
                        rng, c, new SkillCoefficients(), InjuryCatalog.Default) is not null) n++;
            }
            return n;
        }

        var tough = new Player { Name = "頑丈", InjuryResistance = 90 };
        var frail = new Player { Name = "脆い", InjuryResistance = 10 };
        Assert.True(Count(tough) < Count(frail), "怪我耐性が試合中判定に効いていない");

        var durable = new Player { Name = "故障しにくい", Skills = new SkillSet(new[] { Skill.Durable }) };
        var prone = new Player { Name = "ケガしやすい", Skills = new SkillSet(new[] { Skill.InjuryProne }) };
        Assert.True(Count(durable) < Count(prone), "体質スキルが試合中判定に効いていない");
    }

    // ===== C. 試合後の反映（試合中発生 ＋ 押して出場） =====

    [Fact]
    public void Ledger_AppliesMatchInjury_ToRosterById()
    {
        var dp = new DevelopingPlayer { Id = 7, Name = "受傷者" };
        var draw = new InjuryDraw(InjuryType.Fracture, InjurySite.Hand, InjurySeverity.Moderate, 2.0);
        var detail = new GameResult
        {
            AwayName = "A", HomeName = "H", AwayRuns = 0, HomeRuns = 0,
            InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
            Injuries = new[]
            {
                new MatchInjuryEvent(5, true, InjuryScene.HitByPitch, "A", "受傷者", 7, 3, draw),
            },
        };

        var outcomes = MatchInjuryLedger.Apply(detail, managerIsAway: true, new[] { dp },
            new Xoshiro256Random(1), Inj);

        Assert.Equal(InjurySeverity.Moderate, dp.Injury);
        Assert.Equal(InjuryType.Fracture, dp.InjuryType);
        Assert.Equal(InjurySite.Hand, dp.InjurySite);
        // 骨折の回復倍率2.0が効く（中度4週×2.0＝8週）。
        Assert.Equal(InjuryModel.RecoveryWeeks(InjurySeverity.Moderate, Inj, 2.0), dp.InjuryWeeksRemaining);
        Assert.Single(outcomes);
        Assert.Equal(MatchInjuryOutcomeKind.Occurred, outcomes[0].Kind);
    }

    [Fact]
    public void Ledger_PlayThrough_ExtendsRecovery_EvenWithoutWorsening()
    {
        // 悪化しない設定（worsen_prob=0）でも、出場した分だけ全治が延びる。
        var c = Inj with { PlayThroughWorsenProb = 0.0, PlayThroughExtraWeeks = 1 };
        var dp = new DevelopingPlayer { Id = 3, Name = "強行出場" };
        dp.Injury = InjurySeverity.Minor;
        dp.InjuryType = InjuryType.Sprain;
        dp.InjuryWeeksRemaining = 2;

        var detail = GameWithAppearance(id: 3);
        var outcomes = MatchInjuryLedger.Apply(detail, managerIsAway: true, new[] { dp },
            new Xoshiro256Random(1), c);

        Assert.Equal(InjurySeverity.Minor, dp.Injury);       // 悪化はしていない
        Assert.Equal(3, dp.InjuryWeeksRemaining);            // 2 → 3週
        Assert.Equal(MatchInjuryOutcomeKind.Delayed, outcomes[0].Kind);
    }

    [Fact]
    public void Ledger_PlayThrough_Worsens_WhenRolled()
    {
        var c = Inj with { PlayThroughWorsenProb = 1.0 };
        var dp = new DevelopingPlayer { Id = 3, Name = "強行出場" };
        dp.Injury = InjurySeverity.Minor;
        dp.InjuryType = InjuryType.Sprain;
        dp.InjuryWeeksRemaining = 2;

        var outcomes = MatchInjuryLedger.Apply(GameWithAppearance(3), managerIsAway: true, new[] { dp },
            new Xoshiro256Random(1), c);

        Assert.Equal(InjurySeverity.Moderate, dp.Injury);
        Assert.True(dp.InjuryWeeksRemaining > 2);
        Assert.Equal(MatchInjuryOutcomeKind.Worsened, outcomes[0].Kind);
    }

    [Fact]
    public void Ledger_HealthyPlayer_IsNotTouched()
    {
        var dp = new DevelopingPlayer { Id = 3, Name = "健常" };
        var outcomes = MatchInjuryLedger.Apply(GameWithAppearance(3), managerIsAway: true, new[] { dp },
            new Xoshiro256Random(1), Inj);
        Assert.Empty(outcomes);
        Assert.Equal(InjurySeverity.None, dp.Injury);
        Assert.Equal(0, dp.InjuryWeeksRemaining);
    }

    // ===== ヘルパ =====

    private static GameResult GameWithAppearance(int id) => new()
    {
        AwayName = "A", HomeName = "H", AwayRuns = 0, HomeRuns = 0,
        InningsPlayed = 9, TotalPitches = 0, PitcherChanges = 0,
        AwayBatting = new[]
        {
            new BattingLine(1, FieldPosition.CenterField, "強行出場", 4, 4, 1, 0, 0, 0, 0, 0, 1, SourceId: id),
        },
    };

    private static Player Pos(FieldPosition pos) => new()
    {
        Position = pos,
        Contact = 50, Power = 50, LaunchTendency = 50, Discipline = 50,
        Speed = 70, ArmStrength = 50, Fielding = 50, Catching = 50, Steal = 80,
    };

    private static Team MatchTeam(string name)
    {
        var order = new List<Player>
        {
            Pos(FieldPosition.Catcher), Pos(FieldPosition.FirstBase), Pos(FieldPosition.SecondBase),
            Pos(FieldPosition.ThirdBase), Pos(FieldPosition.Shortstop), Pos(FieldPosition.LeftField),
            Pos(FieldPosition.CenterField), Pos(FieldPosition.RightField),
            Pos(FieldPosition.Pitcher) with { Name = name + "P", Pitching = PitcherAttributes.LeagueAverage },
        };
        return new Team { Name = name, BattingOrder = order, PitcherSlot = 8 };
    }
}
