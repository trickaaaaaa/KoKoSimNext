using System;
using KokoSim.Engine.Saves.Json;

namespace KokoSim.Engine.Saves;

/// <summary>
/// 列挙型は<b>名前</b>で往復する（issue #507）。int で焼くと列挙メンバの並べ替えに弱いが、名前なら並びに依らない。
/// 配列添字（<c>_level[(int)k]</c> 等）だけは並び順に依存するので位置で焼く（呼び出し側で長さを固定する）。
/// 未知名（新しい版のメンバを旧リーダが読む等）は例外にせず既定値へ落とす＝版差で例外死しない
/// （完了条件「版が違うセーブを読んだとき例外で落ちない」に整合）。
/// </summary>
internal static class EnumJson
{
    public static JsonValue Write<T>(T value) where T : struct, Enum
        => JsonValue.Of(value.ToString());

    public static JsonValue WriteNullable<T>(T? value) where T : struct, Enum
        => value is null ? JsonNull.Instance : JsonValue.Of(value.Value.ToString());

    public static T Read<T>(string? name) where T : struct, Enum
        => name is not null && Enum.TryParse<T>(name, out var v) ? v : default;

    public static T? ReadNullable<T>(string? name) where T : struct, Enum
        => name is not null && Enum.TryParse<T>(name, out var v) ? v : (T?)null;
}
