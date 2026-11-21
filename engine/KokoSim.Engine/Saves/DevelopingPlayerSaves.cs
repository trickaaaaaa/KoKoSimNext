using System;
using System.Collections.Generic;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Players;
using KokoSim.Engine.Saves.Json;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Saves;

/// <summary>
/// 自校部員 <see cref="DevelopingPlayer"/> の JSON 往復（issue #507）。<see cref="DevelopingPlayer.Clone"/> が
/// 列挙する可変状態を漏れなく写す（能力/経験値/才能上限・守備適性3系・精神/リード系・習得球種・練習計画・
/// スキル・怪我・成長イベント）。能力/適性の配列は<b>列挙の並び順に位置で焼く</b>（<c>_level[(int)k]</c> と
/// 同じ規約。列挙は末尾追加のみ＝不変条件#2）。分数の経験値は既定0からの <c>AddExp</c> で厳密に復元する。
/// </summary>
internal static class DevelopingPlayerSaves
{
    private static readonly FieldPosition[] Positions = (FieldPosition[])Enum.GetValues(typeof(FieldPosition));

    public static JsonObject ToJson(DevelopingPlayer p)
    {
        var level = new JsonArray();
        var exp = new JsonArray();
        var cap = new JsonArray();
        foreach (var k in AbilityKinds.All)
        {
            level.Add(JsonValue.Of(p.Level(k)));
            exp.Add(JsonValue.Of(p.Exp(k)));
            cap.Add(JsonValue.Of(p.Cap(k)));
        }

        var apt = new JsonArray();
        var aptExp = new JsonArray();
        var aptCap = new JsonArray();
        foreach (var pos in Positions)
        {
            apt.Add(JsonValue.Of(p.Aptitude(pos)));
            aptExp.Add(JsonValue.Of(p.AptitudeExp(pos)));
            aptCap.Add(JsonValue.Of(p.AptitudeCap(pos)));
        }

        var learned = new JsonArray();
        foreach (var lp in p.LearnedPitches)
            learned.Add(new JsonObject()
                .Add("type", EnumJson.Write(lp.Type))
                .Add("powerOff", lp.PowerOffset)
                .Add("sharpOff", lp.SharpnessOffset));

        var obj = new JsonObject()
            .Add("id", p.Id)
            .Add("name", p.Name)
            .Add("grade", p.Grade)
            .Add("throws", EnumJson.Write(p.Throws))
            .Add("bats", EnumJson.Write(p.Bats))
            // 対左投手経験（設計書02 §2.3「左のプレミア」・issue #1072）。旧セーブ（キー欠損）は0で読む
            // （FromJson側の Has 分岐）＝蓄積なしからのスタート（従来は効果自体が存在しなかったので挙動は近い）。
            .Add("leftExpPa", p.LeftExperiencePa)
            .Add("growthType", EnumJson.Write(p.GrowthType))
            // 成長タイプの分野別化（設計書02 §5.2・issue #866）。null=基本タイプに従う（旧セーブ相当）。
            .Add("pitchingGrowthType", EnumJson.WriteNullable(p.PitchingGrowthType))
            .Add("battingGrowthType", EnumJson.WriteNullable(p.BattingGrowthType))
            .Add("defenseGrowthType", EnumJson.WriteNullable(p.DefenseGrowthType))
            .Add("isProdigy", p.IsProdigy)
            .Add("mental", p.Mental)
            .Add("mentalExp", p.MentalExp)
            .Add("mentalCap", p.MentalCap)
            .Add("lead", p.Lead)
            .Add("leadExp", p.LeadExp)
            .Add("leadCap", p.LeadCap)
            .Add("leadership", p.Leadership)
            .Add("receptiveness", p.Receptiveness)
            .Add("diligence", p.Diligence)
            .Add("selflessness", p.Selflessness)
            .Add("growthJitter", p.SelfGrowthJitter)
            .Add("persKnown", p.PersonalityReading.KnownMask)
            .Add("persHigh", p.PersonalityReading.HighMask)
            .Add("isCaptain", p.IsCaptain)
            .Add("isRetired", p.IsRetired)
            .Add("num", p.UniformNumber)
            .Add("skills", PlayerSaves.SkillSetToJson(p.Skills))
            .Add("pitchingGrowth", p.PitchingGrowth)
            .Add("battingGrowth", p.BattingGrowth)
            .Add("defenseGrowth", p.DefenseGrowth)
            .Add("plan", p.Plan is { } plan ? TrainingPlanToJson(plan) : JsonNull.Instance)
            .Add("individualCoaching", p.IndividualCoaching)
            .Add("condVal", p.ConditionValue)
            .Add("injury", EnumJson.Write(p.Injury))
            .Add("injurySite", EnumJson.Write(p.InjurySite))
            .Add("injuryType", EnumJson.Write(p.InjuryType))
            .Add("injuryWeeks", p.InjuryWeeksRemaining)
            .Add("injuryRes", p.InjuryResistance)
            .Add("slumpWeeks", p.SlumpWeeks)
            .Add("hasYips", p.HasYips)
            .Add("yipsAbility", EnumJson.WriteNullable(p.YipsAbility))
            .Add("yipsDrop", p.YipsDrop)
            .Add("level", level)
            .Add("exp", exp)
            .Add("cap", cap)
            .Add("apt", apt)
            .Add("aptExp", aptExp)
            .Add("aptCap", aptCap)
            .Add("learned", learned);
        // 成長推移（v34・issue #1027）。1点＝[学年, 週, 全能力レベル…]。記録があるときだけ書く
        // ＝AI校の選手（空）にキーを足してセーブを膨らませない。
        if (p.History.Count > 0) obj.Add("hist", HistoryToJson(p.History));
        return obj;
    }

