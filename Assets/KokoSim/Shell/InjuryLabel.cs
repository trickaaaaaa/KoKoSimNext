// 怪我の表示文言（設計書03 §3.5: 傷病名・部位・段階・全治まで残り週は常に可視）。
// 傷病名は engine のカタログ（data/injuries.yaml 由来）が単一ソース。UI 側で日本語を持たない。
// UnityEngine 非依存（設計書06 §1）。
using System;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;

namespace KokoSim.Unity.Shell
{
    /// <summary>怪我の日本語ラベルを全画面で共通化する（故障者カード／選手一覧／詳細／スタメン設定）。</summary>
    public static class InjuryLabel
    {
        private static readonly InjuryCoefficients Coeff = new InjuryCoefficients();

        public static string Severity(InjurySeverity s)
        {
            switch (s)
            {
                case InjurySeverity.Minor: return "軽度";
                case InjurySeverity.Moderate: return "中度";
                case InjurySeverity.Severe: return "重度";
                default: return "";
            }
        }

        public static string Site(InjurySite s)
        {
            switch (s)
            {
                case InjurySite.Shoulder: return "肩";
                case InjurySite.Elbow: return "肘";
                case InjurySite.Back: return "腰";
                case InjurySite.Knee: return "膝";
                case InjurySite.Ankle: return "足首";
                default: return "手";
            }
        }

        /// <summary>傷病名（data/injuries.yaml 由来。種類なしの旧データは空文字）。</summary>
        public static string Type(InjuryType t) => InjuryCatalog.Default.DisplayName(t);

        /// <summary>「捻挫（足首）」。種類が無ければ「足首」だけ。</summary>
        public static string Diagnosis(InjuryType type, InjurySite site)
        {
            var name = Type(type);
            return name.Length > 0 ? name + "（" + Site(site) + "）" : Site(site);
        }

        /// <summary>
        /// 会話で言う形「足首の捻挫」（issue #695。マネージャーの一言に差し込む）。
        /// 種類が無ければ部位だけ（「足首の故障」）。括弧書きの <see cref="Diagnosis"/> は表・フィード用。
        /// </summary>
        public static string Spoken(InjuryType type, InjurySite site)
        {
            var name = Type(type);
            return Site(site) + "の" + (name.Length > 0 ? name : "故障");
        }

        /// <summary>「捻挫（足首）・中度」。健常なら空文字。</summary>
        public static string Short(DevelopingPlayer p)
            => p == null || p.Injury == InjurySeverity.None
                ? ""
                : Diagnosis(p.InjuryType, p.InjurySite) + "・" + Severity(p.Injury);

        /// <summary>「捻挫（足首）・中度／あと 4 週」。健常なら空文字。</summary>
        public static string Full(DevelopingPlayer p)
            => p == null || p.Injury == InjurySeverity.None
                ? ""
                : Short(p) + "／あと " + WeeksToFullRecovery(p) + " 週";

        /// <summary>完治までの残り週（規則は <see cref="InjuryModel.WeeksToFullRecovery"/> が単一ソース）。</summary>
        public static int WeeksToFullRecovery(DevelopingPlayer p)
            => p == null ? 0 : InjuryModel.WeeksToFullRecovery(p, Coeff);

        /// <summary>
        /// 怪我タグの中身（傷病名・段階・あと N 日／週）。健常なら空。スタメン設定と試合中の交代パネルが共有する
        /// （文言と残りの数え方をここ1箇所に置く）。大会中は日、それ以外は週。
        /// </summary>
        public static InjuryChipView Chip(DevelopingPlayer p)
        {
            if (p == null || p.Injury == InjurySeverity.None) return InjuryChipView.None;
            var gs = GameSession.Current;
            var (amount, unit) = Remaining(p, gs.InTournament, gs.TournamentDay);
            return new InjuryChipView
            {
                Head = Type(p.InjuryType) + "・" + Severity(p.Injury) + "／あと ",
                Digits = amount.ToString(),
                Unit = unit,
            };
        }

        /// <summary>試合中の選手（<see cref="Player.SourceId"/>）に対応する自校の部員の怪我タグ。部員でなければ・健常なら空。</summary>
        public static InjuryChipView ChipOf(int? sourceId)
        {
            if (sourceId is not { } id || id <= 0) return InjuryChipView.None;
            foreach (var p in RosterService.Roster)
                if (p.Id == id) return Chip(p);
            return InjuryChipView.None;
        }

        /// <summary>
        /// 完治までの残りを「単位つき」で返す。大会中（日送り）は日、それ以外（週送り）は週。
        /// 健常なら (0, "")。
        /// </summary>
        public static (int Amount, string Unit) Remaining(DevelopingPlayer p, bool inTournament, int tournamentDay)
        {
            if (p == null || p.Injury == InjurySeverity.None) return (0, "");
            var weeks = WeeksToFullRecovery(p);
            return inTournament
                ? (InjuryModel.DaysToFullRecovery(weeks, tournamentDay), "日")
                : (weeks, "週");
        }
    }

    /// <summary>怪我チップ（「捻挫・中度／あと」＋数字＋単位「日」「週」）。数字と和文は別 Label で組むため3つに分ける。</summary>
    public sealed class InjuryChipView
    {
        public static readonly InjuryChipView None = new InjuryChipView();
        public string Head = "";    // 「捻挫・中度／あと」
        public string Digits = "";  // 「3」
        public string Unit = "";    // 「日」（大会中）／「週」
        public bool IsEmpty => Head.Length == 0;
    }
}
