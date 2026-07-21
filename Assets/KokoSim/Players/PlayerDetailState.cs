// ViewModel層（設計書06 §3.3 選手詳細、mock: 野球部監督GM standalone-src 「選手詳細」画面）。
// UnityEngine 非依存。一覧(PlayerListState)と同じ共有ロスター(RosterService)を参照し、安定 index で1名を開く。
// 成長履歴・公式戦成績・球種の物理計測値はエンジン未接続のためプレースホルダ（正直に空状態表示）。
using System.Collections.Generic;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using KokoSim.Unity.Shell;

namespace KokoSim.Unity.Players
{
    /// <summary>能力1行（ラベル・内部値・等級・バー割合0〜1）。バー色は等級から部品側で決まる（RankPalette）。</summary>
    public sealed class AbilityBar
    {
        public string Label = "";
        public int Value;
        public string Grade = "D";
        public float Pct;
    }

    /// <summary>隠しパラメータ1項（未判明なら Known=false で「？」）。</summary>
    public sealed class HiddenParam
    {
        public string Key = "";
        public string Value = "";
        public bool Known;
    }

    /// <summary>球種1つ（プロスピ風 変化チャート用。中心=ボール、方向=変化方向、距離=変化量、チップ=キレ）。</summary>
    public sealed class PitchData
    {
        public string Name = "";
        public string Kire = "C";     // キレ（Sharpness→S〜G）＝チップ
        public float DirX;            // 変化方向（画面座標系: +x右 / +y下）
        public float DirY;
        public float Break01;         // 変化量 0〜1（中心からの距離）
        // 参考値（Statcast風・表示近似）。
        public int Velo;
        public string Move = "";
        public int Rpm;
    }

    public sealed class SkillInfo
    {
        public string Name = "";
        public string Desc = "";
    }

    public sealed class RadarAxis
    {
        public string Label = "";
        public float Value01;

        /// <summary>軸ラベル脇に出す数値（空＝数値を出さない）。隣に並ぶ指標バーと桁を一致させるため
        /// 呼び出し側が整形済みの文字列を渡す（RadarChartView が Value01 から導かない＝丸め差を作らない）。</summary>
        public string ValueText = "";
    }

    /// <summary>選手詳細に表示する一式（スナップショット）。</summary>
    public sealed class PlayerDetailView
    {
        public string Number = "1";
        public string RoleLabel = "野手";
        public string Name = "";
        public string Condition = "普通";
        public string ConditionColorHex = "#EFF4EA";
        public string GradeLabel = "1年";
        public string PosParen = "野手";
        public string ThrowsBats = "右投右打";
        public bool IsCaptain;
        /// <summary>この選手を今この週に主将へ指名できるか（設計書09 §8: 新チーム発足時のみ）。</summary>
        public bool CanDesignateCaptain;
        /// <summary>指名できない理由（できるときは空）。ボタン非活性時に添える。</summary>
        public string DesignateReason = "";
        public string PitchStyle = "";
        public int TopVelocityKmh;
        public bool IsPitcher;
        public string OverallGrade = "D";
        public int OverallValue;

        public List<AbilityBar> PitcherAbilities = new List<AbilityBar>();
        public List<AbilityBar> FielderAbilities = new List<AbilityBar>();
        public List<RadarAxis> Radar = new List<RadarAxis>();
        public List<HiddenParam> Hidden = new List<HiddenParam>();
        public List<PitchData> Pitches = new List<PitchData>();
        public List<SkillInfo> Skills = new List<SkillInfo>();
        public bool HasPitchData;
        public bool HasSkills;

        // 簡易成績（公式戦データ未接続＝プレースホルダ）。
        public string TournamentLabel = "今大会（未接続）";
        public List<(string Label, string Value)> TournamentStats = new List<(string, string)>();
        public List<(string Label, string Value)> CareerStats = new List<(string, string)>();
    }

    /// <summary>
    /// 選手詳細の状態。純エンジンで生成した部員を再現し、安定 index で1名を詳細ビューに整形する。
    /// </summary>
    public sealed class PlayerDetailState
    {
        // 全画面で共有する単一ソースのロスター（主将フラグを含む可変状態を画面間で一致させる, RosterService）。
        private readonly IReadOnlyList<DevelopingPlayer> _roster;
        private readonly SeasonCalendar _calendar = new SeasonCalendar();

