#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Saves;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;
using UnityEngine;

namespace KokoSim.Unity.EditorProbes
{
    /// <summary>
    /// 【開発用・エディタ限定】セーブ →<b>ドメインリロード（＝アプリ再起動相当）</b>→ ロードの往復確認
    /// （issue #483 の完了条件「セーブ → アプリ再起動 → ロードで全部一致する」）。
    ///
    /// <para>engine 側の往復テスト（<c>SaveCodecTests</c> / <c>SaveDeterminismTests</c>）は DTO の形だけを見るので、
    /// <b>Shell の static サービス群（Capture/Restore の配線）と Unity のファイル IO は通らない</b>。
    /// 保存対象が DTO に載っていても Restore の呼び忘れ・遅延初期化での上書きは engine テストでは検出できず、
    /// しかも static が本当に死ぬのは Play 停止（ドメインリロード）の瞬間だけ＝同一 Play 内で
    /// Capture→Restore を往復させても「消えていないだけ」を一致と誤認する。</para>
    ///
    /// <para>使い方: Play 中に <see cref="PhaseA"/> → Play 停止 → 再 Play → <see cref="PhaseB"/>。
    /// 結果は out/save-roundtrip/ に before/after/report を残す。</para>
    /// </summary>
    public static class SaveRoundTripProbe
    {
        private const string Dir = "out/save-roundtrip";
        private const string SlotId = "roundtrip-probe";
        private const string TournamentSlotId = "roundtrip-probe-tournament";

        // ===== Phase A: 状態を作って保存し、指紋を残す =====

        /// <summary>
        /// 大会中セーブ（issue #565 F1）の往復。<see cref="PhaseA"/> と同じ進行を作ってから<b>大会へ入り、
        /// 自校の1戦を消化した「試合と試合の間」で保存する</b>。engine の往復テストは進行体の状態しか見ないので、
        /// Shell の再構築（<c>TournamentEntry.RestoreRunner</c>＝出場校の引き直し・resolver の組み直し）は
        /// ここでしか通らない。
        /// </summary>
        public static string PhaseATournament()
        {
            // 自校が初戦で負けると大会モードを抜ける＝保存できる局面ではなくなる（実測 2026-08-10）。
            // ブラケットに1試合も載っていない状態で往復させても検証が薄いので、**確実に勝てる自校**を作る
            // （母種を総当たりする案は1母種あたり県内211校のブートストラップ＋1球フルシムで数十秒かかり、
            // 6母種で MCP 呼び出しがタイムアウトした＝実測 2026-08-10）。
            var setup = Setup(20260809UL, boostRoster: true);
            if (setup != null) return setup;

            // 夏の大会週まで進める（週入りフックが TournamentEntry.Enter を呼ぶ）。
            for (var i = 0; i < 60 && GameSession.Current.Mode != GameMode.Tournament; i++) GameClock.Advance(1);
            if (GameSession.Current.Mode != GameMode.Tournament)
                return "NG: 60週進めても大会モードに入らなかった";

            // 自校の1戦を自動消化して「試合と試合の間」を作る（結果モーダルは閉じた扱いにする）。
            GameSession.Current.PlayMatch();
            GameSession.Current.ConsumeResult();
            var played = GameSession.Current.Runner == null ? 0
                : GameSession.Current.Runner.BuildBracketView().Matches.Count;
            if (GameSession.Current.Mode != GameMode.Tournament)
                return "NG: 強化した自校が初戦で敗退した（強化幅を上げること）";

            if (!SaveGameIO.CanSave) return "NG: 大会中に保存できない（CanSave=false: " + SaveGameIO.SaveDisabledReason() + "）";
            if (!SaveSlots.Save(TournamentSlotId)) return "NG: 保存できなかった";

            var before = Fingerprint();
            Write("before-tournament.txt", before);
            return "PhaseATournament OK（消化済み試合 " + played + "）\n" + Head(before);
        }

        /// <summary>大会中セーブの Phase B（別ドメインでロードして突き合わせる）。</summary>
        public static string PhaseBTournament()
        {
            var beforePath = Path.Combine(Dir, "before-tournament.txt");
            if (!File.Exists(beforePath)) return "NG: before-tournament.txt が無い（PhaseATournament を先に）";
            var before = File.ReadAllText(beforePath);

            SaveGameIO.ResetForNewGame();
            GameSeed.Reset(1UL);
            var _ = RosterService.Roster;
            var __ = NationService.Rosters.Snapshot();
            var ___ = ManagerService.Manager.Funds;

            string err;
            if (!SaveSlots.Load(TournamentSlotId, out err)) return "NG: ロード失敗 " + err;

            var after = Fingerprint();
            Write("after-tournament.txt", after);
            var report = Diff(before, after);
            Write("report-tournament.txt", report);
            return report;
        }

