using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using KokoSim.Engine.Saves;
using UnityEngine;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// セーブ/ロードの Shell 側入口（issue #507 F0）。「形」（スキーマ・往復）は engine の <see cref="SaveCodec"/> が
    /// 担い、ここは全 static サービスからの収集・復元と、gzip 圧縮＋<c>Application.persistentDataPath</c> への
    /// ファイル IO だけを持つ（設計: 書き出しは Shell・形は engine）。
    ///
    /// <para>スロット管理UI・タイトル接続は F1（issue #508）、オートセーブ・ゲーム中のセーブ導線・
    /// 「タイトルへ戻る」導線は F2（issue #509）でこの土台（Capture/Restore/Reset＋ファイル読み書き）の上に配線した。</para>
    ///
    /// <para><b>大会中はセーブ不可</b>（<see cref="GameMode.Tournament"/>）。進行体 <c>TournamentRunner</c> は
    /// 復元不能（進行中ブラケット・IRandomSource 内部状態・観戦中の MatchProgression を持つ）＝#483 論点3の結論。
    /// オートセーブも大会中は取らない前提。</para>
    /// </summary>
    public static class SaveGameIO
    {
        public const string Extension = ".sav";

        /// <summary>
        /// 大会中は保存できない（進行体が復元不能）。加えて、自校敗退直後は大会モードを抜けていても
        /// 残りブラケットの背景消化（<see cref="BracketFinishSim"/>, issue #529）が終わるまで通算戦績
        /// （<see cref="GameSession.Records"/> への FoldTournament）が未確定＝ここで保存すると今大会の
        /// 勝敗が戦績から抜け落ちたセーブができてしまうため、完了まで待つ。UI 側はこれを見てセーブを抑止する（F1）。
        /// </summary>
        public static bool CanSave => GameSession.Current.Mode == GameMode.Normal && !GameSession.Current.BracketFoldPending;

        /// <summary>全 static サービスから現在状態を集めて DTO を組む。</summary>
        public static SaveState Capture()
        {
            var s = new SaveState();
            GameSeed.Capture(s);
            GameClock.Capture(s);
            RosterService.Capture(s);
            ManagerService.Capture(s);
            NationService.Capture(s);
            GameSession.Current.Capture(s);
            NewTeamService.Capture(s);
            TeamColorProvider.Capture(s);
            return s;
        }

        /// <summary>DTO を全 static サービスへ押し戻す（種→暦→部員→監督→全国→セッション→新チーム→色）。</summary>
        public static void Restore(SaveState s)
        {
            GameSeed.Restore(s);
            GameClock.Restore(s);
            RosterService.Restore(s);
            ManagerService.Restore(s);
            NationService.Restore(s);
            GameSession.Current.Restore(s);
            NewTeamService.Restore(s);
            TeamColorProvider.Restore(s);
        }

        /// <summary>新規ゲーム開始のための完全リセット（全 static サービスを初期状態へ）。母種は次の非決定採取に任せる。</summary>
        public static void ResetForNewGame()
        {
            RosterService.Reset();
            ManagerService.Reset();
            NationService.Reset();
            GameSession.Current.ResetForNewGame();
            NewTeamService.Clear();
            TeamColorProvider.Clear();
            GameClock.Reset();
        }

        /// <summary>既定のセーブ先（persistentDataPath/kokosim/&lt;slot&gt;.sav）。スロットUI接続は F1。</summary>
        public static string PathForSlot(string slot = "slot0")
            => Path.Combine(Application.persistentDataPath, "kokosim", slot + Extension);

        /// <summary>現在状態を JSON+gzip でファイルへ書き出す。大会中は書かず false を返す。</summary>
        public static bool Save(string path)
        {
            if (!CanSave) return false;
            var raw = Encoding.UTF8.GetBytes(SaveCodec.Serialize(Capture()));
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using var fs = File.Create(path);
            using var gz = new GZipStream(fs, System.IO.Compression.CompressionLevel.Optimal);
            gz.Write(raw, 0, raw.Length);
            return true;
        }

        /// <summary>ファイルを読み、gunzip→復元して全サービスへ適用する。読めなければ false（例外で落ちない）。</summary>
        public static bool Load(string path, out string error)
        {
            if (!TryReadText(path, out var json, out error)) return false;
            if (!SaveCodec.TryDeserialize(json, out var state, out error)) return false;
            Restore(state);
            return true;
        }

        /// <summary>
        /// ファイルを読み gunzip して JSON テキストを返す（復元はしない）。読めなければ false（例外で落ちない）。
        /// スロット一覧のヘッダ読み取り（<see cref="SaveSlots.ReadInfo"/>）と <see cref="Load"/> の共通経路。
        /// </summary>
        public static bool TryReadText(string path, out string json, out string error)
        {
            json = "";
            error = "";
            try
            {
                if (!File.Exists(path)) { error = "セーブが無い"; return false; }
                using var fs = File.OpenRead(path);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var reader = new StreamReader(gz, Encoding.UTF8);
                json = reader.ReadToEnd();
                return true;
            }
            catch (Exception e)
            {
                error = e.Message;
                Debug.LogWarning($"[セーブ] 読み取り失敗（{path}）: {e.Message}");
                return false;
            }
        }
    }
}
