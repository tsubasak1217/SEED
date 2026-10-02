using System;
using SEEDEditor.Scripting;

namespace SEED.Binding;

// ============================================================
//  Bind.Node.cs — ノードの表示（GameObject.Visible）と Sprite の色への結び付け（実行中だけの部分）
//
//      Bind.Visible(this, emptyLabel, _isEmpty);                        // bool の観測値をそのまま
//      Bind.Visible(this, badge, _unread, n => n > 0);                  // 値 → 見せるか
//      Bind.Color(this, dot, _status, s => s == Status.Ok ? ok : ng);   // 値 → 色
//  作った時点の値ですぐ当て、変わるたびに当て直す。アクタ・Sprite が消えたら自分を外す。
//  GameObject には「動かす／止める」（Active）の口がまだ無いので Bind.Active は無い（docs/backlog.md）。
// ============================================================

public static partial class Bind
{
    /// <summary>bool の観測値を GameObject の表示（Visible。子孫ごと）へ結ぶ。</summary>
    /// <param name="node">当てる GameObject。</param>
    /// <param name="source">見せるかの観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Visible(GameObject node, IReadOnlyObservable<bool> source)
        => OneWay(new VisibleTarget(node), source);

    /// <summary>bool の観測値を GameObject の表示へ結ぶ（owner の破棄で自動で外れる）。</summary>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <param name="node">当てる GameObject。</param>
    /// <param name="source">見せるかの観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Visible(SEEDScript owner, GameObject node, IReadOnlyObservable<bool> source)
        => Own(owner, Visible(node, source));

    /// <summary>観測値から見せるかを決めて GameObject の表示へ結ぶ。</summary>
    /// <typeparam name="T">値の型。</typeparam>
    /// <param name="node">当てる GameObject。</param>
    /// <param name="source">観測値。</param>
    /// <param name="visible">値 → 見せるか。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Visible<T>(GameObject node, IReadOnlyObservable<T> source, Func<T, bool> visible)
        => OneWay(new VisibleTarget(node), source, visible);

    /// <summary>観測値から見せるかを決めて GameObject の表示へ結ぶ（owner の破棄で自動で外れる）。</summary>
    /// <typeparam name="T">値の型。</typeparam>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <param name="node">当てる GameObject。</param>
    /// <param name="source">観測値。</param>
    /// <param name="visible">値 → 見せるか。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Visible<T>(SEEDScript owner, GameObject node, IReadOnlyObservable<T> source, Func<T, bool> visible)
        => Own(owner, Visible(node, source, visible));

    /// <summary>色の観測値を Sprite の色へ結ぶ。</summary>
    /// <param name="sprite">当てる Sprite。</param>
    /// <param name="source">色の観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Color(Sprite sprite, IReadOnlyObservable<Color> source)
        => OneWay(new SpriteColorTarget(sprite), source);

    /// <summary>色の観測値を Sprite の色へ結ぶ（owner の破棄で自動で外れる）。</summary>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <param name="sprite">当てる Sprite。</param>
    /// <param name="source">色の観測値。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Color(SEEDScript owner, Sprite sprite, IReadOnlyObservable<Color> source)
        => Own(owner, Color(sprite, source));

    /// <summary>観測値から色を決めて Sprite の色へ結ぶ。</summary>
    /// <typeparam name="T">値の型。</typeparam>
    /// <param name="sprite">当てる Sprite。</param>
    /// <param name="source">観測値。</param>
    /// <param name="color">値 → 色。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Color<T>(Sprite sprite, IReadOnlyObservable<T> source, Func<T, Color> color)
        => OneWay(new SpriteColorTarget(sprite), source, color);

    /// <summary>観測値から色を決めて Sprite の色へ結ぶ（owner の破棄で自動で外れる）。</summary>
    /// <typeparam name="T">値の型。</typeparam>
    /// <param name="owner">寿命を合わせるスクリプト（ふつうは this）。</param>
    /// <param name="sprite">当てる Sprite。</param>
    /// <param name="source">観測値。</param>
    /// <param name="color">値 → 色。</param>
    /// <returns>外す口。</returns>
    public static IDisposable Color<T>(SEEDScript owner, Sprite sprite, IReadOnlyObservable<T> source, Func<T, Color> color)
        => Own(owner, Color(sprite, source, color));
}
