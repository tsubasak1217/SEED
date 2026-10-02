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
    /// <summary>面ができる前に Close が呼ばれたときの結果と動きの有無（できたらすぐ閉じる）。</summary>
    internal (bool Requested, object? Result, bool Animate) EarlyClose { get; private set; }

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
    public void Close(object? result = null) => Close(result, animate: true);

    /// <summary>
    /// 閉じる（2026-10-02: 動きの有無を選べる。animate = false なら出る動きを見せずにすぐ閉じ、Closed もこの呼び出しの中で届く）。
    /// 面ができる前に呼んだら、できたときに見せずに閉じる。
    /// 2026-10-03（レビュー #9）: 面の「外から閉じる」入口（<see cref="ModalPlane.CloseFromOutside"/>）を通す。ダイアログはボタンと同じ
    /// 決め方（結果の留め金・入力欄の文字の確定・フォーカスとキーボードの持ち上げの片付け）を通る。
    /// </summary>
    /// <param name="result">結果。</param>
    /// <param name="animate">出る動きを見せるか。</param>
    public void Close(object? result, bool animate)
    {
        if (IsClosed) return;
        // 外から渡された結果を整える（既定はそのまま。ダイアログは DialogResult へ直し、番号の無い Selected は Dismissed へ倒す）
        var normalized = NormalizeCloseResult(result);
        // 面ができていれば面の「外から閉じる」入口へ。できる前なら覚えておき、面ができたときに同じ入口で閉じる（ModalPlane.OnWidgetStart）
        if (Plane is { } plane) plane.CloseFromOutside(normalized, animate);
        else EarlyClose = (true, normalized, animate);
    }

    /// <summary>
    /// 外から（<see cref="Close(object?, bool)"/>）渡された結果を整える（2026-10-03。既定はそのまま。<see cref="DialogHandle"/> が上書きする）。
    /// </summary>
    /// <param name="result">渡された結果。</param>
    /// <returns>面へ渡す結果。</returns>
    internal virtual object? NormalizeCloseResult(object? result) => result;

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
/// <remarks>
/// 【外から閉じる】（2026-10-03。レビュー #9）<see cref="Close(SEED.UI.DialogResult)"/>・<see cref="Dismiss"/>・基底の Close・
/// ModalHost.CloseAll はどれもボタンと同じ決め方（Dialog の Choose）を通る: 結果は 1 回だけ、Positive なら入力欄の文字が
/// <see cref="InputText"/> に入り（入力欄のスクリプトがまだ始まっていなければ初めの文字）、入力欄のフォーカスとキーボードの持ち上げを片付ける。
/// 外から <see cref="SEED.UI.DialogResult.Selected"/> を渡しても選んだ項目の番号が無いので <see cref="SEED.UI.DialogResult.Dismissed"/> で閉じる（警告）。
/// </remarks>
public sealed class DialogHandle : ModalHandle
{
    /// <summary>ログの接頭辞（面の部品と同じ）。</summary>
    private const string LogPrefix = "[UI] modal:";

    /// <summary>結果を待つ口。</summary>
    private readonly TaskCompletionSource<DialogResult> _result = new();

    /// <summary>ダイアログの中身。</summary>
    public DialogOptions Options { get; }

    /// <summary>決まった結果（閉じる前は null）。</summary>
    public DialogResult? DialogResult { get; private set; }

    /// <summary>
    /// 入力欄の結果の文字（W2-6b。<see cref="DialogOptions.Input"/> があり Positive で閉じたときだけ。TrimResult なら前後の空白を落とす。
    /// それ以外は null）。外から <c>Close(DialogResult.Positive)</c> で閉じたときも入る（2026-10-03。入力欄のスクリプトがまだ始まって
    /// いなければ初めの文字）。
    /// </summary>
    public string? InputText { get; private set; }

    /// <summary>入力欄の結果の文字を置く（Dialog が Positive を選んだときに閉じる前に呼ぶ）。</summary>
    internal void SetInputText(string? text) => InputText = text;

