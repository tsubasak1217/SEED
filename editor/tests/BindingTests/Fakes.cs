using System;
using System.Collections.Generic;
using SEED.Binding;

namespace BindingTests;

// ============================================================
//  Fakes.cs — 結び付けのテストの偽物（UI 部品の代わりの当てる先・警告の受け口）
// ============================================================

/// <summary>一方向の当てる先の偽物（書いた値を覚える）。</summary>
/// <typeparam name="T">値の型。</typeparam>
internal sealed class FakeTarget<T> : IBindTarget<T>
{
    /// <summary>書いた値（順に）。</summary>
    public readonly List<T> Writes = new();

    /// <inheritdoc />
    public bool IsAlive { get; set; } = true;

    /// <inheritdoc />
    public bool IsReady { get; set; } = true;

    /// <inheritdoc />
    public void Write(T value) => Writes.Add(value);
}

/// <summary>双方向の当てる先の偽物（部品の値・書いた値・知らせの口を覚え、利用者の操作をまねる）。</summary>
/// <typeparam name="T">値の型。</typeparam>
internal sealed class FakeWidget<T> : ITwoWayBindTarget<T>
{
    /// <summary>付いている知らせの口。</summary>
    private readonly List<Action<T>> _handlers = new();

    /// <summary>結び付けから書かれた値（順に）。</summary>
    public readonly List<T> Writes = new();

    /// <summary>部品の今の値。</summary>
    public T Current = default!;

    /// <summary>Listen が呼ばれた回数。</summary>
    public int ListenCount;

    /// <summary>
    /// 書かれたときにも知らせを鳴らすか（SelectionGroup.Select のように、スクリプトからの変更でも知らせる部品をまねる）。
    /// </summary>
    public bool RaiseOnWrite;

    /// <inheritdoc />
    public bool IsAlive { get; set; } = true;

    /// <inheritdoc />
    public bool IsReady { get; set; } = true;

    /// <summary>今付いている知らせの口の数。</summary>
    public int ActiveListeners => _handlers.Count;

    /// <inheritdoc />
    public void Write(T value)
    {
        Writes.Add(value);
        Current = value;
        if (RaiseOnWrite) Raise(value);
    }

    /// <inheritdoc />
    public IDisposable Listen(Action<T> handler)
    {
        ListenCount++;
        _handlers.Add(handler);
        return new DisposableAction(() => _handlers.Remove(handler));
    }

    /// <summary>利用者が部品を操作した（値を変えて知らせる）。</summary>
    /// <param name="value">新しい値。</param>
    public void UserChange(T value)
    {
        Current = value;
        Raise(value);
    }

    /// <summary>付いている知らせの口を全部呼ぶ。</summary>
    /// <param name="value">値。</param>
    private void Raise(T value)
    {
        foreach (var handler in _handlers.ToArray()) handler(value);
    }
}

/// <summary>行の並びの偽物（呼ばれた口を文字で覚える）。</summary>
internal sealed class FakeRows : IListBindTarget
{
    /// <summary>呼ばれた口（"SetCount(3)"・"Refresh"・"RebindRow(1)"）。</summary>
    public readonly List<string> Calls = new();

    /// <inheritdoc />
    public bool IsAlive { get; set; } = true;

    /// <inheritdoc />
    public void SetCount(int count) => Calls.Add($"SetCount({count})");

    /// <inheritdoc />
    public void Refresh() => Calls.Add("Refresh");

    /// <inheritdoc />
    public void RebindRow(int index) => Calls.Add($"RebindRow({index})");
}

/// <summary>結び付けの警告・エラーを拾う（using の間だけ BindingLog.Listener を差す）。</summary>
internal sealed class LogCapture : IDisposable
{
    /// <summary>拾った行（接頭辞つき）。</summary>
    public readonly List<string> Lines = new();

    /// <summary>拾い始める。</summary>
    public LogCapture()
    {
        BindingLog.Listener = Lines.Add;
    }

    /// <summary>警告・エラーの数。</summary>
    public int Count => Lines.Count;

    /// <summary>拾うのをやめる。</summary>
    public void Dispose() => BindingLog.Listener = null;
}

/// <summary>Dispose された回数を数える物。</summary>
internal sealed class CountingDisposable : IDisposable
{
    /// <summary>Dispose された回数。</summary>
    public int DisposeCount;

    /// <summary>数える。</summary>
    public void Dispose() => DisposeCount++;
}

/// <summary>区切りで呼ばれた回数を数える仕事（続けるかを決められる）。</summary>
internal sealed class CountingTask : IFrameTask
{
    /// <summary>呼ばれた回数。</summary>
    public int RunCount;

    /// <summary>あと何回続けるか（0 なら 1 回で終わる）。</summary>
    public int KeepFor;

    /// <summary>呼ばれたら例外を投げるか。</summary>
    public bool Throw;

    /// <inheritdoc />
    public bool RunFrameTask()
    {
        RunCount++;
        if (Throw) throw new InvalidOperationException("テストの例外");
        if (KeepFor <= 0) return false;
        KeepFor--;
        return true;
    }
}
