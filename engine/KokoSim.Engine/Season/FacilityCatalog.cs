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

    // --- 以下は効果の軸を増やすための拡張。倍率は既定1.0・加算は既定0＝「この段では効果なし」。
    //     倍率は段をまたいで掛け算で累積し、加算は足し算で累積する（FacilityCatalog.AggregateEffects）。
    //     既存4系統はこれらを一切持たないので、全系統Lv3の (2.40 / 700分) は不変（#115の校正を保つ）。

    /// <summary>週次の怪我発生率への倍率（医務室。1.0未満で発生しにくくなる）。</summary>
    public double InjuryProbFactor { get; init; } = 1.0;
    /// <summary>怪我の全治週への倍率（医務室。1.0未満で早く復帰する）。</summary>
    public double InjuryRecoveryFactor { get; init; } = 1.0;
    /// <summary>気づき（見極め）の週次発生率への倍率（ビデオ・分析機材）。</summary>
    public double InsightProbFactor { get; init; } = 1.0;
    /// <summary>気づきの精度への加算（ビデオ・分析機材。上限は InsightCoefficients.AccuracyMax）。</summary>
    public double InsightAccuracyAdd { get; init; }

    /// <summary>打撃分野の練習効率への倍率（打撃の道具）。</summary>
    public double BattingFactor { get; init; } = 1.0;
    /// <summary>守備分野の練習効率への倍率（守備の道具）。</summary>
    public double DefenseFactor { get; init; } = 1.0;
    /// <summary>走塁分野の練習効率への倍率（走塁の道具）。</summary>
    public double BaseRunningFactor { get; init; } = 1.0;
    /// <summary>投手分野の練習効率への倍率（投手の道具）。</summary>
    public double PitchingFactor { get; init; } = 1.0;

    /// <summary>
    /// 能動勧誘枠の解禁数（寮・設計書04 §2.2・issue #588）。段をまたいで<b>足し算で累積する</b>ので、
    /// 「年最大2名」は Lv1 に2を置き、上の段は0のまま（上げても枠は増えない）にする。
    /// </summary>
    public int RecruitSlots { get; init; }
}

/// <summary>
/// 買えるものの区分（設計書04 §1.4）。効果の持ち方は同じ（<see cref="FacilityUpgrade"/> の段）で、
/// 段数と見せ方だけが違う。経営画面はこの区分でブロックを分ける。
/// </summary>
public enum PurchaseKind
{
    /// <summary>施設（Lv1-3・学校資産＝転任で置いていく）。</summary>
    Facility,
    /// <summary>練習道具（効果小 Lv1 / 効果大 Lv2 の2段）。</summary>
    Equipment,
    /// <summary>備品（買い切り・1段のみ）。</summary>
    Gear,
}

