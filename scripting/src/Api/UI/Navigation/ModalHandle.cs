using System;
using System.Threading.Tasks;

namespace SEED.UI;

// ============================================================
//  ModalHandle.cs — 開いた覆い・シート・ダイアログの手札（W2-7）
//
//  ModalHost.ShowSheet / ShowOverlay / ShowDialog が返す。閉じるのを待つ（WhenClosed / Closed）・スクリプトから閉じる（Close）。
//  ダイアログは DialogHandle（結果が DialogResult）。Task の続きは閉じたフレームの Update の中（メインスレッド）で走る。
// ============================================================

/// <summary>開いた覆い・シート・ダイアログの手札。</summary>
public class ModalHandle
{
    /// <summary>閉じたときの結果を待つ口。</summary>
    private readonly TaskCompletionSource<object?> _closed = new();
    /// <summary>面の部品（開いた後。閉じる動きを始めさせる）。</summary>
    internal ModalPlane? Plane { get; set; }
    /// <summary>面ができる前に Close が呼ばれたときの結果（できたらすぐ閉じる）。</summary>
    internal (bool Requested, object? Result) EarlyClose { get; private set; }

    /// <summary>面の種類。</summary>
    public ModalKind Kind { get; }
    /// <summary>閉じたか。</summary>
    public bool IsClosed { get; private set; }
    /// <summary>閉じたときの結果（シート・覆いは中身が Close に渡した値。幕・戻るで閉じたら null）。</summary>
    public object? Result { get; private set; }
    /// <summary>面の根（できる前は IsValid = false）。</summary>
    public GameObject Root { get; internal set; }

    /// <summary>閉じた。</summary>
    public event Action<ModalHandle>? Closed;

    /// <summary>閉じるまで待つ。</summary>
    public Task<object?> WhenClosed => _closed.Task;

    internal ModalHandle(ModalKind kind)
    {
        Kind = kind;
    }

    /// <summary>閉じる（閉じる動きの後に Closed）。</summary>
    public void Close(object? result = null)
    {
        if (IsClosed) return;
        if (Plane is { } plane) plane.RequestClose(result);
        else EarlyClose = (true, result);
    }

    /// <summary>閉じたことを知らせる（1 回だけ）。</summary>
    internal virtual void Complete(object? result)
    {
        if (IsClosed) return;
        IsClosed = true;
        Result = result;
        Closed?.Invoke(this);
        _closed.TrySetResult(result);
    }
}

/// <summary>開いたダイアログの手札（結果は DialogResult）。</summary>
public sealed class DialogHandle : ModalHandle
{
    /// <summary>結果を待つ口。</summary>
    private readonly TaskCompletionSource<DialogResult> _result = new();

    /// <summary>ダイアログの中身。</summary>
    public DialogOptions Options { get; }

    /// <summary>決まった結果（閉じる前は null）。</summary>
    public DialogResult? DialogResult { get; private set; }

    /// <summary>
    /// 入力欄の結果の文字（W2-6b。<see cref="DialogOptions.Input"/> があり Positive で閉じたときだけ。TrimResult なら前後の空白を落とす。
    /// それ以外は null）。
    /// </summary>
    public string? InputText { get; private set; }

    /// <summary>入力欄の結果の文字を置く（Dialog が Positive を選んだときに閉じる前に呼ぶ）。</summary>
    internal void SetInputText(string? text) => InputText = text;

    /// <summary>結果が決まった（閉じる動きの後）。</summary>
    public event Action<DialogResult>? Completed;

    /// <summary>結果を待つ（await できる）。</summary>
    public Task<DialogResult> ResultAsync => _result.Task;

    internal DialogHandle(DialogOptions options) : base(ModalKind.Dialog)
    {
        Options = options;
    }

    /// <summary>ボタンを押さずに閉じる（Dismissed）。</summary>
    public void Dismiss() => Close(SEED.UI.DialogResult.Dismissed);

    /// <inheritdoc />
    internal override void Complete(object? result)
    {
        if (IsClosed) return;
        var value = result is DialogResult r ? r : SEED.UI.DialogResult.Dismissed;
        DialogResult = value;
        base.Complete(value);
        Completed?.Invoke(value);
        _result.TrySetResult(value);
    }
}
