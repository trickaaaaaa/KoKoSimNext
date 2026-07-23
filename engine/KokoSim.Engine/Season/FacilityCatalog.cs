using System;
using System.Collections.Generic;

namespace KokoSim.Engine.Season;

/// <summary>
/// 施設1系統の1レベル分の効果と費用（Issue #128・設計書03 §4 / 04 §4）。
/// Lv(n-1)→Lv(n) の購入費と、その到達で得る施設係数加算・週練習時間加算を持つ。
/// </summary>
public sealed record FacilityUpgrade
{
    /// <summary>Lv(n-1)→Lv(n) に上げる購入費[万円]。Lv0のtier[0]は必ず0。</summary>
    public double Cost { get; init; }
    /// <summary>施設係数への加算（全系統の合計を基準1.0へ足し込む）。</summary>
    public double CoefAdd { get; init; }
    /// <summary>週練習時間[分]への加算（全系統の合計を基準へ足し込む）。</summary>
    public int BudgetAdd { get; init; }
}

/// <summary>
/// 施設1系統の定義（Issue #128）。index=施設レベルの <see cref="FacilityUpgrade"/> 列を持つ。
/// tiers[0] は施設なし（費用0・効果0）で、全系統Lv0＝従来一致（不変条件#2）を担保する。
/// </summary>
public sealed record FacilityDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>index=施設レベル。tiers[0] は必ず費用0・効果0。</summary>
    public IReadOnlyList<FacilityUpgrade> Tiers { get; init; } = new[] { new FacilityUpgrade() };
    /// <summary>この系統の最大レベル（青天井防止の上限＝tiers本数−1）。</summary>
    public int MaxLevel => Math.Max(0, Tiers.Count - 1);
}

/// <summary>
/// 施設カタログ（Issue #128・設計書03 §4 / 04 §4）。多軸の施設定義を束ねる。
/// 全系統Lv0で <see cref="Aggregate"/> は (0, 0) を返し、基準(施設係数1.0 / 練習300分)に足しても
/// 従来と1ビット一致する（不変条件#2）。数値は data/coefficients.yaml facilities: が正典で、
/// <see cref="Default"/> はUnity実プレイ（yaml非読込）とテストの既定として同値を持つ。
/// </summary>
public sealed record FacilityCatalog
{
    public IReadOnlyList<FacilityDef> Facilities { get; init; } = Array.Empty<FacilityDef>();

    public FacilityDef? Find(string id)
    {
        foreach (var f in Facilities)
            if (f.Id == id) return f;
        return null;
    }

    /// <summary>
    /// 全系統の現在レベルを足し合わせ、(施設係数加算, 週練習時間加算[分]) を返す。
    /// レベルは各系統の [0, MaxLevel] にクランプする。
    /// </summary>
    public (double CoefAdd, int BudgetAdd) Aggregate(FacilitySet set)
    {
        double coefAdd = 0;
        var budgetAdd = 0;
        foreach (var f in Facilities)
        {
            var lv = Math.Clamp(set.LevelOf(f.Id), 0, f.MaxLevel);
            for (var i = 1; i <= lv; i++)
            {
                coefAdd += f.Tiers[i].CoefAdd;
                budgetAdd += f.Tiers[i].BudgetAdd;
            }
        }
        return (coefAdd, budgetAdd);
    }

    /// <summary>
    /// 既定の施設カタログ（Issue #128・会話 2026-07-23 確定）。4系統×Lv0-3。
    /// 全系統Lv3で 施設係数+1.40 / 週練習+400分 ＝ 基準に足すと (2.40, 700分) で旧 facility_tiers Lv4 と一致し、
    /// #115の校正（豪腕素質×名将×フル施設で3年+15〜20km/h）を保つ。費用はたたき台（yaml上書き可）。
    /// </summary>
    public static FacilityCatalog Default { get; } = new()
    {
        Facilities = new[]
        {
            // 照明設備: 夜間練習で練習時間だけ増える。
            new FacilityDef
            {
                Id = "lighting", Name = "照明設備",
                Tiers = new[]
                {
                    new FacilityUpgrade(),
                    new FacilityUpgrade { Cost = 40, BudgetAdd = 80 },
                    new FacilityUpgrade { Cost = 70, BudgetAdd = 80 },
                    new FacilityUpgrade { Cost = 110, BudgetAdd = 90 },
                },
            },
            // 室内練習場: 雨天でも練習でき（練習時間）、設備で効率も上がる。
            new FacilityDef
            {
                Id = "indoor", Name = "室内練習場",
                Tiers = new[]
                {
                    new FacilityUpgrade(),
                    new FacilityUpgrade { Cost = 60, CoefAdd = 0.15, BudgetAdd = 50 },
                    new FacilityUpgrade { Cost = 100, CoefAdd = 0.15, BudgetAdd = 50 },
                    new FacilityUpgrade { Cost = 160, CoefAdd = 0.15, BudgetAdd = 50 },
                },
            },
            // 寮: 生活・体調管理で練習効率が上がる。
            new FacilityDef
            {
                Id = "dorm", Name = "寮",
                Tiers = new[]
                {
                    new FacilityUpgrade(),
                    new FacilityUpgrade { Cost = 50, CoefAdd = 0.15 },
                    new FacilityUpgrade { Cost = 90, CoefAdd = 0.15 },
                    new FacilityUpgrade { Cost = 140, CoefAdd = 0.15 },
                },
            },
            // トレーニング場: ウェイト・マシンで練習効率が上がる。
            new FacilityDef
            {
                Id = "weight", Name = "トレーニング場",
                Tiers = new[]
                {
                    new FacilityUpgrade(),
                    new FacilityUpgrade { Cost = 40, CoefAdd = 0.15 },
                    new FacilityUpgrade { Cost = 70, CoefAdd = 0.15 },
                    new FacilityUpgrade { Cost = 110, CoefAdd = 0.20 },
                },
            },
        },
    };
}

/// <summary>
/// 学校の施設保有状態（Issue #128）。系統ID→現在レベルの可変マップ。
/// 施設は学校資産＝転任で置いていく（設計書04 §4）。現ビルドは単一校キャリアなので
/// セッション状態として保持し、転任リセットは将来対応（OPEN-QUESTIONS Q6）。
/// </summary>
public sealed class FacilitySet
{
    private readonly Dictionary<string, int> _levels = new();

    public int LevelOf(string id) => _levels.TryGetValue(id, out var v) ? v : 0;

    public void SetLevel(string id, int level) => _levels[id] = Math.Max(0, level);

    public IReadOnlyDictionary<string, int> Levels => _levels;
}
