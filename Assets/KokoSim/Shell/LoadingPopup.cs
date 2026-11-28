using System;
using System.Diagnostics;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Shell
{
    /// <summary>
    /// ロードの汎用ポップアップ（待ち表示・処理中表示の唯一の部品）の窓口。見た目は
    /// <c>Assets/Resources/UI/LoadingPopup.uxml</c>／<c>.uss</c> の1つだけで、どの画面の上にも同じ見た目で出す
    /// （配色は紙色と墨だけ＝スクールカラーを使わない）。<see cref="ScreenRouter"/> が全画面のルートへ
    /// <see cref="Attach"/> する。
    ///
    /// <para><b>呼び方</b>: 手で出す場面は <see cref="Show"/>（進み具合が分からない状態＝短い棒が流れる）→
    /// 必要なら <see cref="SetProgress"/>（呼んだ時点で割合と伸びる棒に切り替わる）／<see cref="SetMessage"/> →
    /// <see cref="Hide"/>。段を踏む重い処理は <see cref="LoadingJob"/> が包む。裏で走る処理
    /// （<see cref="BackgroundGameOp"/>・残りブラケット・全国裏試合のブースト・ブラウザ版の待ち）は
    /// ここが毎コマ見て自分で出す＝呼び出し側は何もしない。見出しの文字は場面ごとに呼び出し側が決める。</para>
    ///
    /// <para><b>ちらつき防止</b>: 出す要求から <see cref="ShowDelaySeconds"/> たっても終わっていない処理だけ実際に表示し、
    /// 一度表示したら <see cref="MinVisibleSeconds"/> は出しておく。例外は <c>immediate</c>＝この後メインスレッドを
    /// 止める処理（止まると遅延表示が描けない）で、要求したその場で出す（オーナー確認 2026-11-28）。</para>
    /// </summary>
    public static class LoadingPopup
    {
        public const double ShowDelaySeconds = 0.3;
        public const double MinVisibleSeconds = 0.5;

        private const string HostName = "loading-popup";
        private const long TickMs = 33;
        private const float DotPeriod = 1.2f;    // 点の明滅の周期（秒）
        private const float DotStagger = 0.2f;   // 3つの点のずれ（秒）
        private const float FlowPeriod = 1.4f;   // 流れる棒の周期（秒）
        private const float FlowWidth = 30f;     // 流れる棒の幅（％）
        private const float DotMinOpacity = 0.44f;

        private const string StalledTitle = "応答なし";
        private const string StalledMessage = "固まりました。セーブして再起動してください";

        /// <summary>出すもの1件（見出し・一言・割合）。</summary>
        private sealed class Content
        {
            public string Title;
            public string Message;
            public float? Progress;
            public bool Immediate;
            /// <summary>幕の下の操作を通す（固まったときだけ＝「セーブして」が押せるように）。</summary>
            public bool PassThrough;
        }

        private static Content _manual;
        private static Content _shown;
        private static double _wantSince = -1;
        private static double _shownAt = -1;
        private static bool _visible;
        private static VisualTreeAsset _asset;

        private static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        // ===== 窓口 =====

        /// <summary>
        /// 進み具合が分からない状態で出す（短い棒が流れる）。<paramref name="immediate"/> は、この後すぐメインスレッドを
        /// 止める処理だけ true にする（遅延を待たずに出す）。出したまま見出しを替えたいときも、もう一度呼べばよい。
        /// </summary>
        public static void Show(string title, string message = null, bool immediate = false)
        {
            _manual = new Content { Title = title ?? "", Message = message, Immediate = immediate };
            if (immediate) _wantSince = -1;   // 遅延の起点をここに取り直す（直前の自動表示の続きにしない）
        }

        /// <summary>進み具合（0〜1）。呼ばれた時点で割合と伸びる棒の表示に切り替わる。出していなければ何もしない。</summary>
        public static void SetProgress(float progress01)
        {
            if (_manual != null) _manual.Progress = Mathf.Clamp01(progress01);
        }

        /// <summary>一言を差し替える（null か空で消す）。出していなければ何もしない。</summary>
        public static void SetMessage(string message)
        {
            if (_manual != null) _manual.Message = message;
        }

        /// <summary>消す（表示してから <see cref="MinVisibleSeconds"/> たっていなければ、その時刻まで出してから消す）。</summary>
        public static void Hide() => _manual = null;

        /// <summary>いま出す要求があるか（手で出したもの・裏の処理の自動表示のどちらでも）。</summary>
        public static bool Requested => Desired() != null;

        /// <summary>実際に画面に出ているか（遅延中は false・最低表示時間の間は true）。</summary>
        public static bool Visible => _visible;

        // ===== 画面への取り付け =====

        /// <summary>画面ルートの末尾にポップアップを付け、更新を始める（付いていれば今の状態へ合わせるだけ）。</summary>
        public static void Attach(VisualElement root)
        {
            if (root == null) return;
            var host = root.Q<VisualElement>(HostName);
            if (host == null)
            {
                host = Build();
                if (host == null) return;
                root.Add(host);   // 画面を作り直した（SetActive）直後は毎回ここで付け直される
                host.schedule.Execute(() => Tick(host)).Every(TickMs);
            }
            Tick(host);
        }

        /// <summary>同じパネルのポップアップを今すぐ今の状態へ合わせる（次のコマを待たない）。</summary>
        public static void Refresh(VisualElement anyPanelElement)
        {
            var host = anyPanelElement?.panel?.visualTree.Q<VisualElement>(HostName);
            if (host != null) Tick(host);
        }

        private static VisualElement Build()
        {
            if (_asset == null) _asset = Resources.Load<VisualTreeAsset>("UI/LoadingPopup");
            if (_asset == null)
            {
                UnityEngine.Debug.LogError("[LoadingPopup] Resources/UI/LoadingPopup.uxml が読めない");
                return null;
            }
            var host = _asset.Instantiate();
            host.name = HostName;
            host.pickingMode = PickingMode.Ignore;
            host.style.position = Position.Absolute;
            host.style.left = 0; host.style.top = 0; host.style.right = 0; host.style.bottom = 0;
            host.style.display = DisplayStyle.None;
            return host;
        }

        // ===== 何を出すか（手で出したもの → 裏の処理の順） =====

        private static Content Desired()
        {
            if (_manual != null) return _manual;
            if (BracketFinishSim.Running)
                return new Content { Title = "集計中", Message = "他校の試合結果を集計しています", Progress = BracketFinishSim.Progress };
            // 全国裏試合はブースト消化（対戦カード・試合結果の画面）だけ。ライブ観戦中・通常週は待たせていない。
            if (NationBackgroundSim.Running && NationBackgroundSim.Throttle == NationBackgroundSim.SimThrottle.Boost)
                return new Content { Title = "集計中", Message = "他会場の結果を集計しています", Progress = NationBackgroundSim.Progress };
            // ブラウザ版で、操作が裏の仕事を回し切るのを待っている（#1588）。
            if (BackgroundWork.HasWaiters)
                return new Content { Title = "集計中", Message = "他会場の結果を集計しています" };
            if (BackgroundGameOp.Running)
            {
                // 固まり（Q30）。幕がクリックを塞ぐと「セーブして」が押せないので、このときだけ素通しにする。
                if (BackgroundGameOp.Stalled)
                    return new Content { Title = StalledTitle, Message = StalledMessage, PassThrough = true, Immediate = true };
                return new Content
                {
                    Title = BackgroundGameOp.Title, Message = BackgroundGameOp.Message,
                    Immediate = !BackgroundWork.Threaded,   // ブラウザ版は work がメインスレッドを止める＝遅延を待たない
                };
            }
            return null;
        }

        /// <summary>遅延表示・最低表示時間を当てて、いま画面に出す中身を決める（null＝出さない）。</summary>
        private static Content Resolve(double now)
        {
            var want = Desired();
            if (want != null)
            {
                if (_wantSince < 0) _wantSince = now;
                if (!_visible && (want.Immediate || now - _wantSince >= ShowDelaySeconds))
                {
                    _visible = true;
                    _shownAt = now;
                }
                _shown = want;
            }
            else
            {
                _wantSince = -1;
                if (_visible && now - _shownAt >= MinVisibleSeconds) _visible = false;
            }
            return _visible ? _shown : null;
        }

        // ===== 描画 =====

        private static void Tick(VisualElement host)
        {
            var now = Now;
            var c = Resolve(now);
            var scrim = host.Q<VisualElement>("ldp");
            if (scrim == null) return;
            if (c == null)
            {
                host.style.display = DisplayStyle.None;
                return;
            }

            if (host.style.display != DisplayStyle.Flex)
            {
                host.style.display = DisplayStyle.Flex;
                // 幕の下でフォーカスを持ったボタンが Enter/Space で押されないよう外す
                host.panel?.focusController?.focusedElement?.Blur();
            }
            // 出ている間は常にいちばん前（待つ間に開いたイベントの画面・確認ダイアログなどが後から前へ出ても隠れない）
            var parent = host.parent;
            if (parent != null && parent[parent.childCount - 1] != host) host.BringToFront();
            scrim.style.display = DisplayStyle.Flex;
            scrim.pickingMode = c.PassThrough ? PickingMode.Ignore : PickingMode.Position;

            SetText(host, "ldp-title", c.Title);
            var msg = host.Q<Label>("ldp-msg");
            if (msg != null)
            {
                msg.text = c.Message ?? "";
                msg.style.display = string.IsNullOrEmpty(c.Message) ? DisplayStyle.None : DisplayStyle.Flex;
            }

            var t = (float)now;
            for (var i = 0; i < 3; i++)
            {
                var dot = host.Q<VisualElement>("ldp-dot" + i);
                if (dot == null) continue;
                var phase = Mathf.Repeat(t - i * DotStagger, DotPeriod) / DotPeriod;
                // 25%→100%→25%（モックの25%と同じ見え方＝UI はリニア空間で混ぜるので下限は 0.44）
                dot.style.opacity = DotMinOpacity + (1f - DotMinOpacity) * (0.5f - 0.5f * Mathf.Cos(phase * 2f * Mathf.PI));
            }

            var pct = host.Q<VisualElement>("ldp-pct");
            var fill = host.Q<VisualElement>("ldp-fill");
            if (c.Progress is { } p)
            {
                if (pct != null) pct.style.display = DisplayStyle.Flex;
                SetText(host, "ldp-pct-num", Mathf.RoundToInt(p * 100f).ToString());
                if (fill != null)
                {
                    fill.style.left = Length.Percent(0f);
                    fill.style.width = Length.Percent(p * 100f);
                }
            }
            else
            {
                if (pct != null) pct.style.display = DisplayStyle.None;
                if (fill != null)
                {
                    // 幅30%の棒が左の外から右の外へ流れ続ける（枠の外は切れる）。
                    var k = Mathf.Repeat(t, FlowPeriod) / FlowPeriod;
                    fill.style.width = Length.Percent(FlowWidth);
                    fill.style.left = Length.Percent(-FlowWidth + k * (100f + FlowWidth));
                }
            }
        }

        private static void SetText(VisualElement host, string name, string text)
        {
            var l = host.Q<Label>(name);
            if (l != null && l.text != text) l.text = text;
        }

        /// <summary>テスト用: 状態を初期へ戻す。</summary>
        internal static void ResetForTests()
        {
            _manual = null;
            _shown = null;
            _wantSince = -1;
            _shownAt = -1;
            _visible = false;
        }

        /// <summary>テスト用: 時刻を与えて表示の判定だけ回す（遅延・最低表示時間の検証）。</summary>
        internal static bool ResolveVisibleForTests(double now) => Resolve(now) != null;
    }
}
