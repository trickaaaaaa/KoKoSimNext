using System;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Components;   // 部品辞書（NumUnit）
using KokoSim.Unity.Shell;        // SaveSlots / ScreenRouter

namespace KokoSim.Unity.Saves
{
    /// <summary>
    /// セーブデータ画面。<b>手動3枠</b>を縦長のカード3枚（部品 <see cref="SaveSlotCard"/>）で横に並べ、カードの中に
    /// 中身（校名・時期・チーム総合・監督通算・甲子園出場・保存日時）と操作を置く（オート枠は廃止・監督決定 2026-08-09）。
    ///
    /// <para>タイトルの「つづきから」と共通ナビの「システム」タブの2箇所から同じ画面を開く。
    /// <see cref="SaveSlots.Entry"/> で分岐する: タイトル経由（<see cref="SaveSlotsEntry.Title"/>）は
    /// 「今保存すべき進行状態が無い」ため読込／削除だけ（オーナー判断・issue #508）。墨の地に「つづきから」の見出しと
    /// 「最新」の札を出す。タブ経由（<see cref="SaveSlotsEntry.Tab"/>）は紙色の地で、保存と「プレイ中」の札も出す。
    /// カードは同じ部品で、違うのは地・目印の札と枠・ボタンだけ。「タイトルへ戻る」はどちらにも出す
    /// （ゲーム内は確認を挟む・つづきからは失う進行が無いのでそのまま戻る）。</para>
    ///
    /// <para>カードの中身は、保存のときにセーブへ一緒に書いた概要（<see cref="KokoSim.Engine.Saves.SaveSummary"/>）を
    /// 見出しの流し読みで読む＝本体は組み立てない。概要が無い古いセーブの項目は「—」。</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class SaveSlotsController : MonoBehaviour
    {
        private VisualElement _root;
        private VisualElement _confirm;
        private string _errorText = "";
        private Action _confirmAction;   // 確認ダイアログで「はい」を押したときにやること
        /// <summary>進行中のゲームの上で開いているか（保存・タイトルへ戻るはこのときだけ）。</summary>
        private bool _inGame;
        private VisualElement _titleConfirm;

        private Button _confirmYes, _confirmNo, _advanceBtn;
        private Button _titleBtn, _titleYes, _titleNo;

        // 画面内タブ（issue #946）: 0＝セーブデータ／1＝校名の編集（ゲーム中だけ）／最後＝クレジット
        // （issue #1179・オーナー決定「入口は設定」＝この「システム」タブが唯一の設定っぽい画面）。
        // 校名の編集はゲーム中（タブ経由）だけ出るので、配列はそのぶん長さが変わる＝インデックスを
        // 固定の定数にしない（_creditsPage で参照する）。
        private string[] _pageLabels;
        private int _creditsPage;
        private int _settingsPage;
        private int _page;
        private SchoolRenamePage _rename;

        /// <summary>校名の編集ページ（撮影ハーネス用。ゲーム中に開いたときだけ非null）。</summary>
        public SchoolRenamePage RenamePage => _rename;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _confirm = _root.Q<VisualElement>("ss-confirm");
            var entry = SaveSlots.Entry;
            _inGame = entry != SaveSlotsEntry.Title;
            SetupTopBar(entry == SaveSlotsEntry.Tab);

            _confirmYes = Wire("ss-confirm-yes", OnConfirmYes);
            _confirmNo = Wire("ss-confirm-no", CloseConfirm);
            CloseConfirm();   // 画面を離れても隠した状態は残る＝必ず閉じた状態から始める
            // タイトル経由（つづきから）は墨の地・見出しの行・ダイヤモンドの線画。
            _root.Q<VisualElement>(className: "ss-root")?.EnableInClassList("ss-root--night", !_inGame);
            SetupDiamond(!_inGame);
            _titleConfirm = _root.Q<VisualElement>("ss-title-confirm");
            _titleBtn = Wire("ss-title", OnTitleRequest);
            _titleYes = Wire("ss-title-yes", OnTitleConfirm);
            _titleNo = Wire("ss-title-no", OnTitleCancel);
            if (_titleBtn != null)
            {
                _titleBtn.style.display = DisplayStyle.Flex;
                _titleBtn.Clear();   // 中身（左向きの矢印＋文字）。開き直しで二重に足さない
                _titleBtn.Add(new LineIcon(LineIcon.Kind.ChevronLeft) { Stroke = 2.2f });
                var titleText = new Label("タイトルへ戻る");
                titleText.AddToClassList("ss-title-btn__txt");
                _titleBtn.Add(titleText);
            }

            _errorText = "";
            // 校名の編集はゲーム中だけ（タイトル経由は全国がまだ生成されていない＝引いた時点で生成が走る）。
            _rename = _inGame ? new SchoolRenamePage(_root, OnSchoolRenamed) : null;
            _rename?.Bind();
            // クレジット（issue #1179）は校名の編集と違いゲーム中/タイトル経由どちらでも出す
            // ＝配列の長さが変わる（校名の編集はゲーム中だけ挟む）ので、クレジットの位置は末尾固定で持つ。
            // 設定（issue #1315）はクレジットの手前（末尾から2つ目）。
            _pageLabels = _rename != null
                ? new[] { "セーブデータ", "校名の編集", "設定", "クレジット" }
                : new[] { "セーブデータ", "設定", "クレジット" };
            _creditsPage = _pageLabels.Length - 1;
            _settingsPage = _pageLabels.Length - 2;
            BuildCreditsPage();
            ShowPage(0);
        }

