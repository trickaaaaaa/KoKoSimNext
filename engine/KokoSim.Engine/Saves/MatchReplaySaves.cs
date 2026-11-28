using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Match.Pitching;
using KokoSim.Engine.Match.Tactics;
using KokoSim.Engine.Players;
using KokoSim.Engine.Replay;
using KokoSim.Engine.Saves.Json;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Saves;

/// <summary>
/// 注目試合の再生データ台帳（<see cref="MatchReplayBook"/>）の JSON 往復（issue #1580・セーブ v53）。
///
/// <para>試合の中身（打席・1球）は持たず、解き直すための入力（開始乱数状態・両チームの選手と能力・試合設定・采配の決定列）
/// だけを焼く。選手は <see cref="PlayerSaves"/>（試合投影用 Player の正典）に、それが持たない
/// 対左経験・守備位置別の値を足して往復する。チーム内で同じ選手（主将・先発投手が打順や控えと同じ実体）は
/// 1人1回だけ焼き、添字で参照する＝復元後も同じ実体として扱える。</para>
/// </summary>
internal static class MatchReplaySaves
{
    public static JsonArray ToJson(MatchReplayBook book)
    {
        var arr = new JsonArray();
        foreach (var r in book.Records) arr.Add(RecordToJson(r));
        return arr;
    }

    public static void RestoreInto(MatchReplayBook target, JsonArray arr)
    {
        target.Clear();
        foreach (var v in arr.Items)
            if (RecordFromJson(v.AsObject()) is { } r) target.Add(r);
    }

    // ===== 1試合 =====

    private static JsonObject RecordToJson(MatchReplayRecord r)
    {
        var o = new JsonObject()
            .Add("id", r.Id)
            .Add("ver", r.EngineVersion)
            .Add("why", (int)r.Reasons)
            .Add("y", r.Year)
            .Add("k", EnumJson.WriteNullable(r.Kind))
            .Add("m", r.Month)
            .Add("d", r.Day)
            .Add("rd", r.RoundName)
            .Add("md", r.MatchDay)
            .Add("rr", r.RoundsRemaining)
            .Add("aid", r.AwaySchoolId)
            .Add("hid", r.HomeSchoolId)
            .Add("an", r.AwayName)
            .Add("hn", r.HomeName)
            .Add("ar", r.AwayRuns)
            .Add("hr", r.HomeRuns)
            .Add("mercy", r.MercyEnded);
        var rng = new JsonArray();
        foreach (var w in r.RngState) rng.Add(JsonValue.Of(w));
        o.Add("rng", rng)
            .Add("away", TeamToJson(r.Away))
            .Add("home", TeamToJson(r.Home))
            .Add("ctx", ContextToJson(r.Context));
        var dec = new JsonArray();
        foreach (var d in r.Decisions) dec.Add(DecisionToJson(d));
        o.Add("dec", dec);
        return o;
    }

    private static MatchReplayRecord? RecordFromJson(JsonObject o)
    {
        var rng = new List<ulong>();
        foreach (var v in o.Arr("rng").Items) rng.Add(((JsonNumber)v).AsULong());
        var decisions = new List<GameDecision>();
        foreach (var v in o.Arr("dec").Items) decisions.Add(DecisionFromJson(v.AsObject()));
        return new MatchReplayRecord
        {
            Id = o.Str("id") ?? "",
            EngineVersion = o.Str("ver") ?? "",
            Reasons = (NotableReason)o.Int("why"),
            Year = o.Int("y"),
            Kind = EnumJson.ReadNullable<TournamentKind>(o.Str("k")),
            Month = o.Int("m"),
            Day = o.Int("d"),
            RoundName = o.Str("rd") ?? "",
            MatchDay = o.Int("md"),
            RoundsRemaining = o.Int("rr"),
            AwaySchoolId = o.Int("aid"),
            HomeSchoolId = o.Int("hid"),
            AwayName = o.Str("an") ?? "",
            HomeName = o.Str("hn") ?? "",
            AwayRuns = o.Int("ar"),
            HomeRuns = o.Int("hr"),
            MercyEnded = o.Bool("mercy"),
            RngState = rng.ToArray(),
            Away = TeamFromJson(o.Obj("away")),
            Home = TeamFromJson(o.Obj("home")),
            Context = ContextFromJson(o.Obj("ctx")),
            Decisions = decisions,
        };
    }

