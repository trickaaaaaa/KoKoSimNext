using System;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Components;   // 部品辞書（NumUnit）
using KokoSim.Unity.Shell;        // SaveSlots / ScreenRouter

namespace KokoSim.Unity.Saves
{
    /// <summary>
    /// セーブスロット選択画面（issue #508・オーナー承認の案2 リスト＋詳細ペイン）。<b>手動3枠</b>を
    /// 並べ、選択枠の詳細（学校名・年度・週・保存日時）と読込／削除を出す
    /// （オート枠は廃止・監督決定 2026-08-09）。
    ///
    /// <para>タイトルの「つづきから」と共通ナビの「システム」タブの2箇所から同じ画面を開く。
    /// <see cref="SaveSlots.Entry"/> で分岐する: タイトル経由（<see cref="SaveSlotsEntry.Title"/>）は
    /// 「今保存すべき進行状態が無い」ため読込／削除だけ（オーナー判断・issue #508）。タブ経由
    /// （<see cref="SaveSlotsEntry.Tab"/>）は保存（大会中は不可）と「タイトルへ戻る」（issue #730 でホームの
    /// ≡メニューから移設）も出す。</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class SaveSlotsController : MonoBehaviour
    {
        private VisualElement _root;
        private VisualElement _confirm;
        private string _selected;
        private string _errorText = "";
        /// <summary>進行中のゲームの上で開いているか（保存・タイトルへ戻るはこのときだけ）。</summary>
        private bool _inGame;
        private VisualElement _titleConfirm;

        private Button _loadBtn, _deleteBtn, _confirmYes, _confirmNo, _saveBtn, _advanceBtn;
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

            _loadBtn = Wire("ss-load", OnLoad);
            _deleteBtn = Wire("ss-delete", OnDeleteRequest);
            _confirmYes = Wire("ss-confirm-yes", OnDeleteConfirm);
            _confirmNo = Wire("ss-confirm-no", OnDeleteCancel);
            _saveBtn = Wire("ss-save", OnSave);
            _titleConfirm = _root.Q<VisualElement>("ss-title-confirm");
            _titleBtn = Wire("ss-title", OnTitleRequest);
            _titleYes = Wire("ss-title-yes", OnTitleConfirm);
            _titleNo = Wire("ss-title-no", OnTitleCancel);
            if (_titleBtn != null) _titleBtn.style.display = _inGame ? DisplayStyle.Flex : DisplayStyle.None;

            _selected = DefaultSlot(_inGame);
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
                tabs.Add(UiComponents.SegTabs(_pageLabels, _page, ShowPage));
            }
            SetText("ss-heading", _pageLabels[_page]);
            var renamePage = _rename != null ? 1 : -1;
            SetShown("ss-page-saves", _page == 0);
            SetShown("ss-page-rename", _page == renamePage);
            SetShown("ss-page-credits", _page == _creditsPage);
            SetShown("ss-page-settings", _page == _settingsPage);
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
            var res = GameSettings.Resolution;
            var choices = GameSettings.ResolutionChoices();
            var ri = Mathf.Max(0, choices.IndexOf(res));
            Row(host, "解像度", UiComponents.Stepper(res.x + " × " + res.y,
                () => { GameSettings.Resolution = choices[ri - 1]; BuildSettingsPage(); },
                () => { GameSettings.Resolution = choices[ri + 1]; BuildSettingsPage(); },
                ri > 0, ri < choices.Count - 1));
            Row(host, "表示", UiComponents.SegTabs(new[] { "フルスクリーン", "ウインドウ" }, GameSettings.FullScreen ? 0 : 1,
                i => { GameSettings.FullScreen = i == 0; BuildSettingsPage(); }));

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
            if (_loadBtn != null) _loadBtn.clicked -= OnLoad;
            if (_deleteBtn != null) _deleteBtn.clicked -= OnDeleteRequest;
            if (_confirmYes != null) _confirmYes.clicked -= OnDeleteConfirm;
            if (_confirmNo != null) _confirmNo.clicked -= OnDeleteCancel;
            if (_saveBtn != null) _saveBtn.clicked -= OnSave;
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

        private void OnAdvance() => WeekAdvance.FromSideScreen(Render);

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

        // ホーム/タブ経由（inGame）では、今プレイ中の学校のセーブがあればそれを既定選択にする
        // （手動1固定でユーザーが毎回クリックし直す不満の解消）。無ければ／タイトル経由（学校の概念が
        // まだ無い）は従来どおり「存在する最初のスロット」。タイトル側は SaveSlots.AnySaveExists() を
        // 確認してから遷移するため通常どれかは存在するが、無い場合も手動1へフォールバックして落ちない。
        private static string DefaultSlot(bool inGame)
        {
            if (inGame)
            {
                var school = NationService.ManagerSchoolName;
                foreach (var id in SaveSlots.AllSlotIds)
                {
                    var info = SaveSlots.ReadInfo(id);
                    if (info.Exists && !info.Corrupt && info.SchoolName == school) return id;
                }
            }
            foreach (var id in SaveSlots.AllSlotIds)
                if (SaveSlots.ReadInfo(id).Exists) return id;
            return SaveSlots.AllSlotIds[0];
        }

