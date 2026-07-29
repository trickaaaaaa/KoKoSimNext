#if UNITY_EDITOR
using System.Collections;
using System.IO;
using KokoSim.Engine.Nation;
using UnityEngine;
using UnityEngine.UIElements;

namespace KokoSim.Unity.Match
{
    /// <summary>
    /// 【開発用・エディタ限定・使い捨て】チームカラー（設計書18 §3.5.1）のオンボディ検証撮影。
    /// パレット表の配色を実際に着せて、①普通の対戦 ②縦縞の go/no-go（未決事項#6）
    /// ③同系色同士の見分け（実物準拠で衝突を許容した判断の実害確認）を1枚ずつ撮る。
    /// </summary>
    public sealed class DuelTeamColorShooter : MonoBehaviour
    {
        private const int W = 1600;
        private const int H = 900;
        private const string Dir = "screenshots";

        public static string FileName = "i-duel-color";

        /// <summary>撮る組み合わせ（守備側の配色ID, 攻撃側の配色ID, ファイル名の接尾辞）。</summary>
        private static readonly (string Defense, string Offense, string Tag)[] Cases =
        {
            ("white_navy", "cream_crimson", "a-normal"),        // 白＋紺 vs クリーム＋えんじ
            ("white_navy_pin", "gray_black", "b-stripe"),       // 縦縞の go/no-go（守備側が縞）
            ("white_navy", "white_deepnavy", "c-clash"),        // 紺 vs 濃紺＝承知の上の同系色衝突
        };

        /// <summary>
        /// 縦縞の周波数スイープ（未決事項#6 の判断材料）。実物どおりの40本/mは 480×270 では
        /// 縞の間隔が1px を割ってモアレになるはずなので、成立する下限があるかを実測する。
        /// </summary>
        private static readonly float[] StripeFreqSweep = { 40f, 20f, 12f, 8f };

        /// <summary>投手への寄り（DuelGearShotShooter で実測した狙い）。縞は寄りで見ないと判定できない。</summary>
        private const float PitcherAimY = 1.23f, PitcherAimZ = 18.3f, PitcherFov = 0.55f;