        /// <summary>画面内タブを切り替える（0＝セーブデータ／校名の編集（ゲーム中だけ）／末尾＝クレジット）。
        /// 撮影ハーネスからも呼ぶ。</summary>
        public void ShowPage(int page)
        {
            _page = Mathf.Clamp(page, 0, _pageLabels.Length - 1);
            var tabs = _root.Q<VisualElement>("ss-tabs");
            if (tabs != null)
            {
                tabs.Clear();
                var seg = UiComponents.SegTabs(_pageLabels, _page, ShowPage);
                // 選択中は太字（実ウェイトの書体クラス）。最後の項目は右の間隔を持たない。
                seg.Q<VisualElement>(className: "seg-tab--on")?.AddToClassList("f-body-bd");
                if (seg.childCount > 0) seg[seg.childCount - 1].AddToClassList("seg-tab--last");
                tabs.Add(seg);
            }
            var renamePage = _rename != null ? 1 : -1;
            SetShown("ss-page-saves", _page == 0);
            SetShown("ss-page-rename", _page == renamePage);
            SetShown("ss-page-credits", _page == _creditsPage);
            SetShown("ss-page-settings", _page == _settingsPage);
            SetShown("ss-heading", !_inGame && _page == 0);
            SetShown("ss-count-header", _inGame && _page == 0);
            if (_page == _settingsPage) BuildSettingsPage();
            if (_page == renamePage) _rename.Open();
            else if (_page == 0) Render();
        }

        /// <summary>
        /// 設定（issue #1315・オーナー決定 2026-11-23: 項目＝画面・fps・音量／入口＝システムタブだけ／1枚のカードに縦並び）。
        /// 値は <see cref="GameSettings"/>（fps は <see cref="KokoSim.Bootstrap.FrameRateGuard"/>）が持ち、押すたびに保存・反映して組み直す。
        /// 画質（標準／軽量）は計測とセットで別に作る（steam-roadmap 段階1「要るなら軽量設定を作る」）。
        /// </summary>
        private void BuildSettingsPage()
        {
            var host = _root.Q<VisualElement>("ss-set-rows");
            if (host == null) return;
            host.Clear();

            Group(host, "画面", true);
            // ブラウザ版は大きさと全画面をブラウザ（と置き場所の全画面ボタン）が決める＝効かない行は出さない（オーナー決定 2026-11-23）。
            if (GameSettings.DisplayAdjustable)
            {
                var res = GameSettings.Resolution;
                var choices = GameSettings.ResolutionChoices();
                var ri = Mathf.Max(0, choices.IndexOf(res));
                Row(host, "解像度", UiComponents.Stepper(res.x + " × " + res.y,
                    () => { GameSettings.Resolution = choices[ri - 1]; BuildSettingsPage(); },
                    () => { GameSettings.Resolution = choices[ri + 1]; BuildSettingsPage(); },
                    ri > 0, ri < choices.Count - 1));
                Row(host, "表示", UiComponents.SegTabs(new[] { "フルスクリーン", "ウインドウ" }, GameSettings.FullScreen ? 0 : 1,
                    i => { GameSettings.FullScreen = i == 0; BuildSettingsPage(); }));
            }

            var fpsChoices = new[] { 30, 60, 120 };
            var fi = System.Array.IndexOf(fpsChoices, KokoSim.Bootstrap.FrameRateGuard.FpsLimit);
            Row(host, "fps の上限", UiComponents.SegTabs(new[] { "30", "60", "120" }, fi,
                i => { KokoSim.Bootstrap.FrameRateGuard.FpsLimit = fpsChoices[i]; BuildSettingsPage(); }));

            Group(host, "音量", false);
            VolumeRow(host, "全体", () => GameSettings.VolumeMaster, v => GameSettings.VolumeMaster = v);
            VolumeRow(host, "効果音", () => GameSettings.VolumeSe, v => GameSettings.VolumeSe = v);
            VolumeRow(host, "場内放送", () => GameSettings.VolumeAnnouncer, v => GameSettings.VolumeAnnouncer = v);
        }

