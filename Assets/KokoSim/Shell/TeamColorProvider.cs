using System;
using System.IO;
using KokoSim.Engine.Nation;
using UnityEngine;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// チームカラー（data/team-colors.yaml）の Unity 側プロバイダ（設計書18 §3.5.1）。
    /// <see cref="SchoolNameVocabProvider"/> と同型＝StreamingAssets の YAML を読み、解釈はエンジンの
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

        /// <summary>校IDから配色を引く。自校だけは上書きがあればそれを返す。</summary>
        public static TeamColors For(int schoolId)
            => schoolId == NationService.ManagerSchoolId && ManagerOverride != null
                ? ManagerOverride
                : Palette.ColorsFor(schoolId);

        /// <summary>自校の現在の配色（上書き前は校IDからの自動割り当て）。設定画面の初期値。</summary>
        public static TeamColors ManagerColors => For(NationService.ManagerSchoolId);

        /// <summary>テスト・リセット用（プロバイダのキャッシュと上書きを捨てる）。</summary>
        public static void Clear()
        {
            _cached = null;
            ManagerOverride = null;
        }

        private static TeamColorPalette Load()
        {
            try
            {
                var path = Path.Combine(Application.streamingAssetsPath, RelativePath);
                if (File.Exists(path))
                {
                    var palette = TeamColorParser.Parse(File.ReadAllText(path));
                    if (palette != null)
                    {
                        Debug.Log($"[チームカラー] 読込成功: 配色 {palette.Entries.Count} 件");
                        return palette;
                    }
                    Debug.LogWarning($"[チームカラー] {RelativePath} から1件も読めなかった。既定4件で継続。");
                }
                else
                {
                    Debug.LogWarning($"[チームカラー] StreamingAssets に team-colors.yaml が無い（{path}）。既定4件で継続。");
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
