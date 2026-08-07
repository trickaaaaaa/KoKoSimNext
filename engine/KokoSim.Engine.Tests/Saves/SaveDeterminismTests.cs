using System.Collections.Generic;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Roster;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using Xunit;
using NationT = KokoSim.Engine.Nation.Nation;

namespace KokoSim.Engine.Tests.Saves;

/// <summary>
/// セーブ→ロード後に「同じ操作を続けた結果」が、セーブせず続けた結果と一致すること（不変条件#2・issue #507 完了条件）。
/// とくに <see cref="NationRosters.GetOrBootstrap"/> は (校ID, 現在年度) の遅延生成なので、年度を跨ぐと
/// 「保存した実体を進める」のと「現在年度で生成し直す」のが食い違う。その食い違いを起こさないことを固定する。
/// </summary>
public sealed class SaveDeterminismTests
{
    private static System.Func<int, School?> ById(NationT nation)
    {
        var map = new Dictionary<int, School>();
        foreach (var s in nation.Schools) map[s.Id] = s;
        return id => map.TryGetValue(id, out var s) ? s : null;
    }

    /// <summary>NationRosters だけを載せた最小 DTO の直列化文字列（比較用）。</summary>
    private static string SerializeRosters(NationRosters nr)
        => SaveCodec.Serialize(new SaveState { NationRosters = new List<AiSchoolRoster>(nr.Snapshot()) });

    private static List<AiSchoolRoster> RoundTripRosters(NationRosters nr)
    {
        var json = SerializeRosters(nr);
        Assert.True(SaveCodec.TryDeserialize(json, out var s, out var err), err);
        return s.NationRosters;
    }

    [Fact]
    public void NationRosters_CrossYearAdvance_IdenticalWithAndWithoutSaveLoad()
    {
        var nation = SaveTestFixtures.BuildNation();
        var byId = ById(nation);
        var schools = SaveTestFixtures.SampleSchools(nation, 8);
        var deps = new AiRosterDeps();

        // Path A: セーブせず年度3→4へ進める。
        var pathA = new NationRosters(deps);
        pathA.BootstrapAll(schools, 3);
        var beforeAdvance = SerializeRosters(pathA);   // 進める前の状態（teeth 用に控える）
        pathA.AdvanceExistingYear(byId, 4);
        var jsonA = SerializeRosters(pathA);

        // Path B: 年度3の状態でセーブ→ロード→年度3→4へ進める。
        var seed = new NationRosters(deps);
        seed.BootstrapAll(schools, 3);
        var restored = NationRosters.FromSaved(deps, RoundTripRosters(seed));
        restored.AdvanceExistingYear(byId, 4);
        var jsonB = SerializeRosters(restored);

        Assert.Equal(jsonA, jsonB);

        // teeth-1: 年度送りは実際に状態を変える（no-op を等価で通していない）。
        Assert.NotEqual(beforeAdvance, jsonA);

        // teeth-2: 「現在年度(=4)で生成し直す」遅延ブートストラップは別物になる
        //          ＝ materialize 済みを持ち越さないと崩れる、という issue の警告そのもの。
        var lazyRegen = new NationRosters(deps);
        lazyRegen.BootstrapAll(schools, 4);
        Assert.NotEqual(jsonB, SerializeRosters(lazyRegen));
    }

    [Fact]
    public void NationRosters_SaveLoad_IsByteIdentical_BeforeAnyAdvance()
    {
        var nation = SaveTestFixtures.BuildNation();
        var deps = new AiRosterDeps();
        var nr = new NationRosters(deps);
        nr.BootstrapAll(SaveTestFixtures.SampleSchools(nation, 8), 3);

        var before = SerializeRosters(nr);
        var after = SerializeRosters(NationRosters.FromSaved(deps, RoundTripRosters(nr)));
        Assert.Equal(before, after);
    }

    [Fact]
    public void ManagerRoster_ContinuedOperation_IdenticalWithAndWithoutSaveLoad()
    {
        // セーブせず操作した結果と、セーブ→ロード後に同じ操作をした結果が一致する（不変条件#2）。
        // 操作は「各選手のレベル・経験値・適性を読んで経験値を積む」＝状態全体を読むので、
        // 復元が一つでも欠ければ結果が食い違う（＝厳密復元の踏み絵）。
        var pathA = SaveTestFixtures.BuildManagerRoster();
        Continue(pathA);
        var jsonA = SerializeRoster(pathA);

        var seed = SaveTestFixtures.BuildManagerRoster();
        Assert.True(SaveCodec.TryDeserialize(SerializeRoster(seed), out var loaded, out var err), err);
        Continue(loaded.Roster);
        var jsonB = SerializeRoster(loaded.Roster);

        Assert.Equal(jsonA, jsonB);
    }

    private static string SerializeRoster(List<DevelopingPlayer> roster)
        => SaveCodec.Serialize(new SaveState { Roster = roster });

    /// <summary>状態全体を読む決定論操作（セーブ有無で結果が変わらないことの検証用）。</summary>
    private static void Continue(List<DevelopingPlayer> roster)
    {
        foreach (var p in roster)
        {
            var seed = p.Level(AbilityKind.Contact) * 0.3 + p.Exp(AbilityKind.Power) * 0.5
                       + p.Aptitude(KokoSim.Engine.Match.Field.FieldPosition.SecondBase) * 0.01;
            p.AddExp(AbilityKind.Power, seed);
            p.AddAptitudeExp(KokoSim.Engine.Match.Field.FieldPosition.ThirdBase, seed * 0.1);
        }
    }
}