        private void VolumeRow(VisualElement host, string name, System.Func<int> get, System.Action<int> set)
        {
            var v = get();
            const int step = GameSettings.VolumeStep;
            Row(host, name, UiComponents.Stepper(v.ToString(),
                () => { set(v - step); BuildSettingsPage(); },
                () => { set(v + step); BuildSettingsPage(); },
                v > 0, v < 100));
        }

        private static void Group(VisualElement host, string text, bool first)
        {
            var g = new Label(text);
            g.AddToClassList("ss-set__group");
            if (first) g.AddToClassList("ss-set__group--first");
            host.Add(g);
        }

        private static void Row(VisualElement host, string name, VisualElement control)
        {
            var row = new VisualElement();
            row.AddToClassList("ss-set");
            var k = new Label(name);
            k.AddToClassList("ss-set__k");
            row.Add(k);
            row.Add(control);
            host.Add(row);
        }

        /// <summary>クレジット（issue #1179・#1318）: 書体のライセンス全文を一度だけ流し込む（静的内容）。
        /// 場内放送の行は、場内放送が入っていないビルド（体験版）では出さない。</summary>
        private void BuildCreditsPage()
        {
            SetText("credits-notosansjp-body", LoadLicenseText("OFL-notosansjp"));
            SetText("credits-shippori-body", LoadLicenseText("OFL-shipporiminchob1"));
            SetText("credits-dotgothic-body", LoadLicenseText("OFL-dotgothic16"));
            SetText("credits-anton-body", LoadLicenseText("OFL-anton"));
            SetText("credits-delagothic-body", LoadLicenseText("OFL-delagothicone"));
            SetShown("credits-row-announcer", KokoSim.Unity.Audio.MatchAnnouncer.Available);
        }

        /// <summary>
        /// Assets/Resources/Licenses/*.txt を読む（正典は Assets/UI/Fonts/OFL-*.txt・CREDITS.md。
        /// ビルドに含めるには Resources 配下に置く必要があるため、起動時に読む複製をここへ置いている
        /// ＝フォントのライセンスを更新したらこちらも揃える）。
        /// </summary>
        private static string LoadLicenseText(string name)
        {
            var asset = Resources.Load<TextAsset>("Licenses/" + name);
            return asset != null ? asset.text : "";
        }

        /// <summary>改名の後: 自校名が変わるとトップバーの校名も変わる（編集ページはタブ経由＝トップバーが出ている）。</summary>
        private void OnSchoolRenamed() => TopBarMeters.Fill(_root);

        private void SetShown(string name, bool shown)
        {
            var e = _root.Q<VisualElement>(name);
            if (e != null) e.style.display = shown ? DisplayStyle.Flex : DisplayStyle.None;
        }

        // ScreenRouter は同じ GameObject を SetActive で付け外しするだけなので、外さないと
        // 画面往復のたびに多重登録される（HomeDashboardController と同じ作法）。
        private void OnDisable()
        {
            if (_confirmYes != null) _confirmYes.clicked -= OnConfirmYes;
            if (_confirmNo != null) _confirmNo.clicked -= CloseConfirm;
            if (_titleBtn != null) _titleBtn.clicked -= OnTitleRequest;
            if (_titleYes != null) _titleYes.clicked -= OnTitleConfirm;
            if (_titleNo != null) _titleNo.clicked -= OnTitleCancel;
            if (_advanceBtn != null) _advanceBtn.clicked -= OnAdvance;
            _rename?.Unbind();
        }