        public static string PhaseA()
        {
            SaveGameIO.ResetForNewGame();
            GameSeed.Reset(20260809UL);

            // 進行を作る。夏の大会週へ入ると Tournament モードになり保存できなくなるので、その手前まで。
            GameClock.Advance(8);
            if (GameSession.Current.Mode != GameMode.Normal)
                return "NG: 8週進めた時点で大会モードに入っている（週数を減らすこと）";

            // 全国ロスターを materialize（本拠県の全校＝実プレイで最初に触られる範囲）。
            NationService.Rosters.BootstrapAll(NationService.PrefectureSchools, GameClock.YearIndex);

            // 既定値のままだと「復元しなくても一致する」ので、保存対象を必ず既定から動かす。
            var m = ManagerService.Manager;
            m.Funds = 777;
            m.Fame = 42;
            m.Trust = 63;
            m.CoachingBatting = 31;
            m.CareerYears = 3;
            ManagerService.Facilities.SetLevel("lighting", 2);
            ManagerService.Facilities.SetLevel("indoor", 1);

            // 戦績・成績（大会をまたぐ永続分）。
            var rec = GameSession.Current.Records;
            rec.RecordOfficialMatch(NationService.ManagerSchoolId, NationService.PrefectureSchools[0].Id);
            rec.RecordOfficialMatch(NationService.ManagerSchoolId, NationService.PrefectureSchools[1].Id);
            rec.RecordKoshienAppearance(NationService.ManagerSchoolId, KoshienKind.Summer, GameClock.YearIndex);
            GameSession.Current.Year = GameClock.YearIndex;

            // 自校チームカラーの上書き（null でないこと自体が復元対象）。
            TeamColorProvider.ManagerOverride = TeamColorProvider.For(7);

            // 自校部員も既定から動かす（背番号・怪我・調子は DevelopingPlayer に載る）。
            var roster = RosterService.Roster;
            if (roster.Count > 0) roster[0].UniformNumber = 99;

            if (!SaveSlots.Save(SlotId)) return "NG: 保存できなかった（CanSave=false）";

            var before = Fingerprint();
            Write("before.txt", before);
            return "PhaseA OK\n" + Head(before);
        }

        /// <summary>両シナリオ共通の下ごしらえ（新規開始→8週→既定から動かす）。異常なら理由、正常なら null。</summary>
        private static string Setup(ulong seed = 20260809UL, bool boostRoster = false)
        {
            SaveGameIO.ResetForNewGame();
            GameSeed.Reset(seed);
            GameClock.Advance(8);
            if (GameSession.Current.Mode != GameMode.Normal)
                return "NG: 8週進めた時点で大会モードに入っている（週数を減らすこと）";

            NationService.Rosters.BootstrapAll(NationService.PrefectureSchools, GameClock.YearIndex);

            var m = ManagerService.Manager;
            m.Funds = 777;
            m.Fame = 42;
            m.Trust = 63;
            m.CoachingBatting = 31;
            m.CareerYears = 3;
            ManagerService.Facilities.SetLevel("lighting", 2);
            ManagerService.Facilities.SetLevel("indoor", 1);

            var rec = GameSession.Current.Records;
            rec.RecordOfficialMatch(NationService.ManagerSchoolId, NationService.PrefectureSchools[0].Id);
            rec.RecordOfficialMatch(NationService.ManagerSchoolId, NationService.PrefectureSchools[1].Id);
            rec.RecordKoshienAppearance(NationService.ManagerSchoolId, KoshienKind.Summer, GameClock.YearIndex);
            GameSession.Current.Year = GameClock.YearIndex;

            TeamColorProvider.ManagerOverride = TeamColorProvider.For(7);
            var roster = RosterService.Roster;
            if (roster.Count > 0) roster[0].UniformNumber = 99;
            if (boostRoster) BoostRoster(roster);
            return null;
        }

        /// <summary>
        /// 自校を「県内で確実に勝てる」強さにする（大会中セーブの局面づくり専用）。
        /// 能力レベルを一律で上限近くへ置くだけ＝<see cref="Shell.TournamentEntry"/> の自校 Strength
        /// （<c>TeamOverall</c>）が上がり、シードが最上位になって初戦を落としにくくなる。
        /// </summary>
        private static void BoostRoster(IReadOnlyList<DevelopingPlayer> roster)
        {
            foreach (var p in roster)
            {
                if (p.IsRetired) continue;
                foreach (AbilityKind k in
                         Enum.GetValues(typeof(AbilityKind)))
                    p.SetLevel(k, 90);
            }
        }

