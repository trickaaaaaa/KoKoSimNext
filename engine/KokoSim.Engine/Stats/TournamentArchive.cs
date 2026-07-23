using System.Collections.Generic;

namespace KokoSim.Engine.Stats;

/// <summary>
/// 大会別成績の枠（issue #77 決定 2026-07-21・設計書05 §1.5）。
/// 夏は県予選と甲子園を別枠、秋は県/地区/神宮を1枠に合算、春はセンバツ独立。
/// 現状の大会フローは夏(県)=SummerPref・秋(県)=Autumn のみ流れる。SummerKoshien/Senbatsu の
/// タクソノミは前方互換で用意し、甲子園進出・センバツ・秋多段（tournament Phase 3）で populate する。
/// </summary>
public enum TournamentSlot
{
    SummerPref,     // 夏の県大会
    SummerKoshien,  // 夏の甲子園（別枠）
    Autumn,         // 秋（県・地区・神宮を合算）
    Senbatsu,       // 春のセンバツ（独立枠）
}

/// <summary>大会別アーカイブのキー（学年×大会枠, issue #77）。Grade は当時の学年 1〜3。</summary>
public readonly record struct TournamentArchiveKey(int Grade, TournamentSlot Slot);

/// <summary>アーカイブ内の1選手ぶん（当時の背番号＋累積打撃/投手, issue #77）。</summary>
public sealed class ArchivedPlayerStats
{
    /// <summary>その大会当時の背番号（設計書06 §3.3b）。</summary>
    public int UniformNumber { get; set; }
    public BattingStatLine Batting { get; } = new();
    public PitchingStatLine Pitching { get; } = new();
}

/// <summary>
/// 大会別成績アーカイブ（issue #77）。学年×大会枠 → (選手ID → 当時の成績＋背番号)。
/// 大会終了時に今大会スコープの成績を枠へ載せ、既存枠には合算（秋の県/地区/神宮）。
/// 純データ・決定論・UnityEngine 非依存。GameSession がセーブ横断状態として保持する。
/// </summary>
public sealed class TournamentArchive
{
    private readonly Dictionary<TournamentArchiveKey, Dictionary<int, ArchivedPlayerStats>> _slots = new();

    /// <summary>登録済みの枠キー（新しい/古いの順は呼び出し側で整える）。</summary>
    public IEnumerable<TournamentArchiveKey> Keys => _slots.Keys;

    /// <summary>指定枠の成績（選手ID→当時の成績）。未登録なら null。</summary>
    public IReadOnlyDictionary<int, ArchivedPlayerStats>? Slot(TournamentArchiveKey key)
        => _slots.TryGetValue(key, out var m) ? m : null;

    /// <summary>指定枠・指定選手の当時の成績（未登録なら null）。</summary>
    public ArchivedPlayerStats? Get(TournamentArchiveKey key, int sourceId)
        => _slots.TryGetValue(key, out var m) && m.TryGetValue(sourceId, out var a) ? a : null;

    /// <summary>
    /// 大会終了時に今大会スコープ（<paramref name="currentTournament"/>）の成績を枠へ載せる。
    /// 各選手は「当時の学年 × <paramref name="slot"/>」の枠へ振る（1つの大会に1〜3年生が混在するため、
    /// キーの学年は選手ごと）。既存枠がある場合は合算する（秋の県/地区/神宮を「n年秋」1枠にまとめる）。
    /// <paramref name="playerInfo"/> は sourceId→(当時の学年, 当時の背番号)。学年不明の選手はスキップ。
    /// </summary>
    public void Archive(TournamentSlot slot, StatBook currentTournament,
        IReadOnlyDictionary<int, (int Grade, int UniformNumber)> playerInfo)
    {
        foreach (var kv in currentTournament.Players)
        {
            var id = kv.Key;
            if (!playerInfo.TryGetValue(id, out var info) || info.Grade < 1) continue;

            var key = new TournamentArchiveKey(info.Grade, slot);
            if (!_slots.TryGetValue(key, out var slotMap)) { slotMap = new Dictionary<int, ArchivedPlayerStats>(); _slots[key] = slotMap; }
            if (!slotMap.TryGetValue(id, out var a)) { a = new ArchivedPlayerStats(); slotMap[id] = a; }

            a.Batting.Merge(kv.Value.Batting);
            a.Pitching.Merge(kv.Value.Pitching);
            if (info.UniformNumber > 0) a.UniformNumber = info.UniformNumber; // 0=未割当は温存
        }
    }
}
