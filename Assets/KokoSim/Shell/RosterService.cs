using System.Collections.Generic;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// 自校部員（DevelopingPlayer 群）の単一ソース。従来は各画面が seed=42 で別インスタンスの
    /// ロスターを毎回再生成していたため、背番号など可変状態が画面間で共有できなかった。ここで
    /// 1回だけ生成した同一リストを全画面へ供給し、メンバー設定で割り当てた背番号が練習など他画面へ
    /// 一貫して反映されるようにする（設計書06 §3.3b）。
    ///
    /// 生成レシピは従来の PlayerListState/TrainingPlanState と同一（seed=42・grade 1〜3・
    /// ProspectGenerator.Intake）。生成直後に UniformNumberAssigner.AutoAssign で初期背番号（能力順）を振る。
    /// エンジン非依存の純データを保持するだけで、UnityEngine には依存しない。
    /// </summary>
    public static class RosterService
    {
        public const ulong DefaultSeed = 42;

        private static List<DevelopingPlayer> _roster;

        /// <summary>共有ロスター（セッション内で同一インスタンス・順序安定）。初回アクセスで生成。</summary>
        public static IReadOnlyList<DevelopingPlayer> Roster => _roster ??= Build(DefaultSeed);

        private static List<DevelopingPlayer> Build(ulong seed)
        {
            var rng = new Xoshiro256Random(seed);
            var coefficients = new RosterCoefficients();
            var list = new List<DevelopingPlayer>();
            for (var grade = 1; grade <= 3; grade++)
            {
                foreach (var p in ProspectGenerator.Intake(grade, coefficients, rng))
                {
                    p.Grade = grade;
                    list.Add(p);
                }
            }

            // 成績集計の帰属キー（安定ID）を連番付与。生成順＝安定順なので決定論（乱数不使用）。
            // 1始まり（0は「未割当＝集計対象外」の番兵と衝突させない）。
            for (var i = 0; i < list.Count; i++) list[i].Id = i + 1;

            UniformNumberAssigner.AutoAssign(list);   // 初期背番号を能力順で仮割当（プレイヤーが後で編集）

            // 主将（設計書09 §8）: ゲーム開始時点で必ず1名決まっている（最上級生の統率力最大）。
            // プレイヤーが選び直せるのは夏の3年引退後＝新チーム発足時だけ（CaptainSelector.IsDesignationWindow）。
            CaptainSelector.EnsureCaptain(list);
            return list;
        }
    }
}
