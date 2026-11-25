using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Engine.Match.Game;
using KokoSim.Unity.Components;  // 部品辞書（RankBadge / TeamRadar / PositionChip / NumberPatch）
using KokoSim.Unity.Shell;       // ScreenRouter / GameSession / SchoolAccent / LineIcon

namespace KokoSim.Unity.MatchPreview
{
    /// <summary>
    /// 試合開始前（対戦カード）画面のコントローラ（issue #7 → 2026-11-25 リデザイン）。
    /// 上に対戦カードの帯（総合ランク・校名・先攻後攻・球場/天気/気温/風）、本体は 自校スタメン｜戦力比較｜相手スタメン。
    /// 左は常に自校・右は常に相手。戻る導線は持たない。主要操作は「試合開始」1つだけ（UI原則⑦）で、
    /// 押すとこの画面に留まったまま <see cref="MatchStartFlow"/> が準備（BeginMatch 等）を
    /// バックグラウンドで進め、完了後に直接ライブ観戦（MatchLive）へ入る（issue #325: ホームを中継させない）。
    /// 表示専用＝ここではエンジンを進めない（帯不変・決定論に影響なし）。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class MatchPreviewController : MonoBehaviour
    {
        /// <summary>相手校の色（スクールカラーは自校だけ＝相手は墨寄りの中立色）。</summary>
        private static readonly Color OpponentColor = new Color32(0x3A, 0x36, 0x31, 0xFF);

        /// <summary>レーダーの領域（360×266）の中心と、項目名を置く半径。</summary>
        private static readonly Vector2 RadarCenter = new Vector2(180f, 133f);
        private const float AxisLabelRadius = 120f;

        private MatchPreviewState _state;
        private VisualElement _root;
        private Button _start;
        private System.Action _unbindAnnounce;   // 場内放送の切り替え（issue #919・#1193）

        // 準備中の二重起動防止（「試合開始」の連打・BackgroundGameOp 完了前の再入をブロックする）。
        private bool _starting;

        private void OnEnable()
        {
            _state = new MatchPreviewState();
            _root = GetComponent<UIDocument>().rootVisualElement;
            _starting = false;

            _start = _root.Q<Button>("mp-start");
            if (_start != null) _start.clicked += OnStart;
            _unbindAnnounce = KokoSim.Unity.Audio.MatchAnnouncer.BindSwitch(
                _root.Q<VisualElement>("mp-announce"), _root.Q<VisualElement>("mp-announce-switch"));

            Render();
        }

        // 画面を離れるときに登録を解除する（ScreenRouter は同じ GameObject を SetActive で付け外しするだけなので、
        // 外さないと画面往復のたびに多重登録される）。
        private void OnDisable()
        {
            if (_start != null) _start.clicked -= OnStart;
            _start = null;
            _unbindAnnounce?.Invoke();
            _unbindAnnounce = null;
        }

        private void Render()
        {
            var v = _state.BuildView();
            var accent = SchoolAccent.ForManager();

            SetText("mp-kind", v.KindLabel);
            RenderBandSide("mp-own", v.Own, Tint(accent, 0.78f));
            RenderBandSide("mp-opp", v.Opponent, new Color32(0xD9, 0xD3, 0xC7, 0xFF));
            RenderConditions(v);

            RenderLineup("mp-own", v.Own, accent);
            RenderLineup("mp-opp", v.Opponent, OpponentColor);

            RenderRadar("mp-own-radar", v.Own, accent, 0.18f, v.Ready || v.Own.Lineup.Count > 0);
            RenderRadar("mp-opp-radar", v.Opponent, OpponentColor, 0.10f, v.Ready);
            RenderBars(v, accent);

            Paint("mp-band-stripe", accent);
            Paint("mp-own-mark", accent);
            if (_start != null) _start.style.backgroundColor = accent;
        }

        // ── ① 帯 ──

        private void RenderBandSide(string prefix, MatchPreviewSideView side, Color turnBg)
        {
            SetText(prefix + "-name", side.TeamName);
            var turn = _root.Q<Label>(prefix + "-turn");
            if (turn != null)
            {
                turn.text = side.OrderCaption;
                turn.style.backgroundColor = turnBg;
            }
            var hasTeam = side.Lineup.Count > 0;
            SetText(prefix + "-score", hasTeam ? side.OverallValue.ToString() : "");
            var host = _root.Q<VisualElement>(prefix + "-rank");
            if (host != null)
            {
                host.Clear();
                if (hasTeam) host.Add(new RankBadge(side.OverallGrade));
            }
        }

        /// <summary>球場・天気・気温・風。値の無い項目は出さず、見えている項目の間にだけ縦線を置く。</summary>
        private void RenderConditions(MatchPreviewView v)
        {
            var host = _root.Q<VisualElement>("mp-cond");
            if (host == null) return;
            host.Clear();
            var items = new List<VisualElement>();

            if (!string.IsNullOrEmpty(v.StadiumName))
                items.Add(CondItem(LineIcon.Kind.Stadium, "", "球場", v.StadiumName));

            var weather = MatchPreviewState.WeatherLabel(v);
            if (!string.IsNullOrEmpty(weather))
            {
                var (kind, mod) = v.Indoor ? (LineIcon.Kind.Stadium, "")
                    : v.Weather switch
                    {
                        WeatherState.Rainy => (LineIcon.Kind.Rain, "rain"),
                        WeatherState.Cloudy => (LineIcon.Kind.Cloud, "cloud"),
                        _ => (LineIcon.Kind.Sun, "sun"),
                    };
                items.Add(CondItem(kind, mod, "天気", weather));
            }

            if (!string.IsNullOrEmpty(v.Temperature))
                items.Add(CondItem(LineIcon.Kind.Thermometer, "temp", "気温", v.Temperature));

            if (v.Wind != null)
            {
                var wind = CondItem(LineIcon.Kind.WindArrow, "wind", "風", v.Wind.Text);
                var icon = wind.Q(className: "mp-cond__icon");
                if (icon != null) icon.style.rotate = new Rotate(new Angle(v.Wind.ArrowDegrees, AngleUnit.Degree));
                items.Add(wind);
            }

            for (var i = 0; i < items.Count; i++)
            {
                if (i > 0) host.Add(Classed(new VisualElement(), "mp-cond__sep"));
                host.Add(items[i]);
            }
        }

        private static VisualElement CondItem(LineIcon.Kind kind, string iconMod, string key, string value)
        {
            var item = Classed(new VisualElement(), "mp-cond__item");
            var icon = Classed(new LineIcon(kind), "mp-cond__icon");
            if (!string.IsNullOrEmpty(iconMod)) icon.AddToClassList("mp-cond__icon--" + iconMod);
            item.Add(icon);
            item.Add(Text(key, "mp-cond__k", "f-body"));
            item.Add(Text(value, "mp-cond__v", "f-body-bd"));
            return item;
        }

        // ── ② スタメン ──

        private void RenderLineup(string prefix, MatchPreviewSideView side, Color teamColor)
        {
            SetText(prefix + "-lineup-name", side.TeamName);
            SetText(prefix + "-dh", side.Lineup.Count > 0 ? (side.UsesDh ? "DHあり" : "DHなし") : "");
            Paint(prefix + "-mark", teamColor);

            var host = _root.Q<VisualElement>(prefix + "-lineup");
            if (host == null) return;
            host.Clear();
            foreach (var s in side.Lineup) host.Add(Row(s, starter: false));
            // 先発投手はカードの一番下に寄せる（DH の有無に関わらず出す）。
            host.Add(Classed(new VisualElement(), "mp-lineup__fill"));
            if (side.StartingPitcher != null) host.Add(Row(side.StartingPitcher, starter: true));
        }

        private static VisualElement Row(MatchPreviewSlotView s, bool starter)
        {
            var row = Classed(new VisualElement(), "mp-row");
            if (starter) row.AddToClassList("mp-row--sp");
            var ord = starter
                ? Text("先発", "mp-row__ord", "mp-row__ord--sp", "f-body")
                : Text(s.Order, "mp-row__ord", "f-display");
            row.Add(ord);
            row.Add(new PositionChip(s.PosKanji));
            row.Add(Text(s.Name, "mp-row__name", "f-display"));
            row.Add(new NumberPatch(s.Number));
            row.Add(Text(s.Meta, "mp-row__meta", "f-body"));
            return row;
        }

        // ── ② 戦力比較 ──

        private void RenderRadar(string hostName, MatchPreviewSideView side, Color color, float fillAlpha, bool hasData)
        {
            var host = _root.Q<VisualElement>(hostName);
            if (host == null) return;
            host.Clear();
            var area = Classed(new VisualElement(), "mp-radar");
            host.Add(area);

            var radar = new TeamRadar();
            var values = new float[side.Factors.Length];
            for (var i = 0; i < values.Length; i++) values[i] = hasData ? side.Factors[i] / 100f : 0f;
            radar.SetData(values, color, fillAlpha);
            area.Add(radar);

            var labels = MatchPreviewState.FactorLabels;
            for (var i = 0; i < labels.Length; i++)
            {
                var p = TeamRadar.AxisPoint(RadarCenter, AxisLabelRadius, i, labels.Length);
                var l = Text(labels[i], "mp-radar__axis", "f-body");
                l.style.left = p.x - 40f;
                l.style.top = p.y - 8f;
                area.Add(l);
            }

            var team = Classed(new VisualElement(), "mp-radar__team");
            var mark = Classed(new VisualElement(), "mp-radar__mark");
            mark.style.backgroundColor = color;
            team.Add(mark);
            var name = Text(side.TeamName, "mp-radar__name", "f-display");
            name.style.color = color;
            team.Add(name);
            area.Add(team);

            if (hasData) area.Add(new RankBadge(side.OverallGrade));
        }

        private void RenderBars(MatchPreviewView v, Color accent)
        {
            var host = _root.Q<VisualElement>("mp-bars");
            if (host == null) return;
            host.Clear();
            var labels = MatchPreviewState.FactorLabels;
            var showOpp = v.Ready;
            for (var i = 0; i < labels.Length; i++)
            {
                var own = v.Own.Factors[i];
                var opp = v.Opponent.Factors[i];
                // 高いほう（同点は両方）を濃く・太く出す。
                var ownHi = !showOpp || own >= opp;
                var oppHi = showOpp && opp >= own;

                var row = Classed(new VisualElement(), "mp-bar");

                var ownNum = Text(own.ToString(), "mp-bar__num", "mp-bar__num--own", ownHi ? "f-num-bd" : "f-body-md");
                if (ownHi) ownNum.style.color = accent;
                row.Add(ownNum);
                row.Add(Track("own", own, ownHi, accent));

                row.Add(Text(labels[i], "mp-bar__label", "f-body-bd"));

                row.Add(Track("opp", showOpp ? opp : 0, oppHi, OpponentColor));
                var oppNum = Text(showOpp ? opp.ToString() : "", "mp-bar__num", "mp-bar__num--opp",
                    oppHi ? "f-num-bd" : "f-body-md");
                if (oppHi) oppNum.AddToClassList("mp-bar__num--hi-opp");
                row.Add(oppNum);

                host.Add(row);
            }
        }

        private static VisualElement Track(string side, int value, bool hi, Color color)
        {
            var track = Classed(new VisualElement(), "mp-bar__track");
            track.AddToClassList("mp-bar__track--" + side);
            var fill = Classed(new VisualElement(), "mp-bar__fill");
            fill.style.width = Length.Percent(Mathf.Clamp(value, 0, 100));
            fill.style.backgroundColor = color;
            if (!hi) fill.AddToClassList("mp-bar__fill--lo-" + side);
            track.Add(fill);
            return track;
        }

        // ── 共通 ──

        /// <summary>色を白へ <paramref name="t"/> だけ寄せた色（先攻・後攻の札の淡色）。</summary>
        private static Color Tint(Color c, float t) => Color.Lerp(c, Color.white, t);

        private void Paint(string name, Color color)
        {
            var e = _root.Q<VisualElement>(name);
            if (e != null) e.style.backgroundColor = color;
        }

        private static T Classed<T>(T e, string cls) where T : VisualElement
        {
            e.AddToClassList(cls);
            return e;
        }

        private static Label Text(string text, params string[] classes)
        {
            var l = new Label(text);
            foreach (var c in classes) l.AddToClassList(c);
            return l;
        }

        private void SetText(string name, string text)
        {
            var l = _root.Q<Label>(name);
            if (l != null) l.text = text;
        }

        // この画面に留まったまま準備（BeginMatch 等）を進める（issue #325）。完了は MatchStartFlow が
        // ShowDeferred("MatchLive") で直接遷移するので、ここでは開始と入力ブロックだけを見る。
        private void OnStart()
        {
            if (_starting) return;   // 連打ガード
            _starting = true;
            _start?.SetEnabled(false);   // 押せない見た目にする（無言で無視しない）

            // 失敗は2経路ある。①同期＝他のバックグラウンド処理と衝突（二重起動）②非同期＝別スレッドの
            // 準備が例外で落ちた。②を拾わないと「準備中」ロックが解けず**試合開始が二度と押せない**
            // （画面を出入りすれば OnEnable でリセットされるが、その場では詰む）。
            if (!MatchStartFlow.Begin(ReleaseStartLock)) ReleaseStartLock();
        }

        /// <summary>「準備中」の入力ロックを解く（押し直せる状態へ戻す）。</summary>
        private void ReleaseStartLock()
        {
            _starting = false;
            _start?.SetEnabled(true);
        }
    }
}
