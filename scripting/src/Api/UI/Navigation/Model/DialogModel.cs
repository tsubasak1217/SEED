using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  DialogModel.cs — ダイアログの中身・ボタン・結果の決め方（W2-7。2026-10-02 に危険のボタン・選択肢の一覧・進捗の札を足した。純粋な計算）
//
//  - ボタンは 0〜3 つ: 中立（左端）・いいえ（Negative）・はい（Positive。右端）の順（Material 3 の並び。文字が空のボタンは出さない）。
//    どれも空なら「OK」の Positive を 1 つ。ただし選択肢の一覧（Items）・進捗の札（Progress）・ボタンを隠す（HideButtons）では出さない
//  - 危険（DialogButtonKind.Danger）のボタンはテーマの color.error で塗る・書く（Positive は塗りのボタン、ほかは文字のボタン）
//  - 選択肢の一覧（Material の SimpleDialog 相当。長押しのメニュー）: 題の下に項目（アイコン欄＋文字）を縦に並べ、押した項目で閉じる
//    （結果 Selected・DialogHandle.SelectedIndex）。選べない項目（Enabled = false）は灰色で押せない
//  - 進捗の札（Progress）: スピナーと本文の行。ボタンを出さない。外から閉じる（DialogHandle.Close / Dismiss）・本文を変える（SetMessage）
//  - 幕（背景を暗くする面）のタップ: DismissOnScrimTap なら Dismissed で閉じる、そうでなければ何もしない
//  - 戻る: CancelableByBack なら Dismissed で閉じる。そうでなくても戻るは受ける（後ろの画面へ回さない。確認を必ず答えさせる）
//  - 結果は 1 回だけ（DialogResultLatch。ボタンの連打・閉じる動きの途中の戻るで 2 度出さない）
//  - 本文の行の数の見積もりは DialogLayout.cs（Text.Measure は W2-6c）、札の縦の割り付けと出入りの倍率は DialogMetrics.cs、
//    ボタンの横並び・縦積みは DialogActionsLayout.cs
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
    /// <summary>選択肢の一覧の項目を選んだ（どの項目かは DialogHandle.SelectedIndex。2026-10-02）。</summary>
    Selected = 4,
}

/// <summary>ダイアログのボタン・選択肢の種類（2026-10-02）。</summary>
public enum DialogButtonKind
{
    /// <summary>ふつう（Positive は主の色の塗り、ほかは主の色の文字）。</summary>
    Default = 0,
    /// <summary>危険（「削除」「破棄して戻る」など取り消せない操作。テーマの color.error）。</summary>
    Danger = 1,
}

/// <summary>選択肢の一覧のダイアログの項目（2026-10-02。Material の SimpleDialogOption 相当）。</summary>
public sealed class DialogMenuItem
{
    /// <summary>作る。</summary>
    /// <param name="text">文字。</param>
    /// <param name="icon">先頭のアイコン（null = アイコン欄なし）。</param>
    /// <param name="kind">種類（危険なら color.error の文字）。</param>
    public DialogMenuItem(string text, UiIcon? icon = null, DialogButtonKind kind = DialogButtonKind.Default)
    {
        Text = text ?? string.Empty;
        Icon = icon;
        Kind = kind;
    }

    /// <summary>文字。</summary>
    public string Text { get; init; }

    /// <summary>先頭のアイコン（null = アイコン欄なし。一覧のどれかにアイコンがあれば、無い項目も文字の位置をそろえる）。</summary>
    public UiIcon? Icon { get; init; }

    /// <summary>種類（危険なら color.error の文字とアイコン）。</summary>
    public DialogButtonKind Kind { get; init; }

    /// <summary>選べるか（false なら灰色で押せない）。</summary>
    public bool Enabled { get; init; } = true;
}

/// <summary>ダイアログの中身と振る舞い。</summary>
public sealed class DialogOptions
{
    /// <summary>既定の Positive の文字（どのボタンも指定しなかったとき）。</summary>
    public const string DefaultPositiveText = "OK";
    /// <summary>選択肢の 1 行の既定のプレハブ（templates/ui を assets/ui へ取り込んだ置き場。2026-10-02）。</summary>
    public const string DefaultItemPrefab = "assets://ui/prefabs/dialog_item.actor";

