using System.Collections.Generic;
using System.Linq;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// ブラウザ版の校名の入力（Web 体験版 #1323・2026-11-24 オーナー決定「候補から選ぶだけにする」）。
    /// ブラウザ版には日本語入力（IME）が無く、入力欄では英数字しか打てない。そこでブラウザ版では入力欄を隠し、
    /// 同じ場所に今の値を出す表示ラベルを置いて、値は地名の候補ボタンから選ぶ。
    ///
    /// <b>値は隠した入力欄が持ち続ける</b>＝検証・確定など入力欄を読む既存の処理はそのまま動く。
    /// 入力欄を <c>SetValueWithoutNotify</c> で書き換えた呼び元は <see cref="Sync"/> で表示を合わせる。
    /// ブラウザ版でなければ何もしない（<see cref="Active"/>=false）。
    /// </summary>
    public sealed class BrowserNamePicker
    {
        /// <summary>候補の数（新規作成画面の候補と同じ）。</summary>
        public const int CandidateCount = 6;

        private const string Empty = "―";

        private readonly TextField _field;
        private readonly Label _shown;
        private readonly VisualElement _candidates;
        private readonly string _buttonClass;

        /// <summary>ブラウザ版として入力欄を置き換えたか。</summary>
        public bool Active => _shown != null;

        /// <param name="field">置き換える入力欄（隠す）。</param>
        /// <param name="candidateHost">候補ボタンを並べる器。</param>
        /// <param name="buttonClass">候補ボタンに付ける部品のクラス（画面の既存の候補・道具ボタンと揃える）。</param>
        /// <param name="shownClasses">表示ラベルに付けるクラス（同じ画面で値を出している欄と揃える）。</param>
        public BrowserNamePicker(TextField field, VisualElement candidateHost, string buttonClass, params string[] shownClasses)
        {
            _field = field;
            _candidates = candidateHost;
            _buttonClass = buttonClass;
            if (!BuildPlatform.IsBrowser || field == null || candidateHost == null) return;

            _shown = new Label { name = field.name + "-shown" };
            foreach (var c in shownClasses) _shown.AddToClassList(c);
            var parent = field.parent;
            parent.Insert(parent.IndexOf(field) + 1, _shown);
            field.style.display = DisplayStyle.None;
            field.RegisterValueChangedCallback(_ => Sync());
            Sync();
        }

        /// <summary>候補ボタンを作り直す（その県の地名から。校名語彙 data/school-names.yaml と同じ表）。</summary>
        public void SetCandidates(int prefectureId)
        {
            if (!Active) return;
            _candidates.Clear();
            foreach (var place in CandidatesOf(prefectureId))
            {
                var text = place;
                var b = new Button(() => Choose(text)) { text = text };
                b.AddToClassList(_buttonClass);
                b.AddToClassList("name-cand");   // 撮影レシピ・テストから引く目印（見た目は持たない）
                _candidates.Add(b);
            }
            _candidates.style.display = DisplayStyle.Flex;
        }

        /// <summary>候補を選ぶ（候補ボタンの処理）。値は入力欄へ入れ、入力欄の変更通知で画面の検証も走る。</summary>
        public void Choose(string name)
        {
            if (!Active) return;
            _field.value = name;
            Sync();   // 画面に載っていない（通知が配られない）ときも表示は合わせる
        }

        /// <summary>表示ラベルを入力欄の値に合わせる。</summary>
        public void Sync()
        {
            if (!Active) return;
            var v = _field.value;
            _shown.text = string.IsNullOrWhiteSpace(v) ? Empty : v.Trim();
        }

        /// <summary>その県の校名候補（地名の先頭から <see cref="CandidateCount"/> 個）。</summary>
        public static IReadOnlyList<string> CandidatesOf(int prefectureId)
        {
            var vocab = SchoolNameVocabProvider.Default;
            if (vocab.PlacesByPrefecture == null
                || !vocab.PlacesByPrefecture.TryGetValue(prefectureId, out var places)
                || places == null) return new string[0];
            return places.Take(CandidateCount).ToList();
        }
    }
}
