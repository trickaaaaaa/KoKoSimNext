#if UNITY_EDITOR
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using KokoSim.Engine.Core;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Lineup
{
    /// <summary>
    /// 【開発用・エディタ限定】オーダー画面のスコアブック試作スキン（2026-11-09）の確認撮影。
    /// 成績なし／比較（モックと同じ場面）／投手タブ／控え選択／守備位置選択／DHなしの先発選択を撮る。
    /// 成績は撮影の間だけ通算帳簿に足して出す。出力は screenshots/order-restyle/。
    /// </summary>
    public sealed class OrderRestyleShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/order-restyle";
        private const int PrefectureId = 13;   // 神奈川（HomeState と同じ）
        private const int W = 1600;
        private const int H = 900;

        private static readonly BindingFlags Priv = BindingFlags.Instance | BindingFlags.NonPublic;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);
            for (var i = 0; i < 10; i++) yield return null;

            SetUpTournament();

            MonoBehaviour lineup = null;
            yield return Focus("LineupSetting", r => lineup = r);
            if (lineup == null) { Debug.LogError("[order] LineupSetting が見つからない"); yield break; }

            var state = lineup.GetType().GetField("_state", Priv)?.GetValue(lineup) as LineupSettingState;
            if (state == null) { Debug.LogError("[order] state が無い"); yield break; }

            // 成績なし・DHなし・未選択
            Select(lineup, picked: -1, hovered: -1, tab: 0);
            yield return Capture(lineup, "01-dh-off-empty");

            // 成績あり（打撃は選手ごとにばらつかせる。投手は 42 1/3回・自責11・奪三振45）
            AddPitchingToAll();
            AddBattingToAll();
            ToggleDh(lineup);
            var v = state.BuildView();
            // モックと同じ場面：2番を選択（自チーム側）＋控えの先頭にカーソル（比較相手）
            Select(lineup, picked: v.Rows[1].PlayerIndex, hovered: v.Bench[0].Index, tab: 0);
            yield return Capture(lineup, "02-dh-on-compare");
            AuditFonts(lineup);

            // 投手能力タブ（持ち球チャート）
            Select(lineup, picked: v.Rows[1].PlayerIndex, hovered: v.Bench[0].Index, tab: 1);
            yield return Capture(lineup, "03-pitcher-tab");

            // 控えを選んだ状態（墨の枠）＋守備位置チップの選択
            Select(lineup, picked: -1, hovered: -1, tab: 0);
            state.ClickBench(v.Bench[1].Index);
            Render(lineup);
            yield return Capture(lineup, "04-bench-picked");
            state.ClickBench(v.Bench[1].Index);   // 解除
            state.ClickPosition(3);
            Render(lineup);
            yield return Capture(lineup, "05-pos-picked");
            state.ClickPosition(3);

            // DHなしで先発カードを選択中（打順内の投手と連動）
            ToggleDh(lineup);
            state.ClickStartingPitcher();
            Render(lineup);
            yield return Capture(lineup, "06-dh-off-sp-picked");

            Debug.Log("[order] DONE");
        }

        /// <summary>
        /// 書体の監査：画面上の文字要素の解決済みフォントを数え、Noto Sans JP（本文・数字）と
        /// Shippori Mincho B1（名前・打順・題字）以外が使われていたら一覧を出す。
        /// </summary>
        private static void AuditFonts(MonoBehaviour ctrl)
        {
            var root = ctrl.GetComponent<UIDocument>().rootVisualElement;
            var counts = new Dictionary<string, int>();
            var stray = new System.Text.StringBuilder();
            root.Query<TextElement>().ForEach(t =>
            {
                if (string.IsNullOrEmpty(t.text) || t.resolvedStyle.display == DisplayStyle.None) return;
                var fa = t.resolvedStyle.unityFontDefinition.fontAsset;
                var name = fa != null ? fa.name : (t.resolvedStyle.unityFontDefinition.font != null ? "font:" + t.resolvedStyle.unityFontDefinition.font.name : "(none)");
                counts[name] = counts.TryGetValue(name, out var c) ? c + 1 : 1;
                if (!name.StartsWith("NotoSansJP") && !name.StartsWith("ShipporiMinchoB1"))
                    stray.Append("\n  ").Append(name).Append(" <- \"").Append(t.text).Append("\" ").Append(string.Join(".", t.GetClasses()));
            });
            var sb = new System.Text.StringBuilder("[order] fonts:");
            foreach (var kv in counts) sb.Append(" ").Append(kv.Key).Append("=").Append(kv.Value);
            sb.Append(stray.Length == 0 ? "\n  stray: none" : "\n  stray:" + stray);
            Debug.Log(sb.ToString());
        }

        private static void Render(MonoBehaviour ctrl) => ctrl.GetType().GetMethod("Render", Priv)?.Invoke(ctrl, null);

        /// <summary>撮影用: 部員全員の通算帳簿に打撃成績を足す（打率 .158〜.538 くらいに散らす）。</summary>
        private static void AddBattingToAll()
        {
            var book = GameSession.Current.Stats.Career;
            var forM = book.GetType().GetMethod("For", Priv);
            if (forM == null) return;
            var k = 0;
            foreach (var p in RosterService.Active)
            {
                var ps = forM.Invoke(book, new object[] { p.Id }) as KokoSim.Engine.Stats.PlayerStats;
                var ab = 13 + (k % 4) * 2;
                var hits = 2 + (k * 7) % 6;
                ps?.Batting.Add(new KokoSim.Engine.Match.Game.BattingLine(
                    1, KokoSim.Engine.Match.Field.FieldPosition.CenterField, p.Name,
                    PlateAppearances: ab + 2, AtBats: ab, Hits: hits, Doubles: 1, Triples: 0, HomeRuns: k % 7,
                    Rbi: 1 + (k * 5) % 16, Walks: 2, StrikeOuts: 3, SourceId: p.Id));
                k++;
            }
        }

        /// <summary>撮影用: 部員全員の通算帳簿に投手成績を1試合ぶん足す（StatBook.For は非公開＝反射で呼ぶ）。</summary>
        private static void AddPitchingToAll()
        {
            var book = GameSession.Current.Stats.Career;
            var forM = book.GetType().GetMethod("For", Priv);
            if (forM == null) { Debug.LogError("[order] StatBook.For が見つからない"); return; }
            foreach (var p in RosterService.Active)
            {
                var ps = forM.Invoke(book, new object[] { p.Id }) as KokoSim.Engine.Stats.PlayerStats;
                ps?.Pitching.Add(new KokoSim.Engine.Match.Game.PitchingLine(
                    p.Name, Outs: 127, BattersFaced: 170, Hits: 35, Runs: 13, StrikeOuts: 45, Walks: 12, Pitches: 640,
                    SourceId: p.Id, EarnedRuns: 11), started: true);
            }
        }

        // 比較の左右（picked=左, hovered=右）とタブを差し込んで再描画する。
        private static void Select(MonoBehaviour ctrl, int picked, int hovered, int tab)
        {
            var state = ctrl.GetType().GetField("_state", Priv)?.GetValue(ctrl);
            if (state == null) return;
            state.GetType().GetField("_picked", Priv)?.SetValue(state, picked);
            // 選択の種類も合わせる（0=None, 1=OrderPlayer）。種類が None のままだと行が選択表示にならない。
            var kind = state.GetType().GetField("_pickKind", Priv);
            kind?.SetValue(state, System.Enum.ToObject(kind.FieldType, picked >= 0 ? 1 : 0));
            state.GetType().GetField("_hovered", Priv)?.SetValue(state, hovered);
            state.GetType().GetField("_tab", Priv)?.SetValue(state, tab);
            ctrl.GetType().GetMethod("Render", Priv)?.Invoke(ctrl, null);
        }

        private static void ToggleDh(MonoBehaviour ctrl)
        {
            var state = ctrl.GetType().GetField("_state", Priv)?.GetValue(ctrl) as LineupSettingState;
            state?.ToggleDh();
        }

        private static void SetUpTournament()
        {
            var roster = RosterService.Active;
            var manager = new School
            {
                Id = -1,
                Name = NationService.ManagerSchoolName,
                PrefectureId = PrefectureId,
                Strength = TeamOverall.Of(roster),
            };

            var coeff = new NationCoefficients();
            var nation = NationGenerator.Generate(
                SchoolNameVocabProvider.Default, coeff, new Xoshiro256Random(2026));

            var field = new List<School> { manager };
            foreach (var s in nation.InPrefecture(PrefectureId))
            {
                if (s.Id == manager.Id) continue;
                field.Add(s);
            }

            var runner = new TournamentRunner(
                field, manager, coeff, new Xoshiro256Random(4242), new TournamentSchedule(),
                "2027年 夏の神奈川県大会", new PlayerMatchResolver());

            GameSession.Current.Year = 1;
            GameSession.Current.EnterTournament(
                TournamentKind.Summer, "2027年 夏の神奈川県大会", runner, field);
            GameSession.Current.ConsumeBanner();
        }

        private static void SetUpLineup(bool useDh)
        {
            var state = new LineupSettingState();
            if (useDh) state.ToggleDh();
            GameSession.Current.Lineup = state.ToLineupSpec();
        }

        private IEnumerator Capture(MonoBehaviour ctrl, string file)
        {
            yield return null;
            yield return new WaitForEndOfFrame();

            var ps = ctrl.GetComponent<UIDocument>().panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(W, H);
            ps.targetTexture = rt;

            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;
            yield return new WaitForEndOfFrame();

            Texture2D tex = null;
            try
            {
                tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                File.WriteAllBytes(Path.Combine(Dir, file + ".png"), tex.EncodeToPNG());
                Debug.Log("[order] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
        }

        // ScreenRouter 経由で切り替えたうえで、他画面の UIDocument が同フレームに割り込まないよう再保証する。
        private static IEnumerator Focus(string screen, System.Action<MonoBehaviour> onReady)
        {
            if (ScreenRouter.Instance != null) ScreenRouter.Instance.Show(screen);
            var ctrl = Activate(screen);
            yield return null; yield return null;
            ctrl = Activate(screen) ?? ctrl;   // ルータが同フレームで別画面を出しても取り戻す
            yield return null; yield return null;
            onReady(ctrl);
        }

        // 対象画面だけを表示する（他画面の UIDocument が重なると描画が競合する）。
        private static MonoBehaviour Activate(string screen)
        {
            MonoBehaviour found = null;
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                var on = d.gameObject.name == screen;
                d.gameObject.SetActive(on);
                if (!on) continue;
                foreach (var mb in d.GetComponents<MonoBehaviour>())
                    if (mb != null && mb.GetType().Name.EndsWith("Controller")) found = mb;
            }
            return found;
        }
    }
}
#endif
