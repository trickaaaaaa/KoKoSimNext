using System.Collections.Generic;
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
        private HomeState _state;
        private VisualElement _root;
        private VisualElement _banner, _dialog, _result, _newTeam, _xt;

        // 新チーム発足（次期主将の指名）モーダルの選択状態。RosterService.Active 内の index を持つ。
        private KokoSim.Unity.Components.RadarChartView _ntRadar;
        private int _ntPick = -1;

        // 監督オファー（転任の選択, 設計書04 §1.3）モーダルの選択状態。null＝残留。
        private int? _xtPick;

        // 「部の実力」の6角形（部品辞書 RadarChartView）。総合力パネルと同じ絵をカードサイズで出す。
        private KokoSim.Unity.Components.RadarChartView _tsRadar;
        // 320×290 の中に中心(160,145)・半径98（短辺290に対して）、ラベルは半径＋28px。
        private const float TeamRadarRadius = 98f / 290f;
        private const float TeamRadarLabelOffset = (98f + 28f) / 98f;

        // チーム内ランキングの集計範囲（通算／公式戦／今大会）。画面往復では保つ。
        private static KokoSim.Unity.Shell.RankingScope _rankScope = KokoSim.Unity.Shell.RankingScope.Career;

        // 登録したハンドラ（OnDisable で必ず外す）。ScreenRouter は同じ GameObject を SetActive で付け外し
        // するだけなので、外さないと画面往復のたびに多重登録され「1クリックで複数日進む」不具合になる。
        private Button _advanceBtn, _dialogYes, _dialogNo, _resultOk, _newTeamOk, _xtOk;
        private VisualElement _tsRow;
        private EventCallback<ClickEvent> _tsRowClick;

        // イベントのノベル提示（issue #609）。本文域（ヒーロー帯＋3カラム）と二者択一で入れ替える。
        private EventNovelView _novel;
        private VisualElement _hero, _dash;

        // 年契約の更新モーダル（issue #1034）。会話（ノベル）を読み終えたあとに開く。
        private ContractRenewalModal _renewal;
        private DemoEndPanel _demoEnd;   // 体験版の終了画面（#1323）

        private void OnEnable()
        {
            // セッション常駐（画面往復で通知フィードと成長が消えないよう、単一の状態を使い回す）。
            _state = HomeState.Current;
            _root = GetComponent<UIDocument>().rootVisualElement;

            _banner = _root.Q<VisualElement>("tm-banner");
            _dialog = _root.Q<VisualElement>("tm-dialog");
            _result = _root.Q<VisualElement>("tm-result");
            _newTeam = _root.Q<VisualElement>("nt-modal");
            _xt = _root.Q<VisualElement>("xt-modal");
            _ntRadar = new KokoSim.Unity.Components.RadarChartView(
                _root.Q<VisualElement>("nt-d-radar"), NewTeamRadarRadius, NewTeamRadarLabelOffset,
                KokoSim.Unity.Components.RadarLabelSize.Normal, NewTeamRadarLabelSpreadX);
            _tsRadar = new KokoSim.Unity.Components.RadarChartView(
                _root.Q<VisualElement>("hm-radar"), TeamRadarRadius, TeamRadarLabelOffset);
            AddIcon("stats-empty", KokoSim.Unity.Shell.LineIcon.Kind.BarChart);
            AddIcon("rank-empty", KokoSim.Unity.Shell.LineIcon.Kind.Trophy);

            _advanceBtn = _root.Q<Button>("advance");
            if (_advanceBtn != null) _advanceBtn.clicked += OnAdvance;

            _dialogYes = Wire("tm-dialog-yes", OnMatchYes);
            _dialogNo = Wire("tm-dialog-no", OnMatchNo);
            _resultOk = Wire("tm-result-ok", OnResultOk);
            _newTeamOk = Wire("nt-ok", OnNewTeamConfirm);
            _xtOk = Wire("xt-ok", OnTransferConfirm);


            // 部の状態「チーム総合力」行 → 6角形の専用パネルへ（行クリックで詳細）。
            _tsRow = _root.Q<VisualElement>("team-strength-row");
            if (_tsRow != null)
            {
                _tsRowClick = _ => KokoSim.Unity.Shell.ScreenRouter.Instance?.ShowDeferred("TeamStrength");
                _tsRow.RegisterCallback(_tsRowClick);
            }

            _hero = _root.Q<VisualElement>("home-hero");
            _dash = _root.Q<VisualElement>("home-dash");
            _novel = new EventNovelView(_root.Q<VisualElement>("ev-novel"), OnNovelClosed);
            _renewal = new ContractRenewalModal(_root, OnRenewalClosed);
            _demoEnd = new DemoEndPanel(_root);
            _demoEnd.Bind();
            // 本文域は必ず見える状態から始める。UIDocument のツリーは画面を離れても生き残るので、
            // ノベルを開いたまま他タブへ移ると、隠したインライン指定が残って空のホームになる。
            SetBodyVisible(true);

            Render();

            // 側画面の「1日進める」で回送されてきた（issue #1534 項目9）。取り出しは毎回行い、持ち越さない。
            var dayAdvanceRequested = KokoSim.Unity.Shell.WeekAdvance.TakeDayAdvanceRequest();
            // 側画面の週送りが体験版の区切りで止まって回送されてきた（#1323）。
            var demoEndRequested = KokoSim.Unity.Shell.DemoGate.TakeEndScreenRequest();

            // 他画面（練習・大会・練習試合・選手・メンバー）で週を進めて引退週／大会開幕週に入った場合は
            // ここへ回送されてくる（ScreenRouter が Pending/BannerPending を見て回送する, issue #134）。
            // その回送分もこの1箇所で受けて指名モーダル／開幕バナーを出す（演出の導線をホームに集約する）。
            if (KokoSim.Unity.Shell.NewTeamService.Pending) ContinueNewTeamFlow();
            else if (KokoSim.Unity.Shell.UniformNumberService.Pending) GoToUniformNumbers();
            else if (KokoSim.Unity.Shell.ManagerTransferService.Pending) ShowTransferOffer();
            else if (_state.BannerPending) ShowBanner();
            // 年契約の更新の待ち（別画面へ出ていて戻ってきた場合も、ここで会話→更新の画面へ戻す）。
            else if (KokoSim.Unity.Shell.ContractRenewalService.Pending) ContinueRenewal();
            // 側画面で押された「1日進める」をここで実行する（日送り・試合日の開始確認・イベントはホームの押下と同じ経路）。
            else if (dayAdvanceRequested && _state.InTournament
                     && !KokoSim.Unity.Shell.GameSession.Current.ResultPending) OnAdvance();
            else if (demoEndRequested) _demoEnd?.Show();
            // 大会中に溜まったイベント（大会後に持ち越した分）もここで拾う（issue #609）。
            else TryShowEvent();

            var session = KokoSim.Unity.Shell.GameSession.Current;
            if (session.ResultPending && session.LastOutcome != null)
            {
                // ライブ観戦から戻ってきた直後 → 試合結果を表示する。
                ShowResult(session.LastOutcome);
            }
        }

        /// <summary>記録がないときの案内に線画アイコン（34px・線幅1.6）を入れる。二重に入れない。</summary>
        private void AddIcon(string emptyName, KokoSim.Unity.Shell.LineIcon.Kind kind)
        {
            var host = _root.Q<VisualElement>(emptyName)?.Q<VisualElement>(className: "hm-empty__icon");
            if (host == null || host.childCount > 0) return;
            host.Add(new KokoSim.Unity.Shell.LineIcon(kind) { Stroke = 1.6f });
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
            if (_xtOk != null) _xtOk.clicked -= OnTransferConfirm;
            if (_tsRow != null && _tsRowClick != null) _tsRow.UnregisterCallback(_tsRowClick);
            _tsRowClick = null;
            if (_novel != null) { _novel.Dispose(); _novel = null; }
            if (_renewal != null) { _renewal.Dispose(); _renewal = null; }
            if (_demoEnd != null) { _demoEnd.Close(); _demoEnd.Unbind(); _demoEnd = null; }

        }

        // ===== 大会モード進行（要件1〜7） =====

        private void OnAdvance()
        {
            // issue #138: 試合後処理をバックグラウンドで回している間は週送りを受け付けない
            // （メインスレッドの GameClock/ロスター変更が背景処理と競合するのを防ぐ）。
            if (KokoSim.Unity.Shell.BackgroundGameOp.Running) return;
            // ブラウザ版は裏の仕事を回し切ってから進める（#1588）。週送りの節目の「終わるまで待つ」が
            // 1コマで数百試合を回すとメモリを使い切る。回し切ったら同じ操作をもう一度走らせる。
            if (KokoSim.Unity.Shell.BackgroundWork.DeferUntilIdle(OnAdvance)) return;
            // 新チームの背番号を確定するまで週を進めない（issue #954）。確定の画面へ戻す。
            if (KokoSim.Unity.Shell.UniformNumberService.Pending) { GoToUniformNumbers(); return; }
            // 年契約の更新を確定するまで週を進めない（総括が選んだ段の年額を引くため）。
            if (KokoSim.Unity.Shell.ContractRenewalService.Pending) { ContinueRenewal(); return; }
            // 体験版は2年目の夏が全部終わったところで止める（#1323）。週を進めず終了画面を出す。
            if (KokoSim.Unity.Shell.DemoGate.StopBeforeAdvance) { _demoEnd?.Show(); return; }
            if (_state.InTournament)
            {
                // 試合日に着いていたら日を進めず、開始確認を出し直すだけにする。進めると「いいえ」の後に
                // 押すたび大会の日付だけが試合日を追い越し、大会明けの時計送り（TournamentDay/7 週）も増えていた。
                if (_state.ReachedMatchDay) { ShowMatchDay(); return; }
                _state.AdvanceDay();
                Render();
                if (_state.ReachedMatchDay) ShowMatchDay();   // 試合日に到達→開始確認（要件7・惰性防止）
                // 大会中も7日ごとに怪我・気づきのイベントが積まれる。ここで出さないとキューに残ったまま
                // ホームに居続ける限り出ず、セーブも「イベントの場面が残っています」で止まっていた。
                else TryShowEvent();
            }
            else
            {
                _state.AdvanceWeek();
                // 初めて部費で何か買えるようになった週: 部費の説明（issue #1496）。出すのは下の導線のどこかの後（キューに残る）。
                KokoSim.Unity.Shell.TutorialService.RaisePurchasesIfAffordable();
                Render();
                if (KokoSim.Unity.Shell.NewTeamService.Pending) ContinueNewTeamFlow();  // 引退週＝新チーム発足（設計書09 §8）
                else if (KokoSim.Unity.Shell.ManagerTransferService.Pending) ShowTransferOffer();  // 転任判定（設計書04 §1.3）
                else if (_state.BannerPending) ShowBanner();    // 大会開幕演出（要件1・2）
                else if (KokoSim.Unity.Shell.ContractRenewalService.Pending) ContinueRenewal();   // 年契約の更新（issue #1034）
                else TryShowEvent();                            // 週次イベントのノベル提示（issue #609）
            }
        }

        // ===== イベント（ノベル提示・issue #609） =====

        /// <summary>
        /// 溜まっているイベントがあればノベル面へ入れ替える。開いている間は本文域（ヒーロー帯＋3カラム）を
        /// 隠す＝案B「トップバー残し」。新チーム発足・開幕バナーが出る週は<b>呼ばない</b>ので、
        /// イベントはキューに残り、次にホームへ来たとき（<c>OnEnable</c>）に出る。
        /// </summary>
        private void TryShowEvent()
        {
            if (_novel == null || !KokoSim.Unity.Shell.EventService.HasPending) return;
            if (!_novel.OpenNext()) return;
            SetBodyVisible(false);
        }

        /// <summary>ノベルを閉じた（全イベント消化）＝本文域を戻して最新化する。</summary>
        private void OnNovelClosed()
        {
            SetBodyVisible(true);
            Render();
            // 練習試合の初戦の前の采配の説明（issue #1496）を読み終えたら、スタメン設定へ続ける。
            if (KokoSim.Unity.Shell.TutorialService.TakeLineupAfterNovel())
                KokoSim.Unity.Shell.ScreenRouter.Instance?.ShowDeferred("LineupSetting");
            // 夏の引退の会話を読み終えたら主将指名へ続ける（issue #954: 引退の会話 → 主将指名 → 背番号決定）。
            else if (KokoSim.Unity.Shell.NewTeamService.Pending) ShowNewTeam();
            // 試合の日の説明（issue #1496）を読み終えたら、試合の開始確認へ続ける。
            else if (_state.InTournament && _state.ReachedMatchDay) ShowDialog();
            else if (KokoSim.Unity.Shell.UniformNumberService.Pending) GoToUniformNumbers();
            // スカウトの節目の会話（分野選び・候補リスト到着・念押し）を読み終えたらスカウト画面へ（issue #1035）。
            else if (KokoSim.Unity.Shell.ScoutService.TakeNavigation())
                KokoSim.Unity.Shell.ScreenRouter.Instance?.ShowDeferred("ScoutScreen");
            // 年契約の更新の会話を読み終えたら、段を選び直す画面へ（issue #1034）。
            else if (KokoSim.Unity.Shell.ContractRenewalService.Pending) OpenRenewal();
        }

        /// <summary>年契約の更新を進める。会話が残っていれば先に出し（読み終えたら <see cref="OnNovelClosed"/> が画面を開く）、無ければ画面を開く。</summary>
        private void ContinueRenewal()
        {
            if (KokoSim.Unity.Shell.EventService.HasPendingWhere(id => id == KokoSim.Engine.Career.ContractRenewal.EventId)
                && _novel != null && _novel.OpenNext())
            {
                SetBodyVisible(false);
                return;
            }
            OpenRenewal();
        }

        private void OpenRenewal()
        {
            if (_renewal != null && _renewal.OpenIfPending()) SetBodyVisible(false);
        }

        /// <summary>更新を確定した＝本文域を戻し、持ち越したイベントがあれば続けて出す。</summary>
        private void OnRenewalClosed()
        {
            SetBodyVisible(true);
            Render();
            TryShowEvent();
        }

        /// <summary>
        /// 新チーム発足の導線を進める（issue #954）。夏の敗退直後の引退なら、まず引退の会話を出し
        /// （読み終えたら <see cref="OnNovelClosed"/> が主将指名を開く）、無ければすぐ主将指名を開く。
        /// </summary>
        private void ContinueNewTeamFlow()
        {
            // 新規開始の主将指名は、赴任の会話（マネージャーの出迎え・最初の説明）を読み終えてから出す。
            // 読み終えると OnNovelClosed が指名へ続ける。
            if (KokoSim.Unity.Shell.NewTeamService.AtGameStart && _novel != null && _novel.OpenNext())
            {
                SetBodyVisible(false);
                return;
            }
            if (KokoSim.Unity.Shell.EventService.HasPendingWhere(KokoSim.Engine.Season.SummerRetirement.IsRetirementEvent)
                && _novel != null && _novel.OpenNext())
            {
                SetBodyVisible(false);
                return;
            }
            ShowNewTeam();
        }

        /// <summary>新チームの背番号決定（メンバー設定画面・案C）へ直行する（issue #954）。</summary>
        private static void GoToUniformNumbers()
            => KokoSim.Unity.Shell.ScreenRouter.Instance?.ShowDeferred(KokoSim.Unity.Shell.ScreenRouter.UniformNumberScreen);

        /// <summary>
        /// 本文域（ヒーロー帯＋3カラム）の表示。ノベルを出している間は隠す。
        /// <b>週送りボタンも一緒に止める</b>＝トップバーは残る作りなので、会話の途中でも押せてしまい、
        /// 押すと読んでいる最中に次の週のイベントが積み上がる。
        /// </summary>
        private void SetBodyVisible(bool visible)
        {
            var d = visible ? DisplayStyle.Flex : DisplayStyle.None;
            if (_hero != null) _hero.style.display = d;
            if (_dash != null) _dash.style.display = d;
            if (_advanceBtn != null) _advanceBtn.SetEnabled(visible);
        }

        // ===== 新チーム発足：次期主将の指名（設計書09 §8・案C 左リスト＋右詳細） =====

        // 箱（.nt-d__radar＝--nt-radar-h 148px）に5軸のラベル（1段・実測 高さ26px・2字36px）を収め、
        // 図の頂点にかけない値。半径40px・ラベルは頂点の20px外（上のラベルが箱の上端に来る）。旧値（箱128px・半径43px・1.2倍）はラベルが図に重なっていた（issue #1533）。
        private const float NewTeamRadarRadius = 40f / 148f;
        private const float NewTeamRadarLabelOffset = 60f / 40f;
        /// <summary>左右のラベル（精神・走力）は横へ少し逃がして頂点との間を空ける（箱は横に広い）。</summary>
        private const float NewTeamRadarLabelSpreadX = 1.12f;

        /// <summary>指名モーダルを開く。初期選択は自動選出済みの暫定主将（そのまま確定してもよい）。</summary>
        private void ShowNewTeam()
        {
            if (_newTeam == null) return;
            var view = KokoSim.Unity.Shell.NewTeamService.BuildView();
            if (view.Candidates.Count == 0)
            {
                // 新チームが空（下級生ゼロ）＝指名しようがない。導線を閉じて通常進行へ戻す
                // （発足済みの印は残す＝第18週の安全網に二度目の発足をさせない・issue #954）。
                KokoSim.Unity.Shell.NewTeamService.Skip();
                TryShowEvent();   // 発足の流れが終わった＝スカウトの分野選びの会話が積まれていれば出す（issue #1035）
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
            SetText("nt-note", view.AtGameStart ? "" : "指名できるのは新チーム発足のこの1回だけです。");

            var rows = _root.Q<ScrollView>("nt-rows");
            if (rows != null)
            {
                rows.Clear();
                foreach (var c in view.Candidates) rows.Add(BuildCandidateRow(c));
            }
            RenderNewTeamDetail(view);
        }

        private VisualElement BuildCandidateRow(KokoSim.Unity.Shell.CaptainCandidateRow c)
        {
            var row = new VisualElement();
            row.AddToClassList("nt-row");
            if (c.ActiveIndex == _ntPick) row.AddToClassList("nt-row--on");

            var no = new Label(c.Number); no.AddToClassList("nt-c-no"); row.Add(no);
            var name = new Label(c.Name); name.AddToClassList("nt-c-name"); row.Add(name);

            var mental = new Label(c.Mental.ToString()); mental.AddToClassList("nt-c-mental"); row.Add(mental);
            row.Add(TierCell(c.StarterTier));
            row.Add(TierCell(c.BenchTier));
            var state = new Label(c.TeamState); state.AddToClassList("nt-c-state"); row.Add(state);

            var index = c.ActiveIndex;
            row.RegisterCallback<ClickEvent>(_ => OnPickCandidate(index));
            return row;
        }

        private static VisualElement TierCell(string tier)
        {
            var cell = new VisualElement();
            cell.AddToClassList("nt-c-rank");
            cell.Add(UiComponents.RankChip(tier, RankChipSize.Small));
            return cell;
        }

        private void OnPickCandidate(int activeIndex)
        {
            if (_ntPick == activeIndex) return;
            _ntPick = activeIndex;
            RenderNewTeam(KokoSim.Unity.Shell.NewTeamService.BuildView());
        }

        // 右ペイン：選択中の候補を選手詳細と同じ ViewModel で描く（レーダー・可視情報・特殊能力）。
        // 統率力は主将本人しか判らない隠しパラメータなので出さない（設計書09 §8）。
        private void RenderNewTeamDetail(KokoSim.Unity.Shell.NewTeamView view)
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
                var pick = view.Candidates.Find(c => c.ActiveIndex == _ntPick);
                if (pick != null)
                {
                    facts.Add(TierFact("スタメン", pick.StarterTier));
                    facts.Add(TierFact("ベンチ", pick.BenchTier));
                }
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

        private static VisualElement TierFact(string key, string tier)
        {
            var e = new VisualElement();
            e.AddToClassList("nt-fact");
            var k = new Label(key); k.AddToClassList("nt-fact__k");
            var chip = new VisualElement(); chip.AddToClassList("nt-fact__chip");
            chip.Add(UiComponents.RankChip(tier, RankChipSize.Small));
            e.Add(k); e.Add(chip);
            return e;
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

        // ===== 監督オファー（転任の選択, 設計書04 §1.3・issue「監督の転任システムが実ゲームに完全未配線」） =====

        private void ShowTransferOffer()
        {
            if (_xt == null) return;
            var view = KokoSim.Unity.Shell.ManagerTransferService.BuildView();
            if (view.Rows.Count == 0)
            {
                // 候補が組めなかった（想定外）＝導線を閉じて通常進行へ戻す。
                KokoSim.Unity.Shell.ManagerTransferService.Clear();
                return;
            }

            _xtPick = view.Rows[0].SchoolId;
            RenderTransferOffer(view);
            _xt.style.display = DisplayStyle.Flex;
            _xt.BringToFront();
        }

        private void RenderTransferOffer(KokoSim.Unity.Shell.TransferOfferView view)
        {
            SetText("xt-hd-title", view.Headline);
            SetText("xt-lead", view.Lead);
            SetText("xt-note", view.CanStay ? "" : "強制転任のため、この赴任先は拒否できません。");

            var rows = _root.Q<ScrollView>("xt-rows");
            if (rows != null)
            {
                rows.Clear();
                foreach (var r in view.Rows) rows.Add(BuildOfferRow(r));
            }
            RenderTransferOfferDetail(view);
        }

        private VisualElement BuildOfferRow(KokoSim.Unity.Shell.TransferOfferRow r)
        {
            var row = new VisualElement();
            row.AddToClassList("xt-row");
            if (r.SchoolId == _xtPick) row.AddToClassList("xt-row--on");

            var name = new Label(r.IsStay ? r.SchoolName + "（残留）" : r.SchoolName);
            name.AddToClassList("xt-c-name");
            name.AddToClassList("f-display");   // 校名は明朝（書体3役）
            row.Add(name);

            var pref = new Label(r.PrefectureName); pref.AddToClassList("xt-c-pref"); row.Add(pref);

            var rank = new VisualElement(); rank.AddToClassList("xt-c-rank");
            rank.Add(UiComponents.RankChip(r.TierLetter, RankChipSize.Small));
            row.Add(rank);

            var power = new Label(((int)System.Math.Round(r.Strength)).ToString());
            power.AddToClassList("xt-c-power");
            power.AddToClassList("f-num");   // 純数値セル＝コンデンス体（書体3役, 設計書16）
            row.Add(power);

            var id = r.SchoolId;
            row.RegisterCallback<ClickEvent>(_ => OnPickOffer(id));
            return row;
        }

        private void OnPickOffer(int? schoolId)
        {
            if (_xtPick == schoolId) return;
            _xtPick = schoolId;
            RenderTransferOffer(KokoSim.Unity.Shell.ManagerTransferService.BuildView());
        }

        private void RenderTransferOfferDetail(KokoSim.Unity.Shell.TransferOfferView view)
        {
            KokoSim.Unity.Shell.TransferOfferRow picked = null;
            foreach (var r in view.Rows) if (r.SchoolId == _xtPick) { picked = r; break; }
            if (picked == null) return;

            SetText("xt-d-name", picked.IsStay ? picked.SchoolName + "（残留）" : picked.SchoolName);
            SetText("xt-d-sub", picked.PrefectureName);

            var facts = _root.Q<VisualElement>("xt-d-facts");
            if (facts != null)
            {
                facts.Clear();
                facts.Add(XtFact("総合", ((int)System.Math.Round(picked.Strength)).ToString()));
            }

            if (_xtOk != null) _xtOk.text = picked.IsStay ? "残留する" : "この学校へ赴任する";
        }

        private static VisualElement XtFact(string key, string value)
        {
            var e = new VisualElement();
            e.AddToClassList("xt-fact");
            var k = new Label(key); k.AddToClassList("xt-fact__k");
            var v = new Label(value); v.AddToClassList("xt-fact__v"); v.AddToClassList("f-num");
            e.Add(k); e.Add(v);
            return e;
        }

        private void OnTransferConfirm()
        {
            // 確定は裏の全シムの完了待ちを含み得る同期処理（issue #1226）。ManualWait で「転任準備中」を
            // 1フレーム先に描画してからブロックへ入る（OnSave/OnLoad と同じ作法）。
            var pick = _xtPick;
            KokoSim.Unity.Shell.ManualWait.Run(_root, "転任準備中",
                () => KokoSim.Unity.Shell.ManagerTransferService.Confirm(pick), ok =>
            {
                if (!ok) return;
                if (_xt != null) _xt.style.display = DisplayStyle.None;
                Render();
                if (_state.BannerPending) ShowBanner();
                else TryShowEvent();
            });
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
            // 主将の次は新チームの背番号決定（issue #954・案C: メンバー設定画面へ直行）。
            if (KokoSim.Unity.Shell.UniformNumberService.Pending) GoToUniformNumbers();
            else if (_state.BannerPending) ShowBanner();   // 指名の後に大会開幕演出が残っていれば続けて出す
            else TryShowEvent();                      // 開幕が無ければ持ち越したイベントを続けて出す（issue #609）
        }

        /// <summary>
        /// 試合の日に着いた。初めての試合なら采配の説明（issue #1496）を先に出し、読み終えたら
        /// <see cref="OnNovelClosed"/> が開始確認を開く。試合の画面ではノベルを出さないので、ここ（ホーム）で出す。
        /// </summary>
        private void ShowMatchDay()
        {
            // 公式戦・練習試合を問わず最初の1試合でだけ出る（練習試合で見ていれば公式戦の初戦では出ない）。
            if (KokoSim.Unity.Shell.TutorialService.RaiseBeforeMatch(KokoSim.Engine.Career.Tutorial.MatchKind.Official)
                && _novel != null && _novel.OpenNext())
            {
                SetBodyVisible(false);
                return;
            }
            ShowDialog();
        }

        private void ShowBanner()
        {
            _state.PushTournamentOpenFeed();   // 「大会が開幕した」を通知フィードへ（大会遷移は TournamentEntry が実施済み）
            _state.ConsumeBanner();
            Render();   // 積んだ開幕の通知（と自校の印・#1476）を出す。呼び出し元は演出の前に描き終えている
            // 初めての公式戦の開幕: 大会の流れの説明（issue #1496）。開幕の演出が消えてから出す。
            var tutorial = KokoSim.Unity.Shell.TutorialService.Raise(KokoSim.Engine.Career.TutorialScene.FirstTournament);
            if (_banner == null) { if (tutorial) TryShowEvent(); return; }
            SetText("tm-banner-top", _state.BannerTop);
            SetText("tm-banner-name", _state.BannerName);

            _banner.style.display = DisplayStyle.Flex;
            _banner.BringToFront();
            _banner.style.opacity = 0f;
            _banner.experimental.animation.Start(0f, 1f, 350, (e, v) => e.style.opacity = v);
            _banner.schedule.Execute(() =>
            {
                _banner.experimental.animation.Start(1f, 0f, 420, (e, v) => e.style.opacity = v)
                    .OnCompleted(() =>
                    {
                        _banner.style.display = DisplayStyle.None;
                        if (tutorial) TryShowEvent();
                    });
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
                // 出場校決定戦（2校の一発勝負）は勝っても「優勝」ではない
                var playoff = KokoSim.Unity.Shell.RegionalEntry.IsPlayoff(
                    KokoSim.Unity.Shell.GameSession.Current.Kind, KokoSim.Unity.Shell.GameSession.Current.Field);
                headline.text = o.IsChampion && !playoff ? "優勝！" : (win ? "勝利！" : "敗戦");
                headline.EnableInClassList("tm-result__headline--win", win);
                headline.EnableInClassList("tm-result__headline--lose", !win);
            }
            SetText("tm-result-score", KokoSim.Unity.Shell.NationService.ManagerSchoolName + " " + o.ManagerScore + " － " + o.OpponentScore + " " + o.OpponentName);

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
            // 夏の敗退直後は後始末の中で3年が引退する（issue #954）＝終わったらその場で引退の会話 → 主将指名へ続ける。
            KokoSim.Unity.Shell.BackgroundGameOp.Run(() => _state.DismissResult(), () =>
            {
                Render();
                if (KokoSim.Unity.Shell.NewTeamService.Pending) ContinueNewTeamFlow();
                // 後始末の中で積まれたもの（地区予選リーグ通過のイベント・連鎖した大会の開幕）もここで出す。
                // 出さないと、イベントはタブを移って戻るまで出ずセーブもできず、開幕バナーは最初のタブ
                // クリックでホームへ弾き戻されてから出ていた（週送りの分岐と同じ順に揃える）。
                else if (_state.BannerPending) ShowBanner();
                else TryShowEvent();
            });
        }

        private void Render()
        {
            var v = _state.BuildView();

            // 自校の表札・日付・進行ボタンの文言・部費・チーム総合・名声は TopBarMeters／ScoreboardStrip が全画面共通で埋める。
            KokoSim.Unity.Components.ScoreboardStrip.Fill(_root);
            KokoSim.Unity.Shell.TopBarMeters.Fill(_root);

            var accent = KokoSim.Unity.Shell.SchoolAccent.ForManager();
            RenderTeamStrength(accent);
            RenderHero(v);

            // 故障者（0人は高さ56の1行。1人以上は今の一覧を下に出す）。
            SetText("injury-count", v.Injured.Count.ToString());
            var injScroll = _root.Q<VisualElement>("injury-scroll");
            if (injScroll != null) injScroll.style.display = v.Injured.Count > 0 ? DisplayStyle.Flex : DisplayStyle.None;
            BuildList("injury-list", v.Injured, BuildInjured);

            RenderStats(v.TournamentMode);
            RenderRankings(v.TournamentMode);
            BuildList("feed-list", v.Feed, BuildFeed);
            var slots = new System.Collections.Generic.List<VisualElement>();
            for (var i = 0; i < v.Guidance.Count; i++) slots.Add(BuildGuidance(v.Guidance[i], i + 1));
            var gl = _root.Q<VisualElement>("guidance-list");
            if (gl != null) { gl.Clear(); foreach (var e in slots) gl.Add(e); }
            SetText("guidance-used", v.GuidanceUsed.ToString());
            SetText("guidance-total", "/ " + v.GuidanceTotal + " 使用中");

            // スクールカラー（表示用の色）を acc-* の付いた要素へ流し込む（主役のカード・今大会の列・番号・縦線）。
            KokoSim.Unity.Shell.SchoolAccent.Apply(_root, accent);
        }

        /// <summary>主役のカード。大会期間＝次の試合、大会のない時期＝次の大会まで（週の目盛り）。</summary>
        private void RenderHero(HomeView v)
        {
            var heroMatch = _root.Q<VisualElement>("hm-hero-match");
            var heroPlain = _root.Q<VisualElement>("hm-hero-plain");
            if (heroMatch != null) heroMatch.style.display = v.TournamentMode ? DisplayStyle.Flex : DisplayStyle.None;
            if (heroPlain != null) heroPlain.style.display = v.TournamentMode ? DisplayStyle.None : DisplayStyle.Flex;

            if (v.TournamentMode)
            {
                SetText("hero-days", v.HeroDays);
                var unit = _root.Q<Label>("hero-days-unit");
                if (unit != null) unit.style.display = v.HeroDays == "―" ? DisplayStyle.None : DisplayStyle.Flex;
                SetText("game-tag", v.GameTag);
                SetText("tournament-name", v.TournamentName);
                SetText("home-team", v.HomeTeam);
                SetText("away-team", v.AwayTeam);
                var rankGroup = _root.Q<VisualElement>("away-rank-group");
                if (rankGroup != null) rankGroup.style.display = string.IsNullOrEmpty(v.AwayRankGrade) ? DisplayStyle.None : DisplayStyle.Flex;
                var rank = _root.Q<VisualElement>("away-rank");
                if (rank != null)
                {
                    rank.Clear();
                    if (!string.IsNullOrEmpty(v.AwayRankGrade)) rank.Add(UiComponents.RankChipLegacy(v.AwayRankGrade));
                }
                SetText("match-day", v.MatchDay);
                // 天気（issue #803/#1199）。試合当日だけ・屋内は出さない。地はスクールカラー＝暗い背景用のマーク。
                var weatherGroup = _root.Q<VisualElement>("weather-group");
                if (weatherGroup != null)
                    weatherGroup.style.display = v.Weather.HasValue ? DisplayStyle.Flex : DisplayStyle.None;
                if (v.Weather is { } wx)
                    KokoSim.Unity.Shell.WeatherIcons.Apply(_root.Q<VisualElement>("weather-icon"), wx, darkBackground: true);
                SetText("weather-temp-num", v.WeatherTempValue);
                SetText("weather-temp-unit", v.WeatherTempUnit);
                return;
            }

            SetText("hero-cd-label", v.HeroCountdownLabel);
            SetText("hero-cd-num", v.HeroBigValue);
            SetText("hero-cd-unit", v.HeroBigUnit);
            SetText("next-tournament", v.NextTournamentName);
            SetText("opening-week", v.OpeningLabel);

            var ticks = _root.Q<VisualElement>("ticks");
            var labels = _root.Q<VisualElement>("tick-labels");
            if (ticks == null || labels == null) return;
            ticks.Clear(); labels.Clear();
            for (var i = 0; i < v.Ticks.Count; i++)
            {
                var t = v.Ticks[i];
                var last = i == v.Ticks.Count - 1;
                var cell = new VisualElement();
                cell.AddToClassList("hm-tick");
                if (t.Opening) cell.AddToClassList("hm-tick--open");
                else if (t.Now) cell.AddToClassList("hm-tick--now");
                if (last) cell.AddToClassList("hm-tick--last");
                ticks.Add(cell);

                var l = new Label(t.Label);
                l.AddToClassList("hm-tick-label");
                if (t.LabelStrong) l.AddToClassList("hm-tick-label--strong");
                if (last) l.AddToClassList("hm-tick--last");
                labels.Add(l);
            }
        }

        /// <summary>チーム成績。記録が無ければ表の代わりに案内（高さ150）。大会のない時期は今大会の列ごと出さない。</summary>
        private void RenderStats(bool tournament)
        {
            var has = KokoSim.Unity.Shell.TeamStatsService.HasRecords();
            var card = _root.Q<VisualElement>("stats-card");
            card?.EnableInClassList("hm-card--stats-empty", !has);
            Show("stats-empty", !has);
            Show("team-stats", has);
            Show("stats-cur-head", tournament);
            var head = _root.Q<VisualElement>("stats-head");
            if (head != null)
            {
                // 記録なしでも見出し（チーム成績）は出す。列の見出しだけ隠す。
                foreach (var c in head.Query<Label>(className: "hm-st-c").ToList()) c.style.display = has ? DisplayStyle.Flex : DisplayStyle.None;
                _root.Q<Label>("stats-cur-head").style.display = has && tournament ? DisplayStyle.Flex : DisplayStyle.None;
            }
            var list = _root.Q<VisualElement>("team-stats");
            if (list == null) return;
            list.Clear();
            if (!has) return;
            foreach (var r in KokoSim.Unity.Shell.TeamStatsService.TeamTotals()) list.Add(BuildStatRow(r, tournament));
        }

        private void Show(string name, bool on)
        {
            var e = _root.Q<VisualElement>(name);
            if (e != null) e.style.display = on ? DisplayStyle.Flex : DisplayStyle.None;
        }

        /// <summary>チームランクのカード（総合ランク＋6角形）。総合力パネルと同じ ViewModel を引く。
        /// 弱点の言語化（AnalysisWeak/Advice）はレーダーを見れば判るのでホームには出さない。</summary>
        private void RenderTeamStrength(Color accent)
        {
            var ts = new KokoSim.Unity.Squad.TeamStrengthState().BuildView();

            var tsChip = _root.Q<VisualElement>("team-strength-chip");
            if (tsChip != null)
            {
                tsChip.Clear();
                tsChip.Add(UiComponents.RankChipLegacy(ts.OverallGrade));
            }
            SetText("team-strength-value", ts.OverallValue.ToString());
            if (_tsRadar != null)
            {
                _tsRadar.SetData(ts.Radar, ts.OverallGrade);
                _tsRadar.SetAccent(accent, rings: 4);
            }
        }

        // ===== 行ビルダー =====

        /// <summary>チーム成績の1行（項目｜通算｜公式戦｜今大会＝スクールカラー8%の帯に明朝で大きく）。</summary>
        private static VisualElement BuildStatRow(KokoSim.Unity.Shell.TeamStatRow r, bool tournament)
        {
            var row = new VisualElement();
            row.AddToClassList("hm-st-row");

            var k = new Label(r.Label);
            k.AddToClassList("hm-st-k");
            row.Add(k);

            var career = new Label(r.Career); career.AddToClassList("hm-st-c"); row.Add(career);
            var official = new Label(r.Official); official.AddToClassList("hm-st-c"); official.AddToClassList("hm-st-c--off"); row.Add(official);
            if (tournament)
            {
                var cur = new Label(r.Tournament);
                cur.AddToClassList("hm-st-cur");
                cur.AddToClassList("acc-tint");
                row.Add(cur);
            }
            return row;
        }

        /// <summary>チーム内ランキング。打撃／投手を左右2列。右上の切り替えで通算／公式戦／今大会（大会のない時期は今大会なし）。</summary>
        private void RenderRankings(bool tournament)
        {
            var has = KokoSim.Unity.Shell.TeamStatsService.HasRecords();
            Show("rank-empty", !has);
            Show("rank-body", has);
            Show("rank-scope", has);
            if (!tournament && _rankScope == KokoSim.Unity.Shell.RankingScope.Tournament)
                _rankScope = KokoSim.Unity.Shell.RankingScope.Career;
            BuildScopeSwitch(tournament);

            var bat = _root.Q<VisualElement>("rank-bat");
            var pit = _root.Q<VisualElement>("rank-pit");
            bat?.Clear();
            pit?.Clear();
            if (!has) return;

            var names = new List<Label>();
            foreach (var r in KokoSim.Unity.Shell.TeamStatsService.Rankings(_rankScope))
            {
                var host = r.Section == "投手" ? pit : bat;
                host?.Add(BuildRankRow(r, names));
            }
            // 名前の大きさはカード内で全員そろえる（入らない人がいれば全員同じ倍率で縮む・issue #970）。
            UiComponents.ShrinkGroupToFit(names, _root);
        }

        private void BuildScopeSwitch(bool tournament)
        {
            var seg = _root.Q<VisualElement>("rank-scope");
            if (seg == null) return;
            seg.Clear();
            void Add(string text, KokoSim.Unity.Shell.RankingScope scope, bool first)
            {
                var b = new Label(text);
                b.AddToClassList("hm-seg__btn");
                if (first) b.AddToClassList("hm-seg__btn--first");
                if (_rankScope == scope) b.AddToClassList("hm-seg__btn--on");
                b.RegisterCallback<ClickEvent>(_ =>
                {
                    if (_rankScope == scope) return;
                    _rankScope = scope;
                    RenderRankings(tournament);
                });
                seg.Add(b);
            }
            Add("通算", KokoSim.Unity.Shell.RankingScope.Career, true);
            Add("公式戦", KokoSim.Unity.Shell.RankingScope.Official, false);
            if (tournament) Add("今大会", KokoSim.Unity.Shell.RankingScope.Tournament, false);
        }

        /// <summary>チーム内ランキングの1部門（1位＝記録を明朝で大きく＋名前／2位＝右端に小さく記録と名前）。</summary>
        private static VisualElement BuildRankRow(KokoSim.Unity.Shell.RankRow r, List<Label> names)
        {
            var row = new VisualElement();
            row.AddToClassList("hm-rk-row");

            var k = new Label(r.Label);
            k.AddToClassList("hm-rk-k");
            row.Add(k);

            var first = new VisualElement();
            first.AddToClassList("hm-rk-1");
            var second = new VisualElement();
            second.AddToClassList("hm-rk-2");

            var e1 = r.Top.Count > 0 ? r.Top[0] : null;
            if (e1 == null || e1.Empty)
            {
                var none = new Label("—"); none.AddToClassList("hm-rk--none"); first.Add(none);
            }
            else
            {
                var val = UiComponents.SetInnings(new Label(), e1.Value); val.AddToClassList("hm-rk-1__v");
                var name = new Label(e1.Name); name.AddToClassList("hm-rk-1__n");
                names.Add(name);
                first.Add(val); first.Add(name);
            }

            var e2 = r.Top.Count > 1 ? r.Top[1] : null;
            if (e2 == null || e2.Empty)
            {
                var none = new Label("—"); none.AddToClassList("hm-rk--none"); none.AddToClassList("hm-rk-2__v"); second.Add(none);
            }
            else
            {
                var val = UiComponents.SetInnings(new Label(), e2.Value); val.AddToClassList("hm-rk-2__v");
                var name = new Label(e2.Name); name.AddToClassList("hm-rk-2__n");
                second.Add(val); second.Add(name);
            }

            row.Add(first);
            row.Add(second);
            return row;
        }

        /// <summary>故障者の1行（背番号／名前／傷病名・部位・程度／復帰まで。重度だけ警告色＝UI原則②）。</summary>
        private static VisualElement BuildInjured(InjuredRow r)
        {
            var row = new VisualElement();
            row.AddToClassList("hm-inj");

            var no = new Label(r.Number); no.AddToClassList("hm-inj__no"); row.Add(no);
            var name = new Label(r.Name); name.AddToClassList("hm-inj__n"); row.Add(name);
            UiComponents.ShrinkToFit(name);   // 傷病名の欄を固定にした残りに入らない名前だけ縮める（issue #970）

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
            var item = new VisualElement();
            item.AddToClassList("fd-item");

            // 左の縦線はスクールカラー。警告だけは警告色（UI原則②：本当の警告＝怪我など）で、札の文字も同じ色。
            var warn = f.Tag == "警告";
            var bar = new VisualElement();
            bar.AddToClassList("fd-bar");
            bar.AddToClassList(warn ? "fd-bar--warn" : "acc-bg");
            item.Add(bar);

            var body = new VisualElement();
            body.AddToClassList("fd-body");

            var meta = new VisualElement();
            meta.AddToClassList("fd-meta");
            var tag = new Label(f.Tag);
            tag.AddToClassList("fd-tag");
            if (warn) tag.AddToClassList("fd-tag--warn");
            var time = new Label(f.When);
            time.AddToClassList("fd-time");
            meta.Add(tag); meta.Add(time);
            body.Add(meta);

            var text = new Label(Kinsoku.Apply(f.Text));   // 句読点が行頭に落ちない（issue #1501）
            text.AddToClassList("fd-text");
            body.Add(text);

            item.Add(body);

            // 押すと飛び先へ（issue #1474）。飛び先が一意に決まる通知だけ押せる＝押せる行は面の色で示す。
            if (FeedLinks.CanOpen(f))
            {
                item.AddToClassList("fd-item--link");
                item.RegisterCallback<ClickEvent>(_ => FeedLinks.Open(f));
            }
            return item;
        }

        /// <summary>個別指導の1枠。指名済み＝番号の丸（スクールカラー）＋名前＋矢印、空き＝点線の枠に「＋ 選手を指名」。
        /// どちらも押すと練習計画へ（指名・解除は部員表の★で行う, issue #707）。</summary>
        private static VisualElement BuildGuidance(GuidanceSlot g, int number)
        {
            var slot = new VisualElement();
            slot.AddToClassList("gd-slot");
            slot.AddToClassList("gd-slot--link");
            slot.RegisterCallback<ClickEvent>(_ => KokoSim.Unity.Shell.ScreenRouter.Instance?.ShowDeferred("TrainingPlan"));
            if (g.Empty)
            {
                slot.AddToClassList("gd-slot--empty");
                slot.Add(new DashedFrame());
                var plus = new KokoSim.Unity.Shell.LineIcon(KokoSim.Unity.Shell.LineIcon.Kind.Plus) { Stroke = 2.2f };
                plus.AddToClassList("gd-plus");
                slot.Add(plus);
                var t = new Label("選手を指名");
                t.AddToClassList("gd-empty-t");
                slot.Add(t);
                return slot;
            }

            var no = new Label(number.ToString());
            no.AddToClassList("gd-no");
            no.AddToClassList("acc-bg");
            slot.Add(no);
            var name = new Label(g.Name); name.AddToClassList("gd-name");
            slot.Add(name);
            var arrow = new KokoSim.Unity.Shell.LineIcon(KokoSim.Unity.Shell.LineIcon.Kind.ChevronRight) { Stroke = 2.2f };
            arrow.AddToClassList("gd-arrow");
            slot.Add(arrow);
            return slot;
        }

        // ===== 補助 =====

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