    /// <summary>題（空なら出さない）。</summary>
    public string Title { get; init; } = string.Empty;
    /// <summary>本文（札に入りきらない長さなら、本文の窓をスクロールにする）。</summary>
    public string Message { get; init; } = string.Empty;
    /// <summary>はいのボタンの文字（空なら出さない）。</summary>
    public string PositiveText { get; init; } = string.Empty;
    /// <summary>いいえのボタンの文字（空なら出さない）。</summary>
    public string NegativeText { get; init; } = string.Empty;
    /// <summary>中立のボタンの文字（空なら出さない）。</summary>
    public string NeutralText { get; init; } = string.Empty;
    /// <summary>はいのボタンの種類（2026-10-02。Danger で color.error の塗り）。</summary>
    public DialogButtonKind PositiveKind { get; init; } = DialogButtonKind.Default;
    /// <summary>いいえのボタンの種類（2026-10-02。Danger で color.error の文字）。</summary>
    public DialogButtonKind NegativeKind { get; init; } = DialogButtonKind.Default;
    /// <summary>中立のボタンの種類（2026-10-02。Danger で color.error の文字）。</summary>
    public DialogButtonKind NeutralKind { get; init; } = DialogButtonKind.Default;
    /// <summary>幕のタップで閉じるか（Dismissed）。</summary>
    public bool DismissOnScrimTap { get; init; } = true;
    /// <summary>戻るで閉じるか（Dismissed）。false でも戻るは受ける（後ろへ回さない）。</summary>
    public bool CancelableByBack { get; init; } = true;
    /// <summary>
    /// 1 行の入力欄（W2-6b。null なら出さない）。本文とボタンの行の間に入力欄を置き、開いたらフォーカスを当てる（キーボードが出る）。
    /// 結果の文字は <c>DialogHandle.InputText</c>（Positive のときだけ入る）。
    /// </summary>
    public DialogInputOptions? Input { get; init; }
    /// <summary>
    /// 選択肢の一覧（2026-10-02。空なら出さない）。題・本文の下に項目を縦に並べ、押した項目で閉じる（結果 Selected・
    /// <c>DialogHandle.SelectedIndex</c>）。ボタンの文字を指定しなければボタンは出さない。札に入りきらない数ならスクロールする。
    /// </summary>
    public IReadOnlyList<DialogMenuItem> Items { get; init; } = Array.Empty<DialogMenuItem>();
    /// <summary>
    /// 選択肢の 1 行のプレハブ（2026-10-02。空なら既定の <see cref="DefaultItemPrefab"/>。行の根に SEED.UI.DialogItem・子に Icon〈Sprite〉と
    /// Label〈Text〉を持つ形。templates/ui/prefabs/dialog_item.actor）。
    /// </summary>
    public string ItemPrefab { get; init; } = string.Empty;
    /// <summary>
    /// 進捗の札（2026-10-02）: 本文の代わりにスピナー（ProgressSpinner）と本文の行を出す。ボタンの文字を指定しなければボタンは出さない。
    /// 閉じるのは外から（<c>DialogHandle.Close</c>・<c>Dismiss</c>）。本文は開いた後も <c>DialogHandle.SetMessage</c> で変えられる。
    /// </summary>
    public bool Progress { get; init; }
    /// <summary>ボタンの行を出さない（2026-10-02。文字を指定したボタンも出さない）。</summary>
    public bool HideButtons { get; init; }

    /// <summary>
    /// 選択肢の一覧のダイアログ（長押しのメニューなど。幕のタップ・戻るで Dismissed）。
    /// </summary>
    /// <param name="title">題（空なら出さない）。</param>
    /// <param name="items">項目（上から）。</param>
    public static DialogOptions Menu(string title, params DialogMenuItem[] items)
        => new() { Title = title ?? string.Empty, Items = items ?? Array.Empty<DialogMenuItem>() };

