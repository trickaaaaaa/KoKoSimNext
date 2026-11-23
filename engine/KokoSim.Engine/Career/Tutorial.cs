using System;
using System.Collections.Generic;
using KokoSim.Engine.Season;

namespace KokoSim.Engine.Career;

/// <summary>
/// チュートリアルの場面（issue #1496・design-04 §3.4）。「はじめから」で始めた進行だけに、
/// その場面が<b>初めて</b>来たときマネージャーが一言ずつ進め方を教える。
/// 名前はセーブに文字列で載る（<see cref="Saves.SaveCodec"/>）＝改名しない。
/// </summary>
public enum TutorialScene
{
    /// <summary>赴任イベントの直後: 週を送るとゲームが進む／練習メニューの決め方。</summary>
    Basics,
    /// <summary>初めての公式戦の前（大会の開幕）: 大会の流れ・ベンチ入りとスタメンの決め方。</summary>
    FirstTournament,
    /// <summary>
    /// 初めての試合の前: サイン・伝令・委任采配の使い方。<b>公式戦・練習試合を問わず最初の1試合</b>で出す
    /// （公式戦は試合の日の開始確認の前、練習試合は申し込みが成立してスタメン設定へ進む前。2026-11-23 オーナー決定）。
    /// </summary>
    FirstMatch,
    /// <summary>初めてのスカウトの時期（夏の後の契約の会話の前）: スカウトと新入生。</summary>
    Scouting,
    /// <summary>初めて部費で何か買えるようになった週: 部費と施設・道具。</summary>
    Purchases,
    /// <summary>初めての夏の引退（引退の会話の後・主将指名の前）: 3年の引退と新チーム・主将。</summary>
    SummerRetirement,
}

/// <summary>
/// チュートリアルの進み具合（issue #1496）。見た場面の集合と「もう説明はいらない」を持ち、セーブに載る（v42）。
/// 乱数は使わない＝試合結果の指紋に影響しない。
/// </summary>
public sealed class TutorialProgress
{
    private readonly HashSet<TutorialScene> _seen = new();

    /// <summary>「もう説明はいらない」を選んだ＝残りの説明も出さない。</summary>
    public bool Dismissed { get; private set; }

    /// <summary>見た場面（列挙の順）。</summary>
    public IReadOnlyList<TutorialScene> Seen
    {
        get
        {
            var list = new List<TutorialScene>();
            foreach (TutorialScene s in Enum.GetValues(typeof(TutorialScene)))
                if (_seen.Contains(s)) list.Add(s);
            return list;
        }
    }

    /// <summary>何も見ていない（はじめから）。</summary>
    public static TutorialProgress Fresh() => new();

    /// <summary>
    /// 全部見た扱い（v41 以前のセーブ）。途中から遊んでいる人に急に説明を出さない。
    /// 後から場面が増えても出ないよう「もう説明はいらない」も立てる。
    /// </summary>
    public static TutorialProgress AllSeen()
    {
        var p = new TutorialProgress();
        foreach (TutorialScene s in Enum.GetValues(typeof(TutorialScene))) p._seen.Add(s);
        p.Dismissed = true;
        return p;
    }

    /// <summary>セーブからの復元用。</summary>
    public static TutorialProgress Restore(IEnumerable<TutorialScene> seen, bool dismissed)
    {
        var p = new TutorialProgress();
        foreach (var s in seen) p._seen.Add(s);
        p.Dismissed = dismissed;
        return p;
    }

    public bool HasSeen(TutorialScene scene) => _seen.Contains(scene);

    /// <summary>その場面の説明を出すべきか（未見かつ「もう説明はいらない」を選んでいない）。</summary>
    public bool ShouldShow(TutorialScene scene) => !Dismissed && !_seen.Contains(scene);

    /// <summary>出すべきなら見た印を付けて true（同じ説明は二度出さない）。</summary>
    public bool TryBegin(TutorialScene scene)
    {
        if (!ShouldShow(scene)) return false;
        _seen.Add(scene);
        return true;
    }

    /// <summary>「もう説明はいらない」。</summary>
    public void Dismiss() => Dismissed = true;
}

