using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;
using KokoSim.Unity.Components;  // 部品辞書（RankChip / LevelPips / NumUnit / SegTabs）
using KokoSim.Unity.Shell;       // WeekAdvance / TopBarMeters / RosterService / TeamOverall

namespace KokoSim.Unity.Management
{
    /// <summary>
    /// 経営画面（設計書04 §1.4 資金 ／ 設計書03 §4 施設・Issue #128）。
    /// 上＝部費の年間収支（表示専用。収入と固定支出は年度頭にエンジンが自動処理する）、
    /// 下＝施設4系統の台帳で、行の右端から1段ずつ買う。購入の可否・資金の減算はエンジン
    /// （<see cref="KokoSim.Engine.Career.FacilityPurchase"/>）が判定し、ここは表示と入力の中継だけ
    /// （UI-BUILD-METHOD 大原則④）。
    /// 主要CTA（応援団赤）はトップバーの「今週を進める」が持つので、購入ボタンは操作系の空の青
    /// （部品辞書の ToolButton）で出す（UI原則②・⑦）。
    /// </summary>
    [RequireComponent(typeof(UIDocument))]
    public sealed class ManagementScreenController : MonoBehaviour
    {
        private ManagementState _state;
        private VisualElement _root;

        private void OnEnable()
        {
            _state = new ManagementState();
            _root = GetComponent<UIDocument>().rootVisualElement;

            // 全タブ共通の進週処理へ集約（issue #134: 大会モード中はホームへ回送して日送りへ引き継ぐ）。
            var advance = _root.Q<Button>("advance");
            if (advance != null) advance.clicked += () => WeekAdvance.FromSideScreen(Render);

            Render();
        }

        /// <summary>スクショ・自動検証用に1段買う（通常は行の購入ボタン）。</summary>
        public void Buy(string facilityId)
        {
            _state.Buy(facilityId);
            Render();
        }

        /// <summary>年契約の段を1つ上げる（契約する）。</summary>
        public void Sign(string contractId)
        {
            _state.Sign(contractId);
            Render();
        }

        /// <summary>年契約を解約する。</summary>
        public void Cancel(string contractId)
        {
            _state.Cancel(contractId);
            Render();
        }

        private void Render()
        {
            var v = _state.BuildView();

            RenderTopBar();
            RenderLedger(v);
            RenderFacilities(v);
            RenderFoot(v);
        }

        /// <summary>共通トップバーの動的値（週・メーター・自校の総合ランク）を埋める。他画面と同じ単一ソース。</summary>
        private void RenderTopBar()
        {
            ScoreboardStrip.Fill(_root);
            TopBarMeters.Fill(_root);   // 部費残高・名声・信頼度（ManagerService 単一ソース）
            var rank = _root.Q<VisualElement>("team-rank");
            if (rank == null) return;
            rank.Clear();
            rank.Add(UiComponents.RankChipLegacy(TeamOverall.GradeOf(RosterService.Active)));
        }

        // ── 部費（残高・年間収入・年間支出・差引） ─────────────────────────
        private void RenderLedger(ManagementView v)
        {
            var host = _root.Q<VisualElement>("mn-ledger");
            if (host == null) return;
            host.Clear();
            // 財布は2つ（設計書04 §1.4）。部費＝道具・備品・合宿・スカウトの運営費、
            // 施設基金＝学校の設備投資と後援会・OB寄付で積む建物専用の別会計。
            host.Add(Kv("部費 残高", v.Funds, "", signed: false));
            // 差引だけが警告になりうる枠（毎年部費が目減りする＝本当の警告, UI原則②）。
            host.Add(Kv("部費 年間差引", v.Net,
                v.IncomeNote + " − " + v.ExpenseNote, signed: true, warnIfNegative: true));
            host.Add(Kv("施設基金 残高", v.FacilityFund, "", signed: false));
            host.Add(Kv("施設基金 年間収入", v.FundIncome, v.FundNote, signed: true));
        }

