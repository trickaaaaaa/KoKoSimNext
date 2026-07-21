#if KOKOSIM_DEBUG || UNITY_EDITOR || DEVELOPMENT_BUILD
using System.Text;
using KokoSim.Engine.Debugging;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Debugging
{
    /// <summary>
    /// 試合デバッグHUD（設計書17 §5, F3）。<b>F1キーでトグル</b>する右サイドバー（A案・縦一列）。
    /// P1 今の球 / P2 打者判断 / P3 状態 / P4 采配 / P5 再現 を上から積み、下に P6 球ログ表を敷く。
    ///
    /// <para><b>表示専用</b>: データ源は <see cref="DebugTraceHub.Ring"/>（<see cref="RingBufferTraceSink"/>）だけで、
    /// engine を1回も追加で呼ばない。よって HUD の表示/非表示で試合結果は1ビットも変わらない
    /// （設計書17 §9 F3 DoD）。</para>
    ///
    /// <para><b>UI原則7箇条の適用対象外</b>（設計書17 §0）。製品UIではなく計器なので、等幅・高密度・
    /// 情報最優先で組む。ただし色は <c>tokens.uss</c> の変数と同じ値を使う（見た目の語彙は揃える）。</para>
    ///
    /// <para>使い方: 試合画面の GameObject にこの MonoBehaviour を足すか、
    /// <c>KokoSim/Debug/HUD を出す</c> のEditorメニューから生成する。</para>
    /// </summary>
    public sealed class DebugHudOverlay : MonoBehaviour
    {
        // tokens.uss と同じ値（USS を読めない生成UIなので数値で持つ。増やすときは tokens.uss 側と揃える）。
        private static readonly Color Panel = Hex(0x18211CFF);
        private static readonly Color PanelDeep = Hex(0x202A24FF);
        private static readonly Color Chalk = Hex(0xE8ECEAFF);
        private static readonly Color Muted = Hex(0x9AA5A0FF);
        private static readonly Color Lamp = Hex(0xF5C64AFF);
        private static readonly Color Warn = Hex(0xE86A4AFF);
        private static readonly Color Divider = Hex(0x232E28FF);

        private const int Width = 420;
        private const int LogRows = 16;

        private UIDocument _doc;
        private VisualElement _root;
        private Label _p1, _p2, _p3, _p4, _p5;
        private long _renderedPitches = -1;
        private bool _renderedVisible;

        /// <summary>HUD を（無ければ作って）出す。Editorメニュー・<see cref="DebugBridge.ToggleHud"/> から。</summary>
        public static DebugHudOverlay Ensure()
        {
            var existing = FindObjectOfType<DebugHudOverlay>();
            if (existing != null) return existing;

            // UIDocument は OnEnable で rootVisualElement を作るので、PanelSettings を先に入れておく必要がある。
            // GameObject を非アクティブで用意 → 設定 → 起動、の順にしないと root が null のまま組めない。
            var go = new GameObject("~KokoSimDebugHud");
            go.SetActive(false);
            DontDestroyOnLoad(go);
            var doc = go.AddComponent<UIDocument>();
            doc.panelSettings = FindAnyPanelSettings();
            doc.sortingOrder = 1000;   // 常に最前面（計器なので何にも隠されない）
            var hud = go.AddComponent<DebugHudOverlay>();
            go.SetActive(true);
            return hud;
        }

        private void OnEnable()
        {
            _doc = GetComponent<UIDocument>();
            if (_doc == null || _doc.rootVisualElement == null)
            {
                UnityEngine.Debug.LogWarning("[KokoSim/Debug] HUD の UIDocument を用意できません（PanelSettings 不在）。");
                enabled = false;
                return;
            }
            BuildTree();
            _renderedPitches = -1;
            _renderedVisible = false;
        }

        private void Update()
        {
            // F1 トグル。HUD を開いた瞬間から観測を有効にする（開くまではゼロコスト）。
            // このプロジェクトは Input System 有効（activeInputHandler=1）なので旧 Input クラスは使えない。
            var kb = UnityEngine.InputSystem.Keyboard.current;
            if (kb != null && kb.f1Key.wasPressedThisFrame)
            {
                DebugTraceHub.HudVisible = !DebugTraceHub.HudVisible;
                if (DebugTraceHub.HudVisible) DebugTraceHub.Enabled = true;
            }

            var ring = DebugTraceHub.Ring;
            var visible = DebugTraceHub.HudVisible;
            var pitches = ring?.TotalPitches ?? 0;

            // 変化が無ければ描き直さない（毎フレームの文字列生成を避ける）。
            if (visible == _renderedVisible && pitches == _renderedPitches) return;
            _renderedVisible = visible;
            _renderedPitches = pitches;

            _root.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (visible) Render(ring);
        }

        private void Render(RingBufferTraceSink ring)
        {
            if (ring == null || ring.Latest == null)
            {
                _p1.text = "観測なし。DebugBridge.StartMatch か試合画面の開始を待っています。";
                _p2.text = _p3.text = _p4.text = _p5.text = "";
                return;
            }

            var t = ring.Latest;
            var c = System.Globalization.CultureInfo.InvariantCulture;

            _p1.text = string.Format(c,
                "P1 PITCH  #{0}  {1}回{2} {3}out  cnt {4}-{5}\n" +
                "  plan {6,-8} {7,6:F1}km/h  aim({8,6:F2},{9,5:F2}) stuff {10,5:F2}\n" +
                "  act  ({11,6:F2},{12,5:F2}) {13}  ft {14:F3}  ivb {15:F3}  ihb {16:F3}\n" +
                "  batter {17}  vs {18}",
                t.PitchNoInGame, t.Inning, t.IsTop ? "表" : "裏", t.Outs, t.BallsBefore, t.StrikesBefore,
                t.PlanType, t.PlanVelocityKmh, t.PlanAimX, t.PlanAimY, t.PlanStuff,
                t.ActualX, t.ActualY, t.InZone ? "ZONE" : "out ",
                t.FlightTimeSeconds, t.InducedVerticalBreakM, t.InducedHorizontalBreakM,
                t.BatterId, t.PitcherId);

            var hit = t.ExitVelocityKmh.HasValue
                ? string.Format(c, "  ev {0:F1}  la {1:F1}  sa {2:F1}",
                    t.ExitVelocityKmh.Value, t.LaunchAngleDeg ?? 0, t.SprayAngleDeg ?? 0)
                : "";
            _p2.text = string.Format(c,
                "P2 BAT  pSw {0:F3}  {1}  {2}  → {3}{4}",
                t.SwingProbability, t.Swung ? "SWING" : "take ",
                !t.InZone && t.Swung ? "chase" : "     ", t.Kind, hit);

            _p3.text = string.Format(c,
                "P3 ST   press {0,3}  rattled {1}  pf {2,3}  gear {3}  pol {4}",
                t.PressureIndex, t.Rattled ? "YES" : "-", t.PitchingFatigue, t.Gear, t.Policy);
            // 動揺は「本当の警告」なので警告色を使う（状態を一目で拾えるように）。
            _p3.style.color = t.Rattled ? Warn : Muted;

            _p4.text = "P4 AI   sign " + (t.ChosenSign ?? "-")
                       + (t.SignCandidatesCsv == null ? "" : "\n  " + t.SignCandidatesCsv);

            var h = ring.Header;
            _p5.text = string.Format(c,
                "P5 REPRO  fixture {0}  draws/pitch {1}\n  rng {2}…\n  master {3}",
                h?.FixtureFingerprint ?? "-", t.RngDrawsInPitch,
                h == null || h.RngStateHex.Length < 16 ? "-" : h.RngStateHex.Substring(0, 16),
                Shell.GameSeed.MasterHex);

            RenderLog(ring, c);
        }

        /// <summary>
        /// P6 球ログ。列は<b>固定幅の Label を横に並べて</b>作る。
        /// プロジェクトに等幅フォントが無く、1本の文字列に空白を詰める方式では球種名の長さ差
        /// （Fork / Slider / Fastball）で桁が崩れるため、整列はフォントに頼らずレイアウトで取る。
        /// </summary>
        private void RenderLog(RingBufferTraceSink ring, System.Globalization.CultureInfo c)
        {
            var recent = ring.RecentPitches(LogRows);
            EnsureLogRows(recent.Count);

            for (var row = 0; row < _logRows.Count; row++)
            {
                var cells = _logRows[row];
                var visible = row < recent.Count;
                cells[0].parent.style.display = visible ? DisplayStyle.Flex : DisplayStyle.None;
                if (!visible) continue;

                var r = recent[recent.Count - 1 - row];   // 新しい順（直近が上）
                cells[0].text = r.PitchNoInGame.ToString(c);
                cells[1].text = r.Inning.ToString(c) + (r.IsTop ? "表" : "裏");
                cells[2].text = r.BallsBefore.ToString(c) + "-" + r.StrikesBefore.ToString(c);
                cells[3].text = r.PlanType.ToString();
                cells[4].text = r.PlanVelocityKmh.ToString("F1", c);
                cells[5].text = r.InZone ? "Y" : "-";
                cells[6].text = r.SwingProbability.ToString("F2", c);
                cells[7].text = r.Swung ? "Y" : "-";
                cells[8].text = r.Kind.ToString();
                // 見送ったのに振るはずだった球（打者判断モデルの検算対象）を一目で拾えるようにする。
                cells[6].style.color = !r.Swung && r.SwingProbability > 0.7 ? Lamp : Muted;
            }
        }

        private void BuildTree()
        {
            _root = new VisualElement
            {
                name = "debug-hud",
                style =
                {
                    position = Position.Absolute,
                    right = 0, top = 0, bottom = 0, width = Width,
                    backgroundColor = Panel,
                    display = DisplayStyle.None,
                },
            };
            _root.style.borderLeftWidth = 1;
            _root.style.borderLeftColor = Divider;
            _doc.rootVisualElement.Add(_root);

            var title = MakeLabel("KOKOSIM DEBUG HUD  [F1]", Lamp, 12);
            title.style.backgroundColor = PanelDeep;
            title.style.paddingLeft = 6;
            title.style.paddingTop = 4;
            title.style.paddingBottom = 4;
            _root.Add(title);

            _p1 = AddPane(Chalk);
            _p2 = AddPane(Chalk);
            _p3 = AddPane(Muted);
            _p4 = AddPane(Muted);
            _p5 = AddPane(Muted);

            _logHost = new VisualElement { style = { flexGrow = 1, backgroundColor = PanelDeep, paddingTop = 4 } };
            _logHost.Add(MakeLogRow(LogHeaders, Muted));
            _root.Add(_logHost);
        }

        // 列幅[px]と見出し。数値列は右揃え・文字列列は左揃えで、フォントに依らず桁が揃う。
        private static readonly int[] LogColumnWidths = { 38, 34, 30, 58, 42, 16, 34, 16, 90 };
        private static readonly string[] LogHeaders = { "N", "i", "cnt", "ty", "kmh", "z", "pSw", "sw", "res" };

        private VisualElement _logHost;
        private readonly System.Collections.Generic.List<Label[]> _logRows = new System.Collections.Generic.List<Label[]>();

        private void EnsureLogRows(int needed)
        {
            while (_logRows.Count < LogRows)
            {
                var cells = new Label[LogColumnWidths.Length];
                var row = MakeLogRow(null, Chalk, cells);
                _logHost.Add(row);
                _logRows.Add(cells);
            }
        }

        private VisualElement MakeLogRow(string[] texts, Color color, Label[] outCells = null)
        {
            var row = new VisualElement { style = { flexDirection = FlexDirection.Row, paddingLeft = 6 } };
            for (var i = 0; i < LogColumnWidths.Length; i++)
            {
                var l = new Label(texts == null ? "" : texts[i]);
                l.style.color = color;
                l.style.fontSize = 10;
                l.style.width = LogColumnWidths[i];
                // 数値列（N/kmh/pSw）は右揃え。桁を揃えるのは「数字が主役」の基本。
                l.style.unityTextAlign = i == 0 || i == 4 || i == 6
                    ? TextAnchor.MiddleRight
                    : TextAnchor.MiddleLeft;
                row.Add(l);
                if (outCells != null) outCells[i] = l;
            }
            return row;
        }

        private Label AddPane(Color color)
        {
            var label = MakeLabel("", color, 11);
            label.style.paddingTop = 4;
            label.style.paddingBottom = 4;
            label.style.borderBottomWidth = 1;
            label.style.borderBottomColor = Divider;
            _root.Add(label);
            return label;
        }

        private static Label MakeLabel(string text, Color color, int size)
        {
            var l = new Label(text);
            l.style.color = color;
            l.style.fontSize = size;
            l.style.paddingLeft = 6;
            l.style.paddingRight = 6;
            l.style.whiteSpace = WhiteSpace.Normal;
            return l;
        }

        private static PanelSettings FindAnyPanelSettings()
        {
            foreach (var d in FindObjectsOfType<UIDocument>())
            {
                if (d.panelSettings != null) return d.panelSettings;
            }
            UnityEngine.Debug.LogWarning("[KokoSim/Debug] PanelSettings が見つかりません。HUD は描画されません。");
            return null;
        }

        private static Color Hex(uint rgba) => new Color(
            ((rgba >> 24) & 0xFF) / 255f, ((rgba >> 16) & 0xFF) / 255f,
            ((rgba >> 8) & 0xFF) / 255f, (rgba & 0xFF) / 255f);
    }
}
#endif
