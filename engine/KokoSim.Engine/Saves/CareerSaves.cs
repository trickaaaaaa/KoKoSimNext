using System.Collections.Generic;
using KokoSim.Engine.Career;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Saves.Json;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Saves;

/// <summary>
/// 監督メタ <see cref="Manager"/> と施設保有状態 <see cref="FacilitySet"/>、および自校チームカラー上書き
/// <see cref="TeamColors"/> の JSON 往復（issue #507）。施設カタログ（効果・費用）は data 駆動の静的設定なので
/// 保存しない＝保存するのは「施設ID→現在レベル」の辞書だけ。
/// </summary>
internal static class CareerSaves
{
    // ===== Manager =====

    public static JsonObject ManagerToJson(Manager m) => new JsonObject()
        .Add("name", m.Name)
        .Add("status", EnumJson.Write(m.Status))
        .Add("coachBat", m.CoachingBatting)
        .Add("coachPit", m.CoachingPitching)
        .Add("coachDef", m.CoachingDefense)
        .Add("talentEye", m.TalentEye)
        .Add("tactical", m.TacticalSense)
        .Add("fame", m.Fame)
        .Add("trust", m.Trust)
        .Add("funds", m.Funds)
        .Add("facilityFund", m.FacilityFund)
        .Add("careerYears", m.CareerYears)
        .Add("koshienApp", m.KoshienAppearances);

    public static Manager ManagerFromJson(JsonObject o) => new()
    {
        Name = o.Str("name")!,
        Status = EnumJson.Read<ManagerStatus>(o.Str("status")),
        CoachingBatting = o.Double("coachBat"),
        CoachingPitching = o.Double("coachPit"),
        CoachingDefense = o.Double("coachDef"),
        TalentEye = o.Double("talentEye"),
        TacticalSense = o.Double("tactical"),
        Fame = o.Double("fame"),
        Trust = o.Double("trust"),
        Funds = o.Double("funds"),
        // 施設基金は v3 で追加。旧セーブ（v1/v2）には無いので 0 で読む＝建物は買えないが壊れない。
        FacilityFund = o.Has("facilityFund") ? o.Double("facilityFund") : 0,
        CareerYears = o.Int("careerYears"),
        KoshienAppearances = o.Int("koshienApp"),
    };

    // ===== FacilitySet =====

    public static JsonObject FacilitiesToJson(FacilitySet f)
    {
        var levels = new JsonObject();
        var ids = new List<string>(f.Levels.Keys);
        ids.Sort(System.StringComparer.Ordinal);
        foreach (var id in ids) levels.Add(id, f.LevelOf(id));
        return new JsonObject().Add("levels", levels);
    }

    public static FacilitySet FacilitiesFromJson(JsonObject o)
    {
        var set = new FacilitySet();
        var levels = o.Obj("levels");
        foreach (var id in levels.Keys) set.SetLevel(id, levels.Int(id));
        return set;
    }

    // ===== ContractSet（年契約・設計書04 §1.4a） =====
    // 施設と同じく、保存するのは「契約ID→現在の段」の辞書だけ（年額・効果はカタログ側の静的設定）。

    public static JsonObject ContractsToJson(ContractSet c)
    {
        var levels = new JsonObject();
        var ids = new List<string>(c.Levels.Keys);
        ids.Sort(System.StringComparer.Ordinal);
        foreach (var id in ids) levels.Add(id, c.LevelOf(id));
        return new JsonObject().Add("levels", levels);
    }

    public static ContractSet ContractsFromJson(JsonObject o)
    {
        var set = new ContractSet();
        var levels = o.Obj("levels");
        foreach (var id in levels.Keys) set.SetLevel(id, levels.Int(id));
        return set;
    }

    // ===== TeamColors（自校上書き。null＝校IDからの自動割り当てを使う） =====

    public static JsonValue TeamColorsToJson(TeamColors? c)
    {
        if (c is null) return JsonNull.Instance;
        return new JsonObject()
            .Add("shirt", c.Shirt.ToHex())
            .Add("pants", c.Pants.ToHex())
            .Add("undershirt", c.Undershirt.ToHex())
            .Add("socks", c.Socks.ToHex())
            .Add("capCrown", c.CapCrown.ToHex())
            .Add("capVisor", c.CapVisor.ToHex())
            .Add("shoes", c.Shoes.ToHex())
            .Add("stripe", StripeToJson(c.Stripe));
    }

    public static TeamColors? TeamColorsFromJson(JsonValue v)
    {
        if (v is JsonNull) return null;
        var o = v.AsObject();
        return new TeamColors(
            Hex(o.Str("shirt")), Hex(o.Str("pants")), Hex(o.Str("undershirt")), Hex(o.Str("socks")),
            Hex(o.Str("capCrown")), Hex(o.Str("capVisor")), Hex(o.Str("shoes")), StripeFromJson(o.Obj("stripe")));
    }

    private static JsonObject StripeToJson(TeamStripe s) => new JsonObject()
        .Add("on", s.On)
        .Add("color", s.Color.ToHex())
        .Add("freq", s.FreqPerMeter);

    private static TeamStripe StripeFromJson(JsonObject o) =>
        new(o.Bool("on"), Hex(o.Str("color")), o.Double("freq"));

    private static RgbColor Hex(string? hex) => RgbColor.TryParseHex(hex, out var c) ? c : default;
}
