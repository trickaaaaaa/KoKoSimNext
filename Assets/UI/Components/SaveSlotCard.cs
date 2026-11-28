using System;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Components
{
    /// <summary>カードをどちらの画面に置くか（違うのは地・目印・ボタンだけ）。</summary>
    public enum SaveSlotCardMode
    {
        /// <summary>タイトルの「つづきから」（墨の地・「最新」の札・読み込む＋削除）。</summary>
        Continue,
        /// <summary>ゲーム内「システム」タブのセーブデータ（紙色の地・「プレイ中」の札・読み込む＋上書き保存＋削除）。</summary>
        InGame,
    }

    /// <summary>
    /// セーブ枠のカード1枚ぶんの表示データ（ViewModel）。セーブの見出し（<see cref="SaveSlotInfo"/>）から作る。
    /// 古いセーブで値が無い項目は null（カードは「—」を出す）。
    /// </summary>
    public sealed class SaveSlotCardData
    {
        public string SlotId;
        public int Number;
        public bool Exists;
        public bool Corrupt;
        /// <summary>校名（「〜高校」込み・二重にしない）。</summary>
        public string SchoolName;
        /// <summary>都道府県（無い古いセーブは null）。</summary>
        public string Prefecture;
        /// <summary>色の帯の色（スクールカラーの表示用の色。無い古いセーブは炭色）。</summary>
        public Color BandColor = SchoolAccent.Charcoal;
        public string WeekLabel;
        public int YearIndex;
        /// <summary>時期の名前（例「秋の大会」）。無ければ札を出さない。</summary>
        public string Phase;
        /// <summary>チーム総合のランク（S〜G）と点数。無ければ「—」。</summary>
        public string Grade;
        public int? Overall;
        public int? Wins, Losses;
        public int? SpringAppearances, SummerAppearances;
        public DateTime SavedAt;
        /// <summary>保存日時がいちばん新しい枠（つづきからの「最新」）。</summary>
        public bool IsLatest;
        /// <summary>いま遊んでいるデータの枠（セーブデータの「プレイ中」）。</summary>
        public bool IsPlaying;

        /// <summary>見出しから作る。「最新」「プレイ中」は一覧を見てから呼び出し側が立てる。</summary>
        public static SaveSlotCardData From(SaveSlotInfo info, int number)
        {
            var d = new SaveSlotCardData { SlotId = info.SlotId, Number = number, Exists = info.Exists, Corrupt = info.Corrupt };
            if (!info.Exists) return d;
            d.SavedAt = info.SavedAt;
            if (info.Corrupt)
            {
                d.SchoolName = "読み込めません";
                return d;
            }
            d.SchoolName = SchoolNameOf(info);
            d.Prefecture = info.PrefectureId is { } pref ? KokoSim.Engine.Nation.PrefectureNames.JapaneseOf(pref) : null;
            d.WeekLabel = info.WeekLabel;
            d.YearIndex = info.YearIndex;
            if (info.Summary is { } sum)
            {
                d.Phase = string.IsNullOrEmpty(sum.Phase) ? null : sum.Phase;
                d.Overall = sum.Overall;
                d.Grade = SaveSummaries.GradeOf(sum.Overall);
                d.Wins = sum.Wins;
                d.Losses = sum.Losses;
                d.SpringAppearances = sum.SpringAppearances;
                d.SummerAppearances = sum.SummerAppearances;
                if (sum.Color != null && ColorUtility.TryParseHtmlString(sum.Color, out var c)) d.BandColor = c;
            }
            return d;
        }

        /// <summary>カードと確認ダイアログに出す校名（「高校」は二重に付けない）。</summary>
        public static string SchoolNameOf(SaveSlotInfo info)
            => KokoSim.Engine.Nation.SchoolNames.Full(info.SchoolNameOrDefault);
    }

    /// <summary>
    /// セーブ枠のカード（部品辞書）。つづきから／セーブデータの両方で同じ部品を使う。見た目は
    /// <c>Components/SaveSlotCard.uss</c>（使う画面が <c>&lt;Style&gt;</c> する）。上から 色の帯 → 中身 → ボタン の3段。
    /// 地の違い（墨／紙色）は、カードを並べる入れ物に <see cref="OnNightClass"/> か <see cref="OnPaperClass"/> を付けて切り替える。
    /// </summary>
    public static class SaveSlotCard
    {
        public const string OnNightClass = "ssc-on-night";
        public const string OnPaperClass = "ssc-on-paper";

        private const float NameSize = 42f;
        private const float NameMinSize = 30f;
        private const float NameSpacingEm = 0.08f;

        // 墨の地の上の点線（モックの白20%と同じ見え方。UI はリニア空間で混ぜるので値は小さい＝SaveSlotCard.uss の注記）
        private static readonly Color NightDash = new Color(1f, 1f, 1f, 0.057f);
        private static readonly Color PaperDash = new Color32(0xC9, 0xC2, 0xB4, 0xFF);

        /// <summary>カードの操作（使わないものは null）。</summary>
        public sealed class Actions
        {
            public Action Load;
            public Action Save;
            public Action Delete;
            /// <summary>いま保存できるか（できない局面では保存のボタンを押せなくする）。</summary>
            public bool CanSave = true;
        }

        public static VisualElement Build(SaveSlotCardData d, SaveSlotCardMode mode, Actions actions, DateTime now)
        {
            var card = new VisualElement();
            card.AddToClassList("ssc");
            card.name = "ssc-" + d.SlotId;
            if (!d.Exists) BuildEmpty(card, d, mode, actions);
            else BuildFilled(card, d, mode, actions, now);
            return card;
        }

        // ── データのある枠 ──

        private static void BuildFilled(VisualElement card, SaveSlotCardData d, SaveSlotCardMode mode, Actions a, DateTime now)
        {
            card.AddToClassList("ssc--filled");
            var latest = mode == SaveSlotCardMode.Continue && d.IsLatest && !d.Corrupt;
            var playing = mode == SaveSlotCardMode.InGame && d.IsPlaying && !d.Corrupt;
            if (latest) card.AddToClassList("ssc--latest");
            if (playing) card.AddToClassList("ssc--playing");

            // 上の段：色の帯
            var band = new VisualElement();
            band.AddToClassList("ssc__band");
            band.style.backgroundColor = d.BandColor;
            band.Add(Text(d.Number.ToString(), "ssc__wm", "f-display"));
            var tags = new VisualElement();
            tags.AddToClassList("ssc__tags");
            tags.Add(Text("手動 " + d.Number, "ssc__no", "f-body-bd"));
            if (latest) tags.Add(Text("最新", "ssc__mark", "ssc__mark--latest", "f-body-bd"));
            if (playing) tags.Add(Text("プレイ中", "ssc__mark", "ssc__mark--playing", "f-body-bd"));
            band.Add(tags);
            band.Add(Flex("ssc__band-spacer"));
            var name = Text(d.SchoolName ?? "", "ssc__name", "f-display");
            band.Add(name);
            if (!string.IsNullOrEmpty(d.Prefecture)) band.Add(Text(d.Prefecture, "ssc__pref", "f-display-lg"));
            FitName(band, name);
            card.Add(band);

            // 中の段：中身
            var body = new VisualElement();
            body.AddToClassList("ssc__body");
            if (!d.Corrupt)
            {
                var when = new VisualElement();
                when.AddToClassList("ssc__when");
                when.Add(Text(d.WeekLabel ?? "", "ssc__week", "f-display"));
                when.Add(Text(d.YearIndex + "年目", "ssc__year", "f-body"));
                if (!string.IsNullOrEmpty(d.Phase)) when.Add(Text(d.Phase, "ssc__phase", "f-body-bd"));
                body.Add(when);
                body.Add(Stats(d));
            }
            body.Add(Flex("ssc__fill"));
            var saved = new VisualElement();
            saved.AddToClassList("ssc__saved");
            saved.Add(new LineIcon(LineIcon.Kind.Clock));
            saved.Add(Text(d.SavedAt.ToString("yyyy/MM/dd HH:mm"), "ssc__saved-at", "f-num"));
            saved.Add(Text(DaysAgo(d.SavedAt, now), "ssc__saved-ago", "f-body-bd"));
            body.Add(saved);
            card.Add(body);

            // 下の段：ボタン
            var ops = new VisualElement();
            ops.AddToClassList("ssc__ops");
            if (d.Corrupt) ops.Add(Flex("ssc__fill"));   // 壊れた枠は削除だけ（右に寄せる）
            else if (mode == SaveSlotCardMode.Continue)
            {
                var load = MakeButton(a?.Load, "ssc-btn--ink", "ssc-btn--grow");
                load.Add(Text("読み込む", "ssc-btn__txt", "ssc-btn__txt--load", "f-display"));
                var tri = new LineIcon(LineIcon.Kind.Play);
                tri.AddToClassList("licon--after");
                load.Add(tri);
                ops.Add(load);
            }
            else
            {
                var load = MakeButton(a?.Load, "ssc-btn--line", "ssc-btn--load-side");
                load.Add(Text("読み込む", "ssc-btn__txt", "ssc-btn__txt--load-side", "f-display"));
                ops.Add(load);
                var save = MakeButton(a?.Save, "ssc-btn--ink", "ssc-btn--grow", "ssc-btn--gap");
                var floppy = new LineIcon(LineIcon.Kind.Floppy) { Stroke = 2.2f };
                floppy.AddToClassList("licon--before");
                save.Add(floppy);
                save.Add(Text("上書き保存", "ssc-btn__txt", "ssc-btn__txt--save", "f-display"));
                save.SetEnabled(a == null || a.CanSave);
                ops.Add(save);
            }
            var trash = MakeButton(a?.Delete, "ssc-btn--trash");
            if (ops.childCount > 0 && !d.Corrupt) trash.AddToClassList("ssc-btn--gap");
            trash.Add(new LineIcon(LineIcon.Kind.Trash) { Stroke = 1.9f });
            ops.Add(trash);
            card.Add(ops);
        }

        private static VisualElement Stats(SaveSlotCardData d)
        {
            var stats = new VisualElement();
            stats.AddToClassList("ssc__stats");

            var overall = Stat(stats, "チーム総合", false);
            if (d.Overall is { } score && !string.IsNullOrEmpty(d.Grade))
            {
                var chip = new RankBadge(d.Grade);
                chip.AddToClassList("ssc__rank");
                overall.Add(chip);
                overall.Add(Text(score.ToString(), "ssc__score", "f-display"));
            }
            else overall.Add(None());

            var career = Stat(stats, "監督通算", d.Wins != null);
            if (d.Wins is { } w && d.Losses is { } l)
            {
                career.Add(Text(w.ToString(), "ssc__num", "f-num-bd"));
                career.Add(Text("勝", "ssc__unit", "ssc__unit--gap", "f-body"));
                career.Add(Text(l.ToString(), "ssc__num", "f-num-bd"));
                career.Add(Text("敗", "ssc__unit", "f-body"));
            }
            else career.Add(None());

            var koshien = Stat(stats, KokoSim.Engine.Season.DisplayNames.Current.Koshien + "出場", d.SpringAppearances != null);
            if (d.SpringAppearances is { } sp && d.SummerAppearances is { } su)
            {
                koshien.Add(Text("春", "ssc__unit", "ssc__unit--pre", "f-body"));
                koshien.Add(Text(sp.ToString(), "ssc__num", "f-num-bd"));
                koshien.Add(Text("回", "ssc__unit", "f-body"));
                koshien.Add(Text("／", "ssc__sep", "f-body"));
                koshien.Add(Text("夏", "ssc__unit", "ssc__unit--pre", "f-body"));
                koshien.Add(Text(su.ToString(), "ssc__num", "f-num-bd"));
                koshien.Add(Text("回", "ssc__unit", "f-body"));
            }
            else koshien.Add(None());
            return stats;
        }

        /// <summary>項目（上に見出し・その8px下に高さ46pxの値）を足し、値を入れる器を返す。</summary>
        private static VisualElement Stat(VisualElement host, string caption, bool textRow)
        {
            var item = new VisualElement();
            item.AddToClassList("ssc__stat");
            item.Add(Text(caption, "ssc__stat-k", "f-body"));
            var v = new VisualElement();
            v.AddToClassList("ssc__stat-v");
            if (textRow) v.AddToClassList("ssc__stat-v--text");
            item.Add(v);
            host.Add(item);
            return v;
        }

        private static Label None() => Text("—", "ssc__none", "f-body");

        // ── 空いている枠 ──

        private static void BuildEmpty(VisualElement card, SaveSlotCardData d, SaveSlotCardMode mode, Actions a)
        {
            card.AddToClassList("ssc--empty");
            card.Add(new DashedFrame
            {
                Radius = 20f, LineWidth = 2f, Dash = 7f, Gap = 5f,
                LineColor = mode == SaveSlotCardMode.Continue ? NightDash : PaperDash,
            });
            card.Add(Text(d.Number.ToString(), "ssc__wm", "ssc__wm--top", "f-display"));
            card.Add(Text("手動 " + d.Number, "ssc__no", "f-body-bd"));
            card.Add(Text("空き", "ssc__vacant", "f-display"));
            card.Add(Text("この枠にはまだデータがありません", "ssc__vacant-sub", "f-body"));
            if (mode == SaveSlotCardMode.InGame)
            {
                var ops = new VisualElement();
                ops.AddToClassList("ssc__ops");
                ops.AddToClassList("ssc__ops--empty");
                var save = MakeButton(a?.Save, "ssc-btn--ink", "ssc-btn--grow");
                var plus = new LineIcon(LineIcon.Kind.Plus) { Stroke = 2.6f };
                plus.AddToClassList("licon--before");
                save.Add(plus);
                save.Add(Text("ここに保存", "ssc-btn__txt", "ssc-btn__txt--save", "f-display"));
                save.SetEnabled(a == null || a.CanSave);
                ops.Add(save);
                card.Add(ops);
            }
        }

        // ── 校名を帯の幅に収める（長い名前は文字を小さくして1行に。下限30px） ──

        private static void FitName(VisualElement band, Label name)
        {
            band.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                var avail = band.contentRect.width;
                if (float.IsNaN(avail) || avail <= 0f || string.IsNullOrEmpty(name.text)) return;
                var cur = name.resolvedStyle.fontSize;
                if (cur <= 0f) return;
                var measured = name.MeasureTextSize(name.text, 0, VisualElement.MeasureMode.Undefined, 0,
                    VisualElement.MeasureMode.Undefined).x;
                if (measured <= 0f) return;
                // 字間も文字の大きさに比例させるので、幅は文字の大きさにほぼ比例する。
                var at42 = measured * NameSize / cur;
                var size = Mathf.Clamp(Mathf.Floor(NameSize * avail / at42), NameMinSize, NameSize);
                if (Mathf.Abs(size - cur) < 0.5f) return;
                name.style.fontSize = size;
                name.style.letterSpacing = size * NameSpacingEm;
            });
        }

        // ── 小物 ──

        /// <summary>保存日時の後ろの語（当日＝今日／前日＝昨日／それより前＝N日前）。</summary>
        public static string DaysAgo(DateTime savedAt, DateTime now)
        {
            var days = (now.Date - savedAt.Date).Days;
            return days <= 0 ? "今日" : days == 1 ? "昨日" : days + "日前";
        }

        private static Button MakeButton(Action onClick, params string[] classes)
        {
            var b = new Button(onClick);
            b.AddToClassList("ssc-btn");
            foreach (var c in classes) b.AddToClassList(c);
            return b;
        }

        private static VisualElement Flex(string cls)
        {
            var e = new VisualElement { pickingMode = PickingMode.Ignore };
            e.AddToClassList(cls);
            return e;
        }

        private static Label Text(string text, params string[] classes)
        {
            var l = new Label(text) { pickingMode = PickingMode.Ignore };
            foreach (var c in classes) l.AddToClassList(c);
            return l;
        }
    }
}
