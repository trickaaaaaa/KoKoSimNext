// ViewModel層（設計書06 §3.2 選手一覧）。UnityEngine 非依存に保つ。
// ソート・フィルタ付きテーブル（守備位置・学年・能力・調子）。
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Players
{
    public enum PlayerSort { Overall, Grade, Condition, Name }

    public sealed class PlayerRow
    {
        public int Index;                 // roster 生成順の安定 index（詳細画面へ受け渡す）
        public string Name = "";
        public string GradeLabel = "1年";
        /// <summary>カテゴリ別ランク（打撃力/走力/守備力/投手力の4つ, Issue #30）。総合ランクは廃止（2026-07-22 owner決定）。
        /// 部品辞書 <see cref="KokoSim.Unity.Components.UiComponents.CategoryRankChips"/> にそのまま渡す（Issue #140）。</summary>
        public PlayerStrength Strength = new PlayerStrength(0, 0, 0, 0);
        public string Condition = "普通";   // 絶好調 / 好調 / 普通 / 不調 / 絶不調（表示文字列）
        // 色分岐はこの5段階 enum で行う（表示文字列で比較しない。到達不能分岐の再発防止）。
        public KokoSim.Engine.Players.Condition ConditionLevel = KokoSim.Engine.Players.Condition.Normal;
        /// <summary>故障中の短縮表示（例「捻挫・中度」）。健常なら空。詳細は行クリック→選手詳細（UI原則⑦）。</summary>
        public string Injury = "";
    }

    public sealed class PlayerListView
    {
        public string Title = "選手一覧";
        public int Total;
        public PlayerSort Sort;
        public string SortLabel = "";
        public string TeamRankGrade = "C";   // 共通トップバー（スコアボード）へ表示するチーム総合力ランク
        public List<PlayerRow> Rows = new List<PlayerRow>();
    }

    /// <summary>
    /// 選手一覧の状態。純エンジンで生成した部員をソートして表に整形する。
    /// ホーム画面と同じ seed=42 の部員を再現し、同一チームとして見せる。
    /// </summary>
    public sealed class PlayerListState
    {
        // カテゴリ別ランクの重み（単一静的ソースを参照, Issue #30・2026-07-22 owner決定 候補A・#140 集約）。
        private static readonly TeamStrengthCoefficients Coeff = TeamStrengthCoeff.Default;

        private readonly IReadOnlyList<DevelopingPlayer> _roster;
        private readonly Dictionary<DevelopingPlayer, int> _indexOf = new Dictionary<DevelopingPlayer, int>();

        public PlayerSort Sort { get; private set; } = PlayerSort.Overall;

        public PlayerListState()
        {
            // 全画面で共有する単一ソースのロスター（背番号など可変状態を画面間で一致させる, RosterService）。
            _roster = RosterService.Active;
            for (var i = 0; i < _roster.Count; i++)
                _indexOf[_roster[i]] = i;
        }

        public void SetSort(PlayerSort sort) => Sort = sort;

        /// <summary>ソートを順送りで切り替える（ボタン1つで循環）。</summary>
        public void CycleSort()
            => Sort = (PlayerSort)(((int)Sort + 1) % 4);

        public PlayerListView BuildView()
        {
            var view = new PlayerListView
            {
                Total = _roster.Count,
                Sort = Sort,
                SortLabel = SortJp(Sort),
            };

            // 共通トップバー用：6指標のリーグ標準化総合からチーム総合力ランクを算出（③, 全画面統一）。
            if (_roster.Count > 0)
                view.TeamRankGrade = KokoSim.Unity.Shell.TeamOverall.GradeOf(_roster);

            IEnumerable<DevelopingPlayer> q = _roster;

            switch (Sort)
            {
                case PlayerSort.Overall: q = q.OrderByDescending(p => p.AverageLevel()); break;
                case PlayerSort.Grade: q = q.OrderByDescending(p => p.Grade).ThenByDescending(p => p.AverageLevel()); break;
                case PlayerSort.Condition: q = q.OrderByDescending(p => p.ConditionValue); break;
                case PlayerSort.Name: q = q.OrderBy(p => p.Name, System.StringComparer.Ordinal); break;
            }

            foreach (var p in q) view.Rows.Add(BuildRow(p));
            return view;
        }

        private PlayerRow BuildRow(DevelopingPlayer p)
        {
            var condition = KokoSim.Engine.Players.FormModel.Quantize(p.ConditionValue);
            var row = new PlayerRow
            {
                Index = _indexOf.TryGetValue(p, out var idx) ? idx : 0,
                Name = p.Name,
                GradeLabel = p.Grade + "年",
                Condition = ConditionLabels.Jp(condition),
                ConditionLevel = condition,
                // 故障（設計書03 §3.5・UI原則⑥）: 一覧をスキャンするだけで離脱者が拾えるようにする。
                Injury = p.Injury == KokoSim.Engine.Players.InjurySeverity.None
                    ? ""
                    : KokoSim.Unity.Shell.InjuryLabel.Type(p.InjuryType) + "・"
                      + KokoSim.Unity.Shell.InjuryLabel.Severity(p.Injury),
            };

            // カテゴリ別ランク（打撃力/走力/守備力/投手力）: 野手/投手を問わず常に4つ計算する
            // （Issue #30・2026-07-22 owner決定 候補A。非該当側も生成時の余技能力値からそのまま算出する）。
            row.Strength = PlayerStrengthProfile.Compute(p, Coeff);
            return row;
        }

        private static string SortJp(PlayerSort s)
        {
            switch (s)
            {
                case PlayerSort.Overall: return "総合順";
                case PlayerSort.Grade: return "学年順";
                case PlayerSort.Condition: return "調子順";
                case PlayerSort.Name: return "名前順";
                default: return s.ToString();
            }
        }
    }
}
