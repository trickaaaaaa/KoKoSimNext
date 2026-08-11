using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Components; // 部品辞書（RankChip / AbilityRow）

namespace KokoSim.Unity.Home
{
    /// <summary>
    /// ホーム ダッシュボード（設計書06・情報構成 案A 2026-07-21）。UIDocument へ HomeState を束ね、
    /// 左＝部の実力（6指標レーダー）＋故障者 / 中＝チーム成績＋チーム内ランキング /
    /// 右＝今週の出来事＋個別指導 を描画する。「次の試合」は大会モード中だけ出す。
    /// 「今週を進める」で週送り→育成・怪我・イベントをフィードに流し再描画する。
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
        private VisualElement _banner, _dialog, _result, _newTeam;
        private VisualElement _menu, _menuTitleConfirm;
        private Label _menuSaveReason;

        // 新チーム発足（次期主将の指名）モーダルの選択状態。RosterService.Active 内の index を持つ。
        private KokoSim.Unity.Components.RadarChartView _ntRadar;
        private int _ntPick = -1;

        // 「部の実力」の6角形（部品辞書 RadarChartView）。総合力パネルと同じ絵をカードサイズで出す。
        private KokoSim.Unity.Components.RadarChartView _tsRadar;
        private const float TeamRadarRadius = 0.30f;
        private const float TeamRadarLabelOffset = 1.34f;

        // 登録したハンドラ（OnDisable で必ず外す）。ScreenRouter は同じ GameObject を SetActive で付け外し
        // するだけなので、外さないと画面往復のたびに多重登録され「1クリックで複数日進む」不具合になる。
        private Button _advanceBtn, _dialogYes, _dialogNo, _resultOk, _newTeamOk;
        private Button _menuBtn, _menuSaveBtn, _menuTitleBtn, _menuCloseBtn, _menuTitleYes, _menuTitleNo;
        private VisualElement _tsRow;
        private EventCallback<ClickEvent> _tsRowClick;

        private void OnEnable()
        {
            // セッション常駐（画面往復で通知フィードと成長が消えないよう、単一の状態を使い回す）。
            _state = HomeState.Current;
            _root = GetComponent<UIDocument>().rootVisualElement;

            _banner = _root.Q<VisualElement>("tm-banner");
            _dialog = _root.Q<VisualElement>("tm-dialog");
            _result = _root.Q<VisualElement>("tm-result");
            _newTeam = _root.Q<VisualElement>("nt-modal");
            _menu = _root.Q<VisualElement>("hm-menu");
            _menuTitleConfirm = _root.Q<VisualElement>("hm-menu-title-confirm");
            _menuSaveReason = _root.Q<Label>("hm-menu-save-reason");
            _ntRadar = new KokoSim.Unity.Components.RadarChartView(
                _root.Q<VisualElement>("nt-d-radar"), NewTeamRadarRadius);
            _tsRadar = new KokoSim.Unity.Components.RadarChartView(
                _root.Q<VisualElement>("hm-radar"), TeamRadarRadius, TeamRadarLabelOffset);

            _advanceBtn = _root.Q<Button>("advance");
            if (_advanceBtn != null) _advanceBtn.clicked += OnAdvance;

            _dialogYes = Wire("tm-dialog-yes", OnMatchYes);
            _dialogNo = Wire("tm-dialog-no", OnMatchNo);
            _resultOk = Wire("tm-result-ok", OnResultOk);
            _newTeamOk = Wire("nt-ok", OnNewTeamConfirm);

            _menuBtn = Wire("hm-menu-btn", OnMenuOpen);
            _menuSaveBtn = Wire("hm-menu-save", OnMenuSave);
            _menuTitleBtn = Wire("hm-menu-title", OnMenuTitleRequest);
            _menuCloseBtn = Wire("hm-menu-close", OnMenuClose);
            _menuTitleYes = Wire("hm-menu-title-yes", OnMenuTitleConfirm);
            _menuTitleNo = Wire("hm-menu-title-no", OnMenuTitleCancel);

            // 部の状態「チーム総合力」行 → 6角形の専用パネルへ（行クリックで詳細）。
            _tsRow = _root.Q<VisualElement>("team-strength-row");
            if (_tsRow != null)
            {
                _tsRowClick = _ => KokoSim.Unity.Shell.ScreenRouter.Instance?.ShowDeferred("TeamStrength");
                _tsRow.RegisterCallback(_tsRowClick);
            }

            Render();

            // 他画面（練習・大会・練習試合・選手・メンバー）で週を進めて引退週／大会開幕週に入った場合は
            // ここへ回送されてくる（ScreenRouter が Pending/BannerPending を見て回送する, issue #134）。
            // その回送分もこの1箇所で受けて指名モーダル／開幕バナーを出す（演出の導線をホームに集約する）。
            if (KokoSim.Unity.Shell.NewTeamService.Pending) ShowNewTeam();
            else if (_state.BannerPending) ShowBanner();

            var session = KokoSim.Unity.Shell.GameSession.Current;
            if (session.ResultPending && session.LastOutcome != null)
            {
                // ライブ観戦から戻ってきた直後 → 試合結果を表示する。
                ShowResult(session.LastOutcome);
            }
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
            if (_newTeamOk != null) _newTeamOk.clicked -= OnNewTeamConfirm;
            if (_tsRow != null && _tsRowClick != null) _tsRow.UnregisterCallback(_tsRowClick);
            _tsRowClick = null;

            if (_menuBtn != null) _menuBtn.clicked -= OnMenuOpen;
            if (_menuSaveBtn != null) _menuSaveBtn.clicked -= OnMenuSave;
            if (_menuTitleBtn != null) _menuTitleBtn.clicked -= OnMenuTitleRequest;
            if (_menuCloseBtn != null) _menuCloseBtn.clicked -= OnMenuClose;
            if (_menuTitleYes != null) _menuTitleYes.clicked -= OnMenuTitleConfirm;
            if (_menuTitleNo != null) _menuTitleNo.clicked -= OnMenuTitleCancel;
        }

