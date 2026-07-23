// ViewModel層（設計書06 §3.3 選手詳細、mock: 野球部監督GM standalone-src 「選手詳細」画面）。
// UnityEngine 非依存。一覧(PlayerListState)と同じ共有ロスター(RosterService)を参照し、安定 index で1名を開く。
// 成長履歴・公式戦成績・球種の物理計測値はエンジン未接続のためプレースホルダ（正直に空状態表示）。
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using KokoSim.Engine.Nation;
using KokoSim.Engine.Players;
using KokoSim.Engine.Season;
using KokoSim.Engine.Stats;
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
        public float Break01;         // 変化量 0〜1（扇の長さ）
        // ストレートは「変化量」ではなく「伸び」で読ませる（扇の長さは短尺固定・幅で伸びを表す）。
        public bool IsFastball;
        public float Extend01;        // 伸び 0〜1（ストレートのみ意味を持つ＝扇の幅）
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
        public string Name = "";
        public string Condition = "普通";
        public string ConditionColorHex = "#EFF4EA";
        // 表情顔（ConditionFace）の描画に使う enum（表示文字列は比較に使わない）。
        public KokoSim.Engine.Players.Condition ConditionLevel = KokoSim.Engine.Players.Condition.Normal;
        /// <summary>故障表示（設計書03 §3.5: 傷病名・部位・段階・全治まで残り週）。健常なら空文字。</summary>
        public string Injury = "";
        public string GradeLabel = "1年";
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

        // 成績（issue #77）: エンジン接続（PlayerStatStore を SourceId で引く）。スコープ3種＋大会別。
        public ScopeStats CareerStatsFull = new ScopeStats();    // 通算（練習試合含む）
        public ScopeStats OfficialStatsFull = new ScopeStats();  // 公式戦通算
        public List<TournamentStatRow> TournamentRows = new List<TournamentStatRow>(); // 大会別（学年×大会）
        public bool HasAnyStats;   // いずれかのスコープに1試合でも記録があるか
    }

    /// <summary>成績1指標（ラベル＋値。値は整形済み文字列）。</summary>
    public sealed class StatCell
    {
        public string Label = "";
        public string Value = "";
        public StatCell() { }
        public StatCell(string label, string value) { Label = label; Value = value; }
    }

    /// <summary>1スコープ分の打撃・投手フルライン（issue #77）。</summary>
    public sealed class ScopeStats
    {
        public bool HasBatting;
        public bool HasPitching;
        public List<StatCell> Batting = new List<StatCell>();
        public List<StatCell> Pitching = new List<StatCell>();
    }

    /// <summary>大会別の1行（学年×大会枠＋当時の背番号, issue #77）。</summary>
    public sealed class TournamentStatRow
    {
        public string Slot = "";      // 例「2年夏（県）」
        public string Number = "";    // 当時の背番号
        public bool HasPitching;
        public List<StatCell> Batting = new List<StatCell>();
        public List<StatCell> Pitching = new List<StatCell>();
    }

    /// <summary>
    /// 選手詳細の状態。純エンジンで生成した部員を再現し、安定 index で1名を詳細ビューに整形する。
    /// </summary>
    public sealed class PlayerDetailState
    {
        // 全画面で共有する単一ソースのロスター（主将フラグを含む可変状態を画面間で一致させる, RosterService）。
        private readonly IReadOnlyList<DevelopingPlayer> _roster;
        private readonly SeasonCalendar _calendar = new SeasonCalendar();

        // カテゴリ別ランクの重み（単一静的ソースを参照, Issue #30・#93 レーダー統一・#140 集約）。
        private static readonly TeamStrengthCoefficients Coeff = TeamStrengthCoeff.Default;

        public PlayerDetailState()
        {
            _roster = RosterService.Active;   // 主将は生成時に EnsureCaptain 済み（必ず1名）
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
            var condition = FormModel.Quantize(p.ConditionValue);
            var v = new PlayerDetailView
            {
                Number = (index + 1).ToString(),
                Name = p.Name,
                GradeLabel = p.Grade + "年",
                ThrowsBats = HandednessLabels.Combined(p.Throws, p.Bats),
                Condition = ConditionLabels.Jp(condition),
                ConditionLevel = condition,
                IsCaptain = p.IsCaptain,
            };
            v.ConditionColorHex = ConditionLabels.ColorHex(condition);
            // 故障（設計書03 §3.5: 常に可視）。文言は engine のカタログ由来（InjuryLabel が単一ソース）。
            v.Injury = KokoSim.Unity.Shell.InjuryLabel.Full(p);
            v.CanDesignateCaptain = CaptainSelector.CanDesignate(_roster, p, GameClock.Week, _calendar);
            v.DesignateReason = v.CanDesignateCaptain ? "" : DesignateReasonJp(p);

            var overall = (int)System.Math.Round(p.AverageLevel());
            v.OverallValue = overall;
            v.OverallGrade = Tiers.FromStrength(overall).ToString();

            // 投法・最速は全選手で表示する（誰を投手に据えるかはプレイヤーが決めるため、判断材料を全員分出す, Issue #93）。
            v.PitchStyle = "投法：オーバースロー"; // 投法はモデル未保持のため既定表記
            v.TopVelocityKmh = Kmh(p.Level(AbilityKind.Velocity));

            // 投手能力・野手能力（両方表示：モック準拠）。
            v.PitcherAbilities.Add(Bar(p, AbilityKind.Velocity, AbilityLabels.Jp(AbilityKind.Velocity)));
            v.PitcherAbilities.Add(Bar(p, AbilityKind.Control, AbilityLabels.Jp(AbilityKind.Control)));
            v.PitcherAbilities.Add(Bar(p, AbilityKind.Stamina, AbilityLabels.Jp(AbilityKind.Stamina)));
            v.PitcherAbilities.Add(Bar(p, AbilityKind.PitchRank, AbilityLabels.Jp(AbilityKind.PitchRank)));

            v.FielderAbilities.Add(Bar(p, AbilityKind.Contact, "ミート"));
            v.FielderAbilities.Add(Bar(p, AbilityKind.Power, "パワー"));
            v.FielderAbilities.Add(Bar(p, AbilityKind.LaunchTendency, "弾道"));
            v.FielderAbilities.Add(Bar(p, AbilityKind.Discipline, "選球眼"));
            v.FielderAbilities.Add(Bar(p, AbilityKind.Speed, "走力"));
            v.FielderAbilities.Add(Bar(p, AbilityKind.ArmStrength, "肩"));
            v.FielderAbilities.Add(Bar(p, AbilityKind.Fielding, "守備"));
            v.FielderAbilities.Add(Bar(p, AbilityKind.Catching, "捕球"));

            // 能力バランス（レーダー）: 全選手で #30 の4カテゴリ軸（打撃力/走力/守備力/投手力）＋精神に統一する。
            // 役割（投/野）でUIの軸を切り替えない（誰を投手にするかはプレイヤーが決める, Issue #93）。
            var strength = PlayerStrengthProfile.Compute(p, Coeff);
            v.Radar.Add(CategoryAxis("打撃力", strength.Batting));
            v.Radar.Add(CategoryAxis("走力", strength.Mobility));
            v.Radar.Add(CategoryAxis("守備力", strength.Defense));
            v.Radar.Add(CategoryAxis("投手力", strength.Pitching));
            v.Radar.Add(new RadarAxis { Label = "精神", Value01 = p.Mental / 100f });

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

            // 球種データ（全選手・表示近似）。誰を投手に据えるかはプレイヤーが決めるため、球種チャートは
            // 役割でゲートせず全員に出す。習得球種が無ければストレートだけを表示する（Issue #93）。
            {
                v.HasPitchData = true;
                var baseVel = p.Level(AbilityKind.Velocity);

                // ストレートは基準球。LearnedPitches に無ければ上方向に必ず追加する（未習得選手はこの1球だけになる）。
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
                        // ストレートは変化球ではない＝変化量は短尺固定。伸びは Extend01（扇の幅）で表す。
                        Break01 = FastballBreak01,
                        IsFastball = true,
                        Extend01 = ClampF(baseVel / 100f, 0f, 1f),
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
                    // 変化量: 球威+キレのオフセットから 0.42〜1.0。
                    // ストレートだけは変化球ではないので短尺固定にし、伸び（キレ）は扇の幅で表す。
                    var breakScore = lp.PowerOffset + lp.SharpnessOffset;
                    v.Pitches.Add(new PitchData
                    {
                        Name = PitchJp(lp.Type),
                        Kire = Tiers.FromStrength(kireScore).ToString(),
                        DirX = dir.x,
                        DirY = dir.y,
                        Break01 = isFast ? FastballBreak01 : ClampF(0.45f + breakScore / 46f, 0.42f, 1.0f),
                        IsFastball = isFast,
                        Extend01 = isFast ? ClampF((baseVel + lp.SharpnessOffset * 2) / 100f, 0f, 1f) : 0f,
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

            // 成績（issue #77）: PlayerStatStore を SourceId(=DevelopingPlayer.Id) で引いて接続。
            // 役割でUIを切り替えず打撃・投手の両方を全選手で出す（Issue #93）。記録が無ければ空状態を正直に表示。
            BuildStats(v, p.Id);
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

        // カテゴリ別ランク（0〜100 スケール）を 0〜1 のレーダー軸へ正規化する（Issue #93）。
        private static RadarAxis CategoryAxis(string label, double value)
            => new RadarAxis { Label = label, Value01 = System.Math.Max(0f, System.Math.Min(1f, (float)value / 100f)) };

        // ===== 成績（issue #77） =====

        private static readonly CultureInfo Ci = CultureInfo.InvariantCulture;
        // 打率・出塁率などの率（先頭0を落として .341 表記, 3桁）。
        private static string Rate3(double v) => v.ToString(".000", Ci);
        // 防御率・WHIP・K9（2桁小数）。
        private static string Dec2(double v) => v.ToString("0.00", Ci);
        private static string N(int v) => v.ToString(Ci);

        private void BuildStats(PlayerDetailView v, int sourceId)
        {
            var store = GameSession.Current.Stats;
            BuildScope(v.CareerStatsFull, sourceId > 0 ? store.Career.Get(sourceId) : null);
            BuildScope(v.OfficialStatsFull, sourceId > 0 ? store.Official.Get(sourceId) : null);
            if (sourceId > 0) BuildTournamentRows(v.TournamentRows, store.Archive, sourceId);

            v.HasAnyStats = v.CareerStatsFull.HasBatting || v.CareerStatsFull.HasPitching
                || v.OfficialStatsFull.HasBatting || v.OfficialStatsFull.HasPitching
                || v.TournamentRows.Count > 0;
        }

        private static void BuildScope(ScopeStats dst, PlayerStats stats)
        {
            if (stats == null) return;
            if (stats.Batting.Games > 0 || stats.Batting.PlateAppearances > 0)
            {
                dst.HasBatting = true;
                FillBatting(dst.Batting, stats.Batting);
            }
            if (stats.Pitching.Games > 0 || stats.Pitching.BattersFaced > 0)
            {
                dst.HasPitching = true;
                FillPitching(dst.Pitching, stats.Pitching);
            }
        }

        // 打撃フルライン（issue #77 の載せたい指標）。数値セルは右揃え表示（部品側で整える）。
        private static void FillBatting(List<StatCell> cells, BattingStatLine b)
        {
            cells.Add(new StatCell("試合", N(b.Games)));
            cells.Add(new StatCell("打率", Rate3(b.Average)));
            cells.Add(new StatCell("打数", N(b.AtBats)));
            cells.Add(new StatCell("安打", N(b.Hits)));
            cells.Add(new StatCell("二塁打", N(b.Doubles)));
            cells.Add(new StatCell("三塁打", N(b.Triples)));
            cells.Add(new StatCell("本塁打", N(b.HomeRuns)));
            cells.Add(new StatCell("打点", N(b.Rbi)));
            cells.Add(new StatCell("得点", N(b.Runs)));
            cells.Add(new StatCell("四球", N(b.Walks)));
            cells.Add(new StatCell("死球", N(b.HitByPitches)));
            cells.Add(new StatCell("三振", N(b.StrikeOuts)));
            cells.Add(new StatCell("盗塁", N(b.StolenBases)));
            cells.Add(new StatCell("盗塁率", (b.StolenBases + b.CaughtStealing) > 0 ? Rate3(b.StolenBaseRate) : "—"));
            cells.Add(new StatCell("出塁率", Rate3(b.Obp)));
            cells.Add(new StatCell("長打率", Rate3(b.Slg)));
            cells.Add(new StatCell("OPS", Rate3(b.Ops)));
        }

        // 投手フルライン（高校野球のため勝敗・セーブは載せない, issue #77）。
        private static void FillPitching(List<StatCell> cells, PitchingStatLine p)
        {
            cells.Add(new StatCell("登板", N(p.Games)));
            cells.Add(new StatCell("先発", N(p.GamesStarted)));
            cells.Add(new StatCell("防御率", Dec2(p.Era)));
            cells.Add(new StatCell("投球回", p.InningsText));
            cells.Add(new StatCell("被安打", N(p.Hits)));
            cells.Add(new StatCell("失点", N(p.Runs)));
            cells.Add(new StatCell("被本塁打", N(p.HomeRunsAllowed)));
            cells.Add(new StatCell("奪三振", N(p.StrikeOuts)));
            cells.Add(new StatCell("K/9", Dec2(p.KPer9)));
            cells.Add(new StatCell("与四球", N(p.Walks)));
            cells.Add(new StatCell("与死球", N(p.HitBatters)));
            cells.Add(new StatCell("球数", N(p.Pitches)));
            cells.Add(new StatCell("WHIP", Dec2(p.Whip)));
        }

        // 大会枠の並び順（学年内で春→夏県→夏甲子園→秋の暦順, issue #77）。
        private static int SlotOrder(TournamentSlot s) => s switch
        {
            TournamentSlot.Senbatsu => 0,
            TournamentSlot.SummerPref => 1,
            TournamentSlot.SummerKoshien => 2,
            TournamentSlot.Autumn => 3,
            _ => 9,
        };

        private static string SlotJp(TournamentSlot s) => s switch
        {
            TournamentSlot.SummerPref => "夏（県）",
            TournamentSlot.SummerKoshien => "夏（甲子園）",
            TournamentSlot.Autumn => "秋",
            TournamentSlot.Senbatsu => "春（センバツ）",
            _ => "",
        };

        // 大会別（学年×大会枠＋当時の背番号）を暦順に整形（issue #77）。
        private static void BuildTournamentRows(List<TournamentStatRow> rows, TournamentArchive archive, int sourceId)
        {
            var keys = archive.Keys
                .Where(k => archive.Get(k, sourceId) != null)
                .OrderBy(k => k.Grade).ThenBy(k => SlotOrder(k.Slot))
                .ToList();

            foreach (var key in keys)
            {
                var a = archive.Get(key, sourceId);
                if (a == null) continue;
                var row = new TournamentStatRow
                {
                    Slot = key.Grade + "年 " + SlotJp(key.Slot),
                    Number = a.UniformNumber > 0 ? a.UniformNumber.ToString(Ci) : "—",
                };
                if (a.Batting.Games > 0 || a.Batting.PlateAppearances > 0) FillBatting(row.Batting, a.Batting);
                if (a.Pitching.Games > 0 || a.Pitching.BattersFaced > 0)
                {
                    row.HasPitching = true;
                    FillPitching(row.Pitching, a.Pitching);
                }
                rows.Add(row);
            }
        }

        // 球速内部値(1〜100)→km/h。変換はエンジンの公開API（表示層→物理層の唯一の集約点, 不変条件#1）に一本化する。
        // UI側で式を再実装しない（旧独自式 118+Lv*0.42 は廃止, issue #94）。
        private static int Kmh(int velLevel)
            => (int)System.Math.Round(PitcherAttributes.VelocityKmhFromLevel(velLevel));

        // ストレートの扇の長さ（変化量軸では短尺固定。伸びは Extend01＝扇の幅で読ませる）。
        private const float FastballBreak01 = 0.15f;

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
