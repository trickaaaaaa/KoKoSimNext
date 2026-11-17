using System.Collections.Generic;
using KokoSim.Engine.Career;

namespace KokoSim.Engine.Season;

/// <summary>スカウト網の段の選択肢1つ（夏の会話で選ぶ。issue #1064）。</summary>
public sealed record ScoutContractChoice(int Level, double AnnualCost, int Slots, string Label);

/// <summary>探す分野の選択肢1つ。</summary>
public sealed record ScoutFieldChoice(ScoutField Field, string Label);

/// <summary>
/// スカウトの一年の入口（issue #1064・オーナー決定 2026-11-17）。夏の大会後（新チーム発足の流れのあと）に、
/// スカウトとの会話の中で①スカウト網の契約（段）②探す分野、の順に決める。選択肢の中身と言い回しはここ1箇所に置く
/// （提示層は並べるだけ）。年額・枠数は契約カタログから引く＝数値の二重管理をしない。
/// </summary>
public static class ScoutPitch
{
    /// <summary>スカウト網の契約ID（年契約カタログのキー）。</summary>
    public const string ContractId = "scouting";

    /// <summary>未契約の学校に「契約しませんか」と聞く入口の会話。</summary>
    public const string OfferEventId = "scout_contract_offer";
    /// <summary>契約しないと決めたあとの会話（今年はスカウトなし）。</summary>
    public const string DeclinedEventId = "scout_contract_declined";
    /// <summary>契約を決めたあと「どんな子を探しましょう」と聞く会話。</summary>
    public const string FieldAskEventId = "scout_field_ask";
    /// <summary>分野を決めたあと「10月にリストを持ってきます」で締める会話。</summary>
    public const string FieldDoneEventId = "scout_field_done";

    /// <summary>探す分野の並び（画面の並びと同じ）。</summary>
    public static IReadOnlyList<ScoutFieldChoice> FieldChoices { get; } = new[]
    {
        new ScoutFieldChoice(ScoutField.Omakase, "おまかせ"),
        new ScoutFieldChoice(ScoutField.Pitching, "投球"),
        new ScoutFieldChoice(ScoutField.Batting, "打撃"),
        new ScoutFieldChoice(ScoutField.Defense, "守備走塁"),
    };

    /// <summary>探す分野の表示名。</summary>
    public static string FieldLabel(ScoutField field)
    {
        foreach (var c in FieldChoices) if (c.Field == field) return c.Label;
        return "おまかせ";
    }

    /// <summary>
    /// 契約の段の選択肢（0＝契約しない〜最上段）。年額と枠数を言い回しに書く。
    /// カタログにスカウト網が無ければ空。
    /// </summary>
    public static IReadOnlyList<ScoutContractChoice> ContractChoices(ContractCatalog catalog)
    {
        var def = catalog.Find(ContractId);
        var list = new List<ScoutContractChoice>();
        if (def == null) return list;
        for (var lv = 0; lv <= def.MaxLevel; lv++)
        {
            var t = def.Tiers[lv];
            var label = lv == 0
                ? "契約しない（0万）"
                : "Lv" + lv + "（" + t.AnnualCost.ToString("0") + "万・枠" + t.ScoutSlots + "人）";
            list.Add(new ScoutContractChoice(lv, t.AnnualCost, t.ScoutSlots, label));
        }
        return list;
    }

    /// <summary>契約して分野まで決めたあとの会話の最終行（結果行）の文。</summary>
    public static string ResultSentence(ScoutContractChoice contract, ScoutField field)
        => "スカウト網 Lv" + contract.Level + "（年" + contract.AnnualCost.ToString("0") + "万・枠"
           + contract.Slots + "人）　探す分野: " + FieldLabel(field);
}
