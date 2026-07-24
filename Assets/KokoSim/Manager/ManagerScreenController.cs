using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Engine.Nation;     // Tiers（数値→S〜Gランクの単一変換）
using KokoSim.Unity.Components;  // 部品辞書（RankChip / AbilityRow）
using KokoSim.Unity.Shell;       // ManagerService / RosterService / TeamOverall / WeekAdvance

namespace KokoSim.Unity.ManagerMeta
{
    /// <summary>
    /// 監督画面（issue #172・設計書04 §1）。表示専用＝<see cref="ManagerService.Manager"/> を読むだけで
    /// ロジックを持たない（UI-BUILD-METHOD 大原則④）。本文はカテゴリ別3カード（指導力／采配・育成／
    /// 評価・資金）で、能力行は部品辞書の AbilityRow（数値右揃え＋ランク連動バー＋カラーチップ文字併記）
    /// に一本化する（UI原則③・⑤）。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class ManagerScreenController : MonoBehaviour
    {
        private VisualElement _root;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;

            // 全タブ共通の進週処理へ集約（issue #134: 大会モード中はホームへ回送して日送りへ引き継ぐ）。
            var advance = _root.Q<Button>("advance");
            if (advance != null) advance.clicked += () => WeekAdvance.FromSideScreen(Render);

            Render();
        }

        private void Render()
        {
            var m = ManagerService.Manager;

            RenderTopBar();

            FillRows("mg-coaching",
                Row("打撃指導", m.CoachingBatting),
                Row("投手指導", m.CoachingPitching),
                Row("守備指導", m.CoachingDefense));

            FillRows("mg-tactics",
                Row("采配", m.TacticalSense),
                Row("育成眼", m.TalentEye));

            FillRows("mg-eval",
                Row("名声", m.Fame),
                Row("信頼度", m.Trust));

            SetText("mg-funds", "¥" + m.Funds.ToString("0") + "万");
        }

        /// <summary>共通トップバーの動的値（週・メーター・自校の総合ランク）を埋める。他画面と同じ単一ソース。</summary>
        private void RenderTopBar()
        {
            ScoreboardStrip.Fill(_root);
            TopBarMeters.Fill(_root);   // 部費残高・名声・信頼度（ManagerService 単一ソース）
            var rank = _root.Q<VisualElement>("team-rank");
            if (rank == null) return;
            rank.Clear();
            rank.Add(UiComponents.RankChipLegacy(TeamOverall.GradeOf(RosterService.Active)));
        }

        /// <summary>監督メタ1本ぶんの能力行（0〜99帯）。ランクは全画面共通の Tiers 変換で併記する。</summary>
        private static VisualElement Row(string label, double value)
        {
            var v = (int)System.Math.Round(value);
            return UiComponents.AbilityRow(new AbilityRowData
            {
                Label = label,
                Value = v.ToString(),
                Pct = (float)(value / 100.0),
                Grade = Tiers.FromStrength(value).ToString(),
            });
        }

        private void FillRows(string hostName, params VisualElement[] rows)
        {
            var host = _root.Q<VisualElement>(hostName);
            if (host == null) return;
            host.Clear();
            foreach (var row in rows) host.Add(row);
        }

        private void SetText(string name, string text)
        {
            var label = _root.Q<Label>(name);
            if (label != null) label.text = text;
        }
    }
}