    private static JsonArray HistoryToJson(List<AbilitySample> history)
    {
        var arr = new JsonArray();
        foreach (var s in history)
        {
            var row = new JsonArray();
            row.Add(JsonValue.Of(s.Grade));
            row.Add(JsonValue.Of(s.Week));
            foreach (var v in s.Levels) row.Add(JsonValue.Of(v));
            arr.Add(row);
        }
        return arr;
    }

    private static void HistoryFromJson(JsonObject o, DevelopingPlayer p)
    {
        // v33 以前には無い＝空（次の週送りで記録が始まる）。
        if (!o.Has("hist")) return;
        var n = AbilityKinds.All.Length;
        foreach (var v in o.Arr("hist").Items)
        {
            var row = (JsonArray)v;
            var levels = new int[n];
            // 能力の列挙は末尾追加のみ。保存時より能力が増えていたら、増えた能力は今の値で埋める。
            for (var i = 0; i < n; i++)
                levels[i] = i + 2 < row.Count ? ((JsonNumber)row[i + 2]).AsInt() : p.Level(AbilityKinds.All[i]);
            p.History.Add(new AbilitySample(((JsonNumber)row[0]).AsInt(), ((JsonNumber)row[1]).AsInt(), levels));
        }
    }

    public static DevelopingPlayer FromJson(JsonObject o)
    {
        var p = new DevelopingPlayer
        {
            Name = o.Str("name")!,
            Throws = EnumJson.Read<Handedness>(o.Str("throws")),
            Bats = EnumJson.Read<Handedness>(o.Str("bats")),
            // 旧セーブ（キー欠損）は0＝蓄積なしから（issue #1072）。
            LeftExperiencePa = o.Has("leftExpPa") ? o.Double("leftExpPa") : 0.0,
            GrowthType = EnumJson.Read<GrowthType>(o.Str("growthType")),
            // 成長タイプの分野別化（issue #866）: 旧セーブ（キー欠損）・null（意図的な「基本タイプに従う」）
            // のどちらも null として読む＝全分野が基本タイプ1つという旧来の挙動のまま（安全な既定・Has分岐）。
            PitchingGrowthType = o.Has("pitchingGrowthType") ? EnumJson.ReadNullable<GrowthType>(o.Str("pitchingGrowthType")) : null,
            BattingGrowthType = o.Has("battingGrowthType") ? EnumJson.ReadNullable<GrowthType>(o.Str("battingGrowthType")) : null,
            DefenseGrowthType = o.Has("defenseGrowthType") ? EnumJson.ReadNullable<GrowthType>(o.Str("defenseGrowthType")) : null,
            IsProdigy = o.Bool("isProdigy"),
            Leadership = o.Int("leadership"),
            // 性格の4軸（設計書23 §1.5・v27）。旧セーブは8タイプ＋PersonalityFactor＝代表値＋隠れた個人差へ（Q47）。
            Receptiveness = PersonalitySaveMigration.DevelopingAxes(o).Receptiveness,
            Diligence = PersonalitySaveMigration.DevelopingAxes(o).Diligence,
            Selflessness = PersonalitySaveMigration.DevelopingAxes(o).Selflessness,
            // 自主成長の隠れた個人差（v30・Q47）。v26 以前は旧 personalityFactor の残り（代表値の勤勉さで説明できない分）、
            // v27〜v29 は持っていない＝0。
            SelfGrowthJitter = PersonalitySaveMigration.DevelopingSelfGrowthJitter(o),
            PersonalityReading = o.Has("persKnown")
                ? new PersonalityReading(o.Int("persKnown"), o.Int("persHigh")) : default,
            InjuryResistance = o.Double("injuryRes"),
            // --- set 可の可変状態 ---
            Id = o.Int("id"),
            Grade = o.Int("grade"),
            Mental = o.Int("mental"),
            MentalExp = o.Double("mentalExp"),
            MentalCap = o.Int("mentalCap"),
            Lead = o.Int("lead"),
            LeadExp = o.Double("leadExp"),
            LeadCap = o.Int("leadCap"),
            IsCaptain = o.Bool("isCaptain"),
            IsRetired = o.Bool("isRetired"),
            UniformNumber = o.Int("num"),
            Skills = PlayerSaves.SkillSetFromJson(o.Obj("skills")),
            PitchingGrowth = o.Double("pitchingGrowth"),
            BattingGrowth = o.Double("battingGrowth"),
            DefenseGrowth = o.Double("defenseGrowth"),
            Plan = o.IsNull("plan") ? null : TrainingPlanFromJson(o.Obj("plan")),
            IndividualCoaching = o.Bool("individualCoaching"),
            ConditionValue = o.Double("condVal"),
            Injury = EnumJson.Read<InjurySeverity>(o.Str("injury")),
            InjurySite = EnumJson.Read<InjurySite>(o.Str("injurySite")),
            InjuryType = EnumJson.Read<InjuryType>(o.Str("injuryType")),
            InjuryWeeksRemaining = o.Int("injuryWeeks"),
            SlumpWeeks = o.Int("slumpWeeks"),
            HasYips = o.Bool("hasYips"),
            YipsAbility = o.Has("yipsAbility") ? EnumJson.ReadNullable<AbilityKind>(o.Str("yipsAbility")) : null,
            YipsDrop = o.Has("yipsDrop") ? o.Int("yipsDrop") : 0,
        };

        // 能力・守備位置の列挙は末尾追加のみ。保存時より列挙が増えていたら、増えた分の列は旧セーブに無い＝
        // 既定値のまま読む（位置で読んで長さを見ないと旧セーブが全滅する・issue #1225）。
        var level = o.Arr("level");
        var exp = o.Arr("exp");
        var cap = o.Arr("cap");
        for (var i = 0; i < AbilityKinds.All.Length; i++)
        {
            var k = AbilityKinds.All[i];
            if (i < level.Count) p.SetLevel(k, ((JsonNumber)level[i]).AsInt());
            if (i < cap.Count) p.SetCap(k, ((JsonNumber)cap[i]).AsInt());
            if (i < exp.Count) p.AddExp(k, exp[i].ToDouble());   // 既定0からの加算で厳密復元
        }

        var apt = o.Arr("apt");
        var aptExp = o.Arr("aptExp");
        var aptCap = o.Arr("aptCap");
        for (var i = 0; i < Positions.Length; i++)
        {
            var pos = Positions[i];
            if (i < apt.Count) p.SetAptitude(pos, ((JsonNumber)apt[i]).AsInt());
            if (i < aptCap.Count) p.SetAptitudeCap(pos, ((JsonNumber)aptCap[i]).AsInt());
            if (i < aptExp.Count) p.AddAptitudeExp(pos, aptExp[i].ToDouble());
        }

        foreach (var v in o.Arr("learned").Items)
        {
            var lo = v.AsObject();
            p.LearnedPitches.Add(new LearnedPitch(
                EnumJson.Read<PitchType>(lo.Str("type")), lo.Int("powerOff"), lo.Int("sharpOff")));
        }

        HistoryFromJson(o, p);   // 能力を復元した後（欠けた列を今の値で埋めるため）
        return p;
    }

    // ===== TrainingPlan =====

    public static JsonObject TrainingPlanToJson(TrainingPlan plan)
    {
        var allocs = new JsonArray();
        foreach (var a in plan.Allocations)
            allocs.Add(new JsonObject().Add("menu", EnumJson.Write(a.Menu)).Add("min", a.Minutes));
        return new JsonObject()
            .Add("preset", EnumJson.Write(plan.Preset))
            .Add("allocations", allocs);
    }

    public static TrainingPlan TrainingPlanFromJson(JsonObject o)
    {
        var allocs = new List<MenuAllocation>();
        foreach (var v in o.Arr("allocations").Items)
        {
            var ao = v.AsObject();
            allocs.Add(new MenuAllocation(EnumJson.Read<TrainingMenu>(ao.Str("menu")), ao.Int("min")));
        }
        return new TrainingPlan
        {
            Preset = EnumJson.Read<TrainingPreset>(o.Str("preset")),
            Allocations = allocs,
        };
    }
}