        /// <summary>
        /// 収支1枠（見出し＋金額＋内訳）。金額は NumUnit＝数字だけ Oswald・単位「万」はサンセリフ
        /// （書体3役ルール: 数字と和文の混植は Label を分ける）。<paramref name="signed"/> は
        /// 増減として読ませる枠（収入・支出・差引）に付け、残高には付けない。
        /// </summary>
        private static VisualElement Kv(string key, double amount, string note, bool signed,
                                        bool warnIfNegative = false)
        {
            var box = new VisualElement();
            box.AddToClassList("mn-kv");
            // 内訳を持つ枠（差引・年間収入）だけ幅を倍取る。4枠を均等に割ると内訳の幅が
            // 足りず、1文字ずつ折り返して縦に潰れる（実測）。
            if (!string.IsNullOrEmpty(note)) box.AddToClassList("mn-kv--wide");
            if (warnIfNegative && amount < 0) box.AddToClassList("mn-kv--neg");

            var k = new Label(key);
            k.AddToClassList("mn-kv__k");
            box.Add(k);

            // 金額と内訳は同じ行に並べる（内訳を下段に置くと4枠ぶんの行が縦に効いて、
            // 下の施設台帳が1画面に収まらなくなる＝実測で 1600x900 に 64px 不足した）。
            var line = new VisualElement();
            line.AddToClassList("mn-kv__line");

            // 符号は数字側に付ける（「+44」「-60」で桁の右揃えを崩さない）。
            var sign = !signed ? "" : amount < 0 ? "-" : "+";
            line.Add(UiComponents.NumUnit(sign + System.Math.Abs(amount).ToString("0"), "万", true, "mn-kv__v"));

            if (!string.IsNullOrEmpty(note))
            {
                var n = new Label(note);
                n.AddToClassList("mn-kv__note");
                line.Add(n);
            }
            box.Add(line);
            return box;
        }

        // ── 台帳（区分タブで排他に切り替え、選択中の区分だけを並べる） ──────────
        // 区分見出しを縦に積むと 15行＋見出し4本＝19行になり、1画面（窓254px）に4.4行しか
        // 入らなかった。タブなら一度に出るのは最大5行で、スクロールなしで全部見える。
        private void RenderFacilities(ManagementView v)
        {
            var tabs = _root.Q<VisualElement>("mn-tabs");
            if (tabs != null)
            {
                tabs.Clear();
                var labels = new List<string>();
                foreach (var g in v.Groups) labels.Add(g.Label);
                tabs.Add(UiComponents.SegTabs(labels, v.SelectedTab, SelectTab));

                var spacer = new VisualElement();
                spacer.style.flexGrow = 1;
                tabs.Add(spacer);

                // 区分の性質（どの財布から出るか等）はタブ帯の右へ添える。
                var note = new Label(v.Selected == null ? "" : v.Selected.Note);
                note.AddToClassList("mn-tabnote");
                tabs.Add(note);
            }

            var host = _root.Q<VisualElement>("mn-list");
            if (host == null) return;
            host.Clear();
            if (v.Selected == null) return;
            foreach (var r in v.Selected.Rows) host.Add(BuildRow(r));
        }

        /// <summary>台帳の区分タブを切り替える（表示だけ。エンジンには触れない）。</summary>
        public void SelectTab(int index)
        {
            _state.SelectTab(index);
            Render();
        }