        // ===== Phase B: 別ドメインでロードし、指紋を突き合わせる =====

        public static string PhaseB()
        {
            var beforePath = Path.Combine(Dir, "before.txt");
            if (!File.Exists(beforePath)) return "NG: before.txt が無い（PhaseA を先に実行する）";
            var before = File.ReadAllText(beforePath);

            // ここが肝心: ロード前に「新規ゲームの初期状態」を一度作って、遅延初期化を全部起こしておく。
            // 実機の「起動 → タイトル → つづきから」は初期化済みの上へロードするので、その順序を再現する。
            SaveGameIO.ResetForNewGame();
            GameSeed.Reset(1UL);
            var _ = RosterService.Roster;
            var __ = NationService.Rosters.Snapshot();
            var ___ = ManagerService.Manager.Funds;

            string err;
            if (!SaveSlots.Load(SlotId, out err)) return "NG: ロード失敗 " + err;

            var after = Fingerprint();
            Write("after.txt", after);

            var report = Diff(before, after);
            Write("report.txt", report);
            return report;
        }

        // ===== 指紋 =====

        /// <summary>
        /// 生きているサービスから読んだ値の一覧＋ DTO 全体の SHA256。
        /// 行単位で比較して食い違った項目名を出せるように、1項目1行のテキストにする。
        /// </summary>
        private static string Fingerprint()
        {
            var b = new StringBuilder();
            b.Append("seed=").Append(GameSeed.MasterHex).Append('\n');
            b.Append("week=").Append(GameClock.Week).Append('\n');
            b.Append("yearIndex=").Append(GameClock.YearIndex).Append('\n');
            b.Append("clockLabel=").Append(GameClock.CurrentLabel()).Append('\n');

            var roster = RosterService.Roster;
            b.Append("roster.count=").Append(roster.Count).Append('\n');
            b.Append("roster.active=").Append(RosterService.Active.Count).Append('\n');
            b.Append("roster.digest=").Append(Sha(RosterDigestSource(roster))).Append('\n');

            var m = ManagerService.Manager;
            b.Append("manager.funds=").Append(m.Funds.ToString("F3")).Append('\n');
            b.Append("manager.fame=").Append(m.Fame.ToString("F3")).Append('\n');
            b.Append("manager.trust=").Append(m.Trust.ToString("F3")).Append('\n');
            b.Append("manager.coachBat=").Append(m.CoachingBatting.ToString("F3")).Append('\n');
            b.Append("manager.careerYears=").Append(m.CareerYears).Append('\n');
            b.Append("facilities=").Append(FacilitiesDigestSource()).Append('\n');

            var snap = NationService.Rosters.Snapshot();
            var players = 0;
            foreach (var r in snap) players += r.Players.Count;
            b.Append("nation.schools=").Append(snap.Count).Append('\n');
            b.Append("nation.players=").Append(players).Append('\n');

            b.Append("session.year=").Append(GameSession.Current.Year).Append('\n');
            b.Append("session.mode=").Append(GameSession.Current.Mode).Append('\n');
            var mine = GameSession.Current.Records.For(NationService.ManagerSchoolId);
            b.Append("records.wins=").Append(mine.OfficialWins).Append('\n');
            b.Append("records.losses=").Append(mine.OfficialLosses).Append('\n');
            b.Append("records.summerApp=").Append(mine.SummerAppearances).Append('\n');
            b.Append("records.count=").Append(GameSession.Current.Records.Records.Count).Append('\n');

            b.Append("tournament=").Append(TournamentDigest()).Append('\n');

            b.Append("newTeam.pending=").Append(NewTeamService.Pending).Append('\n');
            b.Append("color.override=").Append(TeamColorProvider.ManagerOverride == null
                ? "(null)" : TeamColorProvider.ManagerOverride.ToString()).Append('\n');

            // 全体の一致（上に出していない項目もここで効く）。Capture は生きているサービスから読むので、
            // Restore がどれか1つでも押し戻していなければこの SHA が変わる。
            b.Append("dto.sha=").Append(Sha(SaveCodec.Serialize(SaveGameIO.Capture()))).Append('\n');
            return b.ToString();
        }