        private IEnumerator Start()
        {
            Application.runInBackground = true;
            MatchLiveController.CaptureMode = true;
            EnsureOnlyMatchLive();
            yield return null; yield return null;
            EnsureOnlyMatchLive();

            var ctrl = FindController();
            if (ctrl == null) { Debug.LogError("[ColorShot] MatchLive/Controller が見つからない"); yield break; }
            yield return null;
            Directory.CreateDirectory(Dir);

            for (int i = 0; i < 3; i++) ctrl.AdvanceForCapture();
            if (!ctrl.DuelViewOnForCapture) ctrl.ToggleDuelViewForCapture();
            ctrl.RestartCurrentPaForCapture();
            for (int i = 0; i < 24; i++) yield return null;

            var palette = Shell.TeamColorProvider.Palette;
            foreach (var c in Cases)
            {
                var def = palette.FindById(c.Defense);
                var off = palette.FindById(c.Offense);
                if (def == null || off == null)
                {
                    Debug.LogError($"[ColorShot] 配色IDが表に無い: {c.Defense} / {c.Offense}");
                    continue;
                }
                DuelStageRig.Live?.SetTeamColors(TeamColors.FromEntry(def), TeamColors.FromEntry(off));
                for (int i = 0; i < 12; i++) yield return null;
                yield return Capture(FileName + "-" + c.Tag);
                Debug.Log($"[ColorShot] {c.Tag}: 守備={def.Label} 攻撃={off.Label}");
            }

            // 縦縞スイープ（未決事項#6）。守備側＝投手に縞を着せ、投手への寄りで判定する。
            var pin = palette.FindById("white_navy_pin");
            var plain = palette.FindById("gray_black");
            if (pin != null && plain != null)
            {
                // 判定は**本番スケール（480×270のRT）**で行う。寄りで縞が出ていても、
                // 本番の解像度で潰れたら意味がない（この工程が go/no-go の本体）
                foreach (var freq in StripeFreqSweep)
                {
                    var striped = TeamColors.FromEntry(pin) with
                    {
                        Stripe = new TeamStripe(true, pin.Stripe.Color, freq),
                    };
                    DuelStageRig.Live?.SetTeamColors(striped, TeamColors.FromEntry(plain));
                    for (int i = 0; i < 12; i++) yield return null;
                    yield return Capture(FileName + "-full" + Mathf.RoundToInt(freq));
                }

                // 縞の**向き**の確認。既定の構図は投手を背中から見ており、横向きの体では
                // 縦縞が輪郭の縁にしか出ないため「縞に見えない」。横角を振って正面から撮る
                {
                    var striped = TeamColors.FromEntry(pin) with
                    {
                        Stripe = new TeamStripe(true, pin.Stripe.Color, 24f),
                    };
                    DuelStageRig.Live?.SetTeamColors(striped, TeamColors.FromEntry(plain));
                    var prev = DuelStageRig.CalibrationOverride;
                    foreach (var yaw in new[] { 0f, 90f, 180f, 270f })
                    {
                        var cal = DuelCalibration.Default;
                        cal.AimY = PitcherAimY; cal.AimZ = PitcherAimZ;
                        cal.FovDeg = 3.0f; cal.PitchDeg = 4f; cal.YawDeg = yaw;
                        DuelStageRig.CalibrationOverride = cal;
                        DuelStageRig.Live?.ApplyCalibration(cal);
                        for (int i = 0; i < 12; i++) yield return null;
                        yield return Capture(FileName + "-yaw" + Mathf.RoundToInt(yaw));
                    }
                    DuelStageRig.CalibrationOverride = prev;
                    DuelStageRig.Live?.ApplyCalibration(prev ?? DuelCalibration.Default);
                }

                // 本番構図での再判定は**打者**で行う。投手はこの構図では常に背中向き＋小さく、
                // 縦縞が輪郭の縁に潰れて「縞に見えない」＝縞の可否を判断する材料にならない
                foreach (var freq in new[] { 40f, 28f, 20f })
                {
                    var striped = TeamColors.FromEntry(pin) with
                    {
                        Stripe = new TeamStripe(true, pin.Stripe.Color, freq),
                    };
                    DuelStageRig.Live?.SetTeamColors(TeamColors.FromEntry(plain), striped);
                    for (int i = 0; i < 12; i++) yield return null;
                    yield return Capture(FileName + "-bat" + Mathf.RoundToInt(freq));
                }

                // 至近での品質確認（線が細線として成立しているか）。実物どおりの40本/m。
                {
                    var striped = TeamColors.FromEntry(pin) with
                    {
                        Stripe = new TeamStripe(true, pin.Stripe.Color, 40f),
                    };
                    DuelStageRig.Live?.SetTeamColors(striped, TeamColors.FromEntry(plain));
                    var prev = DuelStageRig.CalibrationOverride;
                    var cal = DuelCalibration.Default;
                    cal.AimY = PitcherAimY; cal.AimZ = PitcherAimZ; cal.FovDeg = PitcherFov;
                    DuelStageRig.CalibrationOverride = cal;
                    DuelStageRig.Live?.ApplyCalibration(cal);
                    for (int i = 0; i < 12; i++) yield return null;
                    yield return Capture(FileName + "-near40");
                    DuelStageRig.CalibrationOverride = prev;
                    DuelStageRig.Live?.ApplyCalibration(prev ?? DuelCalibration.Default);
                }
            }
            Debug.Log("[ColorShot] DONE");
        }

        private IEnumerator Capture(string file)
        {
            EnsureOnlyMatchLive();
            yield return null;
            yield return new WaitForEndOfFrame();
            yield return null;

            var ctrl = FindController();
            var ps = ctrl.GetComponent<UIDocument>().panelSettings;
            var prevRt = ps.targetTexture; var prevScale = ps.scaleMode; var prevRef = ps.referenceResolution;
            var rt = new RenderTexture(W, H, 24, RenderTextureFormat.ARGB32); rt.Create();
            ps.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            ps.referenceResolution = new Vector2Int(W, H);
            ps.targetTexture = rt;

            yield return null; yield return new WaitForEndOfFrame();
            yield return null; yield return new WaitForEndOfFrame();

            Texture2D tex = null;
            try
            {
                tex = new Texture2D(W, H, TextureFormat.RGBA32, false);
                var prevActive = RenderTexture.active;
                RenderTexture.active = rt;
                tex.ReadPixels(new Rect(0, 0, W, H), 0, 0);
                tex.Apply();
                RenderTexture.active = prevActive;
                File.WriteAllBytes(Path.Combine(Dir, file + ".png"), tex.EncodeToPNG());
                Debug.Log("[ColorShot] wrote " + file);
            }
            finally
            {
                ps.targetTexture = prevRt; ps.scaleMode = prevScale; ps.referenceResolution = prevRef;
                rt.Release(); Destroy(rt);
                if (tex != null) Destroy(tex);
            }
        }

        private static void EnsureOnlyMatchLive()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                d.gameObject.SetActive(d.gameObject.name == "MatchLive");
            }
        }

        private static MatchLiveController FindController()
        {
            foreach (var d in Resources.FindObjectsOfTypeAll<UIDocument>())
            {
                if (!d.gameObject.scene.IsValid()) continue;
                if (d.gameObject.name == "MatchLive") return d.GetComponent<MatchLiveController>();
            }
            return null;
        }
    }
}
#endif
