using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Components; // 部品辞書（RankChip / AbilityRow）

namespace KokoSim.Unity.Home
{
    /// <summary>
    /// ホーム ダッシュボード（設計書06, mock-ui-v2-dashboard.html）。UIDocument へ HomeState を束ね、
    /// スコアボードヘッダー・次の試合・部の状態ゲージ・注目選手・通知フィード・練習計画・個別指導を描画する。
    /// 「今週を進める」で週送り→育成・イベントをフィードに流し再描画する。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class HomeDashboardController : MonoBehaviour
    {
        private static readonly Color Good = new Color(0.624f, 0.796f, 0.231f);   // #9FCB3B
        private static readonly Color Warn = new Color(0.910f, 0.416f, 0.290f);   // #E86A4A
        private static readonly Color Amber = new Color(0.961f, 0.776f, 0.290f);  // #F5C64A
        private static readonly Color Sub = new Color(0.616f, 0.702f, 0.627f);    // #9DB3A0

        private HomeState _state;
        private VisualElement _root;
        private VisualElement _banner, _dialog, _result;

        // 登録したハンドラ（OnDisable で必ず外す）。ScreenRouter は同じ GameObject を SetActive で付け外し
        // するだけなので、外さないと画面往復のたびに多重登録され「1クリックで複数日進む」不具合になる。
        private Button _advanceBtn, _dialogYes, _dialogNo, _resultOk;
        private VisualElement _tsRow;
        private EventCallback<ClickEvent> _tsRowClick;

        private void OnEnable()
        {
            _state = new HomeState();
            _root = GetComponent<UIDocument>().rootVisualElement;

            _banner = _root.Q<VisualElement>("tm-banner");
            _dialog = _root.Q<VisualElement>("tm-dialog");
            _result = _root.Q<VisualElement>("tm-result");

            _advanceBtn = _root.Q<Button>("advance");
            if (_advanceBtn != null) _advanceBtn.clicked += OnAdvance;

            _dialogYes = Wire("tm-dialog-yes", OnMatchYes);
            _dialogNo = Wire("tm-dialog-no", OnMatchNo);
            _resultOk = Wire("tm-result-ok", OnResultOk);

            // 部の状態「チーム総合力」行 → 6角形の専用パネルへ（行クリックで詳細）。
            _tsRow = _root.Q<VisualElement>("team-strength-row");
            if (_tsRow != null)
            {
                _tsRowClick = _ => KokoSim.Unity.Shell.ScreenRouter.Instance?.ShowDeferred("TeamStrength");
                _tsRow.RegisterCallback(_tsRowClick);
            }

            Render();

            var session = KokoSim.Unity.Shell.GameSession.Current;
            if (session.AwaitingMatchStart)
            {
                // スタメン設定画面から戻ってきた（試合開始フローの中継）→ 実試合をライブ観戦へ渡す。
                session.AwaitingMatchStart = false;
                StartLiveMatch();
            }
            else if (session.ResultPending && session.LastOutcome != null)
            {
                // ライブ観戦から戻ってきた直後 → 試合結果を表示する。
                ShowResult(session.LastOutcome);
            }
        }

        // 自校戦をライブ観戦（打席単位・2D俯瞰）で開始する。終局で結果を大会へ戻し、ホームへ帰って結果表示する。
        private void StartLiveMatch()
        {
            var router = KokoSim.Unity.Shell.ScreenRouter.Instance;
            if (router == null)
            {
                // フォールバック（ルータ不在）：従来どおり一括消化して即結果表示。
                var outcome = _state.PlayMatch();
                Render();
                ShowResult(outcome);
                return;
            }

            var live = _state.BeginMatch();
            var homeState = _state;   // クロージャで捕捉（この HomeState 経由で大会へ結果を戻す）
            KokoSim.Unity.Match.MatchLiveController.Pending = new KokoSim.Unity.Match.MatchLiveController.LiveMatchRequest
            {
                Progression = live.Progression,
                ManagerIsAway = live.ManagerIsAway,
                AwayName = live.ManagerIsAway ? "桜丘" : live.OpponentName,
                HomeName = live.ManagerIsAway ? live.OpponentName : "桜丘",
                ManagerTacticalSense = 70,
                OnComplete = result =>
                {
                    homeState.CompleteMatch(result);                 // 大会へ結果反映＋成績畳み込み＋ResultPending
                    // 「戻る」のクリック配信中なので同期 Show は不可（イベント木が壊れて全画面が落ちる）。
                    KokoSim.Unity.Shell.ScreenRouter.Instance?.ShowDeferred("HomeDashboard");   // 戻ると OnEnable が結果表示
                },
            };
            // この StartLiveMatch は HomeDashboard.OnEnable（＝スタメンOKの Show("HomeDashboard") の内側）から
            // 走るため、ここで Show("MatchLive") を同期呼びすると Show がネストして全画面が非アクティブに落ちる。
            // 次フレームへ遅延して配信外で切り替える（ScreenRouter の再入対策と同じ機構）。
            router.ShowDeferred("MatchLive");
        }

        private Button Wire(string name, System.Action handler)
        {
            var btn = _root.Q<Button>(name);
            if (btn != null) btn.clicked += handler;
            return btn;
        }

        // 画面を離れるときに登録を解除する（OnEnable が毎回登録するため、外さないと往復のたび多重登録になる）。
        private void OnDisable()
        {
            if (_advanceBtn != null) _advanceBtn.clicked -= OnAdvance;
            if (_dialogYes != null) _dialogYes.clicked -= OnMatchYes;
            if (_dialogNo != null) _dialogNo.clicked -= OnMatchNo;
            if (_resultOk != null) _resultOk.clicked -= OnResultOk;
            if (_tsRow != null && _tsRowClick != null) _tsRow.UnregisterCallback(_tsRowClick);
            _tsRowClick = null;
        }

        // ===== 大会モード進行（要件1〜7） =====

        private void OnAdvance()
        {
            if (_state.InTournament)
            {
                _state.AdvanceDay();
                Render();
                if (_state.ReachedMatchDay) ShowDialog();     // 試合日に到達→開始確認（要件7・惰性防止）
            }
            else
            {
                _state.AdvanceWeek();
                Render();
                if (_state.BannerPending) ShowBanner();        // 大会開幕演出（要件1・2）
            }
        }

        private void ShowBanner()
        {
            _state.ConsumeBanner();
            if (_banner == null) return;
            SetText("tm-banner-top", _state.BannerTop);
            SetText("tm-banner-name", _state.BannerName);

            _banner.style.display = DisplayStyle.Flex;
            _banner.BringToFront();
            _banner.style.opacity = 0f;
            _banner.experimental.animation.Start(0f, 1f, 350, (e, v) => e.style.opacity = v);
            _banner.schedule.Execute(() =>
            {
                _banner.experimental.animation.Start(1f, 0f, 420, (e, v) => e.style.opacity = v)
                    .OnCompleted(() => _banner.style.display = DisplayStyle.None);
            }).ExecuteLater(1900);
        }

        private void ShowDialog()
        {
            if (_dialog == null) return;
            SetText("tm-dialog-round", _state.NextRoundLabel);
            SetText("tm-dialog-vs", _state.NextVsLabel);
            _dialog.style.display = DisplayStyle.Flex;
            _dialog.BringToFront();
        }

        private void OnMatchNo()
        {
            if (_dialog != null) _dialog.style.display = DisplayStyle.None;
        }

        private void OnMatchYes()
        {
            if (_dialog != null) _dialog.style.display = DisplayStyle.None;
            // 試合前スタメン設定画面へ遷移（タブ常設ではなく、この試合開始フローからのみ表示）。
            // 確定（OK）でホームへ戻り、AwaitingMatchStart を見て自校戦を消化する。
            var router = KokoSim.Unity.Shell.ScreenRouter.Instance;
            if (router != null)
            {
                KokoSim.Unity.Shell.GameSession.Current.AwaitingMatchStart = true;
                router.ShowDeferred("LineupSetting");   // 「はい」のクリック配信中なので遅延切替
                return;
            }
            // フォールバック（ルータ不在）：従来どおり即消化。
            var outcome = _state.PlayMatch();
            Render();
            ShowResult(outcome);
        }

        private void ShowResult(KokoSim.Engine.Nation.Tournaments.PlayerMatchOutcome o)
        {
            if (_result == null) return;
            SetText("tm-result-round", o.RoundName);

            var headline = _root.Q<Label>("tm-result-headline");
            if (headline != null)
            {
                var win = o.ManagerWon;
                headline.text = o.IsChampion ? "優勝！" : (win ? "勝利！" : "敗戦");
                headline.EnableInClassList("tm-result__headline--win", win);
                headline.EnableInClassList("tm-result__headline--lose", !win);
            }
            SetText("tm-result-score", "桜丘 " + o.ManagerScore + " － " + o.OpponentScore + " " + o.OpponentName);

            _result.style.display = DisplayStyle.Flex;
            _result.BringToFront();
        }

        private void OnResultOk()
        {
            if (_result != null) _result.style.display = DisplayStyle.None;
            _state.DismissResult();
            Render();
        }

        private void Render()
        {
            var v = _state.BuildView();

            // 進行ボタンはモードで文言を切替（通常=週送り / 大会=日送り）。
            var advance = _root.Q<Button>("advance");
            if (advance != null) advance.text = v.TournamentMode ? "1日進める ▶" : "今週を進める ▶";

            SetText("badge", v.Badge);
            SetText("school-name", v.SchoolName);
            SetText("pref", v.Prefecture);
            SetText("week", v.WeekLabel);
            SetText("countdown-k", v.CountdownLabel);
            SetText("countdown", v.CountdownValue);
            SetText("funds", v.Funds);
            SetText("fame", v.FameGrade);
            SetText("trust", v.TrustGrade);

            // チーム総合力ランクチップ。
            var rank = _root.Q<VisualElement>("team-rank");
            if (rank != null) { rank.Clear(); rank.Add(UiComponents.RankChipLegacy(v.TeamRankGrade)); }

            // チーム総合力（6指標の総合ランク）を部の状態に表示（クリックで専用パネル）。
            var tsChip = _root.Q<VisualElement>("team-strength-chip");
            if (tsChip != null)
            {
                var ts = new KokoSim.Unity.Squad.TeamStrengthState().BuildView();
                tsChip.Clear();
                tsChip.Add(UiComponents.RankChipLegacy(ts.OverallGrade));
            }

            // 次の試合。
            SetText("game-tag", v.GameTag);
            SetText("home-team", v.HomeTeam);
            SetText("away-team", v.AwayTeam);
            SetText("home-sub", v.HomeSub);
            SetText("away-sub", v.AwaySub);
            SetText("game-date", v.GameDate);
            SetText("venue", v.Venue);
            SetText("weather", "天気 " + v.Weather);

            // 部の状態。
            var inj = _root.Q<Label>("injuries");
            if (inj != null)
            {
                inj.text = v.Injuries + " 名" + (string.IsNullOrEmpty(v.InjuredNames) ? "" : "（" + v.InjuredNames + "）");
                inj.EnableInClassList("inj-row__v--warn", v.Injuries > 0);
            }

            BuildList("roster-rows", v.Roster, BuildNotable);
            BuildList("feed-list", v.Feed, BuildFeed);
            BuildList("plan-days", v.Plan, BuildDay);
            BuildList("guidance-list", v.Guidance, BuildGuidance);
            SetText("guidance-count", "残り枠 " + (v.GuidanceTotal - v.GuidanceUsed) + " / " + v.GuidanceTotal);
        }

        // ===== 行ビルダー =====

        private static VisualElement BuildNotable(RosterRow r)
        {
            var row = new VisualElement();
            row.AddToClassList("np-row");

            var no = new Label(string.IsNullOrEmpty(r.Number) ? "–" : r.Number);
            no.AddToClassList("np-no");
            row.Add(no);

            var body = new VisualElement();
            body.AddToClassList("np-body");
            var name = new Label(r.Name); name.AddToClassList("np-name");
            var pos = new Label(r.Position); pos.AddToClassList("np-pos");
            body.Add(name); body.Add(pos);
            row.Add(body);

            var cond = new Label(CondArrow(r.Condition));
            cond.AddToClassList("np-cond");
            cond.AddToClassList(CondClass(r.Condition));
            row.Add(cond);

            row.Add(UiComponents.RankChipLegacy(r.OverallGrade));
            return row;
        }

        private static VisualElement BuildFeed(FeedItem f)
        {
            var color = TagColor(f.Tag);
            var item = new VisualElement();
            item.AddToClassList("fd-item");

            var bar = new VisualElement();
            bar.AddToClassList("fd-bar");
            bar.style.backgroundColor = color;
            item.Add(bar);

            var body = new VisualElement();
            body.AddToClassList("fd-body");

            var meta = new VisualElement();
            meta.AddToClassList("fd-meta");
            var tag = new Label(f.Tag);
            tag.AddToClassList("fd-tag");
            tag.style.color = color;
            SetBorder(tag, color);
            var time = new Label(f.When);
            time.AddToClassList("fd-time");
            meta.Add(tag); meta.Add(time);
            body.Add(meta);

            var text = new Label(f.Text);
            text.AddToClassList("fd-text");
            body.Add(text);

            item.Add(body);
            return item;
        }

        private static VisualElement BuildDay(PlanDay d)
        {
            var row = new VisualElement();
            row.AddToClassList("pd-row");
            if (d.Match) row.AddToClassList("pd-row--match");

            var day = new Label(d.Day);
            day.AddToClassList("pd-day");
            if (d.Match) day.AddToClassList("pd-day--match");
            row.Add(day);

            var wrap = new VisualElement();
            wrap.AddToClassList("pd-menu-wrap");
            if (d.Match) wrap.AddToClassList("pd-menu-wrap--match");
            var menu = new Label(d.Menu);
            menu.AddToClassList("pd-menu");
            if (d.Match) menu.AddToClassList("pd-menu--match");
            wrap.Add(menu);
            row.Add(wrap);

            if (d.Match)
            {
                var badge = new Label("試合日");
                badge.AddToClassList("pd-badge");
                row.Add(badge);
            }
            return row;
        }

        private static VisualElement BuildGuidance(GuidanceSlot g)
        {
            var slot = new VisualElement();
            slot.AddToClassList("gd-slot");
            if (g.Empty)
            {
                slot.AddToClassList("gd-slot--empty");
                var t = new Label("＋ 空き枠（選手を指名）");
                t.AddToClassList("gd-empty-t");
                slot.Add(t);
            }
            else
            {
                var name = new Label(g.Name); name.AddToClassList("gd-name");
                var focus = new Label(g.Focus); focus.AddToClassList("gd-focus");
                slot.Add(name); slot.Add(focus);
            }
            return slot;
        }

        // ===== 補助 =====

        private static string CondArrow(string c)
        {
            switch (c)
            {
                case "絶好調": return "↑↑";
                case "好調": return "↑";
                case "不調": return "↓";
                case "絶不調": return "↓↓";
                default: return "→";
            }
        }

        private static string CondClass(string c)
        {
            switch (c)
            {
                case "絶好調": return "cond--best";
                case "好調": return "cond--good";
                case "不調": return "cond--bad";
                case "絶不調": return "cond--worst";
                default: return "cond--normal";
            }
        }

        private static Color TagColor(string tag)
        {
            switch (tag)
            {
                case "成長": return Good;
                case "警告": return Warn;
                case "発見": return Amber;
                default: return Sub;
            }
        }

        private static void SetBorder(VisualElement e, Color c)
        {
            e.style.borderTopWidth = 1; e.style.borderBottomWidth = 1;
            e.style.borderLeftWidth = 1; e.style.borderRightWidth = 1;
            e.style.borderTopColor = c; e.style.borderBottomColor = c;
            e.style.borderLeftColor = c; e.style.borderRightColor = c;
        }

        private void SetText(string name, string text)
        {
            var label = _root.Q<Label>(name);
            if (label != null) label.text = text;
        }

        private void SetWidth(string name, int percent)
        {
            var e = _root.Q<VisualElement>(name);
            if (e != null) e.style.width = Length.Percent(Mathf.Clamp(percent, 0, 100));
        }

        private void BuildList<T>(string containerName, System.Collections.Generic.List<T> items,
            System.Func<T, VisualElement> builder)
        {
            var container = _root.Q<VisualElement>(containerName);
            if (container == null) return;
            container.Clear();
            foreach (var it in items) container.Add(builder(it));
        }
    }
}
