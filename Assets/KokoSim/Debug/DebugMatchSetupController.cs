using System.Collections.Generic;
using System.Globalization;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Nation.Tournaments;
using KokoSim.Unity.Shell;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Debugging
{
    /// <summary>
    /// デバッグ試合の選択画面（issue #905・A案「球場リスト＋右に設定」）。球場・観客・場面・采配側・シード・応援を選び、
    /// セーブ無しで試合画面を開く。試合を組むのは <c>DebugMatchLauncher</c>（開発用ビルドだけに入る）。
    ///
    /// <para>画面の GameObject は全ビルドのシーンに居るが、入口（タイトルのボタン）は開発用ビルドでしか出ない。
    /// 製品ビルドで万一開いても「試合開始」は何もしない（下の #if）。</para>
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class DebugMatchSetupController : MonoBehaviour
    {
        public const string ScreenName = "DebugMatchSetup";

        // 前回の選択（確認を繰り返すときの手間を減らす。読めなければ既定値）。
        private const string PrefStadium = "kokosim.debugmatch.stadium";
        private const string PrefCrowd = "kokosim.debugmatch.crowd";
        private const string PrefScene = "kokosim.debugmatch.scene";
        private const string PrefAway = "kokosim.debugmatch.away";
        private const string PrefSeed = "kokosim.debugmatch.seed";
        private const string PrefBand = "kokosim.debugmatch.band";

        private static readonly (CrowdTurnout Value, string Label)[] CrowdOptions =
            { (CrowdTurnout.Sparse, "まばら"), (CrowdTurnout.Half, "半分"), (CrowdTurnout.Full, "満員") };

        private const string FromStart = "1回表から";

        private VisualElement _root;
        private ScrollView _list;
        private DropdownField _scene;
        private TextField _seed;
        private Label _error;
        private readonly Dictionary<string, VisualElement> _rows = new();
        private readonly List<string> _sceneIds = new();

        private string _stadiumId;
        private CrowdTurnout _crowd = CrowdTurnout.Full;
        private bool _managerIsAway;
        private bool _band;

        private void OnEnable()
        {
            _root = GetComponent<UIDocument>().rootVisualElement;
            _list = _root.Q<ScrollView>("dm-list");
            _scene = _root.Q<DropdownField>("dm-scene");
            _seed = _root.Q<TextField>("dm-seed");
            _error = _root.Q<Label>("dm-error");

            LoadPrefs();
            BuildList();
            BuildScenes();
            if (_seed != null) _seed.SetValueWithoutNotify(PlayerPrefs.GetString(PrefSeed, ""));

            var back = _root.Q<Button>("dm-back");
            if (back != null) { back.clicked -= OnBack; back.clicked += OnBack; }
            var start = _root.Q<Button>("dm-start");
            if (start != null) { start.clicked -= OnStart; start.clicked += OnStart; }

            Render();
        }

        private void LoadPrefs()
        {
            _stadiumId = PlayerPrefs.GetString(PrefStadium, Stadium.Koshien.Id);
            if (StadiumCatalogProvider.Find(_stadiumId) == null) _stadiumId = Stadium.Koshien.Id;
            _crowd = (CrowdTurnout)PlayerPrefs.GetInt(PrefCrowd, (int)CrowdTurnout.Full);
            if (_crowd < CrowdTurnout.Sparse || _crowd > CrowdTurnout.Full) _crowd = CrowdTurnout.Full;
            _managerIsAway = PlayerPrefs.GetInt(PrefAway, 0) == 1;
            _band = PlayerPrefs.GetInt(PrefBand, _stadiumId == Stadium.Koshien.Id ? 1 : 0) == 1;
        }

        // ── 左: 球場の一覧（区分ごとに見出しを挟む） ──────────────────

        /// <summary>区分。yaml の節は読み取り結果に残らないので、tier と id の命名（県大会決勝は *_final）で分ける。</summary>
        private static int GroupOf(Stadium s)
            => s.Tier == StadiumTier.National ? 0 : s.Id.EndsWith("_final") ? 2 : 1;

        private static readonly string[] GroupNames = { "全国", "架空", "県大会決勝" };

        private void BuildList()
        {
            if (_list == null) return;
            _list.Clear();
            _rows.Clear();
            var all = StadiumCatalogProvider.All;
            for (var g = 0; g < GroupNames.Length; g++)
            {
                var header = false;
                foreach (var s in all)
                {
                    if (GroupOf(s) != g) continue;
                    if (!header)
                    {
                        _list.Add(L(GroupNames[g], "dm-group", "f-body-md"));
                        header = true;
                    }
                    var row = new VisualElement();
                    row.AddToClassList("dm-row");
                    row.Add(L(s.Name, "dm-row__name", "f-display"));
                    row.Add(Num(WingText(s), "dm-col-wing"));
                    row.Add(Num(D1(s.CenterM), "dm-col-center"));
                    row.Add(Num(D1(s.FenceHeightM), "dm-col-fence"));
                    var id = s.Id;
                    row.RegisterCallback<PointerDownEvent>(_ => Select(id));
                    _rows[id] = row;
                    _list.Add(row);
                }
            }
            var count = _root.Q<Label>("dm-count");
            if (count != null) count.text = $"{all.Count}件";
        }

        private static Label Num(string text, string col) => L(text, "dm-row__num", "f-num", col);

        private static Label L(string text, params string[] classes)
        {
            var l = new Label(text);
            foreach (var c in classes) l.AddToClassList(c);
            return l;
        }

        // 桁を揃える（UI原則③）: どの列にも小数を持つ球場がある（両翼99.5・中堅117.7・フェンス2.6）ので全行小数1桁。
        private static string WingText(Stadium s)
            => s.LeftM == s.RightM ? D1(s.LeftM) : $"{D1(s.LeftM)}/{D1(s.RightM)}";

        private static string D1(double v) => v.ToString("0.0", CultureInfo.InvariantCulture);

        private void Select(string id)
        {
            var wasKoshien = _stadiumId == Stadium.Koshien.Id;
            _stadiumId = id;
            // 応援は甲子園なら既定ON（本番の「応援は甲子園だけ」と同じ）。甲子園を出入りしたときだけ追従させる
            var isKoshien = id == Stadium.Koshien.Id;
            if (wasKoshien != isKoshien) _band = isKoshien;
            Render();
        }

        // ── 右: 設定 ─────────────────────────────────────────

        private void BuildScenes()
        {
            if (_scene == null) return;
            var labels = new List<string> { FromStart };
            _sceneIds.Clear();
            _sceneIds.Add(null);
#if KOKOSIM_DEBUG || UNITY_EDITOR || DEVELOPMENT_BUILD
            foreach (var def in DebugScenarios.Catalog.All)
            {
                labels.Add(string.IsNullOrEmpty(def.Name) ? def.Id : def.Name);
                _sceneIds.Add(def.Id);
            }
#endif
            _scene.choices = labels;
            var saved = PlayerPrefs.GetString(PrefScene, "");
            var index = Mathf.Max(0, _sceneIds.IndexOf(string.IsNullOrEmpty(saved) ? null : saved));
            _scene.SetValueWithoutNotify(labels[index]);
        }

        private string SelectedSceneId()
        {
            if (_scene == null) return null;
            var i = _scene.choices.IndexOf(_scene.value);
            return i > 0 && i < _sceneIds.Count ? _sceneIds[i] : null;
        }

        private void Render()
        {
            foreach (var kv in _rows) kv.Value.EnableInClassList("dm-row--selected", kv.Key == _stadiumId);

            var s = StadiumCatalogProvider.Find(_stadiumId);
            SetText("dm-sel-name", s?.Name ?? "");
            SetText("dm-tier", s == null ? "" : GroupNames[GroupOf(s)]);
            SetText("dm-sel-wing", s == null ? "" : WingText(s));
            SetText("dm-sel-center", s == null ? "" : D1(s.CenterM));
            SetText("dm-sel-fence", s == null ? "" : D1(s.FenceHeightM));

            var crowd = new string[CrowdOptions.Length];
            var crowdOn = 0;
            for (var i = 0; i < CrowdOptions.Length; i++)
            {
                crowd[i] = CrowdOptions[i].Label;
                if (CrowdOptions[i].Value == _crowd) crowdOn = i;
            }
            Seg("dm-crowd", crowd, crowdOn, i => _crowd = CrowdOptions[i].Value);
            Seg("dm-side", new[] { "先攻", "後攻" }, _managerIsAway ? 0 : 1, i => _managerIsAway = i == 0);
            Seg("dm-band", new[] { "あり", "なし" }, _band ? 0 : 1, i => _band = i == 0);
        }

        /// <summary>選択肢の切替（部品辞書の <c>.sbk-seg</c>）。押したら値を変えて描き直す。</summary>
        private void Seg(string name, string[] labels, int on, System.Action<int> pick)
        {
            var host = _root.Q<VisualElement>(name);
            if (host == null) return;
            host.Clear();
            for (var i = 0; i < labels.Length; i++)
            {
                var opt = new Label(labels[i]);
                opt.AddToClassList("sbk-seg__opt");
                opt.AddToClassList("f-body-bd");
                if (i == on) opt.AddToClassList("sbk-seg__opt--on");
                var index = i;
                opt.RegisterCallback<PointerDownEvent>(_ => { pick(index); Render(); });
                host.Add(opt);
            }
        }

        private void SetText(string name, string text)
        {
            var l = _root.Q<Label>(name);
            if (l != null) l.text = text;
        }

        // ── 操作 ─────────────────────────────────────────────

        private static void OnBack() => ScreenRouter.Instance?.ShowDeferred("TitleScreen");

        private void OnStart()
        {
#if KOKOSIM_DEBUG || UNITY_EDITOR || DEVELOPMENT_BUILD
            var req = new DebugMatchRequest
            {
                StadiumId = _stadiumId,
                Crowd = _crowd,
                ScenarioId = SelectedSceneId(),
                SeedHex = _seed?.value,
                ManagerIsAway = _managerIsAway,
                BrassBand = _band,
            };
            var error = DebugMatchLauncher.Launch(req, ScreenName);
            if (_error != null) _error.text = error ?? "";
            if (error != null) return;
            SavePrefs(req);
#endif
        }

#if KOKOSIM_DEBUG || UNITY_EDITOR || DEVELOPMENT_BUILD
        private static void SavePrefs(DebugMatchRequest req)
        {
            PlayerPrefs.SetString(PrefStadium, req.StadiumId ?? "");
            PlayerPrefs.SetInt(PrefCrowd, (int)req.Crowd);
            PlayerPrefs.SetString(PrefScene, req.ScenarioId ?? "");
            PlayerPrefs.SetInt(PrefAway, req.ManagerIsAway ? 1 : 0);
            PlayerPrefs.SetString(PrefSeed, req.SeedHex ?? "");
            PlayerPrefs.SetInt(PrefBand, req.BrassBand ? 1 : 0);
            PlayerPrefs.Save();
        }

        /// <summary>撮影ハーネス用: 球場を選んだ状態にする。</summary>
        public void SelectForCapture(string stadiumId) => Select(stadiumId);

        /// <summary>撮影ハーネス用: 「試合開始」を押す。返り値はエラー文（null＝成功）。</summary>
        public string StartForCapture()
        {
            OnStart();
            return string.IsNullOrEmpty(_error?.text) ? null : _error.text;
        }
#endif
    }
}