        // ===== ゲーム中メニュー: セーブ／タイトルへ戻る（issue #509） =====

        private void OnMenuOpen()
        {
            // issue #138 と同じ作法: 背景の大会後処理中は静的状態を触る操作を受け付けない。
            if (KokoSim.Unity.Shell.BackgroundGameOp.Running) return;
            if (_menu == null) return;
            var canSave = KokoSim.Unity.Shell.SaveGameIO.CanSave;
            _menuSaveBtn?.SetEnabled(canSave);
            if (_menuSaveReason != null)
            {
                _menuSaveReason.text = canSave ? "" : KokoSim.Unity.Shell.SaveGameIO.SaveDisabledReason();
                _menuSaveReason.style.display = canSave ? DisplayStyle.None : DisplayStyle.Flex;
            }
            _menu.style.display = DisplayStyle.Flex;
            _menu.BringToFront();
        }

        private void OnMenuClose()
        {
            if (_menu != null) _menu.style.display = DisplayStyle.None;
        }

        private void OnMenuSave()
        {
            if (!KokoSim.Unity.Shell.SaveGameIO.CanSave) return;   // 押せない経路への防御（オーナー申し送り）
            OnMenuClose();
            KokoSim.Unity.Shell.SaveSlots.Entry = KokoSim.Unity.Shell.SaveSlotsEntry.Home;
            // クリック配信中の同期 Show は UITK のイベント木を壊すため遅延切替を使う（ScreenRouter.cs 冒頭コメント参照）。
            KokoSim.Unity.Shell.ScreenRouter.Instance?.ShowDeferred("SaveSlots");
        }

        private void OnMenuTitleRequest()
        {
            OnMenuClose();
            if (_menuTitleConfirm == null) return;
            var since = _root.Q<Label>("hm-menu-title-since");
            if (since != null) since.text = LastSavedText();
            _menuTitleConfirm.style.display = DisplayStyle.Flex;
            _menuTitleConfirm.BringToFront();
        }

