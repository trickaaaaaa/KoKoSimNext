using System;
using System.IO;
using KokoSim.Engine.Saves;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// セーブスロット一覧行の表示用スナップショット（issue #508）。存在しない/壊れているスロットも
    /// 例外にせずこの形で表す（<see cref="Exists"/>/<see cref="Corrupt"/> で区別）。
    /// </summary>
    public readonly struct SaveSlotInfo
    {
        public string SlotId { get; }
        public bool IsAuto { get; }
        public bool Exists { get; }
        public bool Corrupt { get; }
        public int Week { get; }
        public int YearIndex { get; }
        public DateTime SavedAt { get; }

        public SaveSlotInfo(string slotId, bool isAuto, bool exists, bool corrupt, int week, int yearIndex, DateTime savedAt)
        {
            SlotId = slotId;
            IsAuto = isAuto;
            Exists = exists;
            Corrupt = corrupt;
            Week = week;
            YearIndex = yearIndex;
            SavedAt = savedAt;
        }
    }

    /// <summary>
    /// セーブスロットの一覧・メタ読み取り・削除（issue #508 F1）。手動3枠＋オート1枠を固定で持つ。
    /// 「形」は <see cref="SaveCodec"/>、ファイルIOは <see cref="SaveGameIO"/> に任せ、ここはスロットの
    /// 並び・存在確認・削除・保存/上書きの入口だけを持つ。
    ///
    /// <para><b>保存/上書きは今回 UI に出さない</b>（オーナー判断・issue #508）。タイトル経由で開いた今回は
    /// 「今保存すべき進行中のゲーム状態」が無いため。サービス側のメソッドは #509（ホーム画面のメニュー入口）で
    /// 使えるようここに用意しておく。</para>
    /// </summary>
    public static class SaveSlots
    {
        public const string AutoSlotId = "auto";

        /// <summary>手動スロットID（3枠）。</summary>
        public static readonly string[] ManualSlotIds = { "manual1", "manual2", "manual3" };

        /// <summary>一覧の表示順（手動1〜3→オート）。</summary>
        public static readonly string[] AllSlotIds = { "manual1", "manual2", "manual3", AutoSlotId };

        public static bool IsAuto(string slotId) => slotId == AutoSlotId;

        public static string PathFor(string slotId) => SaveGameIO.PathForSlot(slotId);

        /// <summary>どれか1枠でも読めるセーブがあるか（タイトル「つづきから」の活性判定）。</summary>
        public static bool AnySaveExists()
        {
            foreach (var id in AllSlotIds)
                if (File.Exists(PathFor(id))) return true;
            return false;
        }

        /// <summary>スロット1枠ぶんの一覧行メタを読む（存在しない/壊れていても例外を投げない）。</summary>
        public static SaveSlotInfo ReadInfo(string slotId)
        {
            var path = PathFor(slotId);
            if (!File.Exists(path))
                return new SaveSlotInfo(slotId, IsAuto(slotId), exists: false, corrupt: false, week: 0, yearIndex: 0, savedAt: default);

            var savedAt = File.GetLastWriteTime(path);
            if (!SaveGameIO.TryReadText(path, out var json, out _) || SaveCodec.PeekHeader(json) is not { } header
                || header.Version < 1 || header.Version > SaveCodec.CurrentVersion)
                return new SaveSlotInfo(slotId, IsAuto(slotId), exists: true, corrupt: true, week: 0, yearIndex: 0, savedAt);

            return new SaveSlotInfo(slotId, IsAuto(slotId), exists: true, corrupt: false, header.Week, header.YearIndex, savedAt);
        }

        /// <summary>スロットへ現在状態を保存する（今回 UI からは呼ばない。#509 で使う）。</summary>
        public static bool Save(string slotId) => SaveGameIO.Save(PathFor(slotId));

        /// <summary>スロットから読み込んで全サービスへ復元する。</summary>
        public static bool Load(string slotId, out string error) => SaveGameIO.Load(PathFor(slotId), out error);

        /// <summary>スロットを削除する（存在しなければ何もしない）。</summary>
        public static void Delete(string slotId)
        {
            var path = PathFor(slotId);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
