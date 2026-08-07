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

        return new JsonObject()
            .Add("id", p.Id)
            .Add("name", p.Name)
            .Add("grade", p.Grade)
            .Add("throws", EnumJson.Write(p.Throws))
            .Add("bats", EnumJson.Write(p.Bats))
            .Add("growthType", EnumJson.Write(p.GrowthType))
            .Add("personalityFactor", p.PersonalityFactor)
            .Add("isProdigy", p.IsProdigy)
            .Add("mental", p.Mental)
            .Add("mentalExp", p.MentalExp)
            .Add("mentalCap", p.MentalCap)
            .Add("lead", p.Lead)
            .Add("leadExp", p.LeadExp)
            .Add("leadCap", p.LeadCap)
            .Add("leadership", p.Leadership)
            .Add("personality", EnumJson.Write(p.Personality))
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
            .Add("level", level)
            .Add("exp", exp)
            .Add("cap", cap)
            .Add("apt", apt)
            .Add("aptExp", aptExp)
            .Add("aptCap", aptCap)
            .Add("learned", learned);
    }

    public static DevelopingPlayer FromJson(JsonObject o)
    {
        var p = new DevelopingPlayer
        {
            Name = o.Str("name")!,
            Throws = EnumJson.Read<Handedness>(o.Str("throws")),
            Bats = EnumJson.Read<Handedness>(o.Str("bats")),
            GrowthType = EnumJson.Read<GrowthType>(o.Str("growthType")),
            PersonalityFactor = o.Double("personalityFactor"),
            IsProdigy = o.Bool("isProdigy"),
            Leadership = o.Int("leadership"),
            Personality = EnumJson.Read<Personality>(o.Str("personality")),
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
        };

        var level = o.Arr("level");
        var exp = o.Arr("exp");
        var cap = o.Arr("cap");
        for (var i = 0; i < AbilityKinds.All.Length; i++)
        {
            var k = AbilityKinds.All[i];
            p.SetLevel(k, ((JsonNumber)level[i]).AsInt());
            p.SetCap(k, ((JsonNumber)cap[i]).AsInt());
            p.AddExp(k, ((JsonNumber)exp[i]).AsDouble());   // 既定0からの加算で厳密復元
        }

        var apt = o.Arr("apt");
        var aptExp = o.Arr("aptExp");
        var aptCap = o.Arr("aptCap");
        for (var i = 0; i < Positions.Length; i++)
        {
            var pos = Positions[i];
            p.SetAptitude(pos, ((JsonNumber)apt[i]).AsInt());
            p.SetAptitudeCap(pos, ((JsonNumber)aptCap[i]).AsInt());
            p.AddAptitudeExp(pos, ((JsonNumber)aptExp[i]).AsDouble());
        }

        foreach (var v in o.Arr("learned").Items)
        {
            var lo = v.AsObject();
            p.LearnedPitches.Add(new LearnedPitch(
                EnumJson.Read<PitchType>(lo.Str("type")), lo.Int("powerOff"), lo.Int("sharpOff")));
        }

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
