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
    /// <para>スロット管理UI・タイトル接続は F1（issue #508）、ゲーム中のセーブ導線・「タイトルへ戻る」導線は
    /// F2（issue #509）でこの土台（Capture/Restore/Reset＋ファイル読み書き）の上に配線した。
    /// <b>オートセーブは廃止</b>（監督決定 2026-08-09）＝保存はプレイヤーが手動3枠へ行う明示操作だけ。</para>
    ///
    /// <para><b>大会中でも試合と試合の間なら保存できる</b>（issue #565 F1。#483 論点3「大会中は不可」の撤回）。
    /// 進行体 <c>TournamentRunner</c> の可変状態はセーブ DTO に載る。<b>観戦中（MatchProgression を持つ）の
    /// 中断セーブだけは対象外</b>で、これは F2。</para>
    /// </summary>
    public static class SaveGameIO
    {
        public const string Extension = ".sav";

        /// <summary>
        /// 保存できる局面か。大会中でも「試合と試合の間」なら保存できる（issue #565 F1）が、
        /// <b>まだ確定していない副作用を抱えた局面では保存しない</b>。保存すると復元後にその副作用
        /// （週送り・通知・戦績の畳み込み）が失われ、進行が詰まったり戦績が欠けたりする。
        ///
        /// <list type="bullet">
        /// <item>観戦中の自校戦がある（<c>Runner.CanCaptureState</c>）＝F2 の対象</item>
        /// <item>試合結果モーダルの表示待ち（<c>ResultPending</c>）＝結果を見る前に落ちると結果が消える</item>
        /// <item>大会終了の後始末待ち（<c>PendingTournamentWrapUp</c>）＝週送りと通知フィードがここで走る</item>
        /// <item>残りブラケットの背景消化中（<c>BracketFoldPending</c>, issue #529）＝通算戦績が未確定</item>
        /// <item>試合開始フローの途中（<c>AwaitingMatchStart</c>・練習試合フロー）＝スタメンを保存しないため</item>
        /// </list>
        /// </summary>
        public static bool CanSave
        {
            get
            {
                var s = GameSession.Current;
                if (s.BracketFoldPending || s.ResultPending || s.PendingTournamentWrapUp != null) return false;
                if (s.AwaitingMatchStart || s.PendingMatchIsPractice) return false;
                if (s.Mode == GameMode.Tournament && (s.Runner == null || !s.Runner.CanCaptureState)) return false;
                return true;
            }
        }

        /// <summary>
        /// <see cref="CanSave"/> が false の理由（UI に1行で出す）。true のときは空文字。
        /// <b>UI 側で条件を書き写さない</b>＝条件が増えたときに片方だけ直して表示と実挙動が食い違う
        /// （実測ではなく構造の問題。issue #565 で条件が2つから5つへ増えたのが契機）。
        /// </summary>
        public static string SaveDisabledReason()
        {
            var s = GameSession.Current;
            if (s.BracketFoldPending) return "大会結果を集計中はセーブできません（集計完了までお待ちください）。";
            if (s.Mode == GameMode.Tournament && s.Runner != null && !s.Runner.CanCaptureState)
                return "試合の観戦中はセーブできません（試合を終えるとセーブできます）。";
            if (s.ResultPending || s.PendingTournamentWrapUp != null)
                return "試合結果を確認するとセーブできます。";
            if (s.AwaitingMatchStart || s.PendingMatchIsPractice)
                return "試合の準備中はセーブできません（ホームに戻るとセーブできます）。";
            return "";
        }

        /// <summary>
        /// この進行を最後に保存／読込した時点の暦（null＝一度も保存していない）。オートセーブを廃止した
        /// （監督決定 2026-08-09）ぶん「保存しないと失う進行」が見えなくなるため、「タイトルへ戻る」の
        /// 確認ダイアログがここを読んで経過を出す。<b>セーブ対象ではない</b>（セッション内の目安）。
        /// </summary>
        public static (int YearIndex, int Week)? LastSavedAt { get; private set; }

        /// <summary>全 static サービスから現在状態を集めて DTO を組む。</summary>
        public static SaveState Capture()
        {
            var s = new SaveState();
            GameSeed.Capture(s);
            GameClock.Capture(s);
            RosterService.Capture(s);
            ProspectService.Capture(s);
            RecruitService.Capture(s);
            ManagerService.Capture(s);
            NationService.Capture(s);
            GameSession.Current.Capture(s);
            NewTeamService.Capture(s);
            ManagerTransferService.Capture(s);
            TeamColorProvider.Capture(s);
            DraftService.Capture(s);
            EventService.Capture(s);
            TrainingBoostService.Capture(s);
            return s;
        }

        /// <summary>
        /// DTO を全 static サービスへ押し戻す（種→暦→部員→監督→全国→セッション→新チーム→色）。
        /// 大会中のセーブなら最後に進行体を組み直す（issue #565 F1。<b>全国ロスター・今大会成績を
        /// 復元した後</b>でないと resolver が空の全国を掴む＝必ずこの順で呼ぶ）。
        /// </summary>
        public static void Restore(SaveState s)
        {
            GameSeed.Restore(s);
            GameClock.Restore(s);
            RosterService.Restore(s);
            ProspectService.Restore(s);   // ロスターの後（合流済み学年は実体をロスター側だけが持つ）
            RecruitService.Restore(s);
            ManagerService.Restore(s);
            NationService.Restore(s);
            GameSession.Current.Restore(s);
            NewTeamService.Restore(s);
            ManagerTransferService.Restore(s);
            TeamColorProvider.Restore(s);
            // 教え子名鑑はロスター**と**成績の後（issue #586）。復元時にバンドの基準を採り直すため、
            // 部員（RosterService）と公式戦通算（GameSession.Stats）の両方が据わっている必要がある。
            DraftService.Restore(s);
            EventService.Restore(s);
            TrainingBoostService.Restore(s);
            if (s.Tournament != null) TournamentEntry.RestoreRunner(s.Tournament);
            // 観るだけの甲子園（自校が出場していない年）。対話モードの大会とは排他なので順序は問わない。
            if (s.Koshien != null) KoshienEntry.RestoreSpectated(s.Koshien);
            // 観るだけのセンバツ（自校が選ばれていない年）。同上。
            if (s.Senbatsu != null) SenbatsuEntry.RestoreSpectated(s.Senbatsu);
        }

        /// <summary>
        /// 新規ゲーム開始のための完全リセット（全 static サービスを初期状態へ）。母種は次の非決定採取に任せる。
        /// 自校の同定（校名・県・地区）も既定（桜丘・神奈川・地区なし）へ戻す＝
        /// <see cref="NationService.Reset"/> はキャッシュしか捨てないので明示で戻す。
        /// </summary>
        public static void ResetForNewGame()
        {
            GameSeed.RerollForNewGame();
            RosterService.Reset();
            ManagerService.Reset();
            NationService.Reset();
            NationService.SetSchoolIdentity(
                NationService.DefaultSchoolName, NationService.DefaultPrefectureId, null);
            GameSession.Current.ResetForNewGame();
            NewTeamService.Clear();
            ManagerTransferService.Clear();
            TeamColorProvider.Clear();
            EventService.Clear();
            TrainingBoostService.Clear();
            GameClock.Reset();
            LastSavedAt = null;   // 新しい進行＝まだ一度も保存していない
        }

        /// <summary>
        /// 新設校として新規ゲームを開始する（issue #585）。<see cref="ResetForNewGame"/> の後に
        /// プレイヤーが決めた同定を据え、初回のロスター生成を「1年生10人だけ」に切り替える。
        /// 新規ゲーム設定画面の「この高校ではじめる」がここだけを通る＝開始手順を2箇所に書かない。
        /// </summary>
        public static void StartNewSchool(string schoolName, int prefectureId, int? districtId)
        {
            ResetForNewGame();
            NationService.SetSchoolIdentity(schoolName, prefectureId, districtId);
            RosterService.ResetAsNewSchool();
        }

        /// <summary>既定のセーブ先（persistentDataPath/kokosim/&lt;slot&gt;.sav）。スロットUI接続は F1。</summary>
        public static string PathForSlot(string slot = "slot0")
            => Path.Combine(Application.persistentDataPath, "kokosim", slot + Extension);

        /// <summary>
        /// 現在状態を JSON+gzip でファイルへ書き出す。保存できない局面（<see cref="CanSave"/>）では書かず false。
        ///
        /// <para>大会中は<b>全国裏試合の背景シムを完了まで待ってから</b>写す（issue #565 論点1＝(a)）。
        /// 待たずに保存すると、他46県の途中結果を焼いたセーブができる。ロード後に背景シムを
        /// 再開する手立ては無い（進捗を保存していない）ので、その県の成績は<b>永久に欠ける</b>。
        /// 待ち時間が出るのはこのためで、単なる作法ではない。</para>
        /// </summary>
        public static bool Save(string path)
        {
            if (!CanSave) return false;
            NationBackgroundSim.EnsureCompleted();
            var raw = Encoding.UTF8.GetBytes(SaveCodec.Serialize(Capture()));
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (var fs = File.Create(path))
            using (var gz = new GZipStream(fs, System.IO.Compression.CompressionLevel.Optimal))
                gz.Write(raw, 0, raw.Length);
            LastSavedAt = (GameClock.YearIndex, GameClock.Week);
            return true;
        }

        /// <summary>ファイルを読み、gunzip→復元して全サービスへ適用する。読めなければ false（例外で落ちない）。</summary>
        public static bool Load(string path, out string error)
        {
            if (!TryReadText(path, out var json, out error)) return false;
            if (!SaveCodec.TryDeserialize(json, out var state, out error)) return false;
            Restore(state);
            // 読んだ直後は「その地点が保存済み」＝未保存の経過は0週から数え直す。
            LastSavedAt = (GameClock.YearIndex, GameClock.Week);
            return true;
        }

        /// <summary>
        /// ファイルを読み gunzip して JSON テキストを返す（復元はしない）。読めなければ false（例外で落ちない）。
        /// <see cref="Load"/> の経路（一覧の見出しは <see cref="TryPeekHeader"/> で流し読みする）。
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

        /// <summary>
        /// スロット一覧用の見出しを、gunzip しながら流し読みする（issue #660）。展開後の文字列を作らず、
        /// 中身も組み立てない（<see cref="SaveCodec.PeekHeader(TextReader)"/>）。読めなければ null（例外で落ちない）。
        /// </summary>
        public static SaveHeader? TryPeekHeader(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                using var fs = File.OpenRead(path);
                using var gz = new GZipStream(fs, CompressionMode.Decompress);
                using var reader = new StreamReader(gz, Encoding.UTF8);
                return SaveCodec.PeekHeader(reader);
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[セーブ] 見出しの読み取り失敗（{path}）: {e.Message}");
                return null;
            }
        }
    }
}
