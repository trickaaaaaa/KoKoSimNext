using System;
using System.Collections.Generic;
using System.IO;
using KokoSim.Engine.Nation;
using UnityEngine;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// チームカラー（data/team-colors.yaml）の Unity 側プロバイダ（設計書18 §3.5.1）。
    /// <see cref="SchoolNameVocabProvider"/> と同型＝Resources の YAML（DataFiles） を読み、解釈はエンジンの
    /// 純パーサ <see cref="TeamColorParser"/> に委譲する。IO は Shell 層に隔離（不変条件#3）。
    ///
    /// 自校の配色だけはプレイヤーが領域ごとに決められる（2026-07-29 監督決定）。上書きは
    /// <see cref="ManagerOverride"/> に持ち、パレット表の外の配色も表現できる。
    /// <b>セーブ機構がまだ無いのでセッション内だけ生きる</b>（Unity を落とせばキャリアごと消えるのは
    /// 現状のゲーム全体と同じ。セーブを入れるときに一緒に載せる）。
    /// </summary>
    public static class TeamColorProvider
    {
        private const string RelativePath = "KokoSim/team-colors.yaml";

        private static TeamColorPalette _cached;

        /// <summary>パレット表。読み込み失敗時はエンジン既定（4件のみ）へフォールバック。</summary>
        public static TeamColorPalette Palette => _cached ?? (_cached = Load());

        /// <summary>自校の配色の上書き（null＝校IDからの自動割り当てを使う）。</summary>
        public static TeamColors ManagerOverride { get; set; }

        /// <summary>
        /// 自校でない学校の配色（校ID→配色）。配色は学校に付いて回る（issue #1261）＝転任で去った学校の配色は
        /// ここへ残り、引き継いだ学校の配色は <see cref="ManagerOverride"/> へ移る。表に無い校は校IDからの自動割り当て。
        /// </summary>
        private static Dictionary<int, TeamColors> _schoolOverrides = new Dictionary<int, TeamColors>();

        /// <summary>校IDから配色を引く。自校だけは上書きがあればそれを返す。</summary>
        public static TeamColors For(int schoolId)
        {
            if (schoolId == NationService.ManagerSchoolId && ManagerOverride != null) return ManagerOverride;
            return _schoolOverrides.TryGetValue(schoolId, out var kept) ? kept : Palette.ColorsFor(schoolId);
        }

        /// <summary>
        /// 転任: 去る学校の配色を、その学校（全国での校ID）へ残す。<paramref name="colors"/> は去る直前の自校の配色
        /// （<see cref="ManagerColors"/>）。新設校は自校の専用IDの自動割り当てで見えていた色を、そのまま持たせる。
        /// </summary>
        public static void LeaveSchool(int schoolId, TeamColors colors) => _schoolOverrides[schoolId] = colors;

        /// <summary>
        /// 転任: 引き継ぐ学校の配色を自校の配色へ据える。その学校に残していた配色（過去に率いた学校）があればそれ、
        /// 無ければ AI校だった頃に見えていた自動割り当てのまま（色が変わらない）。
        /// </summary>
        public static void ArriveAtSchool(int schoolId)
        {
            ManagerOverride = _schoolOverrides.TryGetValue(schoolId, out var kept) ? kept : Palette.ColorsFor(schoolId);
            _schoolOverrides.Remove(schoolId);
        }

        /// <summary>自校の現在の配色（上書き前は校IDからの自動割り当て）。設定画面の初期値。</summary>
        public static TeamColors ManagerColors => For(NationService.ManagerSchoolId);

        /// <summary>テスト・リセット用（プロバイダのキャッシュと上書きを捨てる）。新規ゲームの完全リセットも兼ねる。</summary>
        public static void Clear()
        {
            _cached = null;
            ManagerOverride = null;
            _schoolOverrides = new Dictionary<int, TeamColors>();
        }

        /// <summary>セーブ保存: 自校の配色上書き（null＝自動割り当て）と他校の配色を DTO へ写す（issue #507・#1261）。</summary>
        public static void Capture(KokoSim.Engine.Saves.SaveState s)
        {
            s.TeamColorOverride = ManagerOverride;
            s.SchoolColorOverrides = new Dictionary<int, TeamColors>(_schoolOverrides);
        }

        /// <summary>セーブ復元: 自校の配色上書きと他校の配色を据える（パレット表キャッシュは再読込に任せる）。</summary>
        public static void Restore(KokoSim.Engine.Saves.SaveState s)
        {
            ManagerOverride = s.TeamColorOverride;
            _schoolOverrides = new Dictionary<int, TeamColors>(s.SchoolColorOverrides);
        }

        private static TeamColorPalette Load()
        {
            try
            {
                var path = DataFiles.Describe(RelativePath);
                if (DataFiles.TryReadText(RelativePath, out var text))
                {
                    var palette = TeamColorParser.Parse(text);
                    if (palette != null)
                    {
                        Debug.Log($"[チームカラー] 読込成功: 配色 {palette.Entries.Count} 件");
                        return palette;
                    }
                    Debug.LogWarning($"[チームカラー] {RelativePath} から1件も読めなかった。既定4件で継続。");
                }
                else
                {
                    Debug.LogWarning($"[チームカラー] Resources に team-colors.yaml が無い（{path}）。既定4件で継続。");
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[チームカラー] 読込失敗（{e.Message}）。既定4件で継続。");
            }
            return TeamColorPalette.Fallback;
        }
    }
}
