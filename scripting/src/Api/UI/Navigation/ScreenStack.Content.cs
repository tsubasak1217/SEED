using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  ScreenStack.Content.cs — 画面の中身の出所（2026-10-02。lane3。docs/ui_navigation.md §2.8）
//
//  中身は、渡された組み立て済みの中身（Push(GameObject)・Replace(GameObject)）・置いてある根（RootAdoptChild）・
//  作り置き（Prewarm。ScreenStack.Prewarm.cs）・プレハブの順に決める（純粋な決め方は Model/ScreenContentSource.cs の ScreenContentPlan）。
//  - 渡された中身: 枠ができたフレームに枠の Body（安全領域の中）か枠の直下へ付け替える（できあがっていれば次のフレームで Ready）。
//    作り直せない（プレハブが無い）ので、覆われても手放さない（KeepState は常に true）。外れたときは ScreenContentRelease に従い、
//    消す（既定）か、押下を取り消して元の親へ戻す（枠を消すより先に付け替えるので、中身は枠と一緒に消えない）。
//    2026-10-03（2 回目のレビュー #21・#22。決め方は Model/SuppliedContentRules.cs）:
//    - 元の親は積んだ時点ではなく、枠へ移すフレーム（TakeContent）に読む（同じフレームに作った中身は積んだ時点でできあがっておらず、
//      親が無効に見えて、下ろすとシーンの根の直下へ見えたまま出た）。元の親が無い・消えたなら、根へ移して隠す（警告）
//    - 同じ中身をほかの段が持っていたら積まない（null・警告。二度押しで 2 段目へ移ると 1 段目が空の枠になった）。
//      外れた段が元の親へ戻す（ReturnToParent）ときだけは積める（外れる処理で戻してから新しい段が枠へ移す）
//    - 外れるとき、中身がもうこの枠の下に無ければ（アプリが付け替えた）触らない（引き戻さない・消さない）
//  - 置いてある根: 根の段で 1 回だけ（従来どおり。ScreenStack.cs の TakeAdoptChild）。
// ============================================================

public sealed partial class ScreenStack
{
    /// <summary>渡された中身の段の、ログ・手札に出す名前の頭（assets:// のパスと区別する）。</summary>
    public const string SuppliedPrefabPrefix = "supplied:";

    /// <summary>渡された中身 1 つ（段・中身・外れたときの扱い・枠へ移したフレームに読んだ元の親）。</summary>
    private sealed class SuppliedContent
    {
        /// <summary>中身を積んだ段（外れたかを使用中の判定に使う）。</summary>
        public required ScreenEntry Entry { get; init; }
        /// <summary>中身の根。</summary>
        public required GameObject Content { get; init; }
        /// <summary>外れたときの中身の扱い。</summary>
        public required ScreenContentRelease Release { get; init; }
        /// <summary>
        /// 元の親（枠へ移すフレーム〈TakeContent〉に読む。それまでと、シーンの根にあった中身は無効。レビュー #21）。
        /// </summary>
        public GameObject Parent;
    }

    /// <summary>段の番号 → まだ枠へ移していない渡された中身（枠ができたら Instance.Supplied へ移る）。</summary>
    private readonly Dictionary<int, SuppliedContent> _supplied = new();

    /// <summary>
    /// 組み立て済みの中身を画面として積む（作り置きの中身・アプリが自分で持つ置き場の中身）。中身は枠の中へ付け替える
    /// （自分を隠していても見せる）。プレハブの組み立てが無いので、出入りは中身が落ち着いて（OnScreenEnter の後 1 フレーム）すぐ始まる。
    /// 同じ中身をほかの段が持っていれば積まない（null・警告。外れた段が元の親へ戻す途中なら積める）。
    /// </summary>
    /// <param name="content">中身の根（できあがっていなくてもよい。無効なら積まずに null・エラー）。</param>
    /// <param name="transition">出入りの種類（null = 指定 → スタックの既定）。</param>
    /// <param name="args">画面へ渡す値（中身の根の UiScreen.OnScreenEnter）。</param>
    /// <param name="options">画面ごとの指定（KeepState は常に true として扱う＝作り直せないため）。</param>
    /// <param name="release">画面が外れたときの中身の扱い（既定 Destroy = 画面と一緒に消す）。</param>
    /// <returns>手札（中身が無効・ほかの段で使用中なら null）。</returns>
    public ScreenHandle? Push(GameObject content, NavTransition? transition = null, object? args = null, ScreenOptions? options = null,
        ScreenContentRelease release = ScreenContentRelease.Destroy)
    {
        if (!AcceptSupplied(content, "push")) return null;
        var change = _model.Push(SuppliedName(content), WithSupplied(options, transition), args, DefaultTransition);
        RememberSupplied(change.Incoming!, content, release);
        return Enqueue(change, "push(supplied)");
    }

