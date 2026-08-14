using System.Collections.Generic;
using KokoSim.Engine.Saves.Json;

namespace KokoSim.Engine.Saves;

/// <summary>
/// イベント発火の状態（issue #609・セーブ v9）。<see cref="Season.Events.EventScheduler"/> の
/// クールダウンと年次フラグ。
///
/// <para><b>載せないとロードでクールダウンが全部明ける</b>＝直前に見たイベントが即もう一度出る
/// （毎回セーブして開き直す遊び方だと、同じ会話ばかり出続ける形で壊れる）。
/// イベント表そのもの（data/events.yaml）は正典なので保存しない＝IDだけを持つ。</para>
///
/// <para>知らないIDが載っていても落とさない（表からイベントを消した進行を開いたとき、
/// そのIDのクールダウンが宙に浮くだけで害がない）。</para>
/// </summary>
public sealed class EventSchedulerSaveState
{
    /// <summary>通算経過週（クールダウンの基準。暦の週とは別物で、大会中の日送りでは進まない）。</summary>
    public int AbsoluteWeek { get; set; }

    /// <summary>イベントIDごとの最終発火週（通算週）。</summary>
    public Dictionary<string, int> LastFiredWeeks { get; set; } = new();

    /// <summary>立っている年次フラグ（「今年はもう出た」の印）。</summary>
    public List<string> AnnualFlags { get; set; } = new();

    /// <summary>常駐スケジューラから状態を写し取る（Shell の <c>Capture()</c> 用）。</summary>
    public static EventSchedulerSaveState From(Season.Events.EventScheduler scheduler)
    {
        var s = new EventSchedulerSaveState { AbsoluteWeek = scheduler.AbsoluteWeek };
        foreach (var kv in scheduler.LastFiredWeeks) s.LastFiredWeeks[kv.Key] = kv.Value;
        s.AnnualFlags.AddRange(scheduler.AnnualFlags);
        return s;
    }

    /// <summary>常駐スケジューラへ押し戻す（Shell の <c>Restore()</c> 用）。</summary>
    public void RestoreInto(Season.Events.EventScheduler scheduler)
        => scheduler.Restore(AbsoluteWeek, LastFiredWeeks, AnnualFlags);
}

/// <summary>
/// 期間つきの練習効率補正1本（issue #615・セーブ v10）。
/// 載せないと、強化指定を受けた直後にロードすると効果が消える＝起きたことが無かったことになる。
/// </summary>
public sealed class TrainingBoostSaveState
{
    /// <summary>由来のイベントID。</summary>
    public string Source { get; set; } = "";
    /// <summary>倍率（1.10＝＋10%）。</summary>
    public double Mult { get; set; } = 1.0;
    /// <summary>残り週数。</summary>
    public int WeeksLeft { get; set; }
    /// <summary>対象の選手Id（0＝部全体）。</summary>
    public int PlayerId { get; set; }
}

/// <summary>
/// <see cref="EventSchedulerSaveState"/> の JSON 往復（issue #609）。
/// null（＝旧セーブ・まだ一度も週を送っていない進行）も往復できる。
/// </summary>
internal static class EventSaves
{
    public static JsonValue ToJson(EventSchedulerSaveState? s)
    {
        if (s is null) return JsonNull.Instance;

        // イベントID昇順で固定（往復の再直列化を安定させる。Dictionary の列挙順に依存しない）。
        var ids = new List<string>(s.LastFiredWeeks.Keys);
        ids.Sort(System.StringComparer.Ordinal);
        var fired = new JsonArray();
        foreach (var id in ids)
        {
            fired.Add(new JsonObject().Add("id", id).Add("week", s.LastFiredWeeks[id]));
        }

        var flags = new List<string>(s.AnnualFlags);
        flags.Sort(System.StringComparer.Ordinal);
        var flagArr = new JsonArray();
        foreach (var f in flags) flagArr.Add(JsonValue.Of(f));

        return new JsonObject()
            .Add("absoluteWeek", s.AbsoluteWeek)
            .Add("lastFired", fired)
            .Add("annualFlags", flagArr);
    }

    public static EventSchedulerSaveState? FromJson(JsonValue? v)
    {
        if (v is null || v is JsonNull) return null;
        var o = v.AsObject();
        var s = new EventSchedulerSaveState { AbsoluteWeek = o.Int("absoluteWeek") };
        foreach (var item in o.Arr("lastFired").Items)
        {
            var e = item.AsObject();
            var id = e.Str("id");
            if (!string.IsNullOrEmpty(id)) s.LastFiredWeeks[id!] = e.Int("week");
        }
        foreach (var item in o.Arr("annualFlags").Items)
        {
            if (item is JsonString js && !string.IsNullOrEmpty(js.Value)) s.AnnualFlags.Add(js.Value);
        }
        return s;
    }

    // ===== 練習効率の補正（issue #615・v10） =====

    public static JsonArray BoostsToJson(List<TrainingBoostSaveState>? boosts)
    {
        var arr = new JsonArray();
        if (boosts is null) return arr;
        foreach (var b in boosts)
        {
            arr.Add(new JsonObject()
                .Add("source", b.Source ?? "")
                .Add("mult", b.Mult)
                .Add("weeks", b.WeeksLeft)
                .Add("player", b.PlayerId));
        }
        return arr;
    }

    public static List<TrainingBoostSaveState> BoostsFromJson(JsonArray arr)
    {
        var list = new List<TrainingBoostSaveState>(arr.Count);
        foreach (var v in arr.Items)
        {
            var o = v.AsObject();
            list.Add(new TrainingBoostSaveState
            {
                Source = o.Str("source") ?? "",
                Mult = o.Double("mult"),
                WeeksLeft = o.Int("weeks"),
                PlayerId = o.Int("player"),
            });
        }
        return list;
    }
}
