#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Home
{
    /// <summary>
    /// 【開発用・エディタ限定】イベントのノベル提示（issue #609）の受け入れスクショ。
    /// 固定イベント（夏合宿・週18）を発火させ、会話を1行ずつ送りながら撮る。
    /// 地の文（立ち絵なし）と話者あり、最終行の「クリックで閉じる」、閉じた後のホーム復帰を確認する。使い捨て。
    /// </summary>
    public sealed class EventNovelShooter : MonoBehaviour
    {
        private const string Dir = "screenshots/event-novel";
        private const string SpawnFlag = "KokoSim.EventNovelShooter.Spawn";
        private const int W = 1600;
        private const int H = 900;

        public static void Arm() => UnityEditor.EditorPrefs.SetBool(SpawnFlag, true);

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            if (!UnityEditor.EditorPrefs.GetBool(SpawnFlag, false)) return;
            UnityEditor.EditorPrefs.SetBool(SpawnFlag, false);
            new GameObject("~EventNovelAuto").AddComponent<EventNovelShooter>();
        }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            Directory.CreateDirectory(Dir);

            GameClock.Reset();          // 週0＝入学式（固定イベント）。抽選を通さず必ず出る
            EventService.Clear();
            NewTeamService.Clear();     // 引退週（週18）を踏むと指名モーダルが先に出てノベルが隠れる
            Debug.Log("[EV] table=" + EventTableProvider.Events.Count + " 件"
                + " roster=" + RosterService.Active.Count);

            var feed = EventService.RunWeek();
            Debug.Log("[EV] week=" + GameClock.Week + " fired=" + feed.Count
                + " pending=" + EventService.Pending.Count);
            foreach (var f in feed) Debug.Log("[EV] feed: " + f);
            if (!EventService.HasPending) { Debug.LogError("[EV] 週18でイベントが発火しなかった"); yield break; }

            var doc = Open("HomeDashboard");
            if (doc == null) { Debug.LogError("[EV] HomeDashboard が見つからない"); yield break; }
            yield return null; yield return null;

            var novel = doc.rootVisualElement.Q<VisualElement>("ev-novel");
            if (novel == null) { Debug.LogError("[EV] ev-novel が無い"); yield break; }
            // ★ 開閉の判定は resolvedStyle で見る。style.display は<b>インラインの指定</b>しか返さず、
            //    USS の `.ev { display: none }` は映らないので、閉じていても Flex に見える。
            if (novel.resolvedStyle.display == DisplayStyle.None) Debug.LogError("[EV] ノベル面が開いていない");

            // 本文域が隠れていること（案B＝トップバーは残る）。
            var dash = doc.rootVisualElement.Q<VisualElement>("home-dash");
            Debug.Log("[EV] dash display=" + (dash == null ? "null" : dash.resolvedStyle.display.ToString()));

            // 開いている間ずっと1行ずつ撮る（同じ週に2件発火することもあるので件数で回さない）。
            for (var i = 0; i < 12 && novel.resolvedStyle.display != DisplayStyle.None; i++)
            {
                Debug.Log("[EV] shot " + i + " progress=" + Text(doc, "ev-progress")
                    + " speaker=" + Text(doc, "ev-speaker") + " say=" + Text(doc, "ev-say")
                    + " hint=" + Text(doc, "ev-hint"));
                yield return Capture(doc, string.Format("{0:00}-line", i));
                Click(novel);
                yield return null; yield return null;
            }

            // 最終行の後は閉じてホームへ戻る（本文域が復帰し、フィードに1行残っている）。
            Debug.Log("[EV] closed display=" + novel.resolvedStyle.display
                + " dash=" + (dash == null ? "null" : dash.resolvedStyle.display.ToString())
                + " pending=" + EventService.Pending.Count);
            yield return Capture(doc, "90-home-after");

            // ── 実経路の確認: 「今週を進める」から発火→ノベル→通知フィードまで通す ──
            // ここまでは RunWeek を直接叩いていたので、週送りへの配線とフィード投入は測れていない。
            var advance = doc.rootVisualElement.Q<Button>("advance");
            if (advance == null) { Debug.LogError("[EV] 週送りボタンが無い"); yield break; }

            for (var w = 0; w < 8 && novel.resolvedStyle.display == DisplayStyle.None; w++)
            {
                Submit(advance);
                yield return null; yield return null;
                Debug.Log("[EV] advance week=" + GameClock.Week
                    + " novel=" + novel.resolvedStyle.display + " pending=" + EventService.Pending.Count);
            }

            if (novel.resolvedStyle.display == DisplayStyle.None)
            {
                Debug.LogWarning("[EV] 8週送っても1件も発火しなかった（抽選は毎週50%）");
            }
            else
            {
                Debug.Log("[EV] via advance: speaker=" + Text(doc, "ev-speaker") + " say=" + Text(doc, "ev-say"));
                yield return Capture(doc, "91-advance-fired");
                for (var i = 0; i < 12 && novel.resolvedStyle.display != DisplayStyle.None; i++)
                {
                    Click(novel);
                    yield return null; yield return null;
                }
                yield return Capture(doc, "92-feed-after");   // 通知フィードに要約行が残っていること
            }

            // ── 効果帯（案A2・issue #610）──
            // 故障は確率で起きるので、絵の確認だけは主役つき提示を直接叩いて確実に撮る。
            var subject = RosterService.Active.Count > 0 ? RosterService.Active[0] : null;
            if (subject != null)
            {
                EventService.BeginWeek();
                EventService.Push("injury_occurred", subject.Name, new PresentedEffect
                {
                    Subject = subject.Name,
                    Detail = "右足首 捻挫（軽度）",
                    Items = new[] { new EffectItem("全治", "2", "週") },
                }, subject.Id);

                var home = Open("HomeDashboard");
                yield return null; yield return null;
                novel = home.rootVisualElement.Q<VisualElement>("ev-novel");
                var band = home.rootVisualElement.Q<VisualElement>("ev-effect");
                Debug.Log("[EV] effect band display=" + (band == null ? "null" : band.resolvedStyle.display.ToString()));
                for (var i = 0; i < 6 && novel.resolvedStyle.display != DisplayStyle.None; i++)
                {
                    yield return Capture(home, string.Format("A{0:00}-injury", i));
                    Click(novel);
                    yield return null; yield return null;
                }
            }

            // ── 成長イベントの効果帯（issue #611）──
            // 覚醒・スランプは週あたり 0.15〜0.4% の低確率なので、絵の確認は主役つき提示を直接叩く。
            if (subject != null)
            {
                EventService.BeginWeek();
                var awake = new KokoSim.Engine.Season.GrowthNotice(
                    subject, KokoSim.Engine.Season.GrowthEventType.Awakening,
                    KokoSim.Engine.Season.AbilityKind.Power);
                var coeff = new KokoSim.Engine.Season.GrowthEventCoefficients();
                EventService.Push(GrowthEventText.EventId(awake.Type), subject.Name,
                    GrowthEventText.Effect(awake, coeff), subject.Id);
                Debug.Log("[EV] 成長フィード: " + GrowthEventText.Feed(awake, coeff));

                var home2 = Open("HomeDashboard");
                yield return null; yield return null;
                var nov3 = home2.rootVisualElement.Q<VisualElement>("ev-novel");
                for (var i = 0; i < 6 && nov3.resolvedStyle.display != DisplayStyle.None; i++)
                {
                    yield return Capture(home2, string.Format("B{0:00}-growth", i));
                    Click(nov3);
                    yield return null; yield return null;
                }
            }

            // ── 監督成長の節目（issue #612）──
            // 3月の総括まで48週送るのは大会を跨いで重いので、年度更新だけを直接回して節目を起こし、
            // **提示は実経路**（週送り → RunManagerGrowthFeed）に通す。
            for (var y = 1; y <= 12 && ManagerService.LastGrowthNotices.Count == 0; y++)
                ManagerService.ApplyYearlyGrowth(y);
            Debug.Log("[EV] 監督の節目: " + ManagerService.LastGrowthNotices.Count + " 件");
            if (ManagerService.LastGrowthNotices.Count > 0)
            {
                var doc3 = Open("HomeDashboard");
                yield return null; yield return null;
                var nov4 = doc3.rootVisualElement.Q<VisualElement>("ev-novel");
                var adv2 = doc3.rootVisualElement.Q<Button>("advance");
                Submit(adv2);
                yield return null; yield return null;

                var shot = 0;
                for (var i = 0; i < 12 && nov4.resolvedStyle.display != DisplayStyle.None; i++)
                {
                    if (Text(doc3, "ev-fx-subject").Length > 0 || i == 0)
                    {
                        Debug.Log("[EV] 監督ノベル: " + Text(doc3, "ev-speaker") + " / " + Text(doc3, "ev-say")
                            + " / 帯=" + Text(doc3, "ev-fx-subject") + " " + Text(doc3, "ev-fx-detail"));
                        yield return Capture(doc3, string.Format("C{0:00}-manager", shot++));
                    }
                    Click(nov4);
                    yield return null; yield return null;
                }
            }

            // ── ドラフト（issue #617）──
            // 指名は10月最終週にしか走らないので、結果だけ据えて**提示は実経路**（週送り → RunDraftFeed）に通す。
            if (RosterService.Active.Count >= 2)
            {
                var a = RosterService.Active[0];
                var b = RosterService.Active[1];
                DraftService.LastNomination = new KokoSim.Engine.Career.Draft.DraftResult(1, new[]
                {
                    new KokoSim.Engine.Career.Draft.DraftPick(
                        a.Id, a.Name, 78.0, KokoSim.Engine.Career.Draft.DraftRankBand.UpperRound, true, 3),
                    new KokoSim.Engine.Career.Draft.DraftPick(
                        b.Id, b.Name, 41.0, KokoSim.Engine.Career.Draft.DraftRankBand.LowerRound, false, 0),
                });

                var doc4 = Open("HomeDashboard");
                yield return null; yield return null;
                var nov5 = doc4.rootVisualElement.Q<VisualElement>("ev-novel");
                var adv3 = doc4.rootVisualElement.Q<Button>("advance");
                Submit(adv3);
                yield return null; yield return null;

                var shot = 0;
                for (var i = 0; i < 14 && nov5.resolvedStyle.display != DisplayStyle.None; i++)
                {
                    Debug.Log("[EV] ドラフト: " + Text(doc4, "ev-speaker") + " / " + Text(doc4, "ev-say")
                        + " / 帯=" + Text(doc4, "ev-fx-subject") + " " + Text(doc4, "ev-fx-detail"));
                    yield return Capture(doc4, string.Format("D{0:00}-draft", shot++));
                    Click(nov5);
                    yield return null; yield return null;
                }
            }

            // ── 実経路の確認（怪我）──
            // 週送りで実際に故障が起きたときにノベルが出ることを、12週ぶん回して観測する。
            // 出なければ「この12週では起きなかった」とだけ言う（確率なので失敗にはしない）。
            var seen = 0;
            for (var w = 0; w < 12; w++)
            {
                var doc2 = Open("HomeDashboard");
                yield return null; yield return null;
                var nov2 = doc2.rootVisualElement.Q<VisualElement>("ev-novel");
                var btn = doc2.rootVisualElement.Q<Button>("advance");
                if (btn == null) break;
                Submit(btn);
                yield return null; yield return null;
                foreach (var p in EventService.Pending)
                    if (p.Id.StartsWith("injury_") || p.Id.StartsWith("growth_"))
                    { seen++; Debug.Log("[EV] 実経路で発火: " + p.Id + " / " + p.Lines[0].Say); }
                for (var i = 0; i < 12 && nov2.resolvedStyle.display != DisplayStyle.None; i++)
                {
                    Click(nov2);
                    yield return null; yield return null;
                }
            }
            Debug.Log("[EV] 実経路の怪我イベント: " + seen + " 件（12週）");

            Debug.Log("[EV] DONE all shots");
        }

        private static string Text(UIDocument doc, string name)
        {
            var l = doc.rootVisualElement.Q<Label>(name);
            return l == null ? "(null)" : l.text;
        }

        // ノベル面は PointerDownEvent で進む（UI-BUILD-METHOD の落とし穴＝ClickEvent は down/up の対に依存する）。
        // Button は Clickable マニピュレータ経由なので合成 ClickEvent では発火しない
        // （NewTeamShooter と同じ作法＝決定と同じ NavigationSubmitEvent を送る）。
        private static void Submit(VisualElement target)
        {
            using (var e = NavigationSubmitEvent.GetPooled())
            {
                e.target = target;
                target.SendEvent(e);
            }
        }

        private static void Click(VisualElement target)
        {
            using (var e = PointerDownEvent.GetPooled())
            {
                e.target = target;
                target.SendEvent(e);
            }
        }

        private static UIDocument Open(string screen)
        {
            UIDocument target = null;
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == screen) target = d;
                d.gameObject.SetActive(false);
            }
            if (target != null) target.gameObject.SetActive(true);
            return target;
        }

        private IEnumerator Capture(UIDocument doc, string file)
        {
            var ps = doc.panelSettings;
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
                Debug.Log("[EV] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
        }
    }
}
#endif