        public PlayerDetailState()
        {
            _roster = RosterService.Roster;   // 主将は生成時に EnsureCaptain 済み（必ず1名）
        }

        public int Count => _roster.Count;

        /// <summary>
        /// 主将を手動指名する（設計書09 §8）。UIの「主将に指名」から呼ぶ。
        /// 指名できるのは夏の3年引退後＝新チーム発足時の候補（新最上級生）だけ。可否は engine 側の純関数で判定する。
        /// </summary>
        /// <returns>指名できたら true（false なら状態は変えていない）。</returns>
        public bool DesignateCaptain(int index)
        {
            if (_roster.Count == 0) return false;
            index = System.Math.Max(0, System.Math.Min(index, _roster.Count - 1));
            var p = _roster[index];
            if (!CaptainSelector.CanDesignate(_roster, p, GameClock.Week, _calendar)) return false;
            CaptainSelector.Designate(_roster, p);
            return true;
        }

        public PlayerDetailView BuildView(int index)
        {
            if (_roster.Count == 0) return new PlayerDetailView();
            index = System.Math.Max(0, System.Math.Min(index, _roster.Count - 1));
            var p = _roster[index];
            var v = new PlayerDetailView
            {
                Number = (index + 1).ToString(),
                Name = p.Name,
                IsPitcher = p.IsPitcher,
                GradeLabel = p.Grade + "年",
                ThrowsBats = ThrowsBatsJp(p.Throws, p.Bats),
                Condition = ConditionJp(p.ConditionValue),
                IsCaptain = p.IsCaptain,
            };
            v.ConditionColorHex = CondColor(v.Condition);
            v.CanDesignateCaptain = CaptainSelector.CanDesignate(_roster, p, GameClock.Week, _calendar);
            v.DesignateReason = v.CanDesignateCaptain ? "" : DesignateReasonJp(p);

            var overall = (int)System.Math.Round(p.AverageLevel());
            v.OverallValue = overall;
            v.OverallGrade = Tiers.FromStrength(overall).ToString();

            if (p.IsPitcher)
            {
                v.RoleLabel = "投手";
                v.PosParen = "投手（P）";
                v.PitchStyle = "投法：オーバースロー"; // 投法はモデル未保持のため既定表記
                v.TopVelocityKmh = Kmh(p.Level(AbilityKind.Velocity));
            }
            else
            {
                v.RoleLabel = "野手";
                v.PosParen = "野手";
            }

            // 投手能力・野手能力（両方表示：モック準拠）。
            v.PitcherAbilities.Add(Bar(p, AbilityKind.Velocity, "球速"));
            v.PitcherAbilities.Add(Bar(p, AbilityKind.Control, "制球"));
            v.PitcherAbilities.Add(Bar(p, AbilityKind.Stamina, "スタミナ"));
            v.PitcherAbilities.Add(Bar(p, AbilityKind.PitchRank, "球種"));

            v.FielderAbilities.Add(Bar(p, AbilityKind.Contact, "ミート"));
            v.FielderAbilities.Add(Bar(p, AbilityKind.Power, "パワー"));
            v.FielderAbilities.Add(Bar(p, AbilityKind.LaunchTendency, "弾道"));
            v.FielderAbilities.Add(Bar(p, AbilityKind.Discipline, "選球眼"));
            v.FielderAbilities.Add(Bar(p, AbilityKind.Speed, "走力"));
            v.FielderAbilities.Add(Bar(p, AbilityKind.ArmStrength, "肩"));
            v.FielderAbilities.Add(Bar(p, AbilityKind.Fielding, "守備"));
            v.FielderAbilities.Add(Bar(p, AbilityKind.Catching, "捕球"));

            // レーダー（現在能力から実描画）。
            if (p.IsPitcher)
            {
                v.Radar.Add(Axis(p, AbilityKind.Velocity, "球速"));
                v.Radar.Add(Axis(p, AbilityKind.Control, "制球"));
                v.Radar.Add(Axis(p, AbilityKind.Stamina, "スタミナ"));
                v.Radar.Add(Axis(p, AbilityKind.PitchRank, "球種"));
                v.Radar.Add(new RadarAxis { Label = "精神", Value01 = p.Mental / 100f });
            }
            else
            {
                v.Radar.Add(Axis(p, AbilityKind.Contact, "ミート"));
                v.Radar.Add(Axis(p, AbilityKind.Power, "パワー"));
                v.Radar.Add(Axis(p, AbilityKind.Speed, "走力"));
                v.Radar.Add(Axis(p, AbilityKind.ArmStrength, "肩"));
                v.Radar.Add(Axis(p, AbilityKind.Fielding, "守備"));
                v.Radar.Add(Axis(p, AbilityKind.Discipline, "選球眼"));
            }

            // 隠しパラメータ（推定）: 精神力のみ判明扱い、他はスカウト/面談で段階判明＝？。
            v.Hidden.Add(new HiddenParam { Key = "才能上限", Value = "？", Known = false });
            v.Hidden.Add(new HiddenParam { Key = "成長タイプ", Value = "？", Known = false });
            // 性格（設計書01 §1.1）: 一部は隠し。ここでは起用・面談が進む上級生ほど判明する近似
            // （2年以上＝判明でタイプ名、1年＝？）。実接続時は Insight（気づき）で段階開示。
            v.Hidden.Add(new HiddenParam
            {
                Key = "性格",
                Value = Personalities.DisplayName(p.Personality),
                Known = p.Grade >= 2,
            });
            v.Hidden.Add(new HiddenParam { Key = "精神力", Value = p.Mental.ToString(), Known = true });
            v.Hidden.Add(new HiddenParam { Key = "統率傾向", Value = p.Leadership.ToString(), Known = p.IsCaptain });
            v.Hidden.Add(new HiddenParam { Key = "故障耐性", Value = "？", Known = false });

            // 球種データ（投手のみ・表示近似）。
            if (p.IsPitcher)
            {
                v.HasPitchData = true;
                var baseVel = p.Level(AbilityKind.Velocity);

                // ストレートは全投手が投げる基準球。LearnedPitches に無ければ上方向に必ず追加する。
                var hasFast = false;
                foreach (var lp in p.LearnedPitches) if (lp.Type == PitchType.Fastball) hasFast = true;
                if (!hasFast)
                {
                    var df = PitchDir(PitchType.Fastball);
                    v.Pitches.Add(new PitchData
                    {
                        Name = "ストレート",
                        Kire = Tiers.FromStrength(baseVel).ToString(),   // 球速＝ストレートのキレ相当
                        DirX = df.x,
                        DirY = df.y,
                        Break01 = ClampF(0.5f + baseVel / 200f, 0.5f, 1.0f),
                        Velo = Kmh(baseVel),
                        Move = "—",
                        Rpm = 2000 + baseVel * 4,
                    });
                }

                foreach (var lp in p.LearnedPitches)
                {
                    var isFast = lp.Type == PitchType.Fastball;
                    var velo = isFast ? Kmh(baseVel) : Kmh(baseVel) - 8 - (lp.SharpnessOffset % 20);
                    var kireScore = Clamp(50 + lp.SharpnessOffset * 2, 1, 100);
                    var dir = PitchDir(lp.Type);
                    // 変化量: 球威+キレのオフセットから 0.42〜1.0。ストレートは伸び（キレ）主体。
                    var breakScore = isFast ? lp.SharpnessOffset : (lp.PowerOffset + lp.SharpnessOffset);
                    v.Pitches.Add(new PitchData
                    {
                        Name = PitchJp(lp.Type),
                        Kire = Tiers.FromStrength(kireScore).ToString(),
                        DirX = dir.x,
                        DirY = dir.y,
                        Break01 = ClampF(0.45f + breakScore / 46f, 0.42f, 1.0f),
                        Velo = velo,
                        Move = isFast ? "—" : (18 + (lp.SharpnessOffset % 30)) + "cm",
                        Rpm = 1800 + lp.PowerOffset * 6 + (isFast ? 400 : 0),
                    });
                }
            }

            // 特殊能力（実データ。生成選手は未保有が多い＝空状態を正直に表示）。
            foreach (var sk in p.Skills.Visible)
            {
                v.Skills.Add(new SkillInfo { Name = SkillJp(sk), Desc = SkillDesc(sk) });
            }
            v.HasSkills = v.Skills.Count > 0;

            // 簡易成績（公式戦ログ未接続＝プレースホルダ）。
            if (p.IsPitcher)
            {
                v.TournamentStats.Add(("防御率", "—"));
                v.TournamentStats.Add(("勝敗", "—"));
                v.TournamentStats.Add(("奪三振", "—"));
                v.CareerStats.Add(("防御率", "—"));
                v.CareerStats.Add(("勝敗", "—"));
                v.CareerStats.Add(("奪三振", "—"));
            }
            else
            {
                v.TournamentStats.Add(("打率", "—"));
                v.TournamentStats.Add(("本塁打", "—"));
                v.TournamentStats.Add(("打点", "—"));
                v.CareerStats.Add(("打率", "—"));
                v.CareerStats.Add(("本塁打", "—"));
                v.CareerStats.Add(("打点", "—"));
            }
            return v;
        }