        /// <summary>スクショ・自動検証用に枠を選ぶ（通常は行クリック）。空き枠は選べない。ただしホーム経由
        /// （保存モード）は空き枠も選べる＝新規に保存する先として選べる必要があるため。</summary>
        public void Select(string slotId)
        {
            var info = SaveSlots.ReadInfo(slotId);
            if (!info.Exists && !_inGame) return;
            _selected = slotId;
            _errorText = "";
            Render();
        }

        private void Render()
        {
            RenderList();
            RenderDetail();
        }

        private void RenderList()
        {
            var host = _root.Q<VisualElement>("ss-rows");
            if (host == null) return;
            host.Clear();
            foreach (var id in SaveSlots.AllSlotIds)
                host.Add(BuildRow(SaveSlots.ReadInfo(id)));
        }

        private VisualElement BuildRow(SaveSlotInfo info)
        {
            var row = new VisualElement();
            row.AddToClassList("ss-row");

            var slot = new Label(SlotLabel(info.SlotId));
            slot.AddToClassList("ss-row__slot");
            row.Add(slot);

            var school = new Label(SchoolCellText(info));
            school.AddToClassList("f-display");
            school.AddToClassList("ss-row__school");
            row.Add(school);

            var yw = new Label(info.Exists && !info.Corrupt ? $"{info.YearIndex}/{info.Week}" : "");
            yw.AddToClassList("f-num");
            yw.AddToClassList("ss-row__yw");
            row.Add(yw);

            var selectable = info.Exists || _inGame;
            if (!selectable)
            {
                row.AddToClassList("ss-row--empty");
                return row;   // 「押しても何も起きない見た目」＝クリックハンドラを配線しない
            }
            if (!info.Exists) row.AddToClassList("ss-row--empty");   // 空きの見た目は保つが保存モードでは選択可

            if (info.SlotId == _selected) row.AddToClassList("ss-row--on");
            var id = info.SlotId;
            row.RegisterCallback<ClickEvent>(_ => Select(id));
            return row;
        }

        private void RenderDetail()
        {
            var info = SaveSlots.ReadInfo(_selected);

            // 校名は**そのセーブの中身**から出す（issue #624）。実行中の自校名を出すと、
            // タイトル経由ではどの枠も静的既定の「桜丘」になる＝スロットの区別が付かない。
            SetText("ss-d-name", info.Exists
                ? (info.Corrupt ? "読み込めません" : info.SchoolNameOrDefault)
                : "── 空き ──");
            SetText("ss-d-sub", SlotLabel(_selected));

            var facts = _root.Q<VisualElement>("ss-d-facts");
            if (facts != null)
            {
                facts.Clear();
                if (info.Exists && !info.Corrupt)
                {
                    facts.Add(FactRow("年度", UiComponents.NumUnit(info.YearIndex.ToString(), "年目")));
                    facts.Add(FactRow("週", UiComponents.NumUnit(info.Week.ToString(), "週")));
                    facts.Add(FactRow("保存日時", DateValue(info.SavedAt)));
                }
            }

            var note = _root.Q<Label>("ss-d-note");
            if (note != null)
            {
                var (text, warn) = NoteFor(info);
                note.text = text;
                note.EnableInClassList("ss-d__note--warn", warn);
                note.style.display = string.IsNullOrEmpty(text) ? DisplayStyle.None : DisplayStyle.Flex;
            }

            if (_loadBtn != null) _loadBtn.SetEnabled(info.Exists && !info.Corrupt);
            if (_deleteBtn != null) _deleteBtn.SetEnabled(info.Exists);
            if (_saveBtn != null)
            {
                _saveBtn.style.display = _inGame ? DisplayStyle.Flex : DisplayStyle.None;
                _saveBtn.SetEnabled(_inGame && SaveGameIO.CanSave);
            }
        }

        /// <summary>
        /// 詳細ペインの案内文と、それが<b>本当の警告か</b>を返す（issue #575①）。
        /// 警告色（<c>--color-warn</c>）を許すのは「読み込めない／保存できない／操作が失敗した」＝
        /// プレイヤーが困る事態だけで、「空き枠です」「セーブがありません」はただの案内なので本文色にする
        /// （UI原則②「警告色は本当の警告だけに使う」）。
        /// </summary>
        private (string Text, bool Warn) NoteFor(SaveSlotInfo info)
        {
            if (!string.IsNullOrEmpty(_errorText)) return (_errorText, true);                     // 読込/保存の失敗
            if (_inGame && !SaveGameIO.CanSave) return (SaveGameIO.SaveDisabledReason(), true);    // 保存できない理由
            if (!info.Exists)
                return (_inGame ? "空き枠です。「保存」でここに保存できます。" : "このスロットにはセーブがありません。", false);
            if (info.Corrupt)
                return ("セーブデータを読み込めません（壊れている、または未対応の版です）。削除はできます。", true);
            return ("", false);
        }

