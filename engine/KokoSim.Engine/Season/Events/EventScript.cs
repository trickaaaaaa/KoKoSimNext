using System.Collections.Generic;

namespace KokoSim.Engine.Season.Events;

/// <summary>
/// 会話1行（設計書03 §5・issue #609）。<paramref name="Who"/> は<b>役割ID</b>で、立ち絵スロットと
/// 既定の話者名を同時に決める（manager / player / coach / ob / scout / local …）。
/// <paramref name="Name"/> は話者名の上書き（空なら役割の既定名。差し込み語 <c>{player}</c> 等を含んでよい）。
/// </summary>
/// <param name="Who">役割ID（立ち絵と既定の話者名）。</param>
/// <param name="Name">話者名の上書き（空＝役割の既定名）。</param>
/// <param name="Say">本文（差し込み語を含んでよい）。</param>
public sealed record EventLine(string Who, string Name, string Say);

/// <summary>
/// 会話まるごと1通り（issue #609）。文言のバリエーションは<b>会話単位</b>で持つ
/// （行ごとに独立抽選すると前後が噛み合わない文が出る＝2026-08-13 オーナー決定）。
/// </summary>
public sealed record EventScript(IReadOnlyList<EventLine> Lines)
{
    /// <summary>1行だけの会話（旧 <c>text:</c> しか持たないイベントの後方互換）。</summary>
    public static EventScript Single(string who, string text) => new(new[] { new EventLine(who, "", text) });
}

/// <summary>
/// 役割ID（立ち絵スロットと既定の話者名の単一ソース）。data/events.yaml の <c>who:</c> に書く語。
/// 未知の語は <see cref="Unknown"/> 扱いにして落とさない（データ追記でコードを触らせない）。
/// </summary>
public static class EventRoles
{
    public const string Manager = "manager";
    public const string Player = "player";
    public const string Coach = "coach";
    public const string Ob = "ob";
    public const string Scout = "scout";
    public const string Local = "local";
    public const string Captain = "captain";
    public const string Narration = "narration";
    public const string Unknown = "unknown";

    /// <summary>役割の既定の話者名（<c>name:</c> 未指定のとき使う）。</summary>
    public static string DefaultName(string who) => who switch
    {
        Manager => "監督",
        Player => "部員",
        Coach => "コーチ",
        Ob => "OB",
        Scout => "スカウト",
        Local => "地域の人",
        Captain => "主将",
        Narration => "",
        _ => "",
    };

    /// <summary>ナレーション（立ち絵を出さず地の文として見せる行）か。</summary>
    public static bool IsNarration(string who) => who == Narration;
}