/// <summary>
/// 施設1系統の定義（Issue #128）。index=施設レベルの <see cref="FacilityUpgrade"/> 列を持つ。
/// tiers[0] は施設なし（費用0・効果0）で、全系統Lv0＝従来一致（不変条件#2）を担保する。
/// </summary>
public sealed record FacilityDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    /// <summary>買えるものの区分（既定は施設）。効果の持ち方は共通で、経営画面の並べ方だけが変わる。</summary>
    public PurchaseKind Kind { get; init; } = PurchaseKind.Facility;
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
    /// 全系統の現在レベルから、効果の全軸を1つに集約する（練習効率・週練習時間に加えて
    /// 怪我・気づき・分野別倍率）。倍率は段をまたいで掛け算、加算は足し算で累積する。
    /// 全系統Lv0で <see cref="FacilityEffects.None"/> と等値＝従来と1ビット一致（不変条件#2）。
    /// </summary>
    public FacilityEffects AggregateEffects(FacilitySet set)
    {
        double coefAdd = 0;
        var budgetAdd = 0;
        double injuryProb = 1.0, injuryRecovery = 1.0, insightProb = 1.0, insightAccuracy = 0.0;
        double batting = 1.0, defense = 1.0, baseRunning = 1.0, pitching = 1.0;
        var recruitSlots = 0;

        foreach (var f in Facilities)
        {
            var lv = Math.Clamp(set.LevelOf(f.Id), 0, f.MaxLevel);
            for (var i = 1; i <= lv; i++)
            {
                var t = f.Tiers[i];
                coefAdd += t.CoefAdd;
                budgetAdd += t.BudgetAdd;
                injuryProb *= t.InjuryProbFactor;
                injuryRecovery *= t.InjuryRecoveryFactor;
                insightProb *= t.InsightProbFactor;
                insightAccuracy += t.InsightAccuracyAdd;
                batting *= t.BattingFactor;
                defense *= t.DefenseFactor;
                baseRunning *= t.BaseRunningFactor;
                pitching *= t.PitchingFactor;
                recruitSlots += t.RecruitSlots;
            }
        }

        return new FacilityEffects
        {
            CoefAdd = coefAdd,
            BudgetAdd = budgetAdd,
            InjuryProbFactor = injuryProb,
            InjuryRecoveryFactor = injuryRecovery,
            InsightProbFactor = insightProb,
            InsightAccuracyAdd = insightAccuracy,
            RecruitSlots = recruitSlots,
            Equipment = new EquipmentBonus
            {
                Batting = batting,
                Defense = defense,
                BaseRunning = baseRunning,
                Pitching = pitching,
            },
        };
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
                    new FacilityUpgrade { Cost = 2000, BudgetAdd = 80 },
                    new FacilityUpgrade { Cost = 3000, BudgetAdd = 80 },
                    new FacilityUpgrade { Cost = 5000, BudgetAdd = 90 },
                },
            },
            // 室内練習場: 雨天でも練習でき（練習時間）、設備で効率も上がる。
            new FacilityDef
            {
                Id = "indoor", Name = "室内練習場",
                Tiers = new[]
                {
                    new FacilityUpgrade(),
                    new FacilityUpgrade { Cost = 3000, CoefAdd = 0.15, BudgetAdd = 50 },
                    new FacilityUpgrade { Cost = 5000, CoefAdd = 0.15, BudgetAdd = 50 },
                    new FacilityUpgrade { Cost = 8000, CoefAdd = 0.15, BudgetAdd = 50 },
                },
            },
            // 寮: 生活・体調管理で練習効率が上がる。Lv1 で能動勧誘枠が2つ解禁される（設計書04 §2.2）。
            // 枠は Lv1 だけが持つ＝上の段を買っても「年最大2名」は変わらない。
            new FacilityDef
            {
                Id = "dorm", Name = "寮",
                Tiers = new[]
                {
                    new FacilityUpgrade(),
                    new FacilityUpgrade { Cost = 5000, CoefAdd = 0.15, RecruitSlots = 2 },
                    new FacilityUpgrade { Cost = 8000, CoefAdd = 0.15 },
                    new FacilityUpgrade { Cost = 12000, CoefAdd = 0.15 },
                },
            },
            // トレーニング場: ウェイト・マシンで練習効率が上がる。
            new FacilityDef
            {
                Id = "weight", Name = "トレーニング場",
                Tiers = new[]
                {
                    new FacilityUpgrade(),
                    new FacilityUpgrade { Cost = 500, CoefAdd = 0.15 },
                    new FacilityUpgrade { Cost = 1000, CoefAdd = 0.15 },
                    new FacilityUpgrade { Cost = 2000, CoefAdd = 0.20 },
                },
            },
            // 医務室: 怪我が起きにくくなり、復帰も早まる。練習効率・時間には効かないので
            // 全系統Lv3の (2.40 / 700分) は変わらない（#115の校正を保つ）。
            // 累計 Lv3 で 発生率 0.85³≒0.61倍 / 全治週 0.90³≒0.73倍。
            new FacilityDef
            {
                Id = "clinic", Name = "医務室",
                Tiers = new[]
                {
                    new FacilityUpgrade(),
                    new FacilityUpgrade { Cost = 150, InjuryProbFactor = 0.85, InjuryRecoveryFactor = 0.90 },
                    new FacilityUpgrade { Cost = 300, InjuryProbFactor = 0.85, InjuryRecoveryFactor = 0.90 },
                    new FacilityUpgrade { Cost = 500, InjuryProbFactor = 0.85, InjuryRecoveryFactor = 0.90 },
                },
            },

            // --- 練習道具（効果小 Lv1 / 効果大 Lv2）: 振っているメニューの分野にだけ効く。
            //     累計 Lv2 で 1.05 × 1.07 ≒ +12%。施設の効率係数と違って全メニューには効かない。
            new FacilityDef
            {
                Id = "gear_batting", Name = "打撃の道具", Kind = PurchaseKind.Equipment,
                Tiers = new[]
                {
                    new FacilityUpgrade(),
                    new FacilityUpgrade { Cost = 30, BattingFactor = 1.05 },   // バッティングティー・ネット
                    new FacilityUpgrade { Cost = 70, BattingFactor = 1.07 },   // ピッチングマシン
                },
            },
            new FacilityDef
            {
                Id = "gear_defense", Name = "守備の道具", Kind = PurchaseKind.Equipment,
                Tiers = new[]
                {
                    new FacilityUpgrade(),
                    new FacilityUpgrade { Cost = 30, DefenseFactor = 1.05 },   // ノックバット・防具一式
                    new FacilityUpgrade { Cost = 70, DefenseFactor = 1.07 },   // 守備マシン
                },
            },
            new FacilityDef
            {
                Id = "gear_baserunning", Name = "走塁の道具", Kind = PurchaseKind.Equipment,
                Tiers = new[]
                {
                    new FacilityUpgrade(),
                    new FacilityUpgrade { Cost = 10, BaseRunningFactor = 1.05 },  // ラダー・ミニハードル
                    new FacilityUpgrade { Cost = 70, BaseRunningFactor = 1.07 },  // タイム計測システム
                },
            },
            new FacilityDef
            {
                Id = "gear_pitching", Name = "投手の道具", Kind = PurchaseKind.Equipment,
                Tiers = new[]
                {
                    new FacilityUpgrade(),
                    new FacilityUpgrade { Cost = 15, PitchingFactor = 1.05 },  // 重量球・練習球一式
                    new FacilityUpgrade { Cost = 70, PitchingFactor = 1.07 },  // スピードガン＋ブルペン整備
                },
            },

            // --- 備品（買い切り・1段）---
            // ビデオ・分析機材: 隠しパラメータの気づきが増え、読み違えも減る。
            new FacilityDef
            {
                Id = "video", Name = "ビデオ・分析機材", Kind = PurchaseKind.Gear,
                Tiers = new[]
                {
                    new FacilityUpgrade(),
                    new FacilityUpgrade { Cost = 50, InsightProbFactor = 1.5, InsightAccuracyAdd = 0.05 },
                },
            },
        },
    };
}

