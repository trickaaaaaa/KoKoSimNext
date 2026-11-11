using System;
using System.Collections.Generic;

namespace KokoSim.Engine.Season;

/// <summary>
/// ゲーム開始時のロスターの上級生に、入学してから今日までの練習を済ませる（issue #865）。
///
/// <para>開始時のロスターは3学年とも <see cref="ProspectGenerator.Intake"/>＝<b>入学したての能力</b>で作られる。
/// 入学時の才能中心を下げて3年間の伸びを大きくした（#865）ので、そのままだと2・3年生が1年生と同じ弱さになり、
/// 開始直後の自校が格上に一方的にやられる（#578 で直した体験への逆戻り）。そこで2年生は1年分、
/// 3年生は2年分の練習を、施設なし・既定の指導力・お任せ練習（各選手の Plan があればそれ）で回してから渡す。</para>
///
/// <para>乱数は使わない（練習の伸びは決定論）＝生成の乱数列・氏名・背番号は従来と同じまま、能力だけが育つ。
/// 週ごとの調子・怪我・成長イベントは乱数を引くので回さない。チーム内スキル（練習リーダー等）の判定も入れない。</para>
/// </summary>
public static class StartingRoster
{
    /// <summary>各選手の <see cref="DevelopingPlayer.Grade"/> を見て、(学年−1) 年分の練習を適用する。1年生は何もしない。</summary>
    /// <param name="roster">学年が付いた開始時ロスター（その場で能力を書き換える）。</param>
    /// <param name="ctx">練習の係数・暦・成長段階。null＝既定（施設なし・基準指導力）＝実ゲームの開始時と同じ。</param>
    public static void PreTrain(IReadOnlyList<DevelopingPlayer> roster, SeasonContext? ctx = null)
    {
        if (roster is null) throw new ArgumentNullException(nameof(roster));
        ctx ??= new SeasonContext();
        var (facilityCoef, budget) = ctx.ResolveFacility();
        var training = ctx.Training with { FacilityCoef = facilityCoef };

        foreach (var p in roster)
        {
            var grade = p.Grade;
            var alloc = TrainingPresets.Resolve(p.Plan ?? TrainingPlan.Default, budget);
            try
            {
                // 過去の学年を順に回す（1年生の1年 → 2年生の1年）。成長段階・練習可否は当時の学年で引く。
                for (var past = 1; past < grade; past++)
                {
                    p.Grade = past;
                    for (var week = 0; week < ctx.Calendar.WeeksPerYear; week++)
                    {
                        if (!ctx.Calendar.CanTrain(past, week)) continue;
                        DevelopmentModel.TrainWeekPlan(
                            p, alloc, training.ReferenceWeekMinutes,
                            ctx.Calendar.StageIndex(past, week), ctx.Calendar.CampMultiplier(week, ctx.Training),
                            ctx.Stages, training, ctx.Skills, ctx.Personalities, ctx.Coaching, ctx.Equipment);
                    }
                }
            }
            finally
            {
                p.Grade = grade;
            }
        }
    }
}
