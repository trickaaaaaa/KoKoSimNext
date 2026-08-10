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
        public bool Exists { get; }
        public bool Corrupt { get; }
        public int Week { get; }
        public int YearIndex { get; }
        public DateTime SavedAt { get; }

        public SaveSlotInfo(string slotId, bool exists, bool corrupt, int week, int yearIndex, DateTime savedAt)
        {
            SlotId = slotId;
            Exists = exists;
            Corrupt = corrupt;
            Week = week;
            YearIndex = yearIndex;
            SavedAt = savedAt;
        }
    }

    /// <summary>
    /// セーブスロット選択画面（<c>SaveSlotsController</c>）をどこから開いたか（issue #509 ／ タブは #565）。
    /// 同じ1画面を3つの入口から開くため、呼び出し側が遷移前に必ずこれを設定する
    /// （既定 <see cref="Title"/>＝安全側フォールバック）。
    ///
    /// <list type="bullet">
    /// <item><see cref="Title"/> — タイトルの「つづきから」。読込／削除だけ。まだどのシェルサービスも
    /// 遅延初期化されていないので<b>共通トップバーを出せない</b>（値を引くと初期化が走ってしまう）</item>
    /// <item><see cref="Home"/> — ホームの ≡メニュー「セーブ」。保存も出す。トップバーは出さず
    /// 「◀ ホームへ戻る」で戻る（オーバーレイから開いた流れを保つ）</item>
    /// <item><see cref="Tab"/> — 共通ナビの「システム」タブ。保存も出す。<b>トップバー（タブ帯）を出す</b>
    /// ＝他のタブと同じ見え方にし、戻るボタンは出さない（移動はタブで行う）</item>
    /// </list>
    /// </summary>
    public enum SaveSlotsEntry { Title, Home, Tab }

    /// <summary>
    /// セーブスロットの一覧・メタ読み取り・削除（issue #508 F1）。<b>手動3枠だけ</b>を固定で持つ
    /// （オート枠は廃止・監督決定 2026-08-09。保存はプレイヤーの明示操作だけにする）。
    /// 「形」は <see cref="SaveCodec"/>、ファイルIOは <see cref="SaveGameIO"/> に任せ、ここはスロットの
    /// 並び・存在確認・削除・保存/上書きの入口だけを持つ。
    ///
    /// <para>保存/上書きはホーム画面のメニュー入口から開いたときだけ UI に出す（issue #509・
    /// <see cref="Entry"/> が <see cref="SaveSlotsEntry.Home"/> のとき）。タイトル経由（<see cref="SaveSlotsEntry.Title"/>）
    /// では「今保存すべき進行中のゲーム状態」が無いため出さない（オーナー判断・issue #508）。</para>
    /// </summary>
    public static class SaveSlots
    {
        /// <summary>直近の画面遷移元（<see cref="TitleController"/> / <c>HomeDashboardController</c> が
        /// <c>ShowDeferred("SaveSlots")</c> の直前に設定する）。</summary>
        public static SaveSlotsEntry Entry { get; set; } = SaveSlotsEntry.Title;

        /// <summary>一覧の表示順（手動1〜3）。オート枠は廃止した（監督決定 2026-08-09）。</summary>
        public static readonly string[] AllSlotIds = { "manual1", "manual2", "manual3" };

        /// <summary>旧オートセーブのファイル名（廃止前の生成物。<see cref="PurgeLegacyAutoSave"/> だけが使う）。</summary>
        private const string LegacyAutoSlotId = "auto";

        public static string PathFor(string slotId) => SaveGameIO.PathForSlot(slotId);

        /// <summary>
        /// 旧オート枠のファイルを消す（オートセーブ廃止・2026-08-09）。一覧に出ない枠のファイルが残ると
        /// 「消せないのに容量だけ食う」＋「つづきからが活性なのに一覧が空」になるため、
        /// 一覧・活性判定のどちらから入っても最初に片付ける。
        /// </summary>
        public static void PurgeLegacyAutoSave()
        {
            var path = PathFor(LegacyAutoSlotId);
            if (File.Exists(path)) File.Delete(path);
        }

        /// <summary>どれか1枠でも読めるセーブがあるか（タイトル「つづきから」の活性判定）。</summary>
        public static bool AnySaveExists()
        {
            PurgeLegacyAutoSave();
            foreach (var id in AllSlotIds)
                if (File.Exists(PathFor(id))) return true;
            return false;
        }

        /// <summary>スロット1枠ぶんの一覧行メタを読む（存在しない/壊れていても例外を投げない）。</summary>
        public static SaveSlotInfo ReadInfo(string slotId)
        {
            var path = PathFor(slotId);
            if (!File.Exists(path))
                return new SaveSlotInfo(slotId, exists: false, corrupt: false, week: 0, yearIndex: 0, savedAt: default);

            var savedAt = File.GetLastWriteTime(path);
            if (!SaveGameIO.TryReadText(path, out var json, out _) || SaveCodec.PeekHeader(json) is not { } header
                || header.Version < 1 || header.Version > SaveCodec.CurrentVersion)
                return new SaveSlotInfo(slotId, exists: true, corrupt: true, week: 0, yearIndex: 0, savedAt);

            return new SaveSlotInfo(slotId, exists: true, corrupt: false, header.Week, header.YearIndex, savedAt);
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
