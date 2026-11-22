#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Unity.Home;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Debugging
{
    /// <summary>
    /// データ画面の撮影レシピ（<see cref="ScreenRecipe"/>）を Play 中に実行する撮影係。
    /// 画面ごとに書いていた *Shooter の共通部分（画面を1つだけ点ける・パネルを RenderTexture に描いて読む・
    /// 書体監査）をここに1つだけ持ち、状態の作り方と押す場所はレシピの手順で書く。
    ///
    /// 起動: <c>ScreenCaptureRunner.Run("id", …)</c>（Editor）が <see cref="Arm"/> して Play に入る →
    /// <see cref="AutoSpawn"/> がこのコンポーネントを足す → レシピを順に撮って Play を抜ける。
    /// レシピごとに新しい進行で始めたいときは手順の先頭に <c>new_game</c> を置く（置かなければ前のレシピの状態を引き継ぐ）。
    /// 出力: <c>screenshots/&lt;id&gt;/</c> に *.png・summary.json と、成否の目印 DONE / FAILED。全体は screenshots/_screen-run.txt。
    /// 手順が1つでも失敗したら、その先は撮らずに FAILED で止める（失敗したまま撮った絵を正しいと誤読しないため）。
    /// </summary>
    public sealed class ScreenRecipeShooter : MonoBehaviour
    {
        private const string ArmKey = "KokoSim.ScreenRecipes.Armed";
        public const string OutRoot = "screenshots";
        private const float ScreenWaitSeconds = 30f;

        private string[] _ids;

        public static void Arm(string[] ids) => UnityEditor.SessionState.SetString(ArmKey, string.Join(",", ids));

        public static void Disarm() => UnityEditor.SessionState.EraseString(ArmKey);

        public static string RunMarkerPath => Path.Combine(OutRoot, "_screen-run.txt");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void AutoSpawn()
        {
            var armed = UnityEditor.SessionState.GetString(ArmKey, "");
            if (string.IsNullOrEmpty(armed)) return;
            UnityEditor.SessionState.EraseString(ArmKey);
            var go = new GameObject("ScreenRecipeShooter");
            go.AddComponent<ScreenRecipeShooter>()._ids = armed.Split(',');
        }

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            var runLog = new StringBuilder();

            // 起動直後のタイトルが出し切るまで待つ（待たずに画面を開くと、後から走るタイトル表示に上書きされる）
            float waited = 0f;
            while ((ScreenRouter.Instance == null || ScreenRouter.Instance.CurrentScreen != "TitleScreen") && waited < 60f)
            {
                waited += Time.unscaledDeltaTime;
                yield return null;
            }

            foreach (var id in _ids)
            {
                var errors = new List<string>();
                if (!ScreenRecipes.TryGet(id, out var recipe, errors))
                {
                    WriteOutcome(id, false, errors, null);
                    runLog.Append(id).Append(" FAILED\n");
                    continue;
                }
                var run = new Run(recipe);
                yield return run.Execute(this);
                runLog.Append(id).Append(run.Ok ? " DONE\n" : " FAILED\n");
            }
            Directory.CreateDirectory(OutRoot);
            File.WriteAllText(RunMarkerPath, runLog.ToString());
            Debug.Log("[screen-capture] 終了\n" + runLog);
            UnityEditor.EditorApplication.isPlaying = false;
        }

        private static void WriteOutcome(string id, bool ok, List<string> errors, string summaryJson)
        {
            var dir = Path.Combine(OutRoot, id);
            Directory.CreateDirectory(dir);
            if (summaryJson != null) File.WriteAllText(Path.Combine(dir, "summary.json"), summaryJson);
            File.WriteAllText(Path.Combine(dir, ok ? "DONE" : "FAILED"), string.Join("\n", errors) + "\n");
        }

        // ---------------------------------------------------------------------------------------------

        private sealed class Run
        {
            private readonly ScreenRecipe _r;
            private readonly List<string> _errors = new List<string>();
            private readonly List<string> _notes = new List<string>();
            private readonly List<string> _shots = new List<string>();
            private readonly List<string> _audits = new List<string>();
            private readonly List<string> _reads = new List<string>();
            private string _dir;
            private bool _failed;
            public bool Ok { get; private set; }

            public Run(ScreenRecipe r) { _r = r; }

            public IEnumerator Execute(MonoBehaviour host)
            {
                _dir = Path.Combine(OutRoot, _r.Id);
                if (Directory.Exists(_dir)) Directory.Delete(_dir, true);
                Directory.CreateDirectory(_dir);
                float t0 = Time.realtimeSinceStartup;

                foreach (var step in _r.Steps)
                {
                    if (_failed) break;
                    IEnumerator it = null;
                    try { it = Begin(step); }
                    catch (Exception e) { Fail(step, e); }
                    if (it == null) continue;
                    // 手順の中の例外も拾う（yield の途中で投げたものは MoveNext から出てくる）
                    while (true)
                    {
                        object cur;
                        try
                        {
                            if (!it.MoveNext()) break;
                            cur = it.Current;
                        }
                        catch (Exception e) { Fail(step, e); break; }
                        yield return cur;
                    }
                }

                Ok = !_failed;
                WriteOutcome(_r.Id, Ok, _errors, SummaryJson(Time.realtimeSinceStartup - t0));
            }

            private void Fail(ScreenStep step, Exception e)
            {
                var inner = e is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : e;
                Fail(step, inner.GetType().Name + ": " + inner.Message);
            }

            private void Fail(ScreenStep step, string message)
            {
                _failed = true;
                _errors.Add($"{step.Line}行 {step}: {message}");
            }

            private IEnumerator Begin(ScreenStep s)
            {
                switch (s.Kind)
                {
                    case "new_game": return NewGame(s);
                    case "advance_weeks": return AdvanceWeeks(s);
                    case "advance_days": return AdvanceDays(s);
                    case "advance_to_match_day": return AdvanceToMatchDay(s);
                    case "play_tournament": return PlayTournament(s);
                    case "advance_to_week": return AdvanceToWeek(s);
                    case "boost_roster": return BoostRoster(s);
                    case "show": return Show(s);
                    case "click": return Press(s, submit: false);
                    case "submit": return Press(s, submit: true);
                    case "hover": return Pointer(s, enter: true);
                    case "leave": return Pointer(s, enter: false);
                    case "call": return Call(s);
                    case "set": return Set(s);
                    case "wait": return Wait(s);
                    case "shot": return Shot(s);
                    case "read": return Read(s);
                }
                Fail(s, "実装されていない手順");
                return null;
            }

            // ---- 状態を作る ----

            /// <summary>新設校で新規ゲームを始める（本番の「この高校ではじめる」と同じ1経路）→ ホームへ。
            /// 値: {seed: 1, prefecture: 13, school: 桜丘, district: 0}（どれも省略可。seed の既定は 1＝毎回同じ部員）。</summary>
            private IEnumerator NewGame(ScreenStep s)
            {
                var m = s.Map ?? new Dictionary<string, string>();
                ulong seed = 1;
                int pref = NationService.DefaultPrefectureId;
                string school = NationService.DefaultSchoolName;
                int? district = null;
                if (m.TryGetValue("seed", out var v) && !ulong.TryParse(v, out seed)) { Fail(s, "seed が数でない"); yield break; }
                if (m.TryGetValue("prefecture", out v) && !int.TryParse(v, out pref)) { Fail(s, "prefecture が数でない（0基点＝JIS番号-1）"); yield break; }
                if (m.TryGetValue("school", out v)) school = v;
                if (m.TryGetValue("district", out v))
                {
                    if (!int.TryParse(v, out var d)) { Fail(s, "district が数でない"); yield break; }
                    district = d;
                }
                foreach (var k in m.Keys)
                    if (k != "seed" && k != "prefecture" && k != "school" && k != "district") { Fail(s, "知らないキー " + k); yield break; }

                while (BackgroundGameOp.Running) yield return null;
                SaveGameIO.StartNewSchool(school, pref, district, seed);
                HomeState.Reset();
                foreach (var x in ShowAndWait("HomeDashboard", s)) yield return x;
                _notes.Add($"new_game: {PrefectureNames.JapaneseOf(pref)} {school} seed={seed} 部員={RosterService.Active.Count}人");
            }

            /// <summary>週を送る（ホームの「次の週へ」と同じ経路）。大会に入ったらそこで止める（大会中は日送り）。</summary>
            private IEnumerator AdvanceWeeks(ScreenStep s)
            {
                if (!int.TryParse(s.Arg, out var n) || n <= 0) { Fail(s, "週の数を正の整数で"); yield break; }
                if (HomeState.Current == null) { Fail(s, "進行が無い（先に new_game）"); yield break; }
                int done = 0;
                for (; done < n; done++)
                {
                    if (GameSession.Current.InTournament) { _notes.Add($"advance_weeks: 大会に入ったので {done}/{n} 週で止めた"); break; }
                    HomeState.Current.AdvanceWeek();
                    yield return null;
                    while (BackgroundGameOp.Running) yield return null;
                    SettlePending();
                }
                _notes.Add($"advance_weeks: {done} 週 → 第{GameClock.Week}週");
            }

            /// <summary>日を送る（大会中の「次の日へ」と同じ経路）。</summary>
            private IEnumerator AdvanceDays(ScreenStep s)
            {
                if (!int.TryParse(s.Arg, out var n) || n <= 0) { Fail(s, "日の数を正の整数で"); yield break; }
                if (HomeState.Current == null) { Fail(s, "進行が無い（先に new_game）"); yield break; }
                for (int i = 0; i < n; i++)
                {
                    HomeState.Current.AdvanceDay();
                    yield return null;
                    while (BackgroundGameOp.Running) yield return null;
                }
                _notes.Add($"advance_days: {n} 日 → 第{GameClock.Week}週");
            }

            /// <summary>大会中に、自校の次の試合の日まで日を送る（その日の試合はまだ始めない）。</summary>
            private IEnumerator AdvanceToMatchDay(ScreenStep s)
            {
                if (HomeState.Current == null) { Fail(s, "進行が無い（先に new_game）"); yield break; }
                if (!GameSession.Current.InTournament) { Fail(s, "大会中でない（advance_weeks で大会の週まで進める）"); yield break; }
                int days = 0;
                while (!GameSession.Current.ReachedMatchDay && days < 60)
                {
                    HomeState.Current.AdvanceDay();
                    days++;
                    yield return null;
                    while (BackgroundGameOp.Running) yield return null;
                }
                if (!GameSession.Current.ReachedMatchDay) { Fail(s, "60日送っても試合の日にならない"); yield break; }
                _notes.Add($"advance_to_match_day: {days} 日");
            }

            /// <summary>
            /// いまの大会を最後まで消化する（勝ち進んだ先・続けて開幕する大会も含め、大会中でなくなるまで）。
            /// 自校の試合は本番と同じ経路で解く＝試合の日まで日送り → BeginMatch → 委任で最後まで → CompleteMatch →
            /// 結果を閉じる（DismissResult）。成績・通知・大会の後始末が本番どおりに残る。
            /// </summary>
            private IEnumerator PlayTournament(ScreenStep s)
            {
                if (HomeState.Current == null) { Fail(s, "進行が無い（先に new_game）"); yield break; }
                if (!GameSession.Current.InTournament) { Fail(s, "大会中でない（advance_weeks で大会の週まで進める）"); yield break; }
                foreach (var x in PlayWhileInTournament(s)) yield return x;
            }

            /// <summary>
            /// 途中の大会を消化しながら、指定の週まで進める。値: 39 か {week: 39, year: 2}（year は 1 始まりの年度・省略＝今の年度）。
            /// 大会に入ったら <see cref="PlayTournament"/> と同じく自校の試合を解いて進む。
            /// </summary>
            private IEnumerator AdvanceToWeek(ScreenStep s)
            {
                if (HomeState.Current == null) { Fail(s, "進行が無い（先に new_game）"); yield break; }
                int week, year = GameClock.YearIndex;
                if (s.Map != null)
                {
                    if (!s.Map.TryGetValue("week", out var w) || !int.TryParse(w, out week)) { Fail(s, "week が無いか数でない"); yield break; }
                    if (s.Map.TryGetValue("year", out var y) && !int.TryParse(y, out year)) { Fail(s, "year が数でない"); yield break; }
                }
                else if (!int.TryParse(s.Arg, out week)) { Fail(s, "週を数で（{week: 39, year: 2} も可）"); yield break; }
                if (Before(year, week, GameClock.YearIndex, GameClock.Week)) { Fail(s, $"もう過ぎている（いま {GameClock.YearIndex}年度 第{GameClock.Week}週）"); yield break; }

                int guard = 0;
                while (Before(GameClock.YearIndex, GameClock.Week, year, week) && guard++ < 5000)
                {
                    if (GameSession.Current.InTournament)
                    {
                        foreach (var x in PlayWhileInTournament(s)) yield return x;
                        if (_failed) yield break;
                        continue;
                    }
                    HomeState.Current.AdvanceWeek();
                    yield return null;
                    while (BackgroundGameOp.Running) yield return null;
                    SettlePending();
                }
                if (Before(GameClock.YearIndex, GameClock.Week, year, week)) { Fail(s, "進みきらなかった"); yield break; }
                _notes.Add($"advance_to_week: {GameClock.YearIndex}年度 第{GameClock.Week}週");
            }

            /// <summary>
            /// 週送りの途中でホームが順に出す「答え待ち」に、本番の既定どおり答える（プレイヤーが既定のまま決定を押すのと同じ）。
            /// 新チームの主将（暫定主将＝画面の初期選択、無ければ候補の先頭）／背番号の振り直し（既定のまま確定）／
            /// 年契約の更新（いまの段のまま確定）／転任の打診（残留。強制転任なら最初の候補）。
            /// 答えずに進めると、ホームが答え待ちの画面を出し続けて別の画面を開けない（大会タブを開いてもホームへ戻される）。
            /// </summary>
            private void SettlePending()
            {
                if (NewTeamService.Pending)
                {
                    var view = NewTeamService.BuildView();
                    int pick = -1;
                    foreach (var c in view.Candidates) if (c.IsInterim) pick = c.ActiveIndex;
                    if (pick < 0 && view.Candidates.Count > 0) pick = view.Candidates[0].ActiveIndex;
                    if (pick >= 0 && NewTeamService.Confirm(pick)) _notes.Add($"自動: 新チームの主将を指名（{GameClock.YearIndex}年度 第{GameClock.Week}週）");
                    else { NewTeamService.Skip(); _notes.Add("自動: 主将の指名を見送った"); }
                }
                if (UniformNumberService.Pending)
                {
                    UniformNumberService.Confirm();
                    _notes.Add($"自動: 背番号を既定のまま確定（第{GameClock.Week}週）");
                }
                if (ContractRenewalService.Pending)
                {
                    ContractRenewalService.Confirm();
                    _notes.Add($"自動: 年契約をいまの段のまま更新（第{GameClock.Week}週）");
                }
                if (ManagerTransferService.Pending)
                {
                    if (ManagerTransferService.Confirm(null)) _notes.Add("自動: 転任の打診は残留");
                    else
                    {
                        int? first = null;
                        foreach (var c in ManagerTransferService.BuildView().Rows) if (c.SchoolId != null) { first = c.SchoolId; break; }
                        ManagerTransferService.Confirm(first);
                        _notes.Add("自動: 強制転任で最初の候補へ");
                    }
                }
            }

            private static bool Before(int y1, int w1, int y2, int w2) => y1 < y2 || (y1 == y2 && w1 < w2);

            private IEnumerable PlayWhileInTournament(ScreenStep s)
            {
                int matches = 0;
                while (GameSession.Current.InTournament && matches < 60)
                {
                    HomeState.Current.ConsumeBanner();
                    int days = 0;
                    while (GameSession.Current.InTournament && !GameSession.Current.ReachedMatchDay && days < 60)
                    {
                        HomeState.Current.AdvanceDay();
                        days++;
                        yield return null;
                        while (BackgroundGameOp.Running) yield return null;
                    }
                    if (!GameSession.Current.InTournament) break;
                    if (!GameSession.Current.ReachedMatchDay) { Fail(s, "60日送っても試合の日にならない"); yield break; }

                    while (BackgroundGameOp.Running) yield return null;
                    LivePlayerMatch live = null;
                    bool begun = false;
                    if (!BackgroundGameOp.Run(() => live = HomeState.Current.BeginMatch(), () => begun = true))
                    { Fail(s, "試合の準備を始められなかった"); yield break; }
                    while (!begun) yield return null;
                    if (live == null) { Fail(s, "試合の準備に失敗した（コンソールの例外を見る）"); yield break; }

                    var result = live.Progression.SkipDelegateToAi(live.ManagerIsAway, ManagerService.TacticalSenseForAi);
                    PlayerMatchOutcome outcome = null;
                    bool completed = false;
                    if (!BackgroundGameOp.Run(() => outcome = HomeState.Current.CompleteMatch(result), () => completed = true))
                    { Fail(s, "試合後処理を始められなかった"); yield break; }
                    while (!completed) yield return null;
                    if (outcome != null)
                        _notes.Add($"試合: {outcome.RoundName} {live.OpponentName} {outcome.ManagerScore}-{outcome.OpponentScore} "
                                   + (outcome.ManagerWon ? (outcome.IsChampion ? "優勝" : "勝ち") : "負け"));

                    // 結果画面を閉じる（本番は OnResultOk が背景で DismissResult を回す。大会の後始末・週送りを伴う）
                    bool dismissed = false;
                    if (!BackgroundGameOp.Run(() => HomeState.Current.DismissResult(), () => dismissed = true))
                    { Fail(s, "結果を閉じられなかった"); yield break; }
                    while (!dismissed) yield return null;
                    float idle = 0f;
                    while ((BackgroundGameOp.Running || NationBackgroundSim.Running) && idle < 600f)
                    {
                        idle += Time.unscaledDeltaTime;
                        yield return null;
                    }
                    SettlePending();
                    matches++;
                }
                if (GameSession.Current.InTournament) Fail(s, "60試合でも大会が終わらない");
            }

            /// <summary>自校の部員の全能力を上げて勝ち進ませる（センバツ・甲子園まで行く分岐を見る用）。値: 95 か {level: 95, cap: 99}。</summary>
            private IEnumerator BoostRoster(ScreenStep s)
            {
                int level, cap = 99;
                if (s.Map != null)
                {
                    if (!s.Map.TryGetValue("level", out var l) || !int.TryParse(l, out level)) { Fail(s, "level が無いか数でない"); yield break; }
                    if (s.Map.TryGetValue("cap", out var c) && !int.TryParse(c, out cap)) { Fail(s, "cap が数でない"); yield break; }
                }
                else if (!int.TryParse(s.Arg, out level)) { Fail(s, "能力の値を数で"); yield break; }
                foreach (var p in RosterService.Roster)
                {
                    foreach (var k in KokoSim.Engine.Season.AbilityKinds.All)
                    {
                        p.SetCap(k, cap);
                        p.SetLevel(k, level);
                    }
                    p.Mental = System.Math.Min(95, level);
                    p.ConditionValue = 1.0;
                }
                _notes.Add($"boost_roster: 全員の能力 {level}（上限 {cap}）");
                yield return null;
            }

            // ---- 画面を開く・押す ----

            private IEnumerator Show(ScreenStep s)
            {
                foreach (var x in ShowAndWait(s.Arg, s)) yield return x;
            }

            private IEnumerable ShowAndWait(string screen, ScreenStep s)
            {
                var router = ScreenRouter.Instance;
                if (router == null) { Fail(s, "ScreenRouter が無い"); yield break; }
                if (router.CurrentScreen == screen)
                {
                    // 表示中の画面を開き直す＝一度消してから出す。SetActive(true) のままでは OnEnable が走らず、
                    // 週送り等で変わった状態が描き直されない（本番はボタン経由で画面側が自分で描き直す）
                    foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
                        if (d.gameObject.scene.IsValid() && d.gameObject.name == screen) d.gameObject.SetActive(false);
                    yield return null;
                }
                router.ShowDeferred(screen);
                float waited = 0f;
                while (router.CurrentScreen != screen && waited < ScreenWaitSeconds)
                {
                    waited += Time.unscaledDeltaTime;
                    yield return null;
                }
                if (router.CurrentScreen != screen) { Fail(s, $"画面 {screen} に切り替わらない（いま {router.CurrentScreen}）"); yield break; }
                // 開き直しのときは、ルータの Update が実際に SetActive(true) するまで待つ
                for (int i = 0; i < 120 && !IsActive(screen); i++) yield return null;
                if (!IsActive(screen)) { Fail(s, $"画面 {screen} が表示されない"); yield break; }
                // OnEnable の組み立てとレイアウトを1巡させる
                yield return null;
                yield return null;
            }

            /// <summary>
            /// 要素を押す。値は要素の name か「.クラス」、同じものが複数あれば「[添字]」（例: .seg-tab[3]）。
            /// click は ClickEvent（RegisterCallback&lt;ClickEvent&gt; の行・タブ用）、submit は決定キーと同じ
            /// NavigationSubmitEvent（Button の clicked 用）。Button を click で押すと発火しないので、Button なら自動で submit にする。
            /// </summary>
            private IEnumerator Press(ScreenStep s, bool submit)
            {
                var root = CurrentRoot(s);
                if (root == null) yield break;
                var el = Find(root, s.Arg, out var why);
                if (el == null) { Fail(s, why); yield break; }
                if (submit || el is Button)
                {
                    using (var e = NavigationSubmitEvent.GetPooled()) { e.target = el; el.SendEvent(e); }
                }
                else
                {
                    using (var e = ClickEvent.GetPooled()) { e.target = el; el.SendEvent(e); }
                }
                // 押した結果の画面遷移（ShowDeferred）と再描画を1巡させる
                yield return null;
                yield return null;
                yield return null;
            }

            /// <summary>
            /// マウスを載せる（hover＝PointerEnterEvent）／離す（leave＝PointerLeaveEvent）。値は click と同じ書き方。
            /// ホバーで開くポップオーバー等の確認用。:hover の見た目（USS の疑似クラス）はこれでは変わらない。
            /// </summary>
            private IEnumerator Pointer(ScreenStep s, bool enter)
            {
                var root = CurrentRoot(s);
                if (root == null) yield break;
                var el = Find(root, s.Arg, out var why);
                if (el == null) { Fail(s, why); yield break; }
                if (enter)
                {
                    using (var e = PointerEnterEvent.GetPooled()) { e.target = el; el.SendEvent(e); }
                }
                else
                {
                    using (var e = PointerLeaveEvent.GetPooled()) { e.target = el; el.SendEvent(e); }
                }
                yield return null;
                yield return null;
            }

            /// <summary>
            /// メソッドを呼ぶ。値: <c>Method(1, "a", true)</c>＝いま表示中の画面のコントローラ（その GameObject の MonoBehaviour）のもの、
            /// <c>ManagerService.Contracts.ScheduleCancel("scouting")</c>＝KokoSim の型から辿った先のもの（点を含むとき）。
            /// </summary>
            private IEnumerator Call(ScreenStep s)
            {
                var text = s.Arg.Trim();
                int open = text.IndexOf('(');
                if (open <= 0 || !text.EndsWith(")")) { Fail(s, "Method(引数…) の形で書く"); yield break; }
                var name = text.Substring(0, open).Trim();
                var args = SplitArgs(text.Substring(open + 1, text.Length - open - 2));
                int dot = name.LastIndexOf('.');
                if (dot > 0)
                {
                    if (!TryResolve(name.Substring(0, dot), out var target, out var type, out var why)) { Fail(s, why); yield break; }
                    var method = name.Substring(dot + 1);
                    var bf = (target == null ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.Public | BindingFlags.NonPublic;
                    foreach (var mi in type.GetMethods(bf))
                    {
                        if (mi.Name != method || mi.GetParameters().Length != args.Count) continue;
                        var ps = mi.GetParameters();
                        var values = new object[args.Count];
                        bool ok = true;
                        for (int i = 0; i < ps.Length && ok; i++) ok = TryConvert(args[i], ps[i].ParameterType, out values[i]);
                        if (!ok) continue;
                        mi.Invoke(target, values);
                        _notes.Add("call: " + name);
                        yield return null;
                        yield break;
                    }
                    Fail(s, $"{type.Name} に {method}（引数{args.Count}個・型が合うもの）が無い");
                    yield break;
                }
                var go = CurrentScreenObject(s);
                if (go == null) yield break;
                const BindingFlags F = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
                foreach (var mb in go.GetComponents<MonoBehaviour>())
                {
                    if (mb == null) continue;
                    foreach (var mi in mb.GetType().GetMethods(F))
                    {
                        if (mi.Name != name || mi.GetParameters().Length != args.Count) continue;
                        var ps = mi.GetParameters();
                        var values = new object[args.Count];
                        bool ok = true;
                        for (int i = 0; i < ps.Length && ok; i++) ok = TryConvert(args[i], ps[i].ParameterType, out values[i]);
                        if (!ok) continue;
                        var ret = mi.Invoke(mb, values);
                        if (ret is IEnumerator co) yield return mb.StartCoroutine(co);
                        _notes.Add($"call: {mb.GetType().Name}.{name}");
                        yield return null;
                        yield return null;
                        yield break;
                    }
                }
                Fail(s, $"{go.name} に {name}（引数{args.Count}個・型が合うもの）が無い");
            }

            /// <summary>
            /// static から辿れる値を書き換える。値: {ManagerService.Manager.Funds: 20, Type.Member: v}。
            /// 先頭は KokoSim の型の短い名前、以降はフィールド/プロパティを辿る（途中は static でもインスタンスでもよい）。
            /// </summary>
            private IEnumerator Set(ScreenStep s)
            {
                if (s.Map == null || s.Map.Count == 0) { Fail(s, "{型.メンバー: 値} の形で書く"); yield break; }
                foreach (var kv in s.Map)
                {
                    if (!TrySetPath(kv.Key, kv.Value, out var why)) { Fail(s, why); yield break; }
                    _notes.Add($"set: {kv.Key} = {kv.Value}");
                }
                yield return null;
            }

            /// <summary>待つ。値: フレーム数（既定 2）か「300ms」（UI の schedule.Execute の遅延を越えるとき）。</summary>
            private IEnumerator Wait(ScreenStep s)
            {
                if (s.Arg.EndsWith("ms"))
                {
                    if (!int.TryParse(s.Arg.Substring(0, s.Arg.Length - 2), out var ms) || ms < 0) { Fail(s, "ミリ秒を整数で"); yield break; }
                    float until = Time.realtimeSinceStartup + ms / 1000f;
                    while (Time.realtimeSinceStartup < until) yield return null;
                    yield return null;
                    yield break;
                }
                int frames = 2;
                if (s.Arg.Length > 0 && (!int.TryParse(s.Arg, out frames) || frames < 0)) { Fail(s, "フレーム数を整数か 300ms の形で"); yield break; }
                for (int i = 0; i < frames; i++) yield return null;
            }

            /// <summary>
            /// 要素の中身を summary.json の reads に書く（絵では見えない値の確認用）。値は click と同じ書き方。
            /// DropdownField は「選択中 | 選択肢…」、TextField は値、Label/Button は文字、それ以外は子の文字をつないだもの。
            /// </summary>
            private IEnumerator Read(ScreenStep s)
            {
                var root = CurrentRoot(s);
                if (root == null) yield break;
                var el = Find(root, s.Arg, out var why);
                if (el == null) { Fail(s, why); yield break; }
                string text;
                if (el is DropdownField dd) text = dd.value + " | " + string.Join(" / ", dd.choices ?? new List<string>());
                else if (el is TextField tf) text = tf.value;
                else if (el is TextElement te) text = te.text;
                else text = string.Join(" ", el.Query<TextElement>().ToList().Select(t => t.text).Where(t => !string.IsNullOrEmpty(t)));
                _reads.Add(s.Arg + " = " + text);
                yield return null;
            }

            // ---- 撮る ----

            /// <summary>いまの画面をレシピの解像度で撮る。値: 名前 か {name: x, h: 1400}（縦に長い画面だけ高さを変える）。
            /// 同時に書体監査（<see cref="FontAudit"/>）を summary.json に残す。</summary>
            private IEnumerator Shot(ScreenStep s)
            {
                string name = s.Map != null && s.Map.TryGetValue("name", out var n) ? n : s.Arg;
                int h = _r.Height;
                if (s.Map != null && s.Map.TryGetValue("h", out var hs) && !int.TryParse(hs, out h)) { Fail(s, "h が数でない"); yield break; }
                if (string.IsNullOrEmpty(name)) { Fail(s, "名前が無い"); yield break; }
                var go = CurrentScreenObject(s);
                if (go == null) yield break;
                var doc = go.GetComponent<UIDocument>();
                if (doc == null || doc.panelSettings == null) { Fail(s, go.name + " に UIDocument/panelSettings が無い"); yield break; }

                int w = _r.Width;
                var ps = doc.panelSettings;
                var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
                var rt = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                rt.Create();
                Texture2D tex = null;
                try
                {
                    ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
                    ps.referenceResolution = new Vector2Int(w, h);
                    ps.targetTexture = rt;
                }
                catch (Exception e) { Fail(s, e); }
                if (!_failed)
                {
                    yield return null; yield return new WaitForEndOfFrame();
                    yield return null; yield return new WaitForEndOfFrame();
                }
                try
                {
                    if (!_failed)
                    {
                        tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
                        var prevActive = RenderTexture.active;
                        RenderTexture.active = rt;
                        tex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                        tex.Apply();
                        RenderTexture.active = prevActive;
                        var file = name + ".png";
                        File.WriteAllBytes(Path.Combine(_dir, file), tex.EncodeToPNG());
                        _shots.Add(file);
                        _audits.Add(name + ": " + FontAudit.Report(doc.rootVisualElement).Replace("\n", " / "));
                    }
                }
                finally
                {
                    ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                    rt.Release(); Destroy(rt);
                    if (tex != null) Destroy(tex);
                }
            }

            // ---- 下回り ----

            private static bool IsActive(string screen)
            {
                foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
                    if (d.gameObject.scene.IsValid() && d.gameObject.name == screen && d.gameObject.activeInHierarchy) return true;
                return false;
            }

            private GameObject CurrentScreenObject(ScreenStep s)
            {
                var router = ScreenRouter.Instance;
                var screen = router == null ? null : router.CurrentScreen;
                if (string.IsNullOrEmpty(screen)) { Fail(s, "表示中の画面が無い"); return null; }
                foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
                    if (d.gameObject.scene.IsValid() && d.gameObject.name == screen && d.gameObject.activeInHierarchy)
                        return d.gameObject;
                Fail(s, $"表示中の画面 {screen} の UIDocument が見つからない");
                return null;
            }

            private VisualElement CurrentRoot(ScreenStep s)
            {
                var go = CurrentScreenObject(s);
                var doc = go == null ? null : go.GetComponent<UIDocument>();
                if (go != null && (doc == null || doc.rootVisualElement == null)) Fail(s, go.name + " に UI が無い");
                return doc == null ? null : doc.rootVisualElement;
            }

            private static VisualElement Find(VisualElement root, string query, out string why)
            {
                why = null;
                var q = query.Trim();
                int index = 0;
                int br = q.IndexOf('[');
                if (br > 0 && q.EndsWith("]"))
                {
                    if (!int.TryParse(q.Substring(br + 1, q.Length - br - 2), out index) || index < 0)
                    { why = "添字が読めない: " + query; return null; }
                    q = q.Substring(0, br);
                }
                var hits = q.StartsWith(".")
                    ? root.Query<VisualElement>(className: q.Substring(1)).ToList()
                    : root.Query<VisualElement>(name: q).ToList();
                hits = hits.Where(e => e.resolvedStyle.display != DisplayStyle.None).ToList();
                if (hits.Count == 0) { why = "見つからない（表示中のものに限る）: " + q; return null; }
                if (index >= hits.Count) { why = $"{q} は {hits.Count} 個しかない（添字 {index}）"; return null; }
                return hits[index];
            }

            private static List<string> SplitArgs(string s)
            {
                var list = new List<string>();
                if (s.Trim().Length == 0) return list;
                var sb = new StringBuilder();
                bool q = false;
                foreach (var c in s)
                {
                    if (c == '"') q = !q;
                    if (c == ',' && !q) { list.Add(sb.ToString().Trim()); sb.Clear(); continue; }
                    sb.Append(c);
                }
                list.Add(sb.ToString().Trim());
                return list;
            }

            private static bool TryConvert(string raw, Type t, out object value)
            {
                value = null;
                var v = ScreenRecipes.Unquote(raw);
                var nt = Nullable.GetUnderlyingType(t);
                if (nt != null)
                {
                    if (v == "null") return true;
                    t = nt;
                }
                if (t == typeof(string)) { value = v; return true; }
                if (t == typeof(bool)) { if (bool.TryParse(v, out var b)) { value = b; return true; } return false; }
                if (t == typeof(int)) { if (int.TryParse(v, out var i)) { value = i; return true; } return false; }
                if (t == typeof(long)) { if (long.TryParse(v, out var l)) { value = l; return true; } return false; }
                if (t == typeof(ulong)) { if (ulong.TryParse(v, out var ul)) { value = ul; return true; } return false; }
                if (t == typeof(float)) { if (ScreenRecipes.TryFloat(v, out var f)) { value = f; return true; } return false; }
                if (t == typeof(double))
                {
                    if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { value = d; return true; }
                    return false;
                }
                if (t.IsEnum)
                {
                    try { value = Enum.Parse(t, v); return true; }
                    catch (ArgumentException) { return false; }
                }
                return false;
            }

            /// <summary>
            /// 「型.メンバー.メンバー…」を辿る。先頭は KokoSim の型の短い名前（static の起点）、以降はフィールド/プロパティ。
            /// 数字の段はリストの添字（RosterService.Active.0.IsCaptain）。target が null なら static のまま＝型だけ返す。
            /// </summary>
            private static bool TryResolve(string path, out object target, out Type type, out string why)
            {
                target = null; type = null; why = null;
                var parts = path.Split('.');
                type = FindType(parts[0]);
                if (type == null) { why = "型が見つからない: " + parts[0]; return false; }
                for (int i = 1; i < parts.Length; i++)
                {
                    if (!Step(ref target, ref type, parts[i], out why)) return false;
                }
                return true;
            }

            private static bool Step(ref object target, ref Type type, string member, out string why)
            {
                why = null;
                if (int.TryParse(member, out var index))
                {
                    if (!(target is System.Collections.IList list)) { why = member + " の前がリストでない"; return false; }
                    if (index < 0 || index >= list.Count) { why = $"添字 {index} が範囲外（{list.Count} 件）"; return false; }
                    target = list[index];
                }
                else
                {
                    var flags = (target == null ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.Public | BindingFlags.NonPublic;
                    var f = type.GetField(member, flags);
                    var p = f == null ? type.GetProperty(member, flags) : null;
                    if (f == null && p == null) { why = $"{type.Name} に {member} が無い"; return false; }
                    target = f != null ? f.GetValue(target) : p.GetValue(target);
                }
                if (target == null) { why = member + " が null"; return false; }
                type = target.GetType();
                return true;
            }

            private static bool TrySetPath(string path, string raw, out string why)
            {
                why = null;
                int dot = path.LastIndexOf('.');
                if (dot <= 0) { why = "型.メンバー の形で書く: " + path; return false; }
                if (!TryResolve(path.Substring(0, dot), out var target, out var type, out why)) return false;
                var member = path.Substring(dot + 1);
                var flags = (target == null ? BindingFlags.Static : BindingFlags.Instance) | BindingFlags.Public | BindingFlags.NonPublic;
                var f = type.GetField(member, flags);
                var p = f == null ? type.GetProperty(member, flags) : null;
                if (f == null && p == null) { why = $"{type.Name} に {member} が無い"; return false; }
                var mt = f != null ? f.FieldType : p.PropertyType;
                if (!TryConvert(raw, mt, out var value)) { why = $"{raw} を {mt.Name} にできない"; return false; }
                if (f != null) { f.SetValue(target, value); return true; }
                if (p.GetSetMethod(true) == null) { why = member + " は書き込めない"; return false; }
                p.SetValue(target, value);
                return true;
            }

            private static Type FindType(string shortName)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var an = asm.GetName().Name;
                    if (an != "Assembly-CSharp" && !an.StartsWith("KokoSim")) continue;
                    Type[] types;
                    try { types = asm.GetTypes(); }
                    catch (ReflectionTypeLoadException e) { types = e.Types.Where(t => t != null).ToArray(); }
                    foreach (var t in types)
                        if (t.Name == shortName && (t.Namespace ?? "").StartsWith("KokoSim")) return t;
                }
                return null;
            }

            private string SummaryJson(float seconds)
            {
                var sb = new StringBuilder();
                sb.Append("{\"id\": ").Append(Q(_r.Id)).Append(", \"ok\": ").Append(_failed ? "false" : "true")
                  .Append(", \"seconds\": ").Append(seconds.ToString("F1", CultureInfo.InvariantCulture))
                  .Append(", \"week\": ").Append(GameClock.Week)
                  .Append(", \"screen\": ").Append(Q(ScreenRouter.Instance == null ? "" : ScreenRouter.Instance.CurrentScreen))
                  .Append(", \"shots\": [").Append(string.Join(", ", _shots.Select(Q))).Append("]")
                  .Append(", \"notes\": [").Append(string.Join(", ", _notes.Select(Q))).Append("]")
                  .Append(", \"reads\": [").Append(string.Join(", ", _reads.Select(Q))).Append("]")
                  .Append(", \"font_audit\": [").Append(string.Join(", ", _audits.Select(Q))).Append("]")
                  .Append(", \"errors\": [").Append(string.Join(", ", _errors.Select(Q))).Append("]}");
                return sb.ToString();
            }

            private static string Q(string s)
            {
                var sb = new StringBuilder("\"");
                foreach (var c in s ?? "")
                {
                    switch (c)
                    {
                        case '"': sb.Append("\\\""); break;
                        case '\\': sb.Append("\\\\"); break;
                        case '\n': sb.Append("\\n"); break;
                        case '\r': break;
                        case '\t': sb.Append("\\t"); break;
                        default: sb.Append(c); break;
                    }
                }
                return sb.Append('"').ToString();
            }
        }
    }
}
#endif
