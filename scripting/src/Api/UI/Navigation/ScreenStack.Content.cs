using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  ScreenStack.Content.cs — 画面の中身の出所（2026-10-02。lane3。docs/ui_navigation.md §2.8）
//
//  中身は、渡された組み立て済みの中身（Push(GameObject)・Replace(GameObject)）・置いてある根（RootAdoptChild）・
//  作り置き（Prewarm。ScreenStack.Prewarm.cs）・プレハブの順に決める（純粋な決め方は Model/ScreenContentSource.cs の ScreenContentPlan）。
//  - 渡された中身: 枠ができたフレームに枠の Body（安全領域の中）か枠の直下へ付け替える（できあがっていれば次のフレームで Ready）。
//    作り直せない（プレハブが無い）ので、覆われても手放さない（KeepState は常に true）。外れたときは ScreenContentRelease に従い、
//    消す（既定）か、押下を取り消して積んだときの親へ戻す（枠を消すより先に付け替えるので、中身は枠と一緒に消えない）。
//  - 置いてある根: 根の段で 1 回だけ（従来どおり。ScreenStack.cs の TakeAdoptChild）。
// ============================================================

public sealed partial class ScreenStack
{
    /// <summary>渡された中身の段の、ログ・手札に出す名前の頭（assets:// のパスと区別する）。</summary>
    public const string SuppliedPrefabPrefix = "supplied:";

    /// <summary>渡された中身 1 つ（中身・外れたときの扱い・積んだときの親）。</summary>
    private sealed record SuppliedContent(GameObject Content, ScreenContentRelease Release, GameObject Parent);

    /// <summary>段の番号 → まだ枠へ移していない渡された中身（枠ができたら Instance.Supplied へ移る）。</summary>
    private readonly Dictionary<int, SuppliedContent> _supplied = new();

    /// <summary>
    /// 組み立て済みの中身を画面として積む（作り置きの中身・アプリが自分で持つ置き場の中身）。中身は枠の中へ付け替える
    /// （自分を隠していても見せる）。プレハブの組み立てが無いので、出入りは中身が落ち着いて（OnScreenEnter の後 1 フレーム）すぐ始まる。
    /// </summary>
    /// <param name="content">中身の根（できあがっていなくてもよい。無効なら積まずに null・エラー）。</param>
    /// <param name="transition">出入りの種類（null = 指定 → スタックの既定）。</param>
    /// <param name="args">画面へ渡す値（中身の根の UiScreen.OnScreenEnter）。</param>
    /// <param name="options">画面ごとの指定（KeepState は常に true として扱う＝作り直せないため）。</param>
    /// <param name="release">画面が外れたときの中身の扱い（既定 Destroy = 画面と一緒に消す）。</param>
    /// <returns>手札（中身が無効なら null）。</returns>
    public ScreenHandle? Push(GameObject content, NavTransition? transition = null, object? args = null, ScreenOptions? options = null,
        ScreenContentRelease release = ScreenContentRelease.Destroy)
    {
        if (!AcceptSupplied(content, "push")) return null;
        var change = _model.Push(SuppliedName(content), WithSupplied(options, transition), args, DefaultTransition);
        _supplied[change.Incoming!.Id] = new SuppliedContent(content, release, content.Parent);
        return Enqueue(change, "push(supplied)");
    }

    /// <summary>
    /// いちばん上の画面を、組み立て済みの中身で置き換える（<see cref="Push(GameObject, NavTransition?, object?, ScreenOptions?, ScreenContentRelease)"/> の置き換えの版）。
    /// </summary>
    /// <param name="content">中身の根（無効なら置き換えずに null・エラー）。</param>
    /// <param name="transition">出入りの種類（null = 指定 → スタックの既定）。</param>
    /// <param name="args">画面へ渡す値。</param>
    /// <param name="options">画面ごとの指定（KeepState は常に true）。</param>
    /// <param name="release">画面が外れたときの中身の扱い。</param>
    /// <returns>手札（中身が無効なら null）。</returns>
    public ScreenHandle? Replace(GameObject content, NavTransition? transition = null, object? args = null, ScreenOptions? options = null,
        ScreenContentRelease release = ScreenContentRelease.Destroy)
    {
        if (!AcceptSupplied(content, "replace")) return null;
        var change = _model.Replace(SuppliedName(content), WithSupplied(options, transition), args, DefaultTransition);
        _supplied[change.Incoming!.Id] = new SuppliedContent(content, release, content.Parent);
        return Enqueue(change, "replace(supplied)");
    }

