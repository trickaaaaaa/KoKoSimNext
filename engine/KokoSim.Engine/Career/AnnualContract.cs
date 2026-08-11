using System;
using System.Collections.Generic;

namespace KokoSim.Engine.Career;

/// <summary>
/// 年契約1段の内容（設計書04 §1.4a）。
///
/// <para><b>施設・練習道具との決定的な違いは「累積しない」こと。</b>
/// 施設（<see cref="Season.FacilityUpgrade"/>）は段を買い足すたびに効果が積み上がる買い切りだが、
/// 年契約は「いま何段で契約しているか」だけが効く継続契約で、Lv2 の年額と効果は Lv1 を<b>置き換える</b>。
/// 段を下げれば効果も年額もその場で下がる（＝解約できる）。</para>
/// </summary>
public sealed record ContractTier
{
    /// <summary>年額[万円]。年度頭に部費から引かれる。</summary>
    public double AnnualCost { get; init; }

    /// <summary>新入生の才能中心値への加算（スカウト網）。名声由来の上振れに上乗せする。</summary>
    public double ScoutTalentAdd { get; init; }

    /// <summary>
    /// 入部予定者の事前開示精度 0〜1（スカウト網の効果2・設計書04 §2.1・issue #587）。
    /// 3月に確定した予定者を4月の入部前にどこまで見通せるか＝推定能力の誤差の小ささと
    /// 隠しパラメータが判明する確率。0＝ほぼ何も分からない、1＝ほぼ実値。
    /// </summary>
    public double ProspectDisclosure { get; init; }

    /// <summary>打撃系の指導力への加算（専門コーチ）。監督自身の指導力は変えず、実効値だけ底上げする。</summary>
    public double CoachingBattingAdd { get; init; }

    /// <summary>投手系の指導力への加算（専門コーチ）。</summary>
    public double CoachingPitchingAdd { get; init; }

    /// <summary>守備・走塁系の指導力への加算（専門コーチ）。</summary>
    public double CoachingDefenseAdd { get; init; }

    /// <summary>県外の学校とも練習試合を組めるようにする（遠征）。</summary>
    public bool OutOfPrefectureOpponents { get; init; }
}

/// <summary>年契約1系統の定義。<c>Tiers[0]</c> は「未契約」＝年額0・効果なし。</summary>
public sealed record ContractDef
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";

    /// <summary>段の一覧（[0]=未契約）。</summary>
    public IReadOnlyList<ContractTier> Tiers { get; init; } = new[] { new ContractTier() };

    /// <summary>契約できる最大段（Tiers.Count-1）。</summary>
    public int MaxLevel => Math.Max(0, Tiers.Count - 1);
}

/// <summary>
/// 契約中の全系統をまとめた実効値。年額は合計、能力への加算は軸ごとに合計、
/// 遠征のような可否フラグは論理和で畳む。
/// </summary>
public sealed record ContractEffects
{
    /// <summary>契約中の年額合計[万円/年]。</summary>
    public double AnnualCost { get; init; }

    public double ScoutTalentAdd { get; init; }
    public double CoachingBattingAdd { get; init; }
    public double CoachingPitchingAdd { get; init; }
    public double CoachingDefenseAdd { get; init; }
    public bool OutOfPrefectureOpponents { get; init; }

    /// <summary>
    /// 入部予定者の事前開示精度 0〜1（issue #587）。他の軸が加算で畳まれるのに対し、これだけは
    /// <b>最大値</b>で畳む。「見えている度合い」は重ねても倍にならず、いちばん通った網の分だけ見える。
    /// </summary>
    public double ProspectDisclosure { get; init; }

    /// <summary>何も契約していない状態（年額0・効果なし）。</summary>
    public static ContractEffects None { get; } = new();
}

/// <summary>
/// 年契約のカタログ（設計書04 §1.4a・data/coefficients.yaml <c>contracts:</c> 駆動）。
/// 費用・効果はここだけが持ち、UI は表示と入力の中継しかしない。
/// </summary>
public sealed record ContractCatalog
{
    public IReadOnlyList<ContractDef> Contracts { get; init; } = Array.Empty<ContractDef>();

    public ContractDef? Find(string id)
    {
        foreach (var d in Contracts)
            if (d.Id == id) return d;
        return null;
    }

    /// <summary>契約状態から実効値を畳む。段は 0..MaxLevel にクランプするので未知の段でも壊れない。</summary>
    public ContractEffects Aggregate(ContractSet set)
    {
        double cost = 0, scout = 0, bat = 0, pit = 0, def = 0, disclosure = 0;
        var away = false;
        foreach (var d in Contracts)
        {
            var lv = Math.Clamp(set.LevelOf(d.Id), 0, d.MaxLevel);
            if (lv <= 0) continue;
            // 累積しない＝いまの段だけを見る（施設の Aggregate が 1..lv を合算するのとは別物）。
            var t = d.Tiers[lv];
            cost += t.AnnualCost;
            scout += t.ScoutTalentAdd;
            bat += t.CoachingBattingAdd;
            pit += t.CoachingPitchingAdd;
            def += t.CoachingDefenseAdd;
            away |= t.OutOfPrefectureOpponents;
            disclosure = Math.Max(disclosure, t.ProspectDisclosure);   // 開示は重ねない＝最大値
        }
        return new ContractEffects
        {
            AnnualCost = cost,
            ScoutTalentAdd = scout,
            CoachingBattingAdd = bat,
            CoachingPitchingAdd = pit,
            CoachingDefenseAdd = def,
            OutOfPrefectureOpponents = away,
            ProspectDisclosure = disclosure,
        };
    }