    // ===== チーム =====

    private static JsonObject TeamToJson(ReplayTeamSpec spec)
    {
        var team = spec.Team;
        var table = new List<Player>();
        var index = new Dictionary<Player, int>(ByReference.Instance);
        int Idx(Player p)
        {
            if (index.TryGetValue(p, out var i)) return i;
            index[p] = table.Count;
            table.Add(p);
            return table.Count - 1;
        }

        var order = IndexArray(team.BattingOrder, Idx);
        var reserves = IndexArray(team.Reserves, Idx);
        var sp = team.StartingPitcher is { } s ? Idx(s) : -1;
        var captain = team.Captain is { } c ? Idx(c) : -1;

        var players = new JsonArray();
        var recovery = new JsonArray();
        var prior = new JsonArray();
        foreach (var p in table)
        {
            players.Add(PlayerToJson(p));
            recovery.Add(spec.PitchRecovery is not null && spec.PitchRecovery.TryGetValue(p, out var rv)
                ? JsonValue.Of(rv) : JsonNull.Instance);
            prior.Add(spec.PriorWeekPitches is not null && spec.PriorWeekPitches.TryGetValue(p, out var pv)
                ? JsonValue.Of(pv) : JsonNull.Instance);
        }

        var o = new JsonObject()
            .Add("name", team.Name)
            .Add("players", players)
            .Add("order", order)
            .Add("reserves", reserves)
            .Add("pslot", team.PitcherSlot)
            .Add("dh", team.DhSlot)
            .Add("sp", sp)
            .Add("cap", captain)
            .Add("sense", team.ManagerTacticalSense is int ts ? JsonValue.Of(ts) : JsonNull.Instance)
            .Add("relief", team.RelieveMarginFactor)
            .Add("momentum", team.MomentumLevel)
            .Add("morale", team.Morale is double mo ? JsonValue.Of(mo) : JsonNull.Instance)
            .Add("xp", team.ExtraPressure)
            .Add("cohesion", team.CohesionDelta);

        var battery = new JsonArray();
        if (team.BatteryInnings is { } bi)
            foreach (var kv in bi)
                battery.Add(Row(JsonValue.Of(kv.Key.PitcherId), JsonValue.Of(kv.Key.CatcherId), JsonValue.Of(kv.Value)));
        o.Add("battery", team.BatteryInnings is null ? JsonNull.Instance : battery);

        if (team.PromiseGuard is { } g)
            o.Add("guard", new JsonObject()
                .Add("pinch", IntArray(g.NoPinchHitForIds)).Add("bunt", IntArray(g.NoBuntIds))
                .Add("entry", IntArray(g.NoEntryIds)).Add("pitch", IntArray(g.NoPitchIds)));
        else o.Add("guard", JsonNull.Instance);

        if (spec.PitchRecovery is not null) o.Add("recovery", recovery);
        if (spec.PriorWeekPitches is not null) o.Add("prior", prior);

        if (spec.Brain is { } b)
        {
            var traits = new JsonArray();
            foreach (var t in b.Traits) traits.Add(EnumJson.Write(t));
            o.Add("brain", new JsonObject()
                .Add("sense", b.Profile.TacticalSense).Add("tier", b.Profile.TierRank)
                .Add("style", EnumJson.Write(b.Profile.Style)).Add("traits", traits));
        }
        return o;
    }