        // ===== 補助 =====

        /// <summary>指名できない理由（設計書09 §8）。期間外か、候補（新最上級生）でないかを区別して伝える。</summary>
        private string DesignateReasonJp(DevelopingPlayer p)
        {
            if (!CaptainSelector.IsDesignationWindow(GameClock.Week, _calendar))
                return "主将は夏の3年引退後（新チーム発足時）に指名できます";
            return "主将は新チームの最上級生から指名します";
        }

        private static AbilityBar Bar(DevelopingPlayer p, AbilityKind k, string label)
        {
            var v = p.Level(k);
            return new AbilityBar
            {
                Label = label,
                Value = v,
                Grade = Tiers.FromStrength(v).ToString(),
                Pct = System.Math.Max(0f, System.Math.Min(1f, v / 100f)),
            };
        }

        private static RadarAxis Axis(DevelopingPlayer p, AbilityKind k, string label)
            => new RadarAxis { Label = label, Value01 = System.Math.Max(0f, System.Math.Min(1f, p.Level(k) / 100f)) };

        // 球速内部値(1〜100)→km/h（表示近似。実シムの物理変換はエンジン係数側）。
        private static int Kmh(int velLevel)
            => (int)System.Math.Round(118 + velLevel * 0.42);

        private static string ThrowsBatsJp(Handedness throws, Handedness bats)
        {
            string T = throws == Handedness.Left ? "左投" : "右投";
            string B = bats == Handedness.Left ? "左打" : bats == Handedness.Switch ? "両打" : "右打";
            return T + B;
        }