    /// <summary>
    /// 進捗の札（ボタンなし・幕のタップと戻るでは閉じない。閉じるのは外から DialogHandle.Close / Dismiss）。
    /// </summary>
    /// <param name="message">本文（スピナーの右）。</param>
    /// <param name="title">題（空なら出さない）。</param>
    public static DialogOptions ProgressCard(string message, string title = "")
        => new()
        {
            Title = title ?? string.Empty, Message = message ?? string.Empty, Progress = true,
            DismissOnScrimTap = false, CancelableByBack = false,
        };
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
    /// <summary>選んだ項目が無い（Selected 以外の結果・まだ選んでいない）ときの番号。</summary>
    public const int NoSelection = -1;

    /// <summary>
    /// 出すボタンの結果（左から右の順）。どれも空なら Positive だけ。ただしボタンを隠す・選択肢の一覧・進捗の札でどれも空なら出さない。
    /// </summary>
    public static IReadOnlyList<DialogResult> Buttons(DialogOptions options)
    {
        var list = new List<DialogResult>();
        if (options.HideButtons) return list;
        if (options.NeutralText.Length > 0) list.Add(DialogResult.Neutral);
        if (options.NegativeText.Length > 0) list.Add(DialogResult.Negative);
        if (options.PositiveText.Length > 0) list.Add(DialogResult.Positive);
        // どれも空: ふつうのダイアログは「OK」を 1 つ。選択肢の一覧・進捗の札はボタンなし（項目を押す・外から閉じる）
        if (list.Count == 0 && !HasItems(options) && !options.Progress) list.Add(DialogResult.Positive);
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

    /// <summary>ボタンの種類（2026-10-02）。</summary>
    public static DialogButtonKind ButtonKind(DialogOptions options, DialogResult button) => button switch
    {
        DialogResult.Neutral => options.NeutralKind,
        DialogResult.Negative => options.NegativeKind,
        DialogResult.Positive => options.PositiveKind,
        _ => DialogButtonKind.Default,
    };

    /// <summary>ボタン・選択肢の種類 → ボタンの色の役割（危険 = color.error）。</summary>
    public static ButtonTone ToneOf(DialogButtonKind kind) => kind == DialogButtonKind.Danger ? ButtonTone.Danger : ButtonTone.Primary;

    /// <summary>選択肢の一覧があるか。</summary>
    public static bool HasItems(DialogOptions options) => options.Items is { Count: > 0 };

    /// <summary>選択肢の一覧のどれかにアイコンがあるか（あれば、無い項目もアイコン欄の分だけ文字を右へずらしてそろえる）。</summary>
    public static bool AnyItemIcon(DialogOptions options)
    {
        if (!HasItems(options)) return false;
        foreach (var item in options.Items)
            if (item?.Icon is not null) return true;
        return false;
    }

    /// <summary>番号の項目を選べるか（範囲の外・null・Enabled = false は選べない）。</summary>
    public static bool CanSelect(DialogOptions options, int index)
        => HasItems(options) && index >= 0 && index < options.Items.Count && options.Items[index] is { Enabled: true };

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

    /// <summary>選んだ項目の番号（結果が Selected のときだけ。それ以外は DialogModel.NoSelection）。</summary>
    public int SelectedIndex { get; private set; } = DialogModel.NoSelection;

    /// <summary>決まったか。</summary>
    public bool IsCompleted => Result.HasValue;

    /// <summary>結果を決める（最初の 1 回だけ true）。</summary>
    public bool TryComplete(DialogResult result) => TryComplete(result, DialogModel.NoSelection);

    /// <summary>結果と選んだ項目を決める（最初の 1 回だけ true。番号は Selected のときだけ残す）。</summary>
    /// <param name="result">結果。</param>
    /// <param name="selectedIndex">選んだ項目の番号（Selected 以外は無視）。</param>
    public bool TryComplete(DialogResult result, int selectedIndex)
    {
        if (Result.HasValue) return false;
        Result = result;
        SelectedIndex = result == DialogResult.Selected ? selectedIndex : DialogModel.NoSelection;
        return true;
    }
}
