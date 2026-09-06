using System.Collections.Generic;
using KokoSim.Engine.Season.Events;
using KokoSim.Unity.Shell;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Home
{
    /// <summary>
    /// イベントのノベル提示（設計書03 §5 / 06 §3.10・issue #609）。案B「トップバー残し・立ち絵中」＝
    /// トップバーは残したまま、本文域（ヒーロー帯＋3カラム）をこの面と入れ替える。
    ///
    /// <para>会話は<b>発火時に確定済み</b>（<see cref="EventService"/>）で、ここは1行ずつ送るだけ。
    /// 面のどこを押しても次の行へ進み、最後の行で閉じる。キューに次のイベントが残っていれば続けて開く。</para>
    ///
    /// <para><b>クリックは <c>PointerDownEvent</c> で採る</b>（UI-BUILD-METHOD の落とし穴＝ClickEvent は
    /// down/up の対に依存する）。閉じたあとの復帰（hero/dash を戻す）は呼び出し側の
    /// <paramref name="onClosed"/> に任せる＝この型は自分の面の外を触らない。</para>
    /// </summary>
    public sealed class EventNovelView
    {
        private readonly VisualElement _root;       // ev-novel
        private readonly VisualElement _portrait;
        private readonly VisualElement _portraitImage;
        private readonly Label _eyebrow, _role, _speaker, _say, _progress, _hint;
        private readonly System.Action _onClosed;
        private readonly EventCallback<PointerDownEvent> _click;

        private PresentedEvent _event;
        private int _line;

        public EventNovelView(VisualElement root, System.Action onClosed)
        {
            _root = root;
            _onClosed = onClosed;
            _portrait = root?.Q<VisualElement>("ev-portrait");
            _portraitImage = root?.Q<VisualElement>("ev-portrait-image");
            _eyebrow = root?.Q<Label>("ev-eyebrow");
            _role = root?.Q<Label>("ev-portrait-role");
            _speaker = root?.Q<Label>("ev-speaker");
            _say = root?.Q<Label>("ev-say");
            _progress = root?.Q<Label>("ev-progress");
            _hint = root?.Q<Label>("ev-hint");

            _click = _ => Advance();
            if (_root != null) _root.RegisterCallback(_click);
        }

        /// <summary>いま開いているか（開いている間はホームの本文域を隠す）。</summary>
        public bool IsOpen => _event != null;

        /// <summary>画面を離れるときの後始末（多重登録の防止＝OnDisable から呼ぶ）。</summary>
        public void Dispose()
        {
            if (_root != null && _click != null) _root.UnregisterCallback(_click);
        }

        /// <summary>
        /// キューの先頭を開く。積まれていなければ何もせず false（呼び出し側は通常のホームを描く）。
        /// </summary>
        public bool OpenNext()
        {
            if (_root == null) return false;
            // キューから外すのは見終わってから（<see cref="EventService.Complete"/>）＝
            // 途中で他のタブへ移っても会話が消えず、ホームへ戻れば頭から出直す。
            var next = EventService.Peek();
            if (next == null) return false;

            _event = next;
            _line = 0;
            _root.style.display = DisplayStyle.Flex;
            RenderLine();
            return true;
        }

        /// <summary>1行送る。最後の行を過ぎたら閉じ、次のイベントが残っていれば続けて開く。</summary>
        private void Advance()
        {
            if (_event == null) return;
            _line++;
            if (_line < _event.Lines.Count) { RenderLine(); return; }

            _event = null;
            EventService.Complete();     // ここで初めてキューから外す（見終わった）
            if (OpenNext()) return;      // 同じ週に2件以上発火したときは続けて見せる
            Close();
        }

        private void Close()
        {
            _event = null;
            if (_root != null) _root.style.display = DisplayStyle.None;
            if (_onClosed != null) _onClosed();
        }

        private void RenderLine()
        {
            var line = _event.Lines[_line];
            var narration = EventRoles.IsNarration(line.Who);
            var result = EventRoles.IsResult(line.Who);

            if (_eyebrow != null) _eyebrow.text = "今週の出来事";

            // 立ち絵。地の文・結果行は話者が居ないのでスロットごと消す（空の枠を残すと「絵が抜けている」ように見える）。
            if (_portrait != null)
            {
                _portrait.EnableInClassList("ev-por--none", narration || result);
                _portrait.EnableInClassList("ev-por--photo", EventPortraits.HasImage(line.Who));
                if (_role != null) _role.text = EventPortraits.RoleLabel(line.Who);
                if (_portraitImage != null) EventPortraits.ApplyImageClass(_portraitImage, line.Who);
            }

            if (_speaker != null)
            {
                _speaker.text = line.Name ?? "";
                _speaker.EnableInClassList("ev__speaker--none", string.IsNullOrEmpty(line.Name));
            }
            if (_say != null)
            {
                _say.text = line.Say ?? "";
                _say.EnableInClassList("ev__say--narration", narration);
                // 結果行（監督指示 2026-08-19）: 太字＋色で会話の中に「誰の・何が・いくつ」を言い切る。
                // 色は通知フィードの Up/Warn と同じ意味づけ（吉報＝空の青／凶報＝警告色）。
                // 太字は実ウェイト（.f-body-bd）で出す＝疑似ボールド禁止（設計書16）。
                var bad = result && _event.Effect != null && _event.Effect.Tone == EffectTone.Bad;
                _say.EnableInClassList("ev__say--result", result);
                _say.EnableInClassList("ev__say--result-bad", bad);
                _say.EnableInClassList("f-body-bd", result);
            }

            // 何行の会話の何行目か（残りが見えないと読み飛ばしていいのか判断できない）。
            if (_progress != null) _progress.text = (_line + 1) + " / " + _event.Lines.Count;
            // 「閉じる」と言えるのは、この会話の最後の行で、かつ<b>自分の後ろに</b>控えが無いときだけ
            // （先頭は見終わるまでキューに残るので Count は 1 以下＝自分だけ）。
            if (_hint != null)
                _hint.text = _line + 1 < _event.Lines.Count || EventService.Pending.Count > 1
                    ? "クリックで次へ"
                    : "クリックで閉じる";
        }
    }

    /// <summary>
    /// 役割ID→立ち絵の対応（issue #609）。<b>絵はまだ無い</b>ので、いまは単色シルエット＋役割名の
    /// プレースホルダで、ここが唯一の差し替え口になる（役割が増えたら表に足すだけで済ませる）。
    /// 既定の話者名は engine の <see cref="EventRoles.DefaultName"/> が正典で、ここでは持たない
    /// （2箇所に置くと data/events.yaml の name 省略時だけ食い違う）。
    /// </summary>
    public static class EventPortraits
    {
        private static readonly Dictionary<string, string> Labels = new Dictionary<string, string>
        {
            { EventRoles.Manager, "監督" },
            { EventRoles.Player, "部員" },
            { EventRoles.TeamManager, "マネージャー" },
            { EventRoles.Ob, "OB" },
            { EventRoles.Scout, "スカウト" },
            { EventRoles.Captain, "主将" },
            { EventRoles.Local, "関係者" },
        };

        /// <summary>立ち絵の下に添える役割名（未知の役割は空＝シルエットだけ出す）。</summary>
        public static string RoleLabel(string who)
        {
            string label;
            return Labels.TryGetValue(who ?? "", out label) ? label : "";
        }

        /// <summary>
        /// 実写ポートレートが用意されている役割（Assets/UI/Images/portraits/・issue
        /// manager-npc-replaces-coach-speaker・2026-08-29）。無い役割は単色シルエットのまま。
        /// </summary>
        private static readonly string[] ImageRoles = { EventRoles.Manager, EventRoles.TeamManager };

        public static bool HasImage(string who) => System.Array.IndexOf(ImageRoles, who) >= 0;

        /// <summary>ポートレート要素に役割の画像クラスだけを立てる（他役割の残り香を消してから）。</summary>
        public static void ApplyImageClass(VisualElement el, string who)
        {
            foreach (var role in ImageRoles) el.RemoveFromClassList("ev-por__image--" + role);
            if (HasImage(who)) el.AddToClassList("ev-por__image--" + who);
        }
    }
}
