using System;

namespace SEED.UI;

// ============================================================
//  TabModel.cs — 下のタブの選択と戻るの決め方（W2-7。純粋な計算）
//
//  タブごとに画面のスタックを持ち（TabHost が ScreenStack をタブの数だけ持つ）、選んでいないタブのスタックは
//  隠して保つ（タブを戻ったとき前の位置のまま。Flutter 版の StatefulShellRoute と同じ）。
//  - 選ぶ: 違うタブなら切り替える。**選んでいるタブをもう一度押したら根へ戻る**（Flutter 版の shell_scaffold.dart。
//    根にいるなら「先頭へスクロール」の合図だけを出す）
//  - 戻る: 選んでいるタブのスタックが根より上なら 1 つ下ろす（スタック自身が先に受ける）→ 最初のタブ以外なら
//    最初のタブへ（BackToFirstTab。Android の多くのアプリと同じ）→ それも無ければ受けない（アプリを背面へ）
// ============================================================

/// <summary>タブを押した結果。</summary>
/// <param name="Changed">選択が変わった。</param>
/// <param name="Reselected">選んでいるタブをもう一度押した（根へ戻る・先頭へスクロールの合図）。</param>
/// <param name="Previous">押す前に選んでいたタブ。</param>
public readonly record struct TabSelectResult(bool Changed, bool Reselected, int Previous);

/// <summary>タブの段での戻るの決め方。</summary>
public enum TabBackAction
{
    /// <summary>受けない（アプリの戻るへ回す）。</summary>
    None = 0,
    /// <summary>選んでいるタブのスタックを 1 つ下ろす。</summary>
    PopStack = 1,
    /// <summary>最初のタブへ切り替える。</summary>
    SelectFirst = 2,
}

/// <summary>下のタブの選択。</summary>
public sealed class TabModel
{
    /// <summary>最初のタブの番号。</summary>
    public const int FirstTab = 0;

    /// <summary>タブの数。</summary>
    public int Count { get; private set; }
    /// <summary>選んでいるタブ（タブが無ければ −1）。</summary>
    public int Selected { get; private set; } = -1;

    /// <summary>タブの数と最初の選択を決める（範囲の外は収める）。</summary>
    public TabModel(int count, int initial = FirstTab)
    {
        Resize(count, initial);
    }

    /// <summary>タブの数を変える（選択は範囲へ収める）。</summary>
    public void Resize(int count, int preferred)
    {
        Count = Math.Max(0, count);
        Selected = Count == 0 ? -1 : Math.Clamp(preferred, 0, Count - 1);
    }

    /// <summary>
    /// タブを押した・選んだ。範囲の外は何もしない（Changed も Reselected も false）。
    /// </summary>
    public TabSelectResult Select(int index)
    {
        int previous = Selected;
        if (index < 0 || index >= Count) return new TabSelectResult(false, false, previous);
        if (index == Selected) return new TabSelectResult(false, true, previous);
        Selected = index;
        return new TabSelectResult(true, false, previous);
    }

    /// <summary>
    /// 戻るを受けたときの決め方。
    /// </summary>
    /// <param name="selectedStackDepth">選んでいるタブのスタックの段の数（1 = 根だけ）。</param>
    /// <param name="backToFirstTab">最初のタブ以外なら最初のタブへ戻るか。</param>
    public TabBackAction DecideBack(int selectedStackDepth, bool backToFirstTab)
    {
        if (Selected < 0) return TabBackAction.None;
        if (selectedStackDepth > 1) return TabBackAction.PopStack;
        if (backToFirstTab && Selected != FirstTab) return TabBackAction.SelectFirst;
        return TabBackAction.None;
    }
}