    private static ReplayTeamSpec TeamFromJson(JsonObject o)
    {
        var table = new List<Player>();
        foreach (var v in o.Arr("players").Items) table.Add(PlayerFromJson(v.AsObject()));

        List<Player> Pick(JsonArray a)
        {
            var list = new List<Player>(a.Count);
            foreach (var v in a.Items) list.Add(table[((JsonNumber)v).AsInt()]);
            return list;
        }

        var spIdx = o.Int("sp");
        var capIdx = o.Int("cap");
        var team = new Team
        {
            Name = o.Str("name") ?? "",
            BattingOrder = Pick(o.Arr("order")),
            Reserves = Pick(o.Arr("reserves")),
            PitcherSlot = o.Int("pslot"),
            DhSlot = o.Int("dh"),
            StartingPitcher = spIdx >= 0 ? table[spIdx] : null,
            Captain = capIdx >= 0 ? table[capIdx] : null,
            ManagerTacticalSense = o.IsNull("sense") ? (int?)null : o.Int("sense"),
            RelieveMarginFactor = o.Double("relief"),
            MomentumLevel = o.Int("momentum"),
            Morale = o.IsNull("morale") ? (double?)null : o.Double("morale"),
            ExtraPressure = o.Int("xp"),
            CohesionDelta = o.Double("cohesion"),
            BatteryInnings = o.IsNull("battery") ? null : BatteryFromJson(o.Arr("battery")),
            PromiseGuard = o.IsNull("guard") ? null : GuardFromJson(o.Obj("guard")),
        };

        Dictionary<Player, double>? recovery = null;
        if (o.Has("recovery"))
        {
            recovery = new Dictionary<Player, double>();
            var a = o.Arr("recovery");
            for (var i = 0; i < table.Count; i++)
                if (a[i] is JsonNumber n) recovery[table[i]] = n.AsDouble();
        }
        Dictionary<Player, int>? prior = null;
        if (o.Has("prior"))
        {
            prior = new Dictionary<Player, int>();
            var a = o.Arr("prior");
            for (var i = 0; i < table.Count; i++)
                if (a[i] is JsonNumber n) prior[table[i]] = n.AsInt();
        }

        ReplayBrainSpec? brain = null;
        if (o.Has("brain"))
        {
            var b = o.Obj("brain");
            var traits = new List<ManagerTrait>();
            foreach (var v in b.Arr("traits").Items) traits.Add(EnumJson.Read<ManagerTrait>(((JsonString)v).Value));
            brain = new ReplayBrainSpec(
                new AiProfile(b.Int("sense"), b.Int("tier"), EnumJson.Read<SchoolStyle>(b.Str("style"))), traits);
        }
        return new ReplayTeamSpec(team, brain, recovery, prior);
    }

    private static JsonArray IndexArray(IReadOnlyList<Player> players, Func<Player, int> idx)
    {
        var a = new JsonArray();
        foreach (var p in players) a.Add(JsonValue.Of(idx(p)));
        return a;
    }

    private static JsonArray IntArray(IEnumerable<int> values)
    {
        var a = new JsonArray();
        foreach (var v in values) a.Add(JsonValue.Of(v));
        return a;
    }

    private static Dictionary<(int, int), double> BatteryFromJson(JsonArray a)
    {
        var d = new Dictionary<(int, int), double>();
        foreach (var v in a.Items)
        {
            var r = v.AsArray();
            d[(((JsonNumber)r[0]).AsInt(), ((JsonNumber)r[1]).AsInt())] = r[2].ToDouble();
        }
        return d;
    }

    private static MatchPromiseGuard GuardFromJson(JsonObject o)
    {
        List<int> Ints(string key)
        {
            var l = new List<int>();
            foreach (var v in o.Arr(key).Items) l.Add(((JsonNumber)v).AsInt());
            return l;
        }
        return new MatchPromiseGuard(Ints("pinch"), Ints("bunt"), Ints("entry"), Ints("pitch"));
    }