        /// <summary>
        /// 大会の進行を1行に畳む（issue #565）。<b>樹形図まで入れる</b>＝「モードだけ戻って中身が空」を
        /// 一致と誤認しないため。大会中でなければ "(none)"。
        /// </summary>
        private static string TournamentDigest()
        {
            var s = GameSession.Current;
            if (!s.InTournament) return "(none)";
            var v = s.Runner.BuildBracketView();
            var b = new StringBuilder();
            b.Append(s.Kind).Append('|').Append(s.Title).Append("|day=").Append(s.TournamentDay)
             .Append("|next=").Append(s.Runner.NextOpponent == null ? "-" : s.Runner.NextOpponent.Name)
             .Append("|matchDay=").Append(s.Runner.NextMatchDay)
             .Append("|round=").Append(s.Runner.RoundName)
             .Append("|field=").Append(s.Field.Count)
             .Append("|matches=").Append(v.Matches.Count).Append('|');
            foreach (var m in v.Matches)
                b.Append(m.WinnerId).Append('>').Append(m.LoserId).Append(':')
                 .Append(m.WinnerScore).Append('-').Append(m.LoserScore).Append(';');
            foreach (var round in v.Rounds)
                foreach (var c in round.Cards)
                    b.Append(c.Top.TeamName ?? "-").Append('/').Append(c.Bottom.TeamName ?? "-")
                     .Append(c.IsPlayed ? "*" : "").Append(',');
            return Sha(b.ToString()) + "#" + v.Matches.Count;
        }

        private static string RosterDigestSource(IReadOnlyList<DevelopingPlayer> roster)
        {
            var b = new StringBuilder();
            foreach (var p in roster)
            {
                b.Append(p.Id).Append('|').Append(p.Name).Append('|').Append(p.UniformNumber).Append('|')
                    .Append(p.Grade).Append('|').Append(p.IsRetired).Append('\n');
            }
            return b.ToString();
        }

        private static string FacilitiesDigestSource()
        {
            var keys = new List<string>(ManagerService.Facilities.Levels.Keys);
            keys.Sort(StringComparer.Ordinal);
            var b = new StringBuilder();
            foreach (var k in keys) b.Append(k).Append(':').Append(ManagerService.Facilities.LevelOf(k)).Append(' ');
            return b.ToString().TrimEnd();
        }

        // ===== 比較・出力 =====

        private static string Diff(string before, string after)
        {
            var a = before.Split('\n');
            var c = after.Split('\n');
            var mapA = ToMap(a);
            var mapB = ToMap(c);
            var keys = new List<string>(mapA.Keys);
            foreach (var k in mapB.Keys) if (!mapA.ContainsKey(k)) keys.Add(k);

            var diffs = new List<string>();
            foreach (var k in keys)
            {
                string va, vb;
                mapA.TryGetValue(k, out va);
                mapB.TryGetValue(k, out vb);
                if (va != vb) diffs.Add("  " + k + "\n    保存前: " + (va ?? "(無)") + "\n    復元後: " + (vb ?? "(無)"));
            }

            var b = new StringBuilder();
            b.Append("# セーブ往復（ドメインリロードまたぎ）／issue #483\n");
            b.Append("項目数: ").Append(keys.Count).Append("\n");
            if (diffs.Count == 0)
            {
                b.Append("結果: 一致（全項目）\n\n");
                b.Append(after);
                return b.ToString();
            }
            b.Append("結果: 不一致 ").Append(diffs.Count).Append(" 項目\n");
            foreach (var d in diffs) b.Append(d).Append('\n');
            return b.ToString();
        }

        private static Dictionary<string, string> ToMap(string[] lines)
        {
            var map = new Dictionary<string, string>();
            foreach (var line in lines)
            {
                if (string.IsNullOrEmpty(line)) continue;
                var i = line.IndexOf('=');
                if (i <= 0) continue;
                map[line.Substring(0, i)] = line.Substring(i + 1);
            }
            return map;
        }

        private static string Sha(string text)
        {
            using (var sha = SHA256.Create())
            {
                var h = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                var b = new StringBuilder(h.Length * 2);
                foreach (var x in h) b.Append(x.ToString("x2"));
                return b.ToString();
            }
        }

        private static string Head(string text)
        {
            var lines = text.Split('\n');
            var b = new StringBuilder();
            for (var i = 0; i < lines.Length && i < 8; i++) b.Append(lines[i]).Append('\n');
            return b.ToString();
        }

        private static void Write(string file, string text)
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(Path.Combine(Dir, file), text);
            Debug.Log("[SaveRoundTrip] " + file + " を書き出した");
        }
    }
}
#endif
