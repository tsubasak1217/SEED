using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  FocusModel.cs — フォーカスの範囲（スコープ）と、キーボードで動かす相手（W2-7。純粋な計算）
//
//  - 範囲（scope）は画面ごと（画面のスタックの段・ダイアログ・シート・覆い）。範囲は前後の並びを持ち、いちばん前の範囲だけが
//    「今のフォーカス」を持てる（覆われた画面のホイールは矢印キーを受けない）
//  - 範囲ごとに最後にフォーカスした相手を覚える。前の範囲が外れた・後ろへ回ったら、前に出た範囲の覚えていた相手へ戻る
//    （ダイアログを閉じたら、開く前に選んでいたホイールへ戻る）
//  - 後ろの範囲の相手が Request しても今のフォーカスは奪わない（その範囲の覚えとして残す）
//  - 相手が消えたら Release（覚えからも外す）
//  - 重ねる範囲（overlay。ダイアログ・シート・覆い）は、画面の範囲より常に前にいる（開いている間に下で画面の積み下ろしが
//    終わっても、画面の範囲はいちばん下の重ねる範囲のすぐ後ろへ入る）
//  範囲の鍵は object（部品が画面の枠の GameObject などを包んで渡す）。null の鍵は「どの範囲にも入らない」根の範囲。
// ============================================================

/// <summary>フォーカスの範囲と相手。</summary>
/// <typeparam name="T">フォーカスの相手の型。</typeparam>
public sealed class FocusModel<T> where T : class
{
    /// <summary>根の範囲の鍵（どの範囲にも入らない相手。いちばん後ろ）。</summary>
    private static readonly object RootScope = new();

    /// <summary>範囲の前後の並び（最後がいちばん前）。根の範囲はいつも先頭。</summary>
    private readonly List<object> _scopes = new() { RootScope };
    /// <summary>範囲ごとの覚えている相手。</summary>
    private readonly Dictionary<object, T> _remembered = new();
    /// <summary>重ねる範囲（ダイアログ・シート・覆い。画面の範囲より常に前）。</summary>
    private readonly HashSet<object> _overlays = new();

    /// <summary>いちばん前の範囲の鍵（根なら null）。</summary>
    public object? TopScope => ReferenceEquals(_scopes[^1], RootScope) ? null : _scopes[^1];

    /// <summary>今のフォーカス（いちばん前の範囲の覚えている相手。無ければ null）。</summary>
    public T? Current => _remembered.TryGetValue(_scopes[^1], out var item) ? item : null;

    /// <summary>範囲の数（根を含む）。</summary>
    public int ScopeCount => _scopes.Count;

    /// <summary>範囲を足していちばん前へ出す（既にあれば前へ出すだけ）。overlay = 重ねる範囲（画面の範囲より常に前）。</summary>
    public void PushScope(object scope, bool overlay = false)
    {
        if (overlay) _overlays.Add(scope);
        BringToFront(scope);
    }

    /// <summary>
    /// 範囲を前へ出す（無ければ足す）。重ねる範囲はいちばん前へ、画面の範囲はいちばん下の重ねる範囲のすぐ後ろへ
    /// （重ねる範囲が無ければいちばん前）。
    /// </summary>
    public void BringToFront(object? scope)
    {
        var key = scope ?? RootScope;
        if (ReferenceEquals(key, RootScope))
        {
            // 根は並びの先頭に固定。前へ出すときは、根より前の範囲をすべて後ろへ回したのと同じ＝何もしない
            return;
        }
        _scopes.Remove(key);
        if (_overlays.Contains(key))
        {
            _scopes.Add(key);
            return;
        }
        int firstOverlay = _scopes.FindIndex(s => _overlays.Contains(s));
        if (firstOverlay < 0) _scopes.Add(key);
        else _scopes.Insert(firstOverlay, key);
    }

    /// <summary>範囲を後ろへ回す（根のすぐ前へ。タブを離れた画面・覆われた画面）。</summary>
    public void SendToBack(object scope)
    {
        if (ReferenceEquals(scope, RootScope) || !_scopes.Remove(scope)) return;
        _scopes.Insert(1, scope);
    }

    /// <summary>範囲を外す（覚えている相手も忘れる）。前に出ていたなら、次に前の範囲の覚えている相手が今のフォーカスになる。</summary>
    public void RemoveScope(object scope)
    {
        if (ReferenceEquals(scope, RootScope)) return;
        _scopes.Remove(scope);
        _remembered.Remove(scope);
        _overlays.Remove(scope);
    }

    /// <summary>範囲があるか。</summary>
    public bool HasScope(object scope) => _scopes.Contains(scope);

    /// <summary>
    /// 範囲を重ねる範囲にする・外す（2026-10-02。覆いを画面の下へ回す〈ModalHost.Park〉間は画面の範囲と同じ扱いにして、
    /// 上に積んだ画面の範囲が前に出られるようにする）。並びは変えない（前後は BringToFront・SendToBack で決める）。
    /// </summary>
    /// <param name="scope">範囲。</param>
    /// <param name="overlay">重ねる範囲にするか。</param>
    public void SetOverlay(object scope, bool overlay)
    {
        if (ReferenceEquals(scope, RootScope)) return;
        if (overlay) _overlays.Add(scope);
        else _overlays.Remove(scope);
    }

    /// <summary>重ねる範囲か。</summary>
    public bool IsOverlay(object scope) => _overlays.Contains(scope);

    /// <summary>
    /// 相手をフォーカスにしたい。範囲の覚えとして記し、その範囲がいちばん前なら今のフォーカスになる（true）。
    /// 知らない範囲は足す（後ろ＝根のすぐ前）。
    /// </summary>
    /// <param name="item">相手。</param>
    /// <param name="scope">相手の範囲（null = 根の範囲）。</param>
    public bool Request(T item, object? scope)
    {
        var key = scope ?? RootScope;
        if (!_scopes.Contains(key)) _scopes.Insert(1, key);
        // 他の範囲に覚えられていたら外す（相手は 1 つの範囲にだけ属する）
        foreach (var k in new List<object>(_remembered.Keys))
            if (!ReferenceEquals(k, key) && ReferenceEquals(_remembered[k], item)) _remembered.Remove(k);
        _remembered[key] = item;
        return ReferenceEquals(_scopes[^1], key);
    }

    /// <summary>相手を外す（どの範囲の覚えからも）。今のフォーカスだったら true。</summary>
    public bool Release(T item)
    {
        bool wasCurrent = ReferenceEquals(Current, item);
        foreach (var k in new List<object>(_remembered.Keys))
            if (ReferenceEquals(_remembered[k], item)) _remembered.Remove(k);
        return wasCurrent;
    }

    /// <summary>範囲の覚えている相手（無ければ null）。</summary>
    public T? RememberedIn(object? scope) => _remembered.TryGetValue(scope ?? RootScope, out var item) ? item : null;
}