        /// <summary>
        /// 最後の保存からの経過1行（オートセーブ廃止・監督決定 2026-08-09）。
        /// 自動で拾ってくれる保存が無くなったので、「ここで戻ると何週ぶん失うか」を確認の場で見せる。
        /// 週数は暦の通算週（(年度-1)×50＋週）の差で、単一ソースの <see cref="ManagerService.AbsoluteWeek"/> と同じ式。
        /// </summary>
        private static string LastSavedText()
        {
            if (KokoSim.Unity.Shell.SaveGameIO.LastSavedAt is not { } at)
                return "この進行はまだ一度も保存していません。";
            var lost = KokoSim.Unity.Shell.ManagerService.AbsoluteWeek
                       - KokoSim.Unity.Shell.GameClock.AbsoluteWeekOf(at.YearIndex, at.Week);
            var label = KokoSim.Unity.Shell.SeasonClock.CurrentLabel(at.YearIndex, at.Week);
            return lost <= 0 ? $"最後の保存: {label}（保存済み）" : $"最後の保存: {label}（{lost}週前）";
        }

        private void OnMenuTitleCancel()
        {
            if (_menuTitleConfirm != null) _menuTitleConfirm.style.display = DisplayStyle.None;
            OnMenuOpen();   // メニューへ戻す
        }

        private void OnMenuTitleConfirm()
        {
            if (_menuTitleConfirm != null) _menuTitleConfirm.style.display = DisplayStyle.None;
            // 背景の全国裏試合／敗退後の残りブラケット消化を完了させてから静的リセットする。GameClock.Advance の
            // 年度替わり・新チーム発足と同じ作法（issue #529）＝背景スレッドと同じ NationRosters を同時に触らない。
            // 完了を待たずに Reset すると、リセット後に新しい NationBackgroundSim.Start が走った場合、旧セッションの
            // ワーカーがまだ生きていて共有カウンタ(_nextJob等)を奪い合う実害があるため。
            KokoSim.Unity.Shell.NationBackgroundSim.EnsureCompleted();
            KokoSim.Unity.Shell.BracketFinishSim.EnsureCompleted();
            KokoSim.Unity.Shell.SaveGameIO.ResetForNewGame();
            HomeState.Reset();
            KokoSim.Unity.Shell.ScreenRouter.Instance?.ShowDeferred("TitleScreen");
        }

        // ===== 大会モード進行（要件1〜7） =====

