using System;
using System.Collections.Generic;
using System.Linq;
using KokoSim.Engine.Core;
using KokoSim.Engine.Match.Field;
using KokoSim.Engine.Match.Fielding;
using KokoSim.Engine.Match.Game;
using KokoSim.Engine.Players;

namespace KokoSim.Engine.Season;

/// <summary>
/// 育成選手ロスター(<see cref="DevelopingPlayer"/>)から試合用 <see cref="Team"/> を編成する。
/// 能力は表示層をそのまま Player 属性へ写像（設計書01: 二層構造は物理層で解決）。
/// 投手の球速のみ Level→km/h 変換が必要（StrengthTeamFactory と同式でキャリブレーション整合）。
/// 選抜は決定論（能力平均順）。乱数不要。
/// </summary>
public static class RosterTeamBuilder
{
    // 野手8守備位置（投手を除く）。StrengthTeamFactory.FieldSlots と同順。
    private static readonly FieldPosition[] FieldSlots =
    {
        FieldPosition.Catcher, FieldPosition.FirstBase, FieldPosition.SecondBase,
        FieldPosition.ThirdBase, FieldPosition.Shortstop, FieldPosition.LeftField,
        FieldPosition.CenterField, FieldPosition.RightField,
    };

    /// <summary>
    /// ロスターから打順9人（8守備＋投手9番）＋ブルペン2枚のチームを組む。
    /// tactics に采配ブレイン（プレイヤー手動・委任・敵AI ＝ 設計書09/11 の共通 ITacticsBrain）を渡すと
    /// フル采配の試合になる。null（既定）＝無指示で従来の挙動。集計モデル（StrengthTeamFactory）は常に null 維持。
    /// </summary>
    public static Team Build(
        IReadOnlyList<DevelopingPlayer> roster, string name, Match.Tactics.ITacticsBrain? tactics = null)
    {
        var all = roster.OrderByDescending(p => p.AverageLevel()).ToList();

        var pitchers = all.Where(p => p.IsPitcher).ToList();
        if (pitchers.Count == 0 && all.Count > 0) pitchers.Add(all[0]); // 投手不在なら最良を先発に

        var starter = pitchers.Count > 0 ? pitchers[0] : null;

        // 野手候補: 投手(先発)以外を能力順。不足時は残り選手で補完。
        var fielders = all.Where(p => !p.IsPitcher && !ReferenceEquals(p, starter)).ToList();
        foreach (var p in all)
        {
            if (fielders.Count >= 8) break;
            if (!ReferenceEquals(p, starter) && !fielders.Contains(p)) fielders.Add(p);
        }

        // 主将（設計書09 §8）: 在場判定は参照同一性なので、打順/投手に入れた同一 Player を Team.Captain へ差す。
        var captainDp = roster.FirstOrDefault(p => p.IsCaptain);
        Player? captainPlayer = null;
        void TrackCaptain(DevelopingPlayer? dp, Player pl)
        {
            if (captainDp != null && ReferenceEquals(dp, captainDp)) captainPlayer = pl;
        }

        var order = new List<Player>(9);
        for (var i = 0; i < FieldSlots.Length; i++)
        {
            var dp = i < fielders.Count ? fielders[i] : (starter ?? all.FirstOrDefault());
            var pl = ToPlayer(dp, FieldSlots[i], asPitcher: false);
            order.Add(pl);
            TrackCaptain(dp, pl);
        }
        var starterDp = starter ?? all.FirstOrDefault();
        var starterPlayer = ToPlayer(starterDp, FieldPosition.Pitcher, asPitcher: true);
        order.Add(starterPlayer);
        TrackCaptain(starterDp, starterPlayer);

        var bullpen = new List<Player>(2);
        foreach (var p in pitchers.Skip(1).Take(2))
        {
            var pl = ToPlayer(p, FieldPosition.Pitcher, asPitcher: true);
            bullpen.Add(pl);
            TrackCaptain(p, pl);
        }

        // 主将が試合に出ない（ベンチ）場合も、統率力のベンチ緩和を効かせるため単独投影して差す。
        if (captainDp != null && captainPlayer == null)
        {
            captainPlayer = ToPlayer(captainDp,
                captainDp.IsPitcher ? FieldPosition.Pitcher : FieldPosition.CenterField,
                asPitcher: captainDp.IsPitcher);
        }

        return new Team
        {
            Name = name, BattingOrder = order, PitcherSlot = 8, Bullpen = bullpen,
            Tactics = tactics, Captain = captainPlayer,
        };
    }

