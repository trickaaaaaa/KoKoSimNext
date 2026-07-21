using UnityEngine.UIElements;
using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Components
{
    /// <summary>
    /// ScoreboardStrip（部品辞書 / 設計書16 §4-1）の充填ロジック。
    /// 見た目は Components/components.uss の .sbs* に、器は Components/ScoreboardStrip.uxml にある。
    ///
    /// 升目（マス）は値文字列から1文字ずつ生成する。数字が含まれる値は「数字＝値マス／
    /// それ以外＝単位マス（一段小さい淡色）」、数字を含まない値（「開催中」「本日試合」「―」）は
    /// 全マスを値マスとして出す。これで大会モードの非数値表示も分岐なしで升目に載る。
    ///
    /// 全画面のトップバーはこの1経路だけを通すこと（画面ごとに週の書式を作らない）。
    /// 呼ばない画面は UXML の既定値が出たままになる＝旧実装で実際に起きていた
    /// 「選手一覧のカウントダウンが常に『残り 15 週』」の再発防止。
    /// </summary>
    public static class ScoreboardStrip
    {
        /// <summary>通常週：共有現在週（GameClock）と夏予選までの残り週で埋める。</summary>
        public static void Fill(VisualElement root)
        {
            var left = GameClock.WeeksUntilSummer;
            Fill(root, "夏予選まで", left > 0 ? left + "週" : "開催中", "");
        }

        /// <summary>
        /// カウントダウンを呼び出し側が決める版（大会モードのホーム＝「次戦まで／本日試合」等）。
        /// weekSuffix は年の添え字に続けて出す小書き（「夏の大会 3日目」など。升目には載せない）。
        /// </summary>
        public static void Fill(VisualElement root, string countdownLabel, string countdownValue, string weekSuffix)
        {
            if (root == null) return;

            var year = SeasonClock.YearLabel(GameClock.YearIndex, GameClock.Week);
            var sup = root.Q<Label>("week-year");
            if (sup != null) sup.text = string.IsNullOrEmpty(weekSuffix) ? year : year + "　" + weekSuffix;

            var k = root.Q<Label>("countdown-k");
            if (k != null) k.text = countdownLabel;

            // 週は白ドット、カウントダウンだけアンバー点灯（UI原則②＝アクセントは希少資源。
            // 掲示板の中でも点灯させるのは最重要値ひとつだけ）。
            FillCells(root.Q("week-cells"), SeasonClock.CompactLabel(GameClock.YearIndex, GameClock.Week), false);
            FillCells(root.Q("countdown-cells"), countdownValue, true);
        }

        /// <summary>値文字列を1文字1マスの升目に展開する。</summary>
        private static void FillCells(VisualElement host, string value, bool lit)
        {
            if (host == null) return;
            host.Clear();
            if (string.IsNullOrEmpty(value)) return;

            var hasDigit = false;
            foreach (var ch in value) if (ch >= '0' && ch <= '9') { hasDigit = true; break; }

            foreach (var ch in value)
            {
                if (ch == ' ' || ch == '　') continue;   // 区切りの空白はマスにしない

                var cell = new Label(ch.ToString());
                cell.AddToClassList("sbs-cell");
                cell.AddToClassList("f-dot");

                var isDigit = ch >= '0' && ch <= '9';
                if (hasDigit && !isDigit)
                {
                    cell.AddToClassList("sbs-cell--unit");   // 「月」「週」＝単位は一段落とす
                }
                else
                {
                    if (!hasDigit) cell.AddToClassList("sbs-cell--wide");   // 全角のみの値は広いマス
                    if (lit) cell.AddToClassList("sbs-cell--lit");
                }

                host.Add(cell);
            }
        }
    }
}
