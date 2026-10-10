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
using KokoSim.Unity.Components;   // PitchChartDatum（球種チャート部品への詰め替え, issue #94）
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
        // 弾道など優劣のないタイプ軸（issue #219）: 非空ならバー＋ランクチップの代わりにこのラベルを表示する
        public string TypeLabel = "";
        // スタミナ＝目安投球数（issue #356）: 上限がないので 0〜100 バーを出さず「数値＋和文単位」で見せる。
        public bool HideBar;      // true: バーもランクチップも出さない
        public string Unit = "";  // 非空かつ HideBar のとき Value をこの単位付きで表示（例「135球」）
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
        public string ShortName = "";  // 略記（狭い器＝比較カードの小型チャート用, issue #560）
        public string Kire = "C";     // キレ（Sharpness→S〜G）＝チップ
        public float DirX;            // 変化方向（画面座標系: +x右 / +y下）
        public float DirY;
        public float Break01;         // 変化量 0〜1（扇の長さ）
        // ストレートは「変化量」ではなく「伸び」で読ませる（扇の長さは短尺固定・幅で伸びを表す）。
        public bool IsFastball;
        public float Extend01;        // 伸び 0〜1（ストレートのみ意味を持つ＝扇の幅）
        // 旧 Velo/Move/Rpm（Statcast風の参考値）は廃止＝どの画面にも出しておらず、engine を通らない飾りの式
        // （"18+(オフセット%30)cm" 等）を作るだけだった（2026-08-17）。

        /// <summary>球種チャート部品（部品辞書 PitchChartView）への詰め替え。ストレートには最速 km/h を添える
        /// （投手能力欄の球速と同じ値を渡して一致させる, issue #94/#130/#215）。他球種は VeloText を出さない。</summary>
        public static List<PitchChartDatum> ToPitchChartData(List<PitchData> pitches, int topVelocityKmh)
        {
            var list = new List<PitchChartDatum>();
            if (pitches == null) return list;
            foreach (var pt in pitches)
                list.Add(new PitchChartDatum
                {
                    Name = pt.Name,
                    ShortName = pt.ShortName,
                    Kire = pt.Kire,
                    DirX = pt.DirX,
                    DirY = pt.DirY,
                    Break01 = pt.Break01,
                    IsFastball = pt.IsFastball,
                    Extend01 = pt.Extend01,
                    VeloText = pt.IsFastball ? topVelocityKmh.ToString(CultureInfo.InvariantCulture) : null,
                });
            return list;
        }
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
        public string Number = "—";   // 背番号（未付与・選手なしは「—」。1 の既定はロスター先頭を騙るので使わない）
        public string Name = "";
        /// <summary>故障表示（設計書03 §3.5: 傷病名・部位・段階・全治まで残り週）。健常なら空文字。</summary>
        public string Injury = "";
        public string GradeLabel = "1年";
        public string ThrowsBats = "右投右打";
        public bool IsCaptain;
        /// <summary>この選手を今この週に主将へ指名できるか（設計書09 §8: 新チーム発足時のみ）。</summary>
        public bool CanDesignateCaptain;
        /// <summary>指名できない理由（できるときは空）。ボタン非活性時に添える。</summary>
        public string DesignateReason = "";
        public int TopVelocityKmh;
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
        /// <summary>球種別被打率（issue #180）。投げたことのある球種のみ載る。</summary>
        public List<PitchTypeStatRow> PitchTypeBattingAgainst = new List<PitchTypeStatRow>();
    }

    /// <summary>球種別被打1行（issue #180）。打席確定球（インプレー/被安打の球）だけを対象に集計。</summary>
    public sealed class PitchTypeStatRow
    {
        public string Label = "";
        public string AtBats = "";
        public string Hits = "";
        public string HomeRuns = "";
        public string Average = "";
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
            var v = new PlayerDetailView
            {
                // 背番号の単一ソースは DevelopingPlayer.UniformNumber（engine UniformNumberAssigner が採番・
                // メンバー設定で編集）。ロスターの並び順（index）は背番号と無関係なので使わない。
                // 0＝ベンチ外（未付与）は左一覧と同じく「—」（issue #249）。
                Number = p.UniformNumber > 0 ? p.UniformNumber.ToString(Ci) : "—",
                Name = p.Name,
                GradeLabel = p.Grade + "年",
                ThrowsBats = HandednessLabels.Combined(p.Throws, p.Bats),
                IsCaptain = p.IsCaptain,
            };
            // 故障（設計書03 §3.5: 常に可視）。文言は engine のカタログ由来（InjuryLabel が単一ソース）。
            v.Injury = KokoSim.Unity.Shell.InjuryLabel.Full(p);
            v.CanDesignateCaptain = CaptainSelector.CanDesignate(_roster, p, GameClock.Week, _calendar);
            v.DesignateReason = v.CanDesignateCaptain ? "" : DesignateReasonJp(p);

            var overall = (int)System.Math.Round(p.AverageLevel());
            v.OverallValue = overall;
            v.OverallGrade = Tiers.FromStrength(overall).ToString();

            // 最速（球種チャートのストレートに添える, issue #130）。ヘッダーの投法・最速メタ表記は廃止（issue #215）。
            v.TopVelocityKmh = Kmh(p.Level(AbilityKind.Velocity));

            // 投手能力・野手能力（両方表示：モック準拠）。
            // 球速はこの表に出さない＝すぐ下の持ち球チャートのストレートが km/h で出しているので、
            // 同じ値が同じカードに二度並んでいた（監督決定 2026-08-09, issue #560）。
            // 比較3画面（メンバー設定・スタメン設定・試合中の選手交代）も同じ扱いにしてある。
            v.PitcherAbilities.Add(Bar(p, AbilityKind.Control, AbilityLabels.Jp(AbilityKind.Control)));
            // 項目と並びはエンジンの単一ソース AbilityKinds.Pitching（球速/制球/スタミナ/キレ/球威）に合わせる。
            // スタミナは目安投球数でなく Level で出す＝比較パネル3画面と同じもの（監督決定 2026-08-09, issue #561）。
            v.PitcherAbilities.Add(Bar(p, AbilityKind.Stamina, AbilityLabels.Jp(AbilityKind.Stamina)));
            v.PitcherAbilities.Add(Bar(p, AbilityKind.PitchRank, AbilityLabels.Jp(AbilityKind.PitchRank)));
            // 球威（issue #274）はこの画面にだけ無かった＝新設時の取りこぼし（issue #561）。
            v.PitcherAbilities.Add(Bar(p, AbilityKind.Stuff, AbilityLabels.Jp(AbilityKind.Stuff)));

            v.FielderAbilities.Add(Bar(p, AbilityKind.LaunchTendency, "弾道"));
            v.FielderAbilities.Add(Bar(p, AbilityKind.Contact, "ミート"));
            v.FielderAbilities.Add(Bar(p, AbilityKind.Power, "パワー"));
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
            // 生成ロジックは比較UI（メンバー/スタメン設定のミニチャート）と共用するため静的ヘルパへ集約（issue #94）。
            v.HasPitchData = true;
            v.Pitches.AddRange(BuildPitchData(p));

            // 特殊能力（実データ。生成選手は未保有が多い＝空状態を正直に表示）。
            foreach (var sk in p.Skills.Visible)
            {
                v.Skills.Add(new SkillInfo { Name = SkillCatalogProvider.Name(sk), Desc = SkillCatalogProvider.Description(sk) });
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
            // 弾道はゴロ型〜フライ型のタイプ軸で優劣軸ではない（issue #219）。ランクチップ＋バーではなく
            // タイプラベルのみを表示する（内部値 v 自体は変えない）。
            if (k == AbilityKind.LaunchTendency)
            {
                return new AbilityBar { Label = label, Value = v, TypeLabel = LaunchTendencyLabels.Jp(v) };
            }
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
                FillPitchTypeBattingAgainst(dst.PitchTypeBattingAgainst, stats.Pitching);
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
            cells.Add(new StatCell("犠打", N(b.SacrificeHits)));   // 犠打＝打数に入らない送りバント（issue #638）
            cells.Add(new StatCell("犠飛", N(b.SacrificeFlies)));
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

        // 球種別被打率（issue #180）: 打席確定球がインプレー/被安打の打席のみ集計対象＝公式打数とは別概念。
        // 投げたことのある（＝1打数以上ある）球種のみ載せる。
        private static void FillPitchTypeBattingAgainst(List<PitchTypeStatRow> rows, PitchingStatLine p)
        {
            foreach (PitchType t in System.Enum.GetValues(typeof(PitchType)))
            {
                if (!p.BattingAgainstByPitch.TryGetValue(t, out var line) || line.AtBats == 0) continue;
                rows.Add(new PitchTypeStatRow
                {
                    Label = PitchTypeLabels.Jp(t),
                    AtBats = N(line.AtBats),
                    Hits = N(line.Hits),
                    HomeRuns = N(line.HomeRuns),
                    Average = Rate3(line.Average),
                });
            }
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

        /// <summary>選手の持ち球（LearnedPitches）→球種チャート用 PitchData 一覧。
        /// 詳細画面と比較UI（メンバー/スタメン設定のミニチャート）で同じ絵を出すための単一ソース（issue #94/#93）。
        /// ストレートは基準球として必ず含める（未習得選手はこの1球だけ）。
        /// <para>球種ごとの値は engine の試合用レパートリー <see cref="RosterTeamBuilder.BuildRepertoire"/> をそのまま
        /// 読む＝ストレートは球威(Stuff)、変化球は「キレ(PitchRank)＋個体オフセット」（design-02 §2.2, Issue #274）。
        /// 旧実装は変化球チップを「50＋オフセット×2」で出しており、能力表のキレがEでもチェンジアップにSが付いた
        /// （育成でキレを上げてもチャートが動かない）。UI 側で式を再実装しない（不変条件#1, 2026-08-17）。</para></summary>
        public static List<PitchData> BuildPitchData(DevelopingPlayer p)
        {
            var list = new List<PitchData>();
            // 能力表（Bar(p, PitchRank/Stuff)）と同じ素のレベルを渡す＝チップと能力表の等級が一致する。
            // 試合用（RosterTeamBuilder 内部）は怪我補正込みだが、表示は能力表に合わせる。
            var repertoire = RosterTeamBuilder.BuildRepertoire(
                p, p.Level(AbilityKind.PitchRank), p.Level(AbilityKind.Stuff));

            foreach (var slot in repertoire)
            {
                var isFast = slot.Type == PitchType.Fastball;
                // 有効軸（PitchSlot.Rank）: ストレート=球威(Power)、変化球=キレ(Sharpness)。
                var rank = slot.Rank;
                var dir = PitchDir(slot.Type);
                list.Add(new PitchData
                {
                    Name = PitchTypeLabels.Jp(slot.Type),
                    ShortName = PitchTypeLabels.Abbr(slot.Type),
                    Kire = Tiers.FromStrength(rank).ToString(),
                    DirX = dir.x,
                    DirY = dir.y,
                    // 扇の長さ＝変化量。実効キレ（1〜100）を扇の可動域へ線形に写す＝チップの等級と長さが常に同じ向きに動く。
                    // ストレートは短尺固定（伸びは Extend01＝扇の幅で表す）。
                    Break01 = isFast ? FastballBreak01 : BreakingBreak01(rank),
                    IsFastball = isFast,
                    Extend01 = isFast ? ClampF(rank / 100f, 0f, 1f) : 0f,
                });
            }
            return list;
        }

        // 変化球の扇の長さ。実効キレ 1〜100 → 0.42〜1.0（旧実装と同じ可動域を保つ＝絵の骨格は変えない）。
        private static float BreakingBreak01(int sharpness)
            => ClampF(0.42f + 0.58f * (sharpness / 100f), 0.42f, 1.0f);

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

        private static float ClampF(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }

    /// <summary>一覧→詳細の選手選択を受け渡す（安定 index）。</summary>
    public static class PlayerSelection
    {
        public static int Index;
    }
}