    /// <summary>
    /// ユーザーが決めたスタメン（<see cref="LineupSpec"/>）から試合用 <see cref="Team"/> を組む。
    /// 打順・守備位置・DH・先発は spec 通り。投影は <see cref="ToPlayer"/> を再利用（自動編成 Build と同じ物理層写像）。
    /// 主将は在場なら同一 Player 参照を Team.Captain へ、ベンチ在籍なら単独投影して差す（Build と同じ挙動）。
    /// </summary>
    public static Team BuildFromLineup(
        LineupSpec spec,
        InjuryCoefficients? injuryCoeff = null,
        FieldingCoefficients? fieldingCoeff = null,
        Players.PersonalityCoefficients? personalityCoeff = null)
    {
        if (spec.BattingOrder.Count != 9)
            throw new ArgumentException("打順はちょうど9人必要です。", nameof(spec));
        if (spec.DhSlot < -1 || spec.DhSlot > 8)
            throw new ArgumentException("DhSlot は -1〜8 の範囲です。", nameof(spec));
        if (spec.UsesDh && spec.StartingPitcher == null)
            throw new ArgumentException("DH制では先発投手（StartingPitcher）が必須です。", nameof(spec));
        if (!spec.UsesDh && (spec.PitcherSlot < 0 || spec.PitcherSlot > 8))
            throw new ArgumentException("PitcherSlot は 0〜8 の範囲です。", nameof(spec));

        // spec が参照する全 DevelopingPlayer（打順＋先発＋ブルペン＋ベンチ）。
        var referenced = spec.BattingOrder.Select(s => s.Player)
            .Concat(spec.StartingPitcher != null ? new[] { spec.StartingPitcher } : Array.Empty<DevelopingPlayer>())
            .Concat(spec.Bullpen ?? Array.Empty<DevelopingPlayer>())
            .Concat(spec.Bench ?? Array.Empty<DevelopingPlayer>())
            .ToList();

        // ベンチ入り検証（設計書06 §3.3b: 背番号1〜20＝ベンチ入り・0＝ベンチ外）。
        // 出場登録（スタメン／先発／ブルペン／ベンチ）はベンチ入り選手のみ。UIが防ぐが安全網としてここで弾く。
        var benchOut = referenced.Where(p => p.UniformNumber < 1).Select(p => p.Name).Distinct().ToList();
        if (benchOut.Count > 0)
            throw new ArgumentException(
                "ベンチ外（背番号なし）の選手は出場登録できません: " + string.Join("、", benchOut), nameof(spec));

        // 主将（設計書09 §8）: 参照選手から1名を探し、同一 Player 参照を Team.Captain へ差す。
        var captainDp = referenced.FirstOrDefault(p => p.IsCaptain);
        Player? captainPlayer = null;
        void TrackCaptain(DevelopingPlayer? dp, Player pl)
        {
            if (captainDp != null && ReferenceEquals(dp, captainDp)) captainPlayer = pl;
        }

        Player Project(DevelopingPlayer dp, FieldPosition pos, bool asPitcher)
            => ToPlayer(dp, pos, asPitcher, injuryCoeff, fieldingCoeff, personalityCoeff);

        var order = new List<Player>(9);
        for (var i = 0; i < 9; i++)
        {
            var slot = spec.BattingOrder[i];
            var asPitcher = !spec.UsesDh && i == spec.PitcherSlot;
            var pos = asPitcher ? FieldPosition.Pitcher : slot.Position; // DHスロットの守備位置は表示用（守備には就かない）
            var pl = Project(slot.Player, pos, asPitcher);
            order.Add(pl);
            TrackCaptain(slot.Player, pl);
        }

        Player? startingPitcher = null;
        if (spec.UsesDh)
        {
            startingPitcher = Project(spec.StartingPitcher!, FieldPosition.Pitcher, asPitcher: true);
            TrackCaptain(spec.StartingPitcher, startingPitcher);
        }

        // 守備網羅の検証（DefensiveAlignment が9ポジションを要求する。UIが防ぐが安全網として弾く）。
        var covered = new HashSet<FieldPosition>();
        for (var i = 0; i < 9; i++)
        {
            if (spec.UsesDh && i == spec.DhSlot) continue; // DHは守備に就かない
            covered.Add(order[i].Position);
        }
        if (spec.UsesDh) covered.Add(FieldPosition.Pitcher);
        if (covered.Count != 9)
            throw new ArgumentException("守備位置が9ポジションを一意に網羅していません。", nameof(spec));

        var bullpen = new List<Player>();
        foreach (var dp in spec.Bullpen ?? Array.Empty<DevelopingPlayer>())
        {
            var pl = Project(dp, FieldPosition.Pitcher, asPitcher: true);
            bullpen.Add(pl);
            TrackCaptain(dp, pl);
        }

        var bench = new List<Player>();
        foreach (var dp in spec.Bench ?? Array.Empty<DevelopingPlayer>())
        {
            var pl = Project(dp, dp.IsPitcher ? FieldPosition.Pitcher : FieldPosition.CenterField, asPitcher: dp.IsPitcher);
            bench.Add(pl);
            TrackCaptain(dp, pl);
        }

        // 主将がベンチ外/未出場なら単独投影（統率力のベンチ緩和を効かせるため）。
        if (captainDp != null && captainPlayer == null)
            captainPlayer = Project(captainDp,
                captainDp.IsPitcher ? FieldPosition.Pitcher : FieldPosition.CenterField, captainDp.IsPitcher);

        return new Team
        {
            Name = spec.Name,
            BattingOrder = order,
            PitcherSlot = spec.PitcherSlot,
            DhSlot = spec.DhSlot,
            StartingPitcher = startingPitcher,
            Bullpen = bullpen,
            Bench = bench,
            Tactics = spec.Tactics,
            Captain = captainPlayer,
        };
    }