    /// <summary>
    /// C#既定（data/coefficients.yaml <c>contracts:</c> と一致させること・不変条件#4）。
    ///
    /// <para>スカウト網 Lv1（15万/年）は従来の <see cref="CareerCoefficients.ScoutCost"/> と同額で、
    /// 質への加算は0＝<b>買うのは「見立て」だけ</b>（issue #587 の事前開示）。質が動くのは Lv2 以上で、
    /// 実測では最良軸がC以上の逸材の出現率が 2.78%（未契約）→ 3.99%（Lv2）→ 5.74%（Lv3）と<b>倍化する</b>
    /// （平均が2〜3上がるより、当たりの頻度が変わる効き方）。新規ゲームは<b>未契約</b>から始まる
    /// （<see cref="ContractSet.Initial"/>）。</para>
    ///
    /// <para>全部を最上段で契約すると 50+20+30×3＝160万/年。部費の年間収入は信頼度50で120万・
    /// 信頼度100で160万なので、合宿費45万と合わせると全部は抱えられない＝「どれを取るか」の選択になる。</para>
    /// </summary>
    public static ContractCatalog Default { get; } = new()
    {
        Contracts = new[]
        {
            // スカウト網: 新入生の質。Lv1 は従来のスカウト費と同額・同効果（＝据え置き）。
            new ContractDef
            {
                Id = "scouting",
                Name = "スカウト網",
                Tiers = new[]
                {
                    new ContractTier(),
                    // 効果1＝新入生の質（ScoutTalentAdd）／効果2＝入部前にどこまで見えるか（ProspectDisclosure）。
                    // Lv1 は従来のスカウト費と同額で質も据え置き。上げると質より先に「見通し」が良くなる。
                    new ContractTier { AnnualCost = 15, ScoutTalentAdd = 0, ProspectDisclosure = 0.15 },   // 県内を回る（従来どおり）
                    new ContractTier { AnnualCost = 30, ScoutTalentAdd = 3, ProspectDisclosure = 0.45 },   // 近隣県まで足を伸ばす
                    new ContractTier { AnnualCost = 50, ScoutTalentAdd = 6, ProspectDisclosure = 0.75 },   // 全国に網を張る
                },
            },
            // 遠征: 県外の学校と練習試合が組める（相手の幅＝実戦成長の相手ティアの幅）。
            new ContractDef
            {
                Id = "expedition",
                Name = "遠征",
                Tiers = new[]
                {
                    new ContractTier(),
                    new ContractTier { AnnualCost = 20, OutOfPrefectureOpponents = true },
                },
            },
            // 専門コーチ: 分野別指導力の実効値を底上げする。監督自身の指導力は変えないので、
            // 解約すればその年から元に戻る（雇っている間だけ効く）。
            new ContractDef
            {
                Id = "coach_batting",
                Name = "打撃コーチ",
                Tiers = new[] { new ContractTier(), new ContractTier { AnnualCost = 30, CoachingBattingAdd = 10 } },
            },
            new ContractDef
            {
                Id = "coach_pitching",
                Name = "投手コーチ",
                Tiers = new[] { new ContractTier(), new ContractTier { AnnualCost = 30, CoachingPitchingAdd = 10 } },
            },
            new ContractDef
            {
                Id = "coach_defense",
                Name = "守備走塁コーチ",
                Tiers = new[] { new ContractTier(), new ContractTier { AnnualCost = 30, CoachingDefenseAdd = 10 } },
            },
        },
    };
}

/// <summary>
/// 契約状態（契約ID→現在の段）。カタログ（費用・効果）は静的設定なので保存対象はこの辞書だけ。
/// </summary>
public sealed class ContractSet
{
    private readonly Dictionary<string, int> _levels = new();

    public int LevelOf(string id) => _levels.TryGetValue(id, out var v) ? v : 0;

    public void SetLevel(string id, int level) => _levels[id] = Math.Max(0, level);

    public IReadOnlyDictionary<string, int> Levels => _levels;

    /// <summary>
    /// 新規ゲームの既定＝<b>何も契約していない</b>（監督決定 2026-08-11）。
    ///
    /// <para>スカウトは「金を払っている間だけ効く継続契約」で、払わなければ何も見えないし質も素のまま
    /// ＝<b>新入生は毎年8〜13人が勝手に入ってくる（受動枠）が、それが誰なのかは分からない</b>という
    /// 立ち上がりになる。以前は Lv1 を既定にして「年契約を導入しても収支が変わらない」ことを優先していたが、
    /// 監督が一度も選んでいない15万/年が既定で引かれ続けるのは筋が悪い。</para>
    /// </summary>
    public static ContractSet Initial() => new ContractSet();

    /// <summary>
    /// 年契約が無かった版（セーブ v1/v2）を読むときの既定。当時は
    /// <see cref="CareerCoefficients.ScoutCost"/>（15万/年）が固定で引かれていたので、
    /// 同額・同効果のスカウト網 Lv1 で読む＝<b>その進行の収支を後から変えない</b>。
    /// </summary>
    public static ContractSet LegacyScoutingLevel1()
    {
        var s = new ContractSet();
        s.SetLevel("scouting", 1);
        return s;
    }
}