        /// <summary>
        /// タブ経由のときだけ共通トップバー（掲示板ヘッダ＋タブ帯）を出して値を埋める（issue #565）。
        /// タイトル経由では出せない＝この画面はシェルサービスの遅延初期化より前に開くので、
        /// 値を引いた時点でロスターや全国が生成されてしまう（<see cref="SaveSlotsEntry"/> の注記）。
        /// </summary>
        private void SetupTopBar(bool show)
        {
            var bar = _root.Q<VisualElement>("ss-topbar");
            if (bar != null) bar.style.display = show ? DisplayStyle.Flex : DisplayStyle.None;
            if (!show) return;

            KokoSim.Unity.Components.ScoreboardStrip.Fill(_root);
            TopBarMeters.Fill(_root);
            var rank = _root.Q<VisualElement>("team-rank");
            if (rank != null)
            {
                rank.Clear();
                rank.Add(UiComponents.RankChipLegacy(TeamOverall.GradeOf(RosterService.Active)));
            }

            // 「今週を進める」は他のタブと同じ共通実装へ配線する（配線しないと死にボタンになる＝issue #134）。
            _advanceBtn = _root.Q<Button>("advance");
            if (_advanceBtn != null) _advanceBtn.clicked += OnAdvance;
        }

        // 週送りのあとはトップバー（日付・部費）も描き直す（issue #1527: 日付が変わらず、進んでいないように見えた）。
        private void OnAdvance() => WeekAdvance.FromSideScreen(() => { TopBarMeters.Fill(_root); Render(); });

        private Button Wire(string name, Action handler)
        {
            var btn = _root.Q<Button>(name);
            if (btn != null) btn.clicked += handler;
            return btn;
        }

        private void SetText(string name, string text)
        {
            var l = _root.Q<Label>(name);
            if (l != null) l.text = text;
        }

        private void Render()
        {
            RenderNote();
            RenderSlots();
        }

        /// <summary>
        /// 見出しの上の1行。出すのは<b>本当の警告だけ</b>＝操作の失敗と、保存できない理由（UI原則②）。
        /// 何も無ければ隠す（空き枠の案内などは出さない＝カードが見せている）。
        /// </summary>
        private void RenderNote()
        {
            var note = _root.Q<Label>("ss-note");
            if (note == null) return;
            var text = !string.IsNullOrEmpty(_errorText) ? _errorText
                : _inGame && !SaveGameIO.CanSave ? SaveGameIO.SaveDisabledReason()   // 条件を書き写さない（単一ソース）
                : "";
            note.text = Kinsoku.Apply(text);   // 「。」だけが次の行へ落ちない（issue #1501/#1533）
            note.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
        }

        // ── 3枠のカード ──────────────────────────────────

        private void RenderSlots()
        {
            var host = _root.Q<VisualElement>("ss-slots");
            if (host == null) return;
            host.Clear();
            host.EnableInClassList(SaveSlotCard.OnNightClass, !_inGame);
            host.EnableInClassList(SaveSlotCard.OnPaperClass, _inGame);

            var cards = new SaveSlotCardData[SaveSlots.AllSlotIds.Length];
            var used = 0;
            var latest = -1;
            for (var i = 0; i < cards.Length; i++)
            {
                var info = SaveSlots.ReadInfo(SaveSlots.AllSlotIds[i]);
                var d = SaveSlotCardData.From(info, i + 1);
                d.IsPlaying = _inGame && info.Exists && SaveSlots.IsActive(info.SlotId);
                cards[i] = d;
                if (!info.Exists) continue;
                used++;
                if (!info.Corrupt && (latest < 0 || info.SavedAt > cards[latest].SavedAt)) latest = i;
            }
            if (latest >= 0) cards[latest].IsLatest = true;

            var mode = _inGame ? SaveSlotCardMode.InGame : SaveSlotCardMode.Continue;
            var now = DateTime.Now;
            for (var i = 0; i < cards.Length; i++)
            {
                var d = cards[i];
                var actions = new SaveSlotCard.Actions
                {
                    Load = () => AskLoad(d),
                    Delete = () => AskDelete(d),
                    // 空きへの保存は確認なし・上書きは確認を挟む。保存はゲーム中だけ。
                    Save = _inGame ? (d.Exists ? (Action)(() => AskOverwrite(d)) : () => DoSave(d.SlotId)) : null,
                    CanSave = _inGame && SaveGameIO.CanSave,
                };
                var card = SaveSlotCard.Build(d, mode, actions, now);
                if (i == cards.Length - 1) card.AddToClassList("ssc--last");
                host.Add(card);
            }
            RenderCount(used, cards.Length);
        }