    /// <summary>
    /// 育成選手→試合用 Player 投影。野手能力は直写像、投手能力は PitcherAttributes へ。
    /// 怪我中は段階に応じた一律係数で能力ダウン（設計書03 §3.5「痛みを押しての出場」）。
    /// </summary>
    public static Player ToPlayer(DevelopingPlayer? dp, FieldPosition pos, bool asPitcher,
        InjuryCoefficients? injuryCoeff = null, FieldingCoefficients? fieldingCoeff = null,
        Players.PersonalityCoefficients? personalityCoeff = null)
    {
        if (dp == null)
        {
            return new Player { Name = "選手", Position = pos, Pitching = asPitcher ? new PitcherAttributes() : null };
        }

        // 性格④（設計書01 §1.1）: タイプ→試合効果スカラーを投影時に解決し Player へ焼き込む（ホットパスでのテーブル引き回し回避）。
        var pProfile = (personalityCoeff ?? new Players.PersonalityCoefficients()).Profile(dp.Personality);

        // 怪我の一律係数（健常=1.0）。表示層で乗算してから物理層変換に通す（パイプライン不変）。
        // スランプ中（設計書03 §5.5）は追加の一時係数（怪我より軽く、期間で解ける）。
        var injF = InjuryModel.PerformanceFactor(dp.Injury, injuryCoeff ?? new InjuryCoefficients());
        if (dp.SlumpWeeks > 0) injF *= new GrowthEventCoefficients().SlumpPerformanceFactor;
        int L(AbilityKind k) => (int)MathUtil.Clamp(Math.Round(dp.Level(k) * injF), 1, 100);

        // 守備位置適性→実効守備力（設計書01 §1.1）。出場ポジションの適性で Fielding を補正（基準50で×1.0）。
        var aptF = (fieldingCoeff ?? new FieldingCoefficients()).AptitudeFactor(dp.Aptitude(pos));
        int Field() => (int)MathUtil.Clamp(Math.Round(dp.Level(AbilityKind.Fielding) * injF * aptF), 1, 100);

        PitcherAttributes? pitching = null;
        if (asPitcher)
        {
            var pitchRank = L(AbilityKind.PitchRank);
            pitching = new PitcherAttributes
            {
                // 球速Level→km/h は PitcherAttributes に一箇所集約（不変条件#1）。相手校生成と同一の式。
                MaxVelocityKmh = PitcherAttributes.VelocityKmhFromLevel(L(AbilityKind.Velocity)),
                Control = L(AbilityKind.Control),
                // スタミナ＝目安投球数（設計書02 §1.1e）。Level→球数の変換は一箇所に集約。
                StaminaPitches = PitcherAttributes.StaminaPitchesFromLevel(L(AbilityKind.Stamina)),
                PitchRank = pitchRank,
                Repertoire = BuildRepertoire(dp, pitchRank),
            };
        }

        return new Player
        {
            Name = dp.Name,
            SourceId = dp.Id,             // 成績集計の帰属キー（相手校の生成選手投影は Id 無し＝null のまま）
            UniformNumber = dp.UniformNumber,   // 背番号（0=ベンチ外/未割当・1〜=ベンチ入り）
            Grade = dp.Grade,                   // 学年（表示用・純データ）
            Position = pos,
            Throws = dp.Throws,
            Bats = dp.Bats,
            Contact = L(AbilityKind.Contact),
            Power = L(AbilityKind.Power),
            LaunchTendency = dp.Level(AbilityKind.LaunchTendency), // 弾道は型属性（怪我で変わらない）
            Discipline = L(AbilityKind.Discipline),
            Speed = L(AbilityKind.Speed),
            ArmStrength = L(AbilityKind.ArmStrength),
            ThrowAccuracy = L(AbilityKind.ThrowAccuracy),
            Fielding = Field(),
            Catching = L(AbilityKind.Catching),
            Bunt = L(AbilityKind.Bunt),
            Steal = L(AbilityKind.Steal),
            Baserunning = L(AbilityKind.Baserunning),
            Mental = dp.Mental,           // 精神系は怪我係数の対象外
            Leadership = dp.Leadership,
            Personality = dp.Personality,
            BuntSuccessBonus = pProfile.BuntSuccessBonus,
            ChanceHitFactor = pProfile.ChanceHitFactor,
            Lead = dp.Lead,               // 捕手リード（設計書01 §2①）。怪我係数の対象外
            Skills = dp.Skills,           // スキルはフラグなので怪我係数の対象外
            HasPitcherBackground = dp.HasPitcherBackground,
            Condition = FormModel.Quantize(dp.ConditionValue),
            Injury = dp.Injury,
            Pitching = pitching,
        };
    }

    /// <summary>
    /// 育成レイヤの習得球種→試合用レパートリー（設計書02 §2.2）。
    /// ストレート必修＋習得変化球（ランク＝PitchRankレベル＋個体オフセット。育成で全球種が底上げされる）。
    /// </summary>
    private static IReadOnlyList<PitchSlot> BuildRepertoire(DevelopingPlayer dp, int pitchRank)
    {
        var list = new List<PitchSlot>(1 + dp.LearnedPitches.Count)
        {
            PitchSlot.FastballOf(pitchRank),
        };
        foreach (var lp in dp.LearnedPitches)
        {
            list.Add(new PitchSlot
            {
                Type = lp.Type,
                Power = (int)MathUtil.Clamp(pitchRank + lp.PowerOffset, 1, 100),
                Sharpness = (int)MathUtil.Clamp(pitchRank + lp.SharpnessOffset, 1, 100),
            });
        }
        return list;
    }
}
