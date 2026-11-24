using System;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Components;   // 部品辞書（NumUnit）
using KokoSim.Unity.Shell;        // SaveSlots / ScreenRouter

namespace KokoSim.Unity.Saves
{
    /// <summary>
    /// セーブデータ画面。<b>手動3枠</b>を1枠ずつのカードで並べ、カードの中に中身（校名・時期・チーム総合・
    /// 監督通算・甲子園出場・保存日時）と操作（削除／読み込む／ここに上書き保存）を置く
    /// （オート枠は廃止・監督決定 2026-08-09）。右列は、いま遊んでいるデータ。
    ///
    /// <para>タイトルの「つづきから」と共通ナビの「システム」タブの2箇所から同じ画面を開く。
    /// <see cref="SaveSlots.Entry"/> で分岐する: タイトル経由（<see cref="SaveSlotsEntry.Title"/>）は
    /// 「今保存すべき進行状態が無い」ため読込／削除だけで、右列も出さない（オーナー判断・issue #508）。タブ経由
    /// （<see cref="SaveSlotsEntry.Tab"/>）は保存と「タイトルへ戻る」（issue #730 でホームの
    /// ≡メニューから移設）も出す。</para>
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
        private Color _accent = SchoolAccent.Default;
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
            // スクールカラー（プレイ中の枠と札）。タイトル経由は進行が無い＝配色を引かない（引くと初期化が走る）。
            _accent = _inGame ? SchoolAccent.ForManager() : SchoolAccent.Default;
            _titleConfirm = _root.Q<VisualElement>("ss-title-confirm");
            _titleBtn = Wire("ss-title", OnTitleRequest);
            _titleYes = Wire("ss-title-yes", OnTitleConfirm);
            _titleNo = Wire("ss-title-no", OnTitleCancel);
            if (_titleBtn != null)
            {
                _titleBtn.style.display = _inGame ? DisplayStyle.Flex : DisplayStyle.None;
                _titleBtn.Clear();   // 中身（左向きの矢印＋文字）。開き直しで二重に足さない
                _titleBtn.Add(new LineIcon(LineIcon.Kind.ChevronLeft) { Stroke = 2.2f });
                var titleText = new Label("タイトルへ戻る");
                titleText.AddToClassList("ss-title-btn__txt");
                _titleBtn.Add(titleText);
            }
            SetShown("ss-now", _inGame);

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
            RenderNow();
        }

        /// <summary>
        /// 左列の上の1行。出すのは<b>本当の警告だけ</b>＝操作の失敗と、保存できない理由（UI原則②）。
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

        // ── 左列：枠のカード ──────────────────────────────────

        private void RenderSlots()
        {
            var host = _root.Q<VisualElement>("ss-slots");
            if (host == null) return;
            host.Clear();
            for (var i = 0; i < SaveSlots.AllSlotIds.Length; i++)
                host.Add(BuildCard(SaveSlots.ReadInfo(SaveSlots.AllSlotIds[i]), i + 1));
            SchoolAccent.Apply(host, _accent);   // プレイ中の枠と札へスクールカラーを流す
        }

        private VisualElement BuildCard(SaveSlotInfo info, int number)
        {
            var card = new VisualElement();
            card.AddToClassList("ss-card");
            var playing = _inGame && info.Exists && SaveSlots.IsActive(info.SlotId);

            // 左端：枠の番号
            var no = new VisualElement();
            no.AddToClassList("ss-card__no");
            no.Add(Text("手動", "ss-card__no-k"));
            no.Add(Text(number.ToString(), "ss-card__no-v", "f-display"));
            card.Add(no);

            if (!info.Exists)
            {
                // 空いている枠：点線の枠・番号の右も点線・中央に「空き」・右端に「＋ ここに保存」（ゲーム中だけ）
                card.AddToClassList("ss-card--empty");
                var frame = new DashedFrame { Radius = 14f, LineColor = EmptyLine };
                frame.style.left = -1f; frame.style.top = -1f; frame.style.right = -1f; frame.style.bottom = -1f;
                card.Insert(0, frame);
                card.Add(new DashedRule(EmptyLine));

                var info0 = new VisualElement();
                info0.AddToClassList("ss-card__info");
                info0.Add(Text("空き", "ss-card__vacant"));
                card.Add(info0);

                if (_inGame)
                {
                    var ops0 = new VisualElement();
                    ops0.AddToClassList("ss-card__ops");
                    var id0 = info.SlotId;
                    var save0 = OpButton("ここに保存", () => DoSave(id0), fill: true, plus: true);   // 空きへの保存は確認なし
                    save0.SetEnabled(SaveGameIO.CanSave);
                    ops0.Add(save0);
                    card.Add(ops0);
                }
                return card;
            }

            if (playing)
            {
                card.AddToClassList("ss-card--playing");
                card.AddToClassList("acc-bd");
            }

            // 中：情報（校名・県／4項目／保存日時）
            var body = new VisualElement();
            body.AddToClassList("ss-card__info");

            var head = new VisualElement();
            head.AddToClassList("ss-card__head");
            // 校名は**そのセーブの中身**から出す（issue #624）。実行中の自校名ではない。
            head.Add(Text(info.Corrupt ? "読み込めません" : SchoolOf(info), "ss-card__name", "f-display"));
            if (!info.Corrupt && info.PrefectureId is { } pref)
                head.Add(Text(KokoSim.Engine.Nation.PrefectureNames.JapaneseOf(pref), "ss-card__pref", "f-display-lg"));
            if (playing) head.Add(Text("プレイ中", "ss-card__playing", "acc-bg", "f-body-bd"));
            body.Add(head);

            if (!info.Corrupt)
            {
                var sum = info.Summary;
                var items = new VisualElement();
                items.AddToClassList("ss-card__items");

                var when = Item(items, "時期");
                when.Add(Text(info.WeekLabel, "ss-item__big", "f-display"));
                when.Add(Text(info.YearIndex + "年目", "ss-item__year"));
                if (sum != null && !string.IsNullOrEmpty(sum.Phase)) when.Add(Text(sum.Phase, "ss-item__tag"));

                var overall = Item(items, "チーム総合");
                if (sum == null) overall.Add(None());
                else
                {
                    overall.Add(UiComponents.RankChip(SaveSummaries.GradeOf(sum.Overall)));
                    overall.Add(Text(sum.Overall.ToString(), "ss-item__score", "f-display"));
                }

                var career = Item(items, "監督通算");
                if (sum == null) career.Add(None());
                else
                {
                    career.Add(Text(sum.Wins.ToString(), "ss-item__num", "f-num-bd"));
                    career.Add(Text("勝", "ss-item__unit"));
                    career.Add(Text(sum.Losses.ToString(), "ss-item__num", "f-num-bd"));
                    career.Add(Text("敗", "ss-item__unit"));
                }

                var koshien = Item(items, KokoSim.Engine.Season.DisplayNames.Current.Koshien + "出場");
                if (sum == null) koshien.Add(None());
                else
                {
                    koshien.Add(Text("春", "ss-item__unit"));
                    koshien.Add(Text(sum.SpringAppearances.ToString(), "ss-item__num", "f-num-bd"));
                    koshien.Add(Text("回", "ss-item__unit"));
                    koshien.Add(Text("／", "ss-item__sep"));
                    koshien.Add(Text("夏", "ss-item__unit"));
                    koshien.Add(Text(sum.SummerAppearances.ToString(), "ss-item__num", "f-num-bd"));
                    koshien.Add(Text("回", "ss-item__unit"));
                }
                body.Add(items);
            }

            body.Add(Text("保存日時　" + info.SavedAt.ToString("yyyy/MM/dd HH:mm") + "（" + DaysAgo(info.SavedAt) + "）",
                "ss-card__saved", "f-num"));
            card.Add(body);

            // 右端：操作。壊れた枠は削除だけ。保存はゲーム中だけ。
            var ops = new VisualElement();
            ops.AddToClassList("ss-card__ops");
            var trash = new Button(() => AskDelete(info));
            trash.AddToClassList("ss-op-trash");
            trash.Add(new LineIcon(LineIcon.Kind.Trash) { Stroke = 1.9f });
            ops.Add(trash);
            if (!info.Corrupt)
            {
                ops.Add(OpButton("読み込む", () => AskLoad(info), fill: false));
                if (_inGame)
                {
                    var save = OpButton("ここに上書き保存", () => AskOverwrite(info), fill: true);
                    save.SetEnabled(SaveGameIO.CanSave);
                    ops.Add(save);
                }
            }
            card.Add(ops);
            return card;
        }

        private static readonly Color EmptyLine = new Color32(0xC9, 0xC2, 0xB4, 0xFF);   // 空きの枠の点線（#c9c2b4）

        /// <summary>項目（上に見出し・下に高さ32pxの値）を足し、値を入れる器を返す。</summary>
        private static VisualElement Item(VisualElement host, string caption)
        {
            var item = new VisualElement();
            item.AddToClassList("ss-item");
            item.Add(Text(caption, "ss-item__k"));
            var v = new VisualElement();
            v.AddToClassList("ss-item__v");
            item.Add(v);
            host.Add(item);
            return v;
        }

        /// <summary>値がない項目（概要を書く前の古いセーブ）。</summary>
        private static Label None() => Text("—", "ss-item__none");

        private static Label Text(string text, params string[] classes)
        {
            var l = new Label(text);
            foreach (var c in classes) l.AddToClassList(c);
            return l;
        }

        private static Button OpButton(string text, Action onClick, bool fill, bool plus = false)
        {
            var b = new Button(onClick);
            b.AddToClassList("ss-op");
            b.AddToClassList("f-body-bd");
            if (fill) b.AddToClassList("ss-op--fill");
            if (plus)
            {
                b.Add(new LineIcon(LineIcon.Kind.Plus) { Stroke = 2.4f });
                b.Add(Text(text, "ss-op__txt", "f-body-bd"));
            }
            else b.text = text;
            return b;
        }

        /// <summary>保存日時のかっこ内（当日＝今日／前日＝昨日／それより前＝N日前）。</summary>
        private static string DaysAgo(DateTime savedAt)
        {
            var days = (DateTime.Now.Date - savedAt.Date).Days;
            return days <= 0 ? "今日" : days == 1 ? "昨日" : days + "日前";
        }

        private static string SchoolOf(SaveSlotInfo info) => info.SchoolNameOrDefault + "高校";

        /// <summary>空きの枠の番号の右に引く縦の点線。</summary>
        private sealed class DashedRule : VisualElement
        {
            private readonly Color _color;

            public DashedRule(Color color)
            {
                _color = color;
                pickingMode = PickingMode.Ignore;
                AddToClassList("ss-card__vrule");
                generateVisualContent += OnGenerate;
            }

            private void OnGenerate(MeshGenerationContext mgc)
            {
                var r = contentRect;
                if (float.IsNaN(r.height) || r.height <= 0f) return;
                var p = mgc.painter2D;
                p.strokeColor = _color;
                p.lineWidth = 1.5f;
                var x = r.width * 0.5f;
                for (var y = 0f; y < r.height; y += 9f)
                {
                    p.BeginPath();
                    p.MoveTo(new Vector2(x, y));
                    p.LineTo(new Vector2(x, Mathf.Min(r.height, y + 5f)));
                    p.Stroke();
                }
            }
        }

        // ── 右列：いま遊んでいるデータ（ゲーム中だけ） ────────────────

        private void RenderNow()
        {
            if (!_inGame) return;   // タイトル経由は進行が無い（値を引くと初期化が走る）
            SetText("ss-now-name", TopBarMeters.SchoolDisplayName());
            SetText("ss-now-pref", KokoSim.Engine.Nation.PrefectureNames.JapaneseOf(NationService.PrefectureId));

            var host = _root.Q<VisualElement>("ss-now-rows");
            if (host == null) return;
            host.Clear();

            var s = GameSession.Current;
            var sum = SaveSummaries.Current();
            var year = GameClock.YearIndex;
            host.Add(NowRow("年目", Text(year + "年目（" + SeasonClock.CalendarYear(year, GameClock.Week) + "年）", "f-num")));
            host.Add(NowRow("日付", Text(ScoreboardStrip.DateNow(), "f-num")));
            host.Add(NowRow("時期", Text(sum.Phase)));
            var (_, record) = ScoreboardStrip.RecordNow();
            host.Add(NowRow("戦績", Text(
                (s.InTournament ? KokoSim.Engine.Season.TournamentKindNames.Short(s.Kind) + " " : "") + record, "f-num")));
            host.Add(NowRow("チーム総合",
                UiComponents.RankChip(SaveSummaries.GradeOf(sum.Overall)), Text(sum.Overall.ToString(), "f-num")));
            host.Add(NowRow("監督通算", Text(sum.Wins + "勝" + sum.Losses + "敗", "f-num")));
            host.Add(NowRow(KokoSim.Engine.Season.DisplayNames.Current.Koshien + "出場",
                Text("春 " + sum.SpringAppearances + "回／夏 " + sum.SummerAppearances + "回", "f-num")));
        }

        private static VisualElement NowRow(string caption, params VisualElement[] value)
        {
            var row = new VisualElement();
            row.AddToClassList("ss-now__row");
            row.Add(Text(caption, "ss-now__k"));
            var v = new VisualElement();
            v.AddToClassList("ss-now__v");
            foreach (var e in value) v.Add(e);
            row.Add(v);
            return row;
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
        private void AskOverwrite(SaveSlotInfo info)
        {
            if (!_inGame || !SaveGameIO.CanSave) return;
            var id = info.SlotId;
            Ask(SlotLabel(id) + "（" + SchoolOf(info) + "・" + info.WeekLabel + "）に上書きします。よろしいですか？",
                "上書き保存", danger: false, () => DoSave(id));
        }

        /// <summary>
        /// 読み込みの確認。ゲーム中は「保存していない進行は失われます」を挟む。タイトル経由は失う進行が無いので
        /// 確認せずに読み込む。
        /// </summary>
        private void AskLoad(SaveSlotInfo info)
        {
            var id = info.SlotId;
            if (!_inGame) { DoLoad(id); return; }
            Ask(SlotLabel(id) + "（" + SchoolOf(info) + "・" + info.WeekLabel + "）を読み込みます。保存していない進行は失われます。",
                "読み込む", danger: false, () => DoLoad(id));
        }

        private void AskDelete(SaveSlotInfo info)
        {
            var id = info.SlotId;
            var what = info.Corrupt ? "読み込めないデータ" : SchoolOf(info);
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
            // ファイル読込＋復元は同期処理（メインスレッドを止める）。ManualWait で「ロード中」を
            // 1フレーム先に描画してからブロックへ入り、何も表示されず固まって見えるのを防ぐ（要望対応）。
            // SaveGameIO.Load は復元の前に旧進行の裏シムの完了を自分で待つ（issue #1226）＝
            // ここ（_inGame でもロードできる経路）から呼んでも別セーブの結果は混ざらない。
            string error = null;
            ManualWait.Run(_root, "ロード中", () => SaveSlots.Load(slotId, out error), ok =>
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
            });
        }

        /// <summary>ゲーム中だけ配線される。保存できない局面ではボタンが押せない状態だが、
        /// 経路が増えても落ちないよう再度ガードする（issue #509・オーナー申し送り）。</summary>
        private void DoSave(string slotId)
        {
            if (!_inGame || !SaveGameIO.CanSave) return;
            // SaveGameIO.Save は裏試合の完了待ち（Task.Wait）を含み得る同期処理。ManualWait で
            // 「セーブ中」を1フレーム先に描画してからブロックへ入る（要望対応、DoLoad と同じ作法）。
            ManualWait.Run(_root, "セーブ中", () => SaveSlots.Save(slotId), ok =>
            {
                _errorText = ok ? "" : "保存に失敗しました。";
                Render();   // その枠のカード（時期・保存日時・プレイ中）を保存直後の内容へ更新する
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
            TitleReturn.Run(_root);
        }
    }
}
