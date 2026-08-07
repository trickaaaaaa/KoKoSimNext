using System.Collections.Generic;
using KokoSim.Engine.Players;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Saves.Json;

namespace KokoSim.Engine.Saves;

/// <summary>
/// 試合投影用 <see cref="Player"/> とその内包型（<see cref="PitcherAttributes"/> / <see cref="PitchSlot"/> /
/// <see cref="SkillSet"/>）の JSON 往復（issue #507）。AI校ロスターの能力スナップショットに使う。
/// フィールド名の単一ソースはここ。<see cref="Player"/> は init-only レコードなので初期化子で再構築する。
/// </summary>
internal static class PlayerSaves
{
    public static JsonObject ToJson(Player p)
    {
        var o = new JsonObject()
            .Add("name", p.Name)
            .Add("pos", EnumJson.Write(p.Position))
            .Add("srcId", p.SourceId is int sid ? JsonValue.Of(sid) : JsonNull.Instance)
            .Add("num", p.UniformNumber)
            .Add("grade", p.Grade)
            .Add("throws", EnumJson.Write(p.Throws))
            .Add("bats", EnumJson.Write(p.Bats))
            .Add("contact", p.Contact)
            .Add("power", p.Power)
            .Add("launch", p.LaunchTendency)
            .Add("disc", p.Discipline)
            .Add("speed", p.Speed)
            .Add("arm", p.ArmStrength)
            .Add("throwAcc", p.ThrowAccuracy)
            .Add("field", p.Fielding)
            .Add("catch", p.Catching)
            .Add("lead", p.Lead)
            .Add("bunt", p.Bunt)
            .Add("steal", p.Steal)
            .Add("baserun", p.Baserunning)
            .Add("mental", p.Mental)
            .Add("leadership", p.Leadership)
            .Add("personality", EnumJson.Write(p.Personality))
            .Add("buntBonus", p.BuntSuccessBonus)
            .Add("chanceHit", p.ChanceHitFactor)
            .Add("skills", SkillSetToJson(p.Skills))
            .Add("phenom", EnumJson.Write(p.Phenom))
            .Add("cond", EnumJson.Write(p.Condition))
            .Add("condVal", p.ConditionValue)
            .Add("injury", EnumJson.Write(p.Injury))
            .Add("injuryRes", p.InjuryResistance)
            .Add("pitching", PitcherToJson(p.Pitching));
        return o;
    }

    public static Player PlayerFromJson(JsonObject o) => new()
    {
        Name = o.Str("name")!,
        Position = EnumJson.Read<FieldPosition>(o.Str("pos")),
        SourceId = o.IsNull("srcId") ? (int?)null : o.Int("srcId"),
        UniformNumber = o.Int("num"),
        Grade = o.Int("grade"),
        Throws = EnumJson.Read<Handedness>(o.Str("throws")),
        Bats = EnumJson.Read<Handedness>(o.Str("bats")),
        Contact = o.Int("contact"),
        Power = o.Int("power"),
        LaunchTendency = o.Int("launch"),
        Discipline = o.Int("disc"),
        Speed = o.Int("speed"),
        ArmStrength = o.Int("arm"),
        ThrowAccuracy = o.Int("throwAcc"),
        Fielding = o.Int("field"),
        Catching = o.Int("catch"),
        Lead = o.Int("lead"),
        Bunt = o.Int("bunt"),
        Steal = o.Int("steal"),
        Baserunning = o.Int("baserun"),
        Mental = o.Int("mental"),
        Leadership = o.Int("leadership"),
        Personality = EnumJson.Read<Personality>(o.Str("personality")),
        BuntSuccessBonus = o.Double("buntBonus"),
        ChanceHitFactor = o.Double("chanceHit"),
        Skills = SkillSetFromJson(o.Obj("skills")),
        Phenom = EnumJson.Read<PhenomType>(o.Str("phenom")),
        Condition = EnumJson.Read<Condition>(o.Str("cond")),
        ConditionValue = o.Double("condVal"),
        Injury = EnumJson.Read<InjurySeverity>(o.Str("injury")),
        InjuryResistance = o.Double("injuryRes"),
        Pitching = PitcherFromJson(o.Obj("pitching")),
    };

    // ===== PitcherAttributes =====

    public static JsonObject PitcherToJson(PitcherAttributes a)
    {
        var o = new JsonObject()
            .Add("maxKmh", a.MaxVelocityKmh)
            .Add("control", a.Control)
            .Add("stamina", a.StaminaPitches)
            .Add("pitchRank", a.PitchRank)
            .Add("style", EnumJson.Write(a.Style))
            .Add("archetype", EnumJson.WriteNullable(a.Archetype));
        if (a.Repertoire is { } rep)
        {
            var arr = new JsonArray();
            foreach (var s in rep) arr.Add(PitchSlotToJson(s));
            o.Add("repertoire", arr);
        }
        else
        {
            o.Add("repertoire", JsonNull.Instance);
        }
        return o;
    }

    public static PitcherAttributes PitcherFromJson(JsonObject o)
    {
        IReadOnlyList<PitchSlot>? rep = null;
        if (!o.IsNull("repertoire"))
        {
            var list = new List<PitchSlot>();
            foreach (var v in o.Arr("repertoire").Items) list.Add(PitchSlotFromJson(v.AsObject()));
            rep = list;
        }
        return new PitcherAttributes
        {
            MaxVelocityKmh = o.Double("maxKmh"),
            Control = o.Int("control"),
            StaminaPitches = o.Double("stamina"),
            PitchRank = o.Int("pitchRank"),
            Style = EnumJson.Read<ThrowingStyle>(o.Str("style")),
            Archetype = EnumJson.ReadNullable<PitcherArchetype>(o.Str("archetype")),
            Repertoire = rep,
        };
    }

    // ===== PitchSlot =====

    public static JsonObject PitchSlotToJson(PitchSlot s) => new JsonObject()
        .Add("type", EnumJson.Write(s.Type))
        .Add("power", s.Power)
        .Add("sharp", s.Sharpness);

    public static PitchSlot PitchSlotFromJson(JsonObject o) => new()
    {
        Type = EnumJson.Read<PitchType>(o.Str("type")),
        Power = o.Int("power"),
        Sharpness = o.Int("sharp"),
    };

    // ===== SkillSet =====

    public static JsonObject SkillSetToJson(SkillSet s)
    {
        var visible = new JsonArray();
        foreach (var k in s.Visible) visible.Add(EnumJson.Write(k));
        var hidden = new JsonArray();
        foreach (var k in s.Hidden) hidden.Add(EnumJson.Write(k));
        return new JsonObject().Add("visible", visible).Add("hidden", hidden);
    }

    public static SkillSet SkillSetFromJson(JsonObject o)
    {
        var visible = new List<Skill>();
        foreach (var v in o.Arr("visible").Items) visible.Add(EnumJson.Read<Skill>(((JsonString)v).Value));
        var hidden = new List<Skill>();
        foreach (var v in o.Arr("hidden").Items) hidden.Add(EnumJson.Read<Skill>(((JsonString)v).Value));
        return new SkillSet(visible, hidden);
    }
}
