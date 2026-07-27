// ============================================================
//  ComboBoxKeyGuard.cs — ComboBox のキーボード誤操作ガード（添付ビヘイビア）
//
//  担当:
//   - ドロップダウンが「閉じている」ComboBox の選択が矢印キーで勝手に変わるのを防ぐ
//   - 要素項目（ComboBoxItem / Separator）を持つ ComboBox の壊れた
//     テキスト検索（typeahead）による誤選択を防ぐ
//
//  アプリ全体の暗黙 ComboBox スタイル（App.xaml）から 1 箇所で有効化するため、
//  個々の ComboBox へハンドラをコピペする必要はない。
// ============================================================

using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SEEDEditor.Controls;

/// <summary>
/// ComboBox のキーボード操作による「意図しない選択変更」を抑止する添付ビヘイビア。
///
/// WPF の ComboBox は既定で以下 2 つの挙動を持ち、どちらもエディタでは事故になる:
///
/// 1. <b>閉じたままの矢印キー選択</b><br/>
///    ドロップダウンを閉じていてもフォーカスが残っている限り ↑/↓/PageUp/PageDown で
///    選択項目が変わる。ビューポート上のショートカット操作（カメラ移動等）中に
///    表示モードやレンダリング機能が黙って切り替わってしまう。
///
/// 2. <b>要素項目に対する壊れたテキスト検索（typeahead）</b><br/>
///    項目が <see cref="ComboBoxItem"/> や <see cref="Separator"/> といった UIElement の場合、
///    WPF の TextSearch は表示文字列ではなく <c>ToString()</c>
///    （＝ "System.Windows.Controls.ComboBoxItem: 〜"）を照合対象にする。
///    そのため "s" を打つだけで全項目がヒットし、実測では必ず Separator が選択される
///    （＝ SelectedItem が ComboBoxItem でなくなり、Tag を読む側が既定値へフォールバックする）。
///    カメラ後退の "S" キーがシーンビュー表示モードを既定へ戻していた不具合の原因。
///
/// 本ビヘイビアは「ドロップダウンが閉じている間はキーボードで選択を変えない」を原則とし、
/// 加えて要素項目を持つコンボでは開いている間の typeahead も無効化する
/// （前述のとおり照合が壊れており、機能として成立していないため）。
/// 編集可能（IsEditable=True）なコンボの文字入力はテキスト欄本来の入力なので妨げない。
/// </summary>
public static class ComboBoxKeyGuard
{
    /// <summary>ガードを有効にする添付プロパティ。App.xaml の暗黙スタイルから一括で True にする。</summary>
    public static readonly DependencyProperty EnabledProperty =
        DependencyProperty.RegisterAttached(
            "Enabled",
            typeof(bool),
            typeof(ComboBoxKeyGuard),
            new PropertyMetadata(false, OnEnabledChanged));

    /// <summary>添付プロパティ Enabled のセッター（XAML/コードから使用）。</summary>
    public static void SetEnabled(DependencyObject element, bool value) =>
        element.SetValue(EnabledProperty, value);

    /// <summary>添付プロパティ Enabled のゲッター（XAML/コードから使用）。</summary>
    public static bool GetEnabled(DependencyObject element) =>
        (bool)element.GetValue(EnabledProperty);

    /// <summary>
    /// Enabled の変化でハンドラを着脱する。対象が ComboBox でない場合は何もしない。
    /// プレビュー（トンネル）イベントで処理するのは、ComboBox 自身の既定処理
    /// （KeyDown / TextInput）よりも先に握り潰す必要があるため。
    /// </summary>
    private static void OnEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox combo) return;

        // 二重登録防止のため一旦外してから、有効時のみ付け直す。
        combo.PreviewKeyDown   -= OnPreviewKeyDown;
        combo.PreviewTextInput -= OnPreviewTextInput;
        if (e.NewValue is true)
        {
            combo.PreviewKeyDown   += OnPreviewKeyDown;
            combo.PreviewTextInput += OnPreviewTextInput;
        }
    }

    /// <summary>
    /// ドロップダウンが閉じている間の選択移動キー（↑ / ↓ / PageUp / PageDown）を無効化する。
    /// Alt 併用（Alt+↓ = ドロップダウンを開く標準操作）は妨げない。
    /// 開いている間は WPF 標準どおり矢印で候補を選べる。
    /// </summary>
    private static void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        if (combo.IsDropDownOpen) return;
        // Alt+↓/Alt+↑ は「開く/閉じる」の標準ショートカットなので通す。
        if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) return;

        if (e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown)
            e.Handled = true;
    }

    /// <summary>
    /// テキスト検索（typeahead）による選択変更を抑止する。
    ///   - 閉じている間: 常に抑止（キー入力で選択が変わらないという原則）。
    ///   - 開いている間: 項目が UIElement（ComboBoxItem / Separator 等）のコンボのみ抑止。
    ///     この場合 WPF の照合対象が ToString()（"System.Windows.Controls.…"）になり
    ///     機能として成立していないため。文字列項目・データバインド項目のコンボでは
    ///     従来どおり typeahead が使える。
    /// 編集可能コンボ（IsEditable=True）の文字入力はテキスト欄の入力なので一切妨げない。
    /// </summary>
    private static void OnPreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (sender is not ComboBox combo) return;
        if (combo.IsEditable) return;

        if (!combo.IsDropDownOpen || HasElementItems(combo))
            e.Handled = true;
    }

    /// <summary>項目に UIElement（ComboBoxItem / Separator 等）が含まれるかを返す。</summary>
    private static bool HasElementItems(ComboBox combo) =>
        combo.Items.Cast<object>().Any(item => item is UIElement);
}