        // 調子（内部連続値→5段階）を日本語表示（設計書02 §3.3。正ソースは FormModel）。
        private static string ConditionJp(double conditionValue)
        {
            switch (KokoSim.Engine.Players.FormModel.Quantize(conditionValue))
            {
                case KokoSim.Engine.Players.Condition.Excellent: return "絶好調";
                case KokoSim.Engine.Players.Condition.Good: return "好調";
                case KokoSim.Engine.Players.Condition.Poor: return "不調";
                case KokoSim.Engine.Players.Condition.Terrible: return "絶不調";
                default: return "普通";
            }
        }

        private static string CondColor(string c)
        {
            switch (c)
            {
                case "絶好調": return "#F5C64A";
                case "好調": return "#9FCB3B";
                case "不調": return "#E86A4A";
                case "絶不調": return "#E86A4A";
                default: return "#EFF4EA";
            }
        }

        // 球種ごとの変化方向（画面座標系: +x右 / +y下。上=伸び）。プロスピ風チャートの配置。
        private static (float x, float y) PitchDir(PitchType t)
        {
            switch (t)
            {
                case PitchType.Fastball: return (0f, -1f);       // 上（伸び）
                case PitchType.Cutter:   return (-0.5f, -0.35f); // 左上（小）
                case PitchType.Slider:   return (-1f, 0.15f);    // 左
                case PitchType.Curve:    return (-0.6f, 0.75f);  // 左下
                case PitchType.Fork:     return (0f, 1f);        // 下（落ち）
                case PitchType.Changeup: return (0.55f, 0.6f);   // 右下
                case PitchType.Sinker:   return (0.45f, 0.9f);   // 右下（大きく落ち）
                case PitchType.Shuuto:   return (1f, 0.15f);     // 右
                case PitchType.TwoSeam:  return (0.7f, 0.5f);    // 右下（小）
                default:                 return (0f, -1f);
            }
        }