        /// <summary>「2 / 3 枠を使用中」（ゲーム内は切り替えの右・つづきからは見出しの行の右端）。</summary>
        private void RenderCount(int used, int total)
        {
            var host = _root.Q<VisualElement>(_inGame ? "ss-count-header" : "ss-count-heading");
            if (host == null) return;
            host.Clear();
            host.Add(Text(used.ToString(), "ss-count__n", "f-num-bd"));
            host.Add(Text("/ " + total + " 枠を使用中", "ss-count__rest", "f-body"));
        }

        /// <summary>つづきからの地に敷く内野のダイヤモンドの線画（ゲーム内では外す）。</summary>
        private void SetupDiamond(bool show)
        {
            var host = _root.Q<VisualElement>("ss-diamond-host");
            if (host == null) return;
            host.Clear();
            if (!show) return;
            var lines = new InfieldDiamondLines();
            lines.AddToClassList("ss-diamond");
            host.Add(lines);
        }

        private static Label Text(string text, params string[] classes)
        {
            var l = new Label(text);
            foreach (var c in classes) l.AddToClassList(c);
            return l;
        }

        private static string SlotLabel(string slotId) => slotId switch
        {
            "manual1" => "手動1",
            "manual2" => "手動2",
            "manual3" => "手動3",
            _ => slotId,
        };

        // ── 操作（確認 → 実行） ─────────────────────────────────

        /// <summary>上書き保存の確認（空きへの保存は確認なしで <see cref="DoSave"/>）。</summary>
        private void AskOverwrite(SaveSlotCardData d)
        {
            if (!_inGame || !SaveGameIO.CanSave) return;
            var id = d.SlotId;
            Ask(SlotLabel(id) + "（" + d.SchoolName + "・" + d.WeekLabel + "）に上書きします。よろしいですか？",
                "上書き保存", danger: false, () => DoSave(id));
        }

        /// <summary>
        /// 読み込みの確認。ゲーム中は「保存していない進行は失われます」を挟む。タイトル経由は失う進行が無いので
        /// 確認せずに読み込む。
        /// </summary>
        private void AskLoad(SaveSlotCardData d)
        {
            var id = d.SlotId;
            if (!_inGame) { DoLoad(id); return; }
            Ask(SlotLabel(id) + "（" + d.SchoolName + "・" + d.WeekLabel + "）を読み込みます。保存していない進行は失われます。",
                "読み込む", danger: false, () => DoLoad(id));
        }

        private void AskDelete(SaveSlotCardData d)
        {
            var id = d.SlotId;
            var what = d.Corrupt ? "読み込めないデータ" : d.SchoolName;
            Ask(SlotLabel(id) + "（" + what + "）のデータを削除します。元に戻せません。",
                "削除する", danger: true, () =>
                {
                    SaveSlots.Delete(id);
                    _errorText = "";
                    Render();
                });
        }

        /// <summary>確認ダイアログを開く。確定ボタンは墨（上書き・読み込み）か削除色（削除）。</summary>
        private void Ask(string question, string yesText, bool danger, Action onYes)
        {
            if (_confirm == null) { onYes(); return; }
            var q = _root.Q<Label>("ss-confirm-q");
            if (q != null) q.text = Kinsoku.Apply(question);
            if (_confirmYes != null)
            {
                _confirmYes.text = yesText;
                _confirmYes.EnableInClassList("dlg-btn--ink", !danger);
                _confirmYes.EnableInClassList("dlg-btn--danger", danger);
            }
            _confirmAction = onYes;
            _confirm.style.display = DisplayStyle.Flex;
            _confirm.BringToFront();
        }

