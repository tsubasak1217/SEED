using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  DialogModel.cs — ダイアログの中身・ボタン・結果の決め方（W2-7。純粋な計算）
//
//  - ボタンは 1〜3 つ: 中立（左端）・いいえ（Negative）・はい（Positive。右端）の順（Material 3 の並び。文字が空のボタンは出さない。
//    どれも空なら「OK」の Positive を 1 つ）
//  - 幕（背景を暗くする面）のタップ: DismissOnScrimTap なら Dismissed で閉じる、そうでなければ何もしない
//  - 戻る: CancelableByBack なら Dismissed で閉じる。そうでなくても戻るは受ける（後ろの画面へ回さない。確認を必ず答えさせる）
//  - 結果は 1 回だけ（DialogResultLatch。ボタンの連打・閉じる動きの途中の戻るで 2 度出さない）
//  - 本文の行の数の見積もりは DialogLayout.cs（Text.Measure は W2-6c）、札の縦の割り付けと出入りの倍率は DialogMetrics.cs
// ============================================================

/// <summary>ダイアログの結果。</summary>
public enum DialogResult
{
    /// <summary>はい・OK（右端のボタン）。</summary>
    Positive = 0,
    /// <summary>いいえ・やめる。</summary>
    Negative = 1,
    /// <summary>中立（左端のボタン）。</summary>
    Neutral = 2,
    /// <summary>ボタンを押さずに閉じた（幕のタップ・戻る・スクリプトからの Dismiss）。</summary>
    Dismissed = 3,
}

/// <summary>ダイアログの中身と振る舞い。</summary>
public sealed class DialogOptions
{
    /// <summary>既定の Positive の文字（どのボタンも指定しなかったとき）。</summary>
    public const string DefaultPositiveText = "OK";

    /// <summary>題（空なら出さない）。</summary>
    public string Title { get; init; } = string.Empty;
    /// <summary>本文。</summary>
    public string Message { get; init; } = string.Empty;
    /// <summary>はいのボタンの文字（空なら出さない）。</summary>
    public string PositiveText { get; init; } = string.Empty;
    /// <summary>いいえのボタンの文字（空なら出さない）。</summary>
    public string NegativeText { get; init; } = string.Empty;
    /// <summary>中立のボタンの文字（空なら出さない）。</summary>
    public string NeutralText { get; init; } = string.Empty;
    /// <summary>幕のタップで閉じるか（Dismissed）。</summary>
    public bool DismissOnScrimTap { get; init; } = true;
    /// <summary>戻るで閉じるか（Dismissed）。false でも戻るは受ける（後ろへ回さない）。</summary>
    public bool CancelableByBack { get; init; } = true;
    /// <summary>
    /// 1 行の入力欄（W2-6b。null なら出さない）。本文とボタンの行の間に入力欄を置き、開いたらフォーカスを当てる（キーボードが出る）。
    /// 結果の文字は <c>DialogHandle.InputText</c>（Positive のときだけ入る）。
    /// </summary>
    public DialogInputOptions? Input { get; init; }
}

/// <summary>ダイアログの 1 行の入力欄の中身（W2-6b。名前の変更など）。</summary>
public sealed class DialogInputOptions
{
    /// <summary>初めの文字。</summary>
    public string Text { get; init; } = string.Empty;
    /// <summary>例の文（空のときに薄く出す。例「例：田中太郎」）。</summary>
    public string Placeholder { get; init; } = string.Empty;
    /// <summary>入力の種類。</summary>
    public TextInputKind Kind { get; init; } = TextInputKind.Text;
    /// <summary>最大の長さ（書記素の数。0 = 制限なし）。</summary>
    public int MaxLength { get; init; }
    /// <summary>貼り付けを許すか。</summary>
    public bool AllowPaste { get; init; } = true;
    /// <summary>結果の文字の前後の空白を落とすか（既定 true。名前の変更）。</summary>
    public bool TrimResult { get; init; } = true;
    /// <summary>キーボードの完了（Done）で Positive を選ぶか（既定 true）。</summary>
    public bool SubmitOnDone { get; init; } = true;

    /// <summary>結果の文字（TrimResult なら前後の空白を落とす）。</summary>
    public string Finish(string text) => TrimResult ? (text ?? string.Empty).Trim() : text ?? string.Empty;
}

/// <summary>ダイアログの決め方。</summary>
public static class DialogModel
{
    /// <summary>
    /// 出すボタンの結果（左から右の順）。どれも空なら Positive だけ。
    /// </summary>
    public static IReadOnlyList<DialogResult> Buttons(DialogOptions options)
    {
        var list = new List<DialogResult>();
        if (options.NeutralText.Length > 0) list.Add(DialogResult.Neutral);
        if (options.NegativeText.Length > 0) list.Add(DialogResult.Negative);
        if (options.PositiveText.Length > 0 || list.Count == 0) list.Add(DialogResult.Positive);
        return list;
    }

    /// <summary>ボタンの文字（Positive が空で出すときは既定の「OK」）。</summary>
    public static string ButtonText(DialogOptions options, DialogResult button) => button switch
    {
        DialogResult.Neutral => options.NeutralText,
        DialogResult.Negative => options.NegativeText,
        DialogResult.Positive => options.PositiveText.Length > 0 ? options.PositiveText : DialogOptions.DefaultPositiveText,
        _ => string.Empty,
    };

    /// <summary>幕のタップの結果（閉じないなら null）。</summary>
    public static DialogResult? OnScrimTap(DialogOptions options) => options.DismissOnScrimTap ? DialogResult.Dismissed : null;

    /// <summary>
    /// 戻るの結果。戻るは常に受ける（Consumed = true）。閉じるなら Result に Dismissed。
    /// </summary>
    public static (bool Consumed, DialogResult? Result) OnBack(DialogOptions options)
        => (true, options.CancelableByBack ? DialogResult.Dismissed : null);
}

/// <summary>結果を 1 回だけ受ける留め金。</summary>
public sealed class DialogResultLatch
{
    /// <summary>決まった結果（まだなら null）。</summary>
    public DialogResult? Result { get; private set; }

    /// <summary>決まったか。</summary>
    public bool IsCompleted => Result.HasValue;

    /// <summary>結果を決める（最初の 1 回だけ true）。</summary>
    public bool TryComplete(DialogResult result)
    {
        if (Result.HasValue) return false;
        Result = result;
        return true;
    }
}