    /// <summary>渡された中身を受け付けるか（無効ならエラーを出して断る）。</summary>
    private bool AcceptSupplied(GameObject content, string op)
    {
        if (content.IsValid) return true;
        Debug.LogError($"{LogPrefix} {gameObject.Name} {op}: 渡された中身が無効です（積みません）");
        return false;
    }

    /// <summary>渡された中身の段の名前（ログ・ScreenHandle.Prefab。プレハブのパスとは重ならない）。</summary>
    private static string SuppliedName(GameObject content) => SuppliedPrefabPrefix + content.Name;

    /// <summary>渡された中身の段の指定（出入りの種類を重ね、KeepState を true にする＝作り直せないため）。</summary>
    private static ScreenOptions WithSupplied(ScreenOptions? options, NavTransition? transition)
    {
        var o = options ?? ScreenOptions.Default;
        return new ScreenOptions
        {
            Transition = transition ?? o.Transition,
            Opaque = o.Opaque,
            KeepState = ScreenContentPlan.EffectiveKeepState(ScreenContentSource.Supplied, o.KeepState),
            IgnoreBack = o.IgnoreBack,
            SafeArea = o.SafeArea,
        };
    }

    /// <summary>置いてある根を引き取れる見込みか（根の段で、まだ試していない。子が本当にあるかは引き取るときに調べる）。</summary>
    private bool AdoptAvailable(ScreenEntry entry) =>
        !_adoptTaken && RootAdoptChild.Length > 0 && RootPrefab.Length > 0 && entry.Prefab == RootPrefab;

    /// <summary>
    /// 枠ができたフレームに中身を引き取る: 渡された中身（1 回）→ 置いてある根（根の段・1 回）。無ければ無効な GameObject
    /// （呼び手はプレハブから作る）。置いてある根が見つからなかったら出所をプレハブへ落とす。
    /// </summary>
    /// <param name="instance">中身を作る段の実体。</param>
    /// <returns>引き取る中身か、無効な GameObject。</returns>
    private GameObject TakeContent(Instance instance)
    {
        if (_supplied.Remove(instance.Entry.Id, out var supplied))
        {
            instance.Supplied = supplied;
            instance.Source = ScreenContentSource.Supplied;
            return supplied.Content;
        }
        var adopted = TakeAdoptChild(instance.Entry);
        if (!adopted.IsValid && instance.Source == ScreenContentSource.Adopted) instance.Source = ScreenContentSource.Prefab;
        return adopted;
    }

    /// <summary>
    /// 段の実体を消す直前の中身の後始末（DestroyEntry から。OnScreenExit の後）。
    /// 渡された中身（ReturnToParent）は押下を取り消して元の親へ戻す（枠は消す）。借りた作り置きは作り置きへ返す
    /// （使い回すなら枠ごと隠して残す）。
    /// </summary>
    /// <param name="instance">消す段の実体。</param>
    /// <returns>枠を消さずに残すなら true（使い回す作り置き）。</returns>
    private bool ReleaseContent(Instance instance)
    {
        if (instance.Supplied is { Release: ScreenContentRelease.ReturnToParent } supplied && instance.Content.IsValid)
        {
            // 押している最中の指が戻した中身へ届かないよう取り消し、枠を消すより先に付け替える（シーンの操作は発行順に当たる）
            instance.Content.CancelGestures();
            if (supplied.Parent.IsValid) instance.Content.SetParent(supplied.Parent);
            else instance.Content.SetParent((GameObject?)null);
            DetachScreen(instance);
            Debug.Log($"{LogPrefix} {gameObject.Name} 渡された中身を元の親へ戻した: {instance.Entry}");
            return false;
        }
        if (instance.Prewarm is { } prewarm) return ReturnPrewarm(prewarm, instance);
        // 渡された中身（既定）・置いてある根・プレハブ: 枠と一緒に消す
        return false;
    }

    /// <summary>画面のスクリプトからスタックと手札の参照を外す（戻した・使い回す中身が、古い手札で自分を閉じようとしない）。</summary>
    private static void DetachScreen(Instance instance)
    {
        if (instance.Screen is not { } screen) return;
        screen.Navigator = null;
        screen.Handle = null;
    }
}