        private void OnAdvance()
        {
            // issue #138: 試合後処理をバックグラウンドで回している間は週送りを受け付けない
            // （メインスレッドの GameClock/ロスター変更が背景処理と競合するのを防ぐ）。
            if (KokoSim.Unity.Shell.BackgroundGameOp.Running) return;
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
                if (KokoSim.Unity.Shell.NewTeamService.Pending) ShowNewTeam();  // 引退週＝新チーム発足（設計書09 §8）
                else if (_state.BannerPending) ShowBanner();    // 大会開幕演出（要件1・2）
            }
        }

        // ===== 新チーム発足：次期主将の指名（設計書09 §8・案C 左リスト＋右詳細） =====

        private const float NewTeamRadarRadius = 0.34f;

        /// <summary>指名モーダルを開く。初期選択は自動選出済みの暫定主将（そのまま確定してもよい）。</summary>
        private void ShowNewTeam()
        {
            if (_newTeam == null) return;
            var view = KokoSim.Unity.Shell.NewTeamService.BuildView();
            if (view.Candidates.Count == 0)
            {
                // 新チームが空（下級生ゼロ）＝指名しようがない。導線を閉じて通常進行へ戻す。
                KokoSim.Unity.Shell.NewTeamService.Clear();
                return;
            }

            _ntPick = -1;
            foreach (var c in view.Candidates) if (c.IsInterim) _ntPick = c.ActiveIndex;
            if (_ntPick < 0) _ntPick = view.Candidates[0].ActiveIndex;

            RenderNewTeam(view);
            _newTeam.style.display = DisplayStyle.Flex;
            _newTeam.BringToFront();
        }

        private void RenderNewTeam(KokoSim.Unity.Shell.NewTeamView view)
        {
            SetText("nt-lead", view.Lead);
            SetText("nt-retired", view.RetiredLabel);
            SetText("nt-note", "指名できるのは新チーム発足のこの1回だけです。");

            var rows = _root.Q<ScrollView>("nt-rows");
            if (rows != null)
            {
                rows.Clear();
                foreach (var c in view.Candidates) rows.Add(BuildCandidateRow(c));
            }
            RenderNewTeamDetail();
        }

        private VisualElement BuildCandidateRow(KokoSim.Unity.Shell.CaptainCandidateRow c)
        {
            var row = new VisualElement();
            row.AddToClassList("nt-row");
            if (c.ActiveIndex == _ntPick) row.AddToClassList("nt-row--on");

            var no = new Label(c.Number); no.AddToClassList("nt-c-no"); row.Add(no);
            var name = new Label(c.Name); name.AddToClassList("nt-c-name"); row.Add(name);

            var mental = new Label(c.Mental.ToString()); mental.AddToClassList("nt-c-mental"); row.Add(mental);

            var index = c.ActiveIndex;
            row.RegisterCallback<ClickEvent>(_ => OnPickCandidate(index));
            return row;
        }

        private void OnPickCandidate(int activeIndex)
        {
            if (_ntPick == activeIndex) return;
            _ntPick = activeIndex;
            RenderNewTeam(KokoSim.Unity.Shell.NewTeamService.BuildView());
        }

        // 右ペイン：選択中の候補を選手詳細と同じ ViewModel で描く（レーダー・可視情報・特殊能力）。
        // 統率力は主将本人しか判らない隠しパラメータなので出さない（設計書09 §8）。
        private void RenderNewTeamDetail()
        {
            if (_ntPick < 0) return;
            var v = new KokoSim.Unity.Players.PlayerDetailState().BuildView(_ntPick);

            SetText("nt-d-name", v.Name);
            // 役割（投手/野手）は出さない（プレイヤーが決める, Issue #93）。学年と利き手のみ。
            SetText("nt-d-sub", v.GradeLabel + " / " + v.ThrowsBats);
            if (_ntRadar != null) _ntRadar.SetData(v.Radar, v.OverallGrade);

            var facts = _root.Q<VisualElement>("nt-d-facts");
            if (facts != null)
            {
                facts.Clear();
                facts.Add(Fact("精神力", MentalOf(v)));
                facts.Add(Fact("性格", PersonalityOf(v)));
            }

            var skills = _root.Q<VisualElement>("nt-d-skills");
            if (skills != null)
            {
                skills.Clear();
                if (v.HasSkills)
                {
                    foreach (var s in v.Skills)
                    {
                        var chip = new Label(s.Name);
                        chip.AddToClassList("nt-skill");
                        skills.Add(chip);
                    }
                }
                else
                {
                    var none = new Label("なし");
                    none.AddToClassList("nt-skill");
                    none.AddToClassList("nt-skill--none");
                    skills.Add(none);
                }
            }
        }

        private static VisualElement Fact(string key, string value)
        {
            var e = new VisualElement();
            e.AddToClassList("nt-fact");
            var k = new Label(key); k.AddToClassList("nt-fact__k");
            var v = new Label(value); v.AddToClassList("nt-fact__v");
            e.Add(k); e.Add(v);
            return e;
        }

        // 隠しパラメータ一覧（選手詳細と同じ判明ルール）から取り出す。未判明は「？」のまま出す。
        private static string HiddenOf(KokoSim.Unity.Players.PlayerDetailView v, string key)
        {
            foreach (var h in v.Hidden) if (h.Key == key) return h.Known ? h.Value : "？";
            return "？";
        }

        private static string MentalOf(KokoSim.Unity.Players.PlayerDetailView v) => HiddenOf(v, "精神力");
        private static string PersonalityOf(KokoSim.Unity.Players.PlayerDetailView v) => HiddenOf(v, "性格");

        private void OnNewTeamConfirm()
        {
            KokoSim.Unity.Shell.NewTeamService.Confirm(_ntPick);
            if (_newTeam != null) _newTeam.style.display = DisplayStyle.None;
            Render();
            if (_state.BannerPending) ShowBanner();   // 指名の後に大会開幕演出が残っていれば続けて出す
        }

        private void ShowBanner()
        {
            _state.PushTournamentOpenFeed();   // 「大会が開幕した」を通知フィードへ（大会遷移は TournamentEntry が実施済み）
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
            // 引き分け再試合で決着した試合は「再試合」を併記し、大会が勝者を出すことと矛盾させない（issue #253）。
            SetText("tm-result-round", o.RoundName + (o.TieReplays > 0 ? "・再試合" : ""));

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
            if (KokoSim.Unity.Shell.BackgroundGameOp.Running) return;   // 処理中の二度押しを無視
            if (_result != null) _result.style.display = DisplayStyle.None;
            // issue #138: 大会終了時の DismissResult は消費週ぶんの週送り（GameClock.Advance）を伴い、その途中で
            // 全国背景シムへの join（NationBackgroundSim.EnsureCompleted の t.Wait）が走ってメインスレッドを固める。
            // ここも Task.Run で別スレッドへ載せ替え（フリーズ源B, Q1(a)）、完了後にメインスレッドで再描画する。
            // 通常の結果（未終了＝週送りなし）は即返るので体感は変わらない。
            KokoSim.Unity.Shell.BackgroundGameOp.Run(() => _state.DismissResult(), Render);
        }

        private void Render()
        {
            var v = _state.BuildView();

            // 進行ボタンはモードで文言を切替（通常=週送り / 大会=日送り）。
            var advance = _root.Q<Button>("advance");
            if (advance != null) advance.text = v.TournamentMode ? "1日進める ▶" : "今週を進める ▶";

            // 自校の表札（校章・校名・県名）は TopBarMeters が全画面共通で埋める（issue #585）。
            // 掲示板の升目（週・カウントダウン）。大会モードでは文言が変わるので上書き版を使う。
            KokoSim.Unity.Components.ScoreboardStrip.Fill(_root, v.CountdownLabel, v.CountdownCells, v.WeekSuffix);
            KokoSim.Unity.Shell.TopBarMeters.Fill(_root);   // 部費残高・名声・信頼度（ManagerService 単一ソース・全画面共通）

            // チーム総合力ランクチップ。
            var rank = _root.Q<VisualElement>("team-rank");
            if (rank != null) { rank.Clear(); rank.Add(UiComponents.RankChipLegacy(v.TeamRankGrade)); }

            RenderTeamStrength();

            // 主役ヒーロー帯（直近の予定）を二態で切替える。大会週＝対戦カード、通常週＝夏予選カウントダウン
            // （前向きの予定表はエンジン未実装のためカウントダウンで代替＝design-16 §5 の但し書き）。
            var heroMatch = _root.Q<VisualElement>("hm-hero-match");
            var heroPlain = _root.Q<VisualElement>("hm-hero-plain");
            if (heroMatch != null)
                heroMatch.style.display = v.TournamentMode ? DisplayStyle.Flex : DisplayStyle.None;
            if (heroPlain != null)
                heroPlain.style.display = v.TournamentMode ? DisplayStyle.None : DisplayStyle.Flex;
            if (v.TournamentMode)
            {
                SetText("game-tag", v.GameTag);
                SetText("home-team", v.HomeTeam);
                SetText("away-team", v.AwayTeam);
                SetText("home-sub", v.HomeSub);
                SetText("away-sub", v.AwaySub);
                SetText("game-date", v.GameDate);
                SetText("venue", v.Venue);
                SetText("weather", "天気 " + v.Weather);
            }
            else
            {
                SetText("hero-cd-label", v.CountdownLabel);
                SetText("hero-cd-num", v.HeroBigValue);
                SetText("hero-cd-unit", v.HeroBigUnit);
            }

            // 故障者（0名でもカードは残し「該当なし」を出す＝週をまたいでレイアウトが動かない）。
            SetText("injury-count", v.Injured.Count + " 名");
            var injList = _root.Q<VisualElement>("injury-list");
            if (injList != null)
            {
                injList.Clear();
                if (v.Injured.Count == 0)
                {
                    var none = new Label("該当なし");
                    none.AddToClassList("hm-inj--none");
                    injList.Add(none);
                }
                else
                {
                    foreach (var r in v.Injured) injList.Add(BuildInjured(r));
                }
            }

            BuildList("team-stats", KokoSim.Unity.Shell.TeamStatsService.TeamTotals(), BuildStatRow);
            RenderRankings();
            BuildList("feed-list", v.Feed, BuildFeed);
            BuildList("guidance-list", v.Guidance, BuildGuidance);
            SetText("guidance-count", "残り枠 " + (v.GuidanceTotal - v.GuidanceUsed) + " / " + v.GuidanceTotal);
        }

        /// <summary>チームランクのカード（総合ランク＋6角形）。総合力パネルと同じ ViewModel を引く。
        /// 弱点の言語化（AnalysisWeak/Advice）はレーダーを見れば判るのでホームには出さない。</summary>
        private void RenderTeamStrength()
        {
            var ts = new KokoSim.Unity.Squad.TeamStrengthState().BuildView();

            var tsChip = _root.Q<VisualElement>("team-strength-chip");
            if (tsChip != null)
            {
                tsChip.Clear();
                tsChip.Add(UiComponents.RankChipLegacy(ts.OverallGrade));
            }
            SetText("team-strength-value", ts.OverallValue.ToString());
            if (_tsRadar != null) _tsRadar.SetData(ts.Radar, ts.OverallGrade);
        }

        // ===== 行ビルダー =====

        /// <summary>チーム成績の1行（項目名＋通算/公式戦/今大会。未計上は淡色の「—」）。</summary>
        private static VisualElement BuildStatRow(KokoSim.Unity.Shell.TeamStatRow r)
        {
            var row = new VisualElement();
            row.AddToClassList("hm-srow");

            var k = new Label(r.Label);
            k.AddToClassList("hm-sc-k");
            row.Add(k);

            row.Add(StatCell(r.Career));
            row.Add(StatCell(r.Official));
            row.Add(StatCell(r.Tournament));
            return row;
        }

        private static Label StatCell(string text)
        {
            var cell = new Label(text);
            cell.AddToClassList("hm-sc-v");
            cell.AddToClassList("f-num");   // 成績値は純数値＝コンデンス体（決定2-B・純数値セル）
            if (text == "—") cell.AddToClassList("hm-sc-v--none");
            return cell;
        }

        /// <summary>チーム内ランキング。打撃／投手を左右2列へ振り分ける（区分名は各列の見出しが持つ）。</summary>
        private void RenderRankings()
        {
            var bat = _root.Q<VisualElement>("rank-bat");
            var pit = _root.Q<VisualElement>("rank-pit");
            if (bat != null) bat.Clear();
            if (pit != null) pit.Clear();

            foreach (var r in KokoSim.Unity.Shell.TeamStatsService.Rankings())
            {
                var host = r.Section == "投手" ? pit : bat;
                if (host != null) host.Add(BuildRankRow(r));
            }
        }

        /// <summary>チーム内ランキングの1部門（1〜3位。該当なしの枠は淡色の「—」で必ず埋める）。</summary>
        private static VisualElement BuildRankRow(KokoSim.Unity.Shell.RankRow r)
        {
            var row = new VisualElement();
            row.AddToClassList("hm-rrow");

            var k = new Label(r.Label);
            k.AddToClassList("hm-rk-k");
            row.Add(k);

            foreach (var e in r.Top)
            {
                if (e.Empty)
                {
                    var none = new Label("—");
                    none.AddToClassList("hm-rk-e");
                    none.AddToClassList("hm-rk-e--none");
                    row.Add(none);
                    continue;
                }

                var cell = new VisualElement();
                cell.AddToClassList("hm-rk-e");
                var name = new Label(e.Name); name.AddToClassList("hm-rk-e__n");
                var val = new Label(e.Value); val.AddToClassList("hm-rk-e__v"); val.AddToClassList("f-num");
                cell.Add(name); cell.Add(val);
                row.Add(cell);
            }
            return row;
        }

        /// <summary>故障者の1行（背番号／名前／傷病名・部位・程度／復帰まで。重度だけ警告色＝UI原則②）。</summary>
        private static VisualElement BuildInjured(InjuredRow r)
        {
            var row = new VisualElement();
            row.AddToClassList("hm-inj");

            var no = new Label(r.Number); no.AddToClassList("hm-inj__no"); row.Add(no);
            var name = new Label(r.Name); name.AddToClassList("hm-inj__n"); row.Add(name);

            // 傷病名は data/injuries.yaml 由来（種類なしの旧データは部位・程度だけになる）。
            var diagnosis = string.IsNullOrEmpty(r.Type) ? r.Site : r.Type + "・" + r.Site;
            var st = new Label(diagnosis + "・" + r.Severity);
            st.AddToClassList("hm-inj__st");
            if (r.Severe) st.AddToClassList("hm-inj__st--severe");
            row.Add(st);

            var back = new Label(r.Back); back.AddToClassList("hm-inj__bk"); row.Add(back);
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