        private static string SchoolCellText(SaveSlotInfo info)
        {
            if (!info.Exists) return "── 空き ──";
            return info.Corrupt ? "（読み込み不可）" : info.SchoolNameOrDefault;   // issue #624
        }

        private static VisualElement FactRow(string label, VisualElement value)
        {
            var row = new VisualElement();
            row.AddToClassList("ss-d__row");
            var k = new Label(label);
            k.AddToClassList("ss-d__row-k");
            row.Add(k);
            row.Add(value);
            return row;
        }

        private static VisualElement DateValue(DateTime savedAt)
        {
            var l = new Label(savedAt.ToString("yyyy/MM/dd HH:mm"));
            l.AddToClassList("f-num");
            return l;
        }

        private static string SlotLabel(string slotId) => slotId switch
        {
            "manual1" => "手動1",
            "manual2" => "手動2",
            "manual3" => "手動3",
            _ => slotId,
        };

        private void OnLoad()
        {
            // ファイル読込＋復元は同期処理（メインスレッドを止める）。ManualWait で「ロード中」を
            // 1フレーム先に描画してからブロックへ入り、何も表示されず固まって見えるのを防ぐ（要望対応）。
            // SaveGameIO.Load は復元の前に旧進行の裏シムの完了を自分で待つ（issue #1226）＝
            // ここ（_inGame でもロードできる経路）から呼んでも別セーブの結果は混ざらない。
            string error = null;
            ManualWait.Run(_root, "ロード中", () => SaveSlots.Load(_selected, out error), ok =>
            {
                if (!ok)
                {
                    _errorText = string.IsNullOrEmpty(error) ? "読み込みに失敗しました。" : error;
                    RenderDetail();
                    return;
                }
                // 前の進行の通知フィードを残さない（issue #1226。「タイトルへ戻る」の OnTitleConfirm と同じ作法）。
                KokoSim.Unity.Home.HomeState.Reset();
                // クリック配信中の同期 Show は UITK のイベント木を壊すため遅延切替を使う（ScreenRouter.cs 冒頭コメント参照）。
                ScreenRouter.Instance?.ShowDeferred("HomeDashboard");
            });
        }

        /// <summary>ホーム経由（保存モード）のみ配線される。大会中は SetEnabled(false) で既に押せない
        /// 状態だが、経路が増えても落ちないよう再度ガードする（issue #509・オーナー申し送り）。</summary>
        private void OnSave()
        {
            if (!_inGame || !SaveGameIO.CanSave) return;
            // SaveGameIO.Save は裏試合の完了待ち（Task.Wait）を含み得る同期処理。ManualWait で
            // 「セーブ中」を1フレーム先に描画してからブロックへ入る（要望対応、OnLoad と同じ作法）。
            ManualWait.Run(_root, "セーブ中", () => SaveSlots.Save(_selected), ok =>
            {
                if (!ok)
                {
                    _errorText = "保存に失敗しました。";
                    RenderDetail();
                    return;
                }
                _errorText = "";
                Render();   // 一覧・詳細を保存直後の内容（年度/週/保存日時）へ更新して結果を見せる
            });
        }

        // ===== タイトルへ戻る（issue #730 でホームの ≡メニューから移設。挙動は #509 のまま） =====

        private void OnTitleRequest()
        {
            if (!_inGame || BackgroundGameOp.Running || _titleConfirm == null) return;
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
            // 裏の全シムを完了させてから静的リセットする。GameClock.Advance の年度替わり・新チーム発足と
            // 同じ作法（issue #529・#1226）＝背景スレッドと同じ NationRosters を同時に触らない。
            // この待ちは main をブロックし得るので ManualWait で「タイトルへ戻る準備中」を1フレーム先に描画してから入る。
            ManualWait.Run<object>(_root, "タイトルへ戻る準備中", () =>
            {
                BackgroundSims.EnsureAllCompleted();
                return null;
            }, _ =>
            {
                SaveGameIO.ResetForNewGame();
                KokoSim.Unity.Home.HomeState.Reset();
                ScreenRouter.Instance?.ShowDeferred("TitleScreen");
            });
        }

        private void OnDeleteRequest()
        {
            if (_confirm == null) return;
            var q = _root.Q<Label>("ss-confirm-q");
            if (q != null) q.text = SlotLabel(_selected) + "を削除しますか？（元に戻せません）";
            _confirm.style.display = DisplayStyle.Flex;
            _confirm.BringToFront();
        }

        private void OnDeleteCancel()
        {
            if (_confirm != null) _confirm.style.display = DisplayStyle.None;
        }

        private void OnDeleteConfirm()
        {
            if (_confirm != null) _confirm.style.display = DisplayStyle.None;
            SaveSlots.Delete(_selected);
            _selected = DefaultSlot(_inGame);
            _errorText = "";
            Render();
        }
    }
}