    /// <summary>
    /// 選んだ項目の番号（2026-10-02。選択肢の一覧のダイアログで結果が <see cref="SEED.UI.DialogResult.Selected"/> のときだけ
    /// DialogOptions.Items の添字。それ以外・閉じる前は <see cref="DialogModel.NoSelection"/>〈-1〉）。
    /// </summary>
    public int SelectedIndex { get; private set; } = DialogModel.NoSelection;

    /// <summary>選んだ項目の番号を置く（Dialog が Selected を選んだときに閉じる前に呼ぶ）。</summary>
    internal void SetSelectedIndex(int index) => SelectedIndex = index;

    /// <summary>結果が決まった（閉じる動きの後）。</summary>
    public event Action<DialogResult>? Completed;

    /// <summary>結果を待つ（await できる）。</summary>
    public Task<DialogResult> ResultAsync => _result.Task;

    /// <summary>開く前に SetMessage で置いた本文（面ができたら当てる）。</summary>
    private string? _pendingMessage;

    internal DialogHandle(DialogOptions options) : base(ModalKind.Dialog)
    {
        Options = options;
    }

    /// <summary>ボタンを押さずに閉じる（Dismissed）。</summary>
    public void Dismiss() => Close(SEED.UI.DialogResult.Dismissed);

    /// <summary>
    /// 外から結果つきで閉じる（2026-10-02。進捗の札が終わったら Positive で閉じるなど。閉じる動きの後に Completed）。
    /// 面ができる前に呼んでも、できたらすぐ閉じる。ボタンを押したのと同じ決め方を通る（2026-10-03。Positive なら InputText が入る）。
    /// <see cref="SEED.UI.DialogResult.Selected"/> は選んだ項目の番号が無いので Dismissed で閉じる（警告）。
    /// </summary>
    /// <param name="result">結果。</param>
    public void Close(DialogResult result) => base.Close(result);

    /// <summary>外から結果つきで閉じる（2026-10-02: animate = false なら出る動きを見せずにすぐ閉じる）。</summary>
    /// <param name="result">結果。</param>
    /// <param name="animate">出る動きを見せるか。</param>
    public void Close(DialogResult result, bool animate) => base.Close(result, animate);

    /// <summary>
    /// 本文を変える（2026-10-02。進捗の札の「ダウンロード中 40%」など。札の高さは本文の行の数に合わせて割り付け直す）。
    /// 面ができる前に呼んだら、できたときに当てる。閉じた後は何もしない。
    /// </summary>
    /// <param name="message">本文。</param>
    public void SetMessage(string message)
    {
        if (IsClosed) return;
        if (Plane is Dialog dialog) dialog.SetMessage(message);
        else _pendingMessage = message ?? string.Empty;
    }

    /// <summary>面ができる前に置いた本文（Dialog が開くときに読む。無ければ null）。</summary>
    internal string? TakePendingMessage()
    {
        var message = _pendingMessage;
        _pendingMessage = null;
        return message;
    }

    /// <inheritdoc />
    /// <remarks>
    /// 2026-10-03（レビュー #9）: DialogResult へ直す（null・DialogResult 以外は Dismissed）。Selected は選んだ項目の番号を外から渡せないので
    /// Dismissed へ倒す（SelectedIndex が −1 のまま Selected で閉じると、受け手の Items[h.SelectedIndex] が範囲の外になる）。決め方は
    /// <see cref="DialogModel.ExternalCloseResult"/>。
    /// </remarks>
    internal override object? NormalizeCloseResult(object? result)
    {
        if (result is SEED.UI.DialogResult.Selected)
            Debug.LogWarning($"{LogPrefix} DialogHandle.Close(DialogResult.Selected) は選んだ項目の番号が無いので Dismissed で閉じます");
        return DialogModel.ExternalCloseResult(result);
    }

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