        private VisualElement BuildRow(FacilityLedgerRow r)
        {
            var row = new VisualElement();
            row.AddToClassList("row-panel");
            // 4系統＋合計を1画面に収める（縮めて収めるのではなく、行高の詰まった
            // 高密度バリアントを選ぶ＝UI原則①・UI-BUILD-METHOD「潰して収めない」）。
            row.AddToClassList("row-panel--dense");
            row.AddToClassList("mn-row");

            var name = new Label(r.Name);
            name.AddToClassList("mn-cell--name");
            row.Add(name);

            // レベル: 升目（部品辞書 LevelPips）＋「Lv2 / 3」の文字併記。
            var lv = new VisualElement();
            lv.AddToClassList("mn-cell--lv");
            lv.Add(UiComponents.LevelPips(r.Level, r.MaxLevel));
            var lvText = new Label("Lv" + r.Level);
            lvText.AddToClassList("mn-lv-text");
            lv.Add(lvText);
            row.Add(lv);

            var cur = new Label(r.CurrentEffect);
            cur.AddToClassList("mn-cell--eff");
            row.Add(cur);

            var next = new Label(r.NextCost.HasValue ? r.NextEffect : "—");
            next.AddToClassList("mn-cell--eff");
            next.AddToClassList("mn-cell--eff-next");
            row.Add(next);

            // 最大レベルの行は数字が無いので NumUnitAuto に任せる（数字を含まない文字列は
            // 分割せず1枚の Label で返る＝Oswald に無い記号を載せて豆腐にしない）。
            // 年契約は買い切りと単位が違う（毎年かかる）ので「/年」を必ず添える。
            var costText = r.NextCost.HasValue
                ? r.NextCost.Value.ToString("0") + (r.IsContract ? "万/年" : "万")
                : "—";
            var cost = UiComponents.NumUnitAuto(costText, false, "mn-cell--cost");
            if (r.NextCost.HasValue && !r.Affordable) cost.AddToClassList("mn-cell--cost-off");
            row.Add(cost);

            var act = new VisualElement();
            act.AddToClassList("mn-cell--act");
            var id = r.Id;   // クロージャ捕捉
            if (r.NextCost.HasValue)
            {
                var buy = r.IsContract
                    ? new Button(() => Sign(id)) { text = r.ActionLabel }
                    : new Button(() => Buy(id)) { text = r.ActionLabel };
                buy.AddToClassList("tool-btn");
                buy.AddToClassList("mn-buy");
                buy.SetEnabled(r.Affordable);
                buy.EnableInClassList("mn-buy--off", !r.Affordable);
                act.Add(buy);
            }
            else if (!r.IsContract)
            {
                var maxed = new Label("最大レベル");
                maxed.AddToClassList("mn-maxed");
                act.Add(maxed);
            }
            // 解約は年契約だけの操作。買い切りには戻す手段が無い（施設は学校の資産として残る）。
            if (r.CanCancel)
            {
                var cancel = new Button(() => Cancel(id)) { text = "解約" };
                cancel.AddToClassList("tool-btn");
                cancel.AddToClassList("mn-cancel");
                act.Add(cancel);
            }
            row.Add(act);

            return row;
        }

        // ── 合計（いまの練習効率・週練習時間と、全系統を最大にしたときの上限） ────────
        private void RenderFoot(ManagementView v)
        {
            var total = _root.Q<VisualElement>("mn-total");
            if (total != null)
            {
                total.Clear();
                total.Add(TotalItem("練習効率", v.CoefNow.ToString("0.00") + "（最大 " + v.CoefMax.ToString("0.00") + "）"));
                total.Add(TotalItem("週練習", v.MinutesNow + "分（最大 " + v.MinutesMax + "分）"));
            }

            var status = _root.Q<Label>("mn-status");
            if (status == null) return;
            status.text = v.StatusText;
            status.EnableInClassList("mn-status--warn", v.StatusIsWarning);
        }

        private static VisualElement TotalItem(string key, string value)
        {
            var wrap = new VisualElement();
            wrap.style.flexDirection = FlexDirection.Row;
            var k = new Label(key);
            k.AddToClassList("mn-total__k");
            wrap.Add(k);
            var val = new Label(value);
            val.AddToClassList("mn-total__v");
            wrap.Add(val);
            return wrap;
        }
    }
}