        private static int Clamp(int v, int lo, int hi) => v < lo ? lo : v > hi ? hi : v;
        private static float ClampF(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;

        private static string PitchJp(PitchType t)
        {
            switch (t)
            {
                case PitchType.Fastball: return "ストレート";
                case PitchType.TwoSeam: return "ツーシーム";
                case PitchType.Cutter: return "カットボール";
                case PitchType.Slider: return "スライダー";
                case PitchType.Curve: return "カーブ";
                case PitchType.Fork: return "フォーク";
                case PitchType.Changeup: return "チェンジアップ";
                case PitchType.Shuuto: return "シュート";
                case PitchType.Sinker: return "シンカー";
                default: return t.ToString();
            }
        }

        private static string SkillJp(Skill s)
        {
            switch (s)
            {
                case Skill.SlowStarterBat: return "スロースターター（打）";
                case Skill.Streaky: return "ムラっ気";
                case Skill.SprayHitter: return "広角打法";
                case Skill.FirstPitchSwinger: return "初球打ち";
                case Skill.Grinder: return "粘り打ち";
                case Skill.SlowStarterPitch: return "スロースターター（投）";
                case Skill.SecondTimeThrough: return "二巡目注意";
                case Skill.EffectivelyWild: return "荒れ球";
                case Skill.DeceptiveBall: return "打たれ強い球質";
                case Skill.DoublePlayArtist: return "併殺職人";
                case Skill.MasterCatcher: return "扇の要";
                case Skill.Moodmaker: return "ムードメーカー";
                case Skill.SpiritualPillar: return "精神的支柱";
                case Skill.PracticeLeader: return "練習リーダー";
                case Skill.RoleModel: return "手本";
                case Skill.Diligent: return "勤勉";
                case Skill.Lazy: return "怠け癖";
                case Skill.Durable: return "頑健";
                case Skill.InjuryProne: return "故障持ち";
                case Skill.Monster: return "怪物";
                case Skill.SubmarineMastery: return "アンダースロー";
                default: return s.ToString();
            }
        }

        private static string SkillDesc(Skill s)
        {
            switch (s)
            {
                case Skill.SlowStarterBat: return "序盤の打席は本調子でない。回を追うごとにミートが上がる。";
                case Skill.Streaky: return "好不調の波が大きく、成績の分散が広い。";
                case Skill.SprayHitter: return "逆方向にも強い打球を飛ばせる。";
                case Skill.FirstPitchSwinger: return "初球から積極的に振っていく。";
                case Skill.Grinder: return "ファウルで粘り、四球・失投を引き出す。";
                case Skill.SlowStarterPitch: return "立ち上がりが不安定。回を追うごとに制球が安定。";
                case Skill.SecondTimeThrough: return "打線二巡目以降、球威・制球が落ちやすい。";
                case Skill.EffectivelyWild: return "制球は甘いが的を絞らせない球威。";
                case Skill.DeceptiveBall: return "見た目より打ちにくい球質。長打を抑える。";
                case Skill.DoublePlayArtist: return "併殺を取る守備勘に優れる。";
                case Skill.MasterCatcher: return "リード・盗塁阻止に長けた捕手。";
                case Skill.Moodmaker: return "チームの士気を明るく保つ。";
                case Skill.SpiritualPillar: return "主将適性。終盤・接戦での崩れに強い。";
                case Skill.PracticeLeader: return "周囲の練習効率を底上げする。";
                case Skill.RoleModel: return "下級生の手本となり成長を促す。";
                case Skill.Diligent: return "練習の伸びが大きい。";
                case Skill.Lazy: return "練習の伸びが小さい。";
                case Skill.Durable: return "故障しにくい。";
                case Skill.InjuryProne: return "故障しやすい。";
                case Skill.Monster: return "全能力に補正がかかる規格外の逸材。";
                case Skill.SubmarineMastery: return "アンダースローで独特の軌道を投げる。";
                default: return "";
            }
        }
    }

    /// <summary>一覧→詳細の選手選択を受け渡す（安定 index）。</summary>
    public static class PlayerSelection
    {
        public static int Index;
    }
}