/// <summary>
/// 赴任イベントとチュートリアルの判定（issue #1496・design-04 §3.4）。何を積むか（data/events.yaml の id）を
/// ここ1箇所で決め、Unity 側は返った id を順に積むだけにする。乱数不使用。
/// </summary>
public static class Tutorial
{
    /// <summary>新設校（はじめから）の赴任イベント。1年のマネージャーが出迎える。</summary>
    public const string ArrivalNewSchoolEventId = "arrival_new_school";

    /// <summary>転任の赴任イベント。転任先の2年か3年のマネージャーが出迎える。</summary>
    public const string ArrivalTransferEventId = "arrival_transfer";

    /// <summary>「もう説明はいらない」を選んだときのマネージャーの返事。</summary>
    public const string DismissedEventId = "tutorial_dismissed";

    /// <summary>説明の最後に出す選択肢（値は <see cref="ChoiceContinue"/>／<see cref="ChoiceDismiss"/>）。</summary>
    public const string ContinueLabel = "分かった";
    public const string DismissLabel = "もう説明はいらない";
    public const int ChoiceContinue = 0;
    public const int ChoiceDismiss = 1;

    /// <summary>場面ごとの会話の id。</summary>
    public static string EventId(TutorialScene scene) => scene switch
    {
        TutorialScene.Basics => "tutorial_basics",
        TutorialScene.FirstTournament => "tutorial_first_tournament",
        TutorialScene.FirstMatch => "tutorial_first_match",
        TutorialScene.Scouting => "tutorial_scouting",
        TutorialScene.Purchases => "tutorial_purchases",
        TutorialScene.SummerRetirement => "tutorial_summer_retirement",
        _ => throw new ArgumentOutOfRangeException(nameof(scene)),
    };

    /// <summary>チュートリアルの会話の id か（スキップ・選択肢を付ける対象）。</summary>
    public static bool IsTutorialEvent(string? id)
    {
        if (id == null) return false;
        foreach (TutorialScene s in Enum.GetValues(typeof(TutorialScene)))
            if (EventId(s) == id) return true;
        return false;
    }

    /// <summary>赴任イベントの id か（スキップの対象）。</summary>
    public static bool IsArrivalEvent(string? id) => id == ArrivalNewSchoolEventId || id == ArrivalTransferEventId;

    /// <summary>
    /// 「はじめから」で新設校を作った直後に積む会話（順に）。赴任イベント→最初の説明（週の送り方・練習）。
    /// 最初の説明はこの場で見た印を付ける。
    /// </summary>
    public static IReadOnlyList<string> OnNewSchoolStarted(TutorialProgress progress)
    {
        var ids = new List<string> { ArrivalNewSchoolEventId };
        if (progress.TryBegin(TutorialScene.Basics)) ids.Add(EventId(TutorialScene.Basics));
        return ids;
    }

    /// <summary>転任を確定した直後に積む会話。赴任イベントだけ（チュートリアルは転任では出さない）。</summary>
    public static IReadOnlyList<string> OnTransferred(TutorialProgress progress) => new[] { ArrivalTransferEventId };

    /// <summary>試合の種類（<see cref="BeforeMatch"/> の引数）。説明はどちらでも同じで、最初の1試合でだけ出る。</summary>
    public enum MatchKind { Official, Practice }

    /// <summary>
    /// 自校の試合を始める直前（公式戦の開始確認の前／練習試合のスタメン設定の前）。公式戦・練習試合を問わず
    /// 最初の1試合でだけ采配の説明の id を返す＝練習試合で見ていれば公式戦の初戦では出さない（逆も同じ）。
    /// </summary>
    public static string? BeforeMatch(TutorialProgress progress, MatchKind kind)
        => OnScene(progress, TutorialScene.FirstMatch);

    /// <summary>場面に入った。未見なら見た印を付けてその会話の id を返す（出さないなら null）。</summary>
    public static string? OnScene(TutorialProgress progress, TutorialScene scene)
        => progress.TryBegin(scene) ? EventId(scene) : null;

    /// <summary>
    /// 部費で何か1つでも買えるか（<see cref="TutorialScene.Purchases"/> の判定）。施設・道具・備品のどれかの
    /// 次の段が部費以下なら true。
    /// </summary>
    public static bool CanBuySomething(double funds, FacilityCatalog catalog, FacilitySet set)
    {
        foreach (var def in catalog.Facilities)
        {
            var lv = set.LevelOf(def.Id);
            if (lv >= def.MaxLevel) continue;
            if (def.Tiers[lv + 1].Cost <= funds) return true;
        }
        return false;
    }
}