    // ===== 選手（PlayerSaves＋PlayerSaves が持たない試合に効く値） =====

    private static JsonObject PlayerToJson(Player p)
    {
        var o = PlayerSaves.ToJson(p);
        // 既定は +∞（経験は十分）＝JSON にできないので null で表す。
        o.Add("leftExpPa", double.IsPositiveInfinity(p.LeftExperiencePa) ? JsonNull.Instance : JsonValue.Of(p.LeftExperiencePa));
        o.Add("fbp", p.FieldingByPosition is null ? JsonNull.Instance : IntArray(p.FieldingByPosition));
        o.Add("apt", p.PositionAptitudes is null ? JsonNull.Instance : IntArray(p.PositionAptitudes));
        return o;
    }

    private static Player PlayerFromJson(JsonObject o)
    {
        var p = PlayerSaves.PlayerFromJson(o);
        return p with
        {
            LeftExperiencePa = o.IsNull("leftExpPa") ? double.PositiveInfinity : o.Double("leftExpPa"),
            FieldingByPosition = o.IsNull("fbp") ? null : ReadInts(o.Arr("fbp")),
            PositionAptitudes = o.IsNull("apt") ? null : ReadInts(o.Arr("apt")),
        };
    }

    private static int[] ReadInts(JsonArray a)
    {
        var r = new int[a.Count];
        for (var i = 0; i < r.Length; i++) r[i] = ((JsonNumber)a[i]).AsInt();
        return r;
    }

    // ===== 試合設定 =====

    private static JsonObject ContextToJson(ReplayContextSpec c) => new JsonObject()
        .Add("cf", c.CenterFenceM).Add("lf", c.LeftFenceM).Add("rf", c.RightFenceM).Add("fh", c.FenceHeightM)
        .Add("indoor", c.Indoor).Add("base", c.BaseDistanceM).Add("back", c.BackstopM).Add("foul", c.FoulSideM)
        .Add("sid", c.StadiumId).Add("sname", c.StadiumName)
        .Add("reg", c.RegulationInnings).Add("max", c.MaxInnings).Add("mercy", c.MercyRuleEnabled)
        .Add("tb", c.TieBreakEnabled).Add("tbs", c.TieBreakStartInning).Add("wpl", c.WeeklyPitchLimit)
        .Add("psb", c.PressureStageBonus).Add("ret", c.RetirementOnLine).Add("pcp", c.PlayerControlsPitcherChanges)
        .Add("w", c.HasWeather)
        .Add("we", c.WeatherEnabled).Add("wm", c.WeatherMeanC).Add("ws", c.WeatherStdDevC)
        .Add("wlo", c.WeatherMinC).Add("whi", c.WeatherMaxC).Add("wr", c.WeatherRainProb);

    private static ReplayContextSpec ContextFromJson(JsonObject o) => new()
    {
        CenterFenceM = o.Double("cf"), LeftFenceM = o.Double("lf"), RightFenceM = o.Double("rf"),
        FenceHeightM = o.Double("fh"), Indoor = o.Bool("indoor"), BaseDistanceM = o.Double("base"),
        BackstopM = o.Double("back"), FoulSideM = o.Double("foul"),
        StadiumId = o.Str("sid"), StadiumName = o.Str("sname"),
        RegulationInnings = o.Int("reg"), MaxInnings = o.Int("max"), MercyRuleEnabled = o.Bool("mercy"),
        TieBreakEnabled = o.Bool("tb"), TieBreakStartInning = o.Int("tbs"), WeeklyPitchLimit = o.Int("wpl"),
        PressureStageBonus = o.Int("psb"), RetirementOnLine = o.Bool("ret"), PlayerControlsPitcherChanges = o.Bool("pcp"),
        HasWeather = o.Bool("w"),
        WeatherEnabled = o.Bool("we"), WeatherMeanC = o.Double("wm"), WeatherStdDevC = o.Double("ws"),
        WeatherMinC = o.Double("wlo"), WeatherMaxC = o.Double("whi"), WeatherRainProb = o.Double("wr"),
    };