    /// <summary>
    /// いちばん上の画面を、組み立て済みの中身で置き換える（<see cref="Push(GameObject, NavTransition?, object?, ScreenOptions?, ScreenContentRelease)"/> の置き換えの版）。
    /// </summary>
    /// <param name="content">中身の根（無効・ほかの段で使用中なら置き換えずに null）。</param>
    /// <param name="transition">出入りの種類（null = 指定 → スタックの既定）。</param>
    /// <param name="args">画面へ渡す値。</param>
    /// <param name="options">画面ごとの指定（KeepState は常に true）。</param>
    /// <param name="release">画面が外れたときの中身の扱い。</param>
    /// <returns>手札（中身が無効・ほかの段で使用中なら null）。</returns>
    public ScreenHandle? Replace(GameObject content, NavTransition? transition = null, object? args = null, ScreenOptions? options = null,
        ScreenContentRelease release = ScreenContentRelease.Destroy)
    {
        if (!AcceptSupplied(content, "replace")) return null;
        var change = _model.Replace(SuppliedName(content), WithSupplied(options, transition), args, DefaultTransition);
        RememberSupplied(change.Incoming!, content, release);
        return Enqueue(change, "replace(supplied)");
    }

    /// <summary>
    /// 渡された中身を受け付けるか（無効ならエラー、ほかの段で使用中・外れる段と一緒に消える途中なら警告を出して断る。レビュー #22）。
    /// </summary>
    private bool AcceptSupplied(GameObject content, string op)
    {
        if (!content.IsValid)
        {
            Debug.LogError($"{LogPrefix} {gameObject.Name} {op}: 渡された中身が無効です（積みません）");
            return false;
        }
        switch (SuppliedContentRules.CheckSupplied(HoldersOf(content)))
        {
            case SuppliedAvailability.InUse:
                Debug.LogWarning($"{LogPrefix} {gameObject.Name} {op}: 渡された中身 {content.Name} はほかの段で使用中です（積みません。二度押しで同じ中身を 2 回積むなど）");
                return false;
            case SuppliedAvailability.Doomed:
                Debug.LogWarning($"{LogPrefix} {gameObject.Name} {op}: 渡された中身 {content.Name} は外れる段が持っていて、画面と一緒に消えます（Destroy）。" +
                    "積みません（下ろしてすぐ積み直すなら ScreenContentRelease.ReturnToParent で積む）");
                return false;
            default:
                return true;
        }
    }

    /// <summary>
    /// 中身を持っている段（まだ枠へ移していない段と、枠へ移した段の実体）を集める（同じエンティティかで比べる。FFI は呼ばない）。
    /// </summary>
    /// <param name="content">中身の根。</param>
    /// <returns>持っている段の様子（外れたか・外れたときの扱い）。</returns>
    private List<SuppliedHolder> HoldersOf(GameObject content)
    {
        var key = NavNode.Key(content);
        var holders = new List<SuppliedHolder>();
        foreach (var pending in _supplied.Values)
            if (NavNode.Key(pending.Content) == key) holders.Add(new SuppliedHolder(pending.Entry.Removed, pending.Release));
        foreach (var instance in _instances.Values)
            if (instance.Supplied is { } held && NavNode.Key(held.Content) == key)
                holders.Add(new SuppliedHolder(instance.Entry.Removed, held.Release));
        return holders;
    }