/// <summary>
/// 施設・道具・備品の効果を1つに集約した注入値。倍率は 1.0、加算は 0 が「効果なし」。
/// 各モデル（<see cref="DevelopmentModel"/> / InjuryModel / InsightModel）へはここから配る。
/// </summary>
public sealed record FacilityEffects
{
    /// <summary>練習効率係数への加算（基準 1.0 に足す）。</summary>
    public double CoefAdd { get; init; }
    /// <summary>週練習時間[分]への加算（基準 300分に足す）。</summary>
    public int BudgetAdd { get; init; }
    /// <summary>分野別の練習効率倍率（練習道具）。</summary>
    public EquipmentBonus Equipment { get; init; } = EquipmentBonus.None;
    /// <summary>週次の怪我発生率への倍率。</summary>
    public double InjuryProbFactor { get; init; } = 1.0;
    /// <summary>怪我の全治週への倍率。</summary>
    public double InjuryRecoveryFactor { get; init; } = 1.0;
    /// <summary>気づきの週次発生率への倍率。</summary>
    public double InsightProbFactor { get; init; } = 1.0;
    /// <summary>気づきの精度への加算。</summary>
    public double InsightAccuracyAdd { get; init; }
    /// <summary>能動勧誘枠の数（寮・設計書04 §2.2）。0＝勧誘できない。</summary>
    public int RecruitSlots { get; init; }

    /// <summary>何も持っていない状態（全倍率1.0・全加算0）。</summary>
    public static FacilityEffects None { get; } = new();
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
