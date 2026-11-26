using System.Collections.Generic;

namespace KokoSim.Engine.Nation.Tournaments;

/// <summary>Fork キーが分岐する親ストリームの種別。同じ親から分岐するキー同士だけが衝突しうる。</summary>
public enum ForkScope
{
    /// <summary>夏の全国裏試合の基準乱数（区画ごとに分岐）。</summary>
    SummerRoot,
    /// <summary>秋季県大会の基準乱数（県ごとに分岐）。</summary>
    AutumnRoot,
    /// <summary>秋季地区大会の基準乱数（地区ごとに分岐）。</summary>
    RegionalRoot,
    /// <summary>1つの大会（勝ち抜き・リーグ・地区大会の1地区）が持つ乱数から分岐するもの。</summary>
    Tournament,
    /// <summary>Fork ではなく <c>new Xoshiro256Random(seed)</c> のシードへ混ぜるもの（エース温存の抽選）。</summary>
    Seed,
}

/// <summary>
/// Fork キー1つの定義。<see cref="VariableMask"/> は呼び出し側が XOR で混ぜる値（校ID・ラウンド・スロット等）が
/// 取りうるビット範囲＝ここが重なると別の呼び出しが同じストリームIDを引きうる。
/// </summary>
public sealed record ForkKey(string Name, ulong Base, ulong VariableMask, ForkScope Scope);

/// <summary>
/// Nation の大会進行が使う Fork 乱数キーの一覧（単一ソース）。キーを足すときは必ずここへ載せる。
/// <see cref="CanCollide"/> が同じ <see cref="ForkScope"/> 内でIDの重なりを検査する（テストが全組を確かめる）。
/// 試合内（Match/）のストリームIDは親が試合ごとに別なので対象外。
/// </summary>
public static class TournamentForkKeys
{
    // --- 基準乱数から県/区画/地区ごとに分岐するもの ---
    public const ulong SummerPrefecture = 0xF00D_0000UL;
    public const ulong AutumnPrefecture = 0xACE0_0000UL;
    public const ulong RegionalRegion = 0xB10C_0000UL;

    // --- 1大会の乱数から分岐するもの ---
    public const ulong TournamentDraw = 0x44_52_41_57UL;      // 'DRAW'
    public const ulong RegionalPlayoffDraw = 0x50_4C_59_44UL; // 'PLYD'
    public const ulong BlockDraw = 0xB10C_D2A3UL;
    public const ulong RegionalRecorder = 0xB10C_57AD_0000UL;
    public const ulong AceRestSalt = 0xACE5_5A17UL;
    public const ulong KnockoutBackground = 0xB6B6_0000UL;
    public const ulong KnockoutPlayer = 0xA5A5_0000UL;
    public const ulong LeagueBackground = 0xB7B7_0000UL;
    public const ulong LeaguePlayer = 0xA7A7_0000UL;

    // --- Fork ではなくシードへ混ぜるもの ---
    public const ulong AceRestSeed = 0xACE5_0000UL;

    // 呼び出し側が混ぜる値の上限（県ID<65536・ラウンド/再試合回<256・スロット/カード<65536）から決めたビット範囲。
    private const ulong PrefectureIdBits = 0x0000_FFFFUL;
    private const ulong SplitBits = 0x0300_0000UL;
    private const ulong CardBits = 0x0000_FF00_00FF_FFFFUL;

    public static IReadOnlyList<ForkKey> All { get; } = new ForkKey[]
    {
        new(nameof(SummerPrefecture), SummerPrefecture, PrefectureIdBits | SplitBits, ForkScope.SummerRoot),
        new(nameof(AutumnPrefecture), AutumnPrefecture, PrefectureIdBits, ForkScope.AutumnRoot),
        new(nameof(RegionalRegion), RegionalRegion, 0xFFFF_FFFFUL, ForkScope.RegionalRoot),
        new(nameof(TournamentDraw), TournamentDraw, 0, ForkScope.Tournament),
        new(nameof(RegionalPlayoffDraw), RegionalPlayoffDraw, 0, ForkScope.Tournament),
        new(nameof(BlockDraw), BlockDraw, 0, ForkScope.Tournament),
        new(nameof(RegionalRecorder), RegionalRecorder, 0, ForkScope.Tournament),
        new(nameof(AceRestSalt), AceRestSalt, 0, ForkScope.Tournament),
        new(nameof(KnockoutBackground), KnockoutBackground, CardBits, ForkScope.Tournament),
        new(nameof(KnockoutPlayer), KnockoutPlayer, CardBits, ForkScope.Tournament),
        new(nameof(LeagueBackground), LeagueBackground, CardBits, ForkScope.Tournament),
        new(nameof(LeaguePlayer), LeaguePlayer, CardBits, ForkScope.Tournament),
        new(nameof(AceRestSeed), AceRestSeed, ulong.MaxValue, ForkScope.Seed),
    };

    /// <summary>同じ親から分岐する2つのキーが、混ぜる値しだいで同じIDになりうるか（固定ビットの差が両者の可変ビットに収まる）。</summary>
    public static bool CanCollide(ForkKey a, ForkKey b)
        => a.Scope == b.Scope && ((a.Base ^ b.Base) & ~(a.VariableMask | b.VariableMask)) == 0;

    /// <summary>北海道・東京の分割区画は分割位置を足して別ストリーム化する（無分割の県は県IDだけ＝従来キーのまま）。</summary>
    public static ulong SummerPrefectureStream(int prefectureId, int? split)
    {
        var splitBit = split is { } sp ? (uint)(sp + 1) << 24 : 0u;
        return SummerPrefecture ^ (uint)prefectureId ^ splitBit;
    }

    public static ulong AutumnPrefectureStream(int prefectureId) => AutumnPrefecture ^ (uint)prefectureId;

    /// <summary>カード単位（ラウンド×スロット×引き分け再試合回）の Fork ID。再試合回は上位ビットへ載せる＝0回目はラウンド×スロットだけのIDと一致。</summary>
    public static ulong CardStream(ulong key, int round, int slot, int attempt = 0)
        => key ^ ((ulong)(uint)round << 16) ^ (uint)slot ^ ((ulong)(uint)attempt << 40);
}