    /// <summary>渡された中身を、枠ができるまで覚える（元の親はまだ読まない。TakeContent で読む）。</summary>
    private void RememberSupplied(ScreenEntry entry, GameObject content, ScreenContentRelease release) =>
        _supplied[entry.Id] = new SuppliedContent { Entry = entry, Content = content, Release = release };

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
            // 元の親はこのフレームに読む（枠は積んだ後に作るので、枠ができた今は同じフレームに作った中身もできあがっている。
            // 積んだ時点では親が無効に見え、下ろすとシーンの根へ見えたまま出た。レビュー #21）
            supplied.Parent = supplied.Content.Parent;
            instance.Supplied = supplied;
            instance.Source = ScreenContentSource.Supplied;
            return supplied.Content;
        }
        var adopted = TakeAdoptChild(instance.Entry);
        if (!adopted.IsValid && instance.Source == ScreenContentSource.Adopted) instance.Source = ScreenContentSource.Prefab;
        return adopted;
    }

    /// <summary>
    /// 枠へ移す前に外れた段の渡された中身を忘れる（DestroyEntry から。枠ができあがらなかった段など。中身は移していないので触らない。
    /// 使用中の判定に残さない）。
    /// </summary>
    /// <param name="entry">外れた段。</param>
    private void ForgetUntakenSupplied(ScreenEntry entry)
    {
        if (entry.Removed) _supplied.Remove(entry.Id);
    }

    /// <summary>
    /// 段の実体を消す直前の中身の後始末（DestroyEntry から。OnScreenExit の後）。
    /// 渡された中身は SuppliedContentRules.ReleaseAction に従う（元の親へ戻す・根へ移して隠す・触らない・枠と一緒に消す）。
    /// 借りた作り置きは作り置きへ返す（使い回すなら枠ごと隠して残す）。
    /// </summary>
    /// <param name="instance">消す段の実体。</param>
    /// <returns>枠を消さずに残すなら true（使い回す作り置き）。</returns>
    private bool ReleaseContent(Instance instance)
    {
        if (instance.Supplied is { } supplied)
        {
            ReleaseSupplied(instance, supplied);
            return false;
        }
        if (instance.Prewarm is { } prewarm) return ReturnPrewarm(prewarm, instance);
        // 置いてある根・プレハブ: 枠と一緒に消す
        return false;
    }

    /// <summary>
    /// 渡された中身を手放す（枠を消すより先に付け替える。シーンの操作は発行順に当たるので、中身は枠と一緒に消えない）。
    /// 中身がもうこの枠の下に無ければ触らない（レビュー #22）。元の親が無い・消えたなら根へ移して隠す（レビュー #21）。
    /// </summary>
    /// <param name="instance">消す段の実体。</param>
    /// <param name="supplied">渡された中身。</param>
    private void ReleaseSupplied(Instance instance, SuppliedContent supplied)
    {
        var content = instance.Content;
        if (!content.IsValid) return;
        bool underFrame = NavNode.IsDescendantOf(content, instance.Frame);
        bool parentAlive = NavNode.Exists(supplied.Parent);
        switch (SuppliedContentRules.ReleaseAction(supplied.Release, underFrame, parentAlive))
        {
            case SuppliedReleaseAction.DestroyWithFrame:
                // 枠を消すと一緒に消える（中身の持ち主はスタック）
                return;
            case SuppliedReleaseAction.LeaveInPlace:
                // アプリが自分で付け替えた中身: 引き戻さず、消さず、古い手札で自分を閉じないよう参照だけ外す
                DetachScreen(instance);
                Debug.Log($"{LogPrefix} {gameObject.Name} 渡された中身はもう枠の下に無いので触りません: {instance.Entry}");
                return;
            case SuppliedReleaseAction.ReturnToParent:
                // 押している最中の指が戻した中身へ届かないよう取り消してから戻す
                content.CancelGestures();
                content.SetParent(supplied.Parent);
                DetachScreen(instance);
                Debug.Log($"{LogPrefix} {gameObject.Name} 渡された中身を元の親へ戻した: {instance.Entry}");
                return;
            case SuppliedReleaseAction.ReturnToRootHidden:
                // 戻す先が無い: 根へ移すが隠す（根の直下に見えたまま・押せるまま出さない。消さないのでアプリの参照は生きている）
                content.CancelGestures();
                NavNode.SetVisible(content, false);
                content.SetParent((GameObject?)null);
                DetachScreen(instance);
                Debug.LogWarning($"{LogPrefix} {gameObject.Name} 渡された中身の元の親がありません（シーンの根にあった・消えた）。" +
                    $"シーンの根へ移して隠しました（ReturnToParent は隠した置き場の子にして積む）: {instance.Entry}");
                return;
        }
    }

    /// <summary>画面のスクリプトからスタックと手札の参照を外す（戻した・使い回す中身が、古い手札で自分を閉じようとしない）。</summary>
    private static void DetachScreen(Instance instance)
    {
        if (instance.Screen is not { } screen) return;
        screen.Navigator = null;
        screen.Handle = null;
    }
}