        private void CloseConfirm()
        {
            _confirmAction = null;
            if (_confirm != null) _confirm.style.display = DisplayStyle.None;
        }

        private void OnConfirmYes()
        {
            var action = _confirmAction;
            CloseConfirm();
            action?.Invoke();
        }

        private void DoLoad(string slotId)
        {
            // 読み込み・展開・変換は別スレッド、全サービスへ戻すのはメインスレッド（LoadingJob が段ごとにコマをまたぐ
            // ＝待つ間もポップアップの点と棒が動く）。SaveSlots.LoadRoutine は復元の前に旧進行の裏シムの完了を待つ
            // （issue #1226）＝ここ（_inGame でもロードできる経路）から呼んでも別セーブの結果は混ざらない。
            LoadingJob.Run("ロード中", null, SaveSlots.LoadRoutine(slotId, (ok, error) =>
            {
                if (!ok)
                {
                    _errorText = string.IsNullOrEmpty(error) ? "読み込みに失敗しました。" : error;
                    RenderNote();
                    return;
                }
                // 前の進行の通知フィードを残さない（issue #1226。「タイトルへ戻る」の OnTitleConfirm と同じ作法）。
                KokoSim.Unity.Home.HomeState.Reset();
                // クリック配信中の同期 Show は UITK のイベント木を壊すため遅延切替を使う（ScreenRouter.cs 冒頭コメント参照）。
                ScreenRouter.Instance?.ShowDeferred("HomeDashboard");
            }));
        }

        /// <summary>ゲーム中だけ配線される。保存できない局面ではボタンが押せない状態だが、
        /// 経路が増えても落ちないよう再度ガードする（issue #509・オーナー申し送り）。</summary>
        private void DoSave(string slotId)
        {
            if (!_inGame || !SaveGameIO.CanSave) return;
            // 裏シムの完了待ちはコマをまたぎ、変換・書き出しは別スレッド（SaveSlots.SaveRoutine）。
            LoadingJob.Run("セーブ中", null, SaveSlots.SaveRoutine(slotId, ok =>
            {
                _errorText = ok ? "" : "保存に失敗しました。";
                Render();   // その枠のカード（時期・保存日時・プレイ中）を保存直後の内容へ更新する
            }));
        }

        // ===== タイトルへ戻る（issue #730 でホームの ≡メニューから移設。挙動は #509 のまま） =====

        private void OnTitleRequest()
        {
            // つづきから（タイトル経由）は失う進行が無い＝確認なしでタイトルへ戻る。
            if (!_inGame) { ScreenRouter.Instance?.ShowDeferred("TitleScreen"); return; }
            if (BackgroundGameOp.Running || LoadingJob.Running || _titleConfirm == null) return;
            var since = _root.Q<Label>("ss-title-since");
            if (since != null) since.text = LastSavedText();
            _titleConfirm.style.display = DisplayStyle.Flex;
            _titleConfirm.BringToFront();
        }

        private void OnTitleCancel()
        {
            if (_titleConfirm != null) _titleConfirm.style.display = DisplayStyle.None;
        }

        /// <summary>
        /// 最後の保存からの経過1行（オートセーブ廃止・監督決定 2026-08-09）。
        /// 自動で拾ってくれる保存が無いので、「ここで戻ると何週ぶん失うか」を確認の場で見せる。
        /// 週数は暦の通算週（(年度-1)×50＋週）の差で、単一ソースの <see cref="ManagerService.AbsoluteWeek"/> と同じ式。
        /// </summary>
        private static string LastSavedText()
        {
            if (SaveGameIO.LastSavedAt is not { } at)
                return "この進行はまだ一度も保存していません。";
            var lost = ManagerService.AbsoluteWeek - GameClock.AbsoluteWeekOf(at.YearIndex, at.Week);
            var label = SeasonClock.CurrentLabel(at.YearIndex, at.Week);
            return lost <= 0 ? $"最後の保存: {label}（保存済み）" : $"最後の保存: {label}（{lost}週前）";
        }

        private void OnTitleConfirm()
        {
            if (_titleConfirm != null) _titleConfirm.style.display = DisplayStyle.None;
            TitleReturn.Run(_root);
        }
    }
}