    // ===== 采配の決定列 =====

    private static JsonObject DecisionToJson(GameDecision d)
    {
        var o = new JsonObject()
            .Add("at", d.AtStep).Add("k", EnumJson.Write(d.Kind)).Add("away", d.OffenseIsAway).Add("bench", d.BenchIndex);
        if (d.Batting is { } b) o.Add("batting", EnumJson.Write(b));
        if (d.Policy is { } po) o.Add("policy", EnumJson.Write(po));
        if (d.Gear is { } g) o.Add("gear", EnumJson.Write(g));
        if (d.PitchIndex != 0) o.Add("pitch", d.PitchIndex);
        if (d.TargetIndex != 0) o.Add("target", d.TargetIndex);
        if (d.At is { } at) o.Add("pos", EnumJson.Write(at));
        if (d.Approach is { } ap) o.Add("approach", EnumJson.Write(ap));
        if (d.Sign is { } sg) o.Add("sign", EnumJson.Write(sg));
        if (d.Start is { } st) o.Add("start", EnumJson.Write(st));
        if (d.StealTarget != StealTarget.Second) o.Add("steal", EnumJson.Write(d.StealTarget));
        if (d.Infield is { } inf) o.Add("infield", EnumJson.Write(inf));
        if (d.Outfield is { } outf) o.Add("outfield", EnumJson.Write(outf));
        if (d.On) o.Add("on", true);
        if (d.AsOffense is { } ao) o.Add("asOff", ao);
        if (d.BuntShift is { } bs) o.Add("bunt", bs);
        return o;
    }

    private static GameDecision DecisionFromJson(JsonObject o) => new(
        o.Int("at"), EnumJson.Read<GameDecisionKind>(o.Str("k")), o.Bool("away"), o.Int("bench"),
        Batting: o.Has("batting") ? EnumJson.Read<PitchBattingOverride>(o.Str("batting")) : null,
        Policy: o.Has("policy") ? EnumJson.Read<PitchPolicy>(o.Str("policy")) : null,
        Gear: o.Has("gear") ? EnumJson.Read<PitcherGear>(o.Str("gear")) : null,
        PitchIndex: o.Has("pitch") ? o.Int("pitch") : 0,
        TargetIndex: o.Has("target") ? o.Int("target") : 0,
        At: o.Has("pos") ? EnumJson.Read<FieldPosition>(o.Str("pos")) : null,
        Approach: o.Has("approach") ? EnumJson.Read<BattingApproach>(o.Str("approach")) : null,
        Sign: o.Has("sign") ? EnumJson.Read<OffensiveSign>(o.Str("sign")) : null,
        Start: o.Has("start") ? EnumJson.Read<StartType>(o.Str("start")) : null,
        StealTarget: o.Has("steal") ? EnumJson.Read<StealTarget>(o.Str("steal")) : StealTarget.Second,
        Infield: o.Has("infield") ? EnumJson.Read<DefenseDepth>(o.Str("infield")) : null,
        Outfield: o.Has("outfield") ? EnumJson.Read<DefenseDepth>(o.Str("outfield")) : null,
        On: o.Has("on") && o.Bool("on"),
        AsOffense: o.Has("asOff") ? o.Bool("asOff") : null,
        BuntShift: o.Has("bunt") ? o.Bool("bunt") : null);

    private static JsonArray Row(params JsonValue[] items)
    {
        var a = new JsonArray();
        foreach (var i in items) a.Add(i);
        return a;
    }

    private sealed class ByReference : IEqualityComparer<Player>
    {
        public static readonly ByReference Instance = new();
        public bool Equals(Player? x, Player? y) => ReferenceEquals(x, y);
        public int GetHashCode(Player obj) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(obj);
    }
}
