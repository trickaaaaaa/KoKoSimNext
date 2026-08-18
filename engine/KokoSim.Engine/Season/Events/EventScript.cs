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
    /// <summary>
    /// 結果行（issue event-result-line-in-dialogue・2026-08-19）。「{player} のミートが ＋5 上がった！」のように
    /// <b>実際に動いた数値</b>を会話の最終行として言い切る行。data/events.yaml には書かず、
    /// 効果を知っている呼び出し元（怪我・成長・資金…）が提示時に足す＝会話の文と数字が別々の場所で
    /// 作られてずれるのを防ぐ。立ち絵も話者名も無い（地の文と同じ扱い）。
    /// </summary>
    public const string Result = "result";
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
        Result => "",
        _ => "",
    };

    /// <summary>ナレーション（立ち絵を出さず地の文として見せる行）か。</summary>
    public static bool IsNarration(string who) => who == Narration;

    /// <summary>結果行（立ち絵なし・数値を言い切る強調行）か。</summary>
    public static bool IsResult(string who) => who == Result;

    /// <summary>立ち絵（話者）を出さない行か＝地の文と結果行。</summary>
    public static bool IsVoiceless(string who) => IsNarration(who) || IsResult(who);
}
