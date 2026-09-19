// ============================================================
//  SeedThemeColors.cs — 色表（SeedColorTable）を WPF の Color として公開する
//
//  【役割】
//  XAML から {x:Static theme:SeedThemeColors.ButtonBg} の形で
//  <see cref="SeedColorTable"/> の値を参照できるようにするだけの薄い変換層。
//
//  【なぜ XAML に直接 #RRGGBB を書かないのか】
//  色を XAML に書くと、コントラスト比の自動テスト（WPF を読み込まない
//  コンソールテスト）から検査できなくなり、
//  「テストは通るが画面の色は別物」という二重管理が生まれる。
//  値の出所を SeedColorTable ただ 1 つに絞るため、XAML はここを経由する。
//
//  【使い方】
//  SeedButtonStyles.xaml のブラシ定義だけがここを参照する。
//  個々の画面が直接ここを参照して色を決めるのは禁止
//  （ボタンの見た目は Style を指定して決める。docs/editor_ui_style.md）。
// ============================================================

using System.Windows.Media;

namespace SEEDEditor.Theme;

/// <summary>
/// <see cref="SeedColorTable"/> の 16 進定数を WPF の <see cref="Color"/> にしたもの。
/// </summary>
public static class SeedThemeColors
{
    // ── 通常ボタン ──────────────────────────────────────────

    /// <inheritdoc cref="SeedColorTable.BUTTON_BG"/>
    public static readonly Color ButtonBg = ToColor(SeedColorTable.BUTTON_BG);

    /// <inheritdoc cref="SeedColorTable.BUTTON_BG_HOVER"/>
    public static readonly Color ButtonBgHover = ToColor(SeedColorTable.BUTTON_BG_HOVER);

    /// <inheritdoc cref="SeedColorTable.BUTTON_BG_PRESSED"/>
    public static readonly Color ButtonBgPressed = ToColor(SeedColorTable.BUTTON_BG_PRESSED);

    /// <inheritdoc cref="SeedColorTable.BUTTON_BG_DISABLED"/>
    public static readonly Color ButtonBgDisabled = ToColor(SeedColorTable.BUTTON_BG_DISABLED);

    /// <inheritdoc cref="SeedColorTable.BUTTON_FG"/>
    public static readonly Color ButtonFg = ToColor(SeedColorTable.BUTTON_FG);

    /// <inheritdoc cref="SeedColorTable.BUTTON_FG_HOVER"/>
    public static readonly Color ButtonFgHover = ToColor(SeedColorTable.BUTTON_FG_HOVER);

    /// <inheritdoc cref="SeedColorTable.BUTTON_FG_DISABLED"/>
    public static readonly Color ButtonFgDisabled = ToColor(SeedColorTable.BUTTON_FG_DISABLED);

    /// <inheritdoc cref="SeedColorTable.BUTTON_BORDER"/>
    public static readonly Color ButtonBorder = ToColor(SeedColorTable.BUTTON_BORDER);

    /// <inheritdoc cref="SeedColorTable.BUTTON_BORDER_FOCUS"/>
    public static readonly Color ButtonBorderFocus = ToColor(SeedColorTable.BUTTON_BORDER_FOCUS);

    // ── 主操作ボタン ────────────────────────────────────────

    /// <inheritdoc cref="SeedColorTable.PRIMARY_BG"/>
    public static readonly Color PrimaryBg = ToColor(SeedColorTable.PRIMARY_BG);

    /// <inheritdoc cref="SeedColorTable.PRIMARY_BG_HOVER"/>
    public static readonly Color PrimaryBgHover = ToColor(SeedColorTable.PRIMARY_BG_HOVER);

    /// <inheritdoc cref="SeedColorTable.PRIMARY_BG_PRESSED"/>
    public static readonly Color PrimaryBgPressed = ToColor(SeedColorTable.PRIMARY_BG_PRESSED);

    /// <inheritdoc cref="SeedColorTable.PRIMARY_BG_DISABLED"/>
    public static readonly Color PrimaryBgDisabled = ToColor(SeedColorTable.PRIMARY_BG_DISABLED);

    /// <inheritdoc cref="SeedColorTable.PRIMARY_FG"/>
    public static readonly Color PrimaryFg = ToColor(SeedColorTable.PRIMARY_FG);

    /// <inheritdoc cref="SeedColorTable.PRIMARY_FG_DISABLED"/>
    public static readonly Color PrimaryFgDisabled = ToColor(SeedColorTable.PRIMARY_FG_DISABLED);

    // ── 完了・生成ボタン ────────────────────────────────────

    /// <inheritdoc cref="SeedColorTable.SUCCESS_BG"/>
    public static readonly Color SuccessBg = ToColor(SeedColorTable.SUCCESS_BG);

    /// <inheritdoc cref="SeedColorTable.SUCCESS_BG_HOVER"/>
    public static readonly Color SuccessBgHover = ToColor(SeedColorTable.SUCCESS_BG_HOVER);

    /// <inheritdoc cref="SeedColorTable.SUCCESS_BG_PRESSED"/>
    public static readonly Color SuccessBgPressed = ToColor(SeedColorTable.SUCCESS_BG_PRESSED);

    /// <inheritdoc cref="SeedColorTable.SUCCESS_FG"/>
    public static readonly Color SuccessFg = ToColor(SeedColorTable.SUCCESS_FG);

    // ── 危険操作ボタン ──────────────────────────────────────

    /// <inheritdoc cref="SeedColorTable.DANGER_BG"/>
    public static readonly Color DangerBg = ToColor(SeedColorTable.DANGER_BG);

    /// <inheritdoc cref="SeedColorTable.DANGER_BG_HOVER"/>
    public static readonly Color DangerBgHover = ToColor(SeedColorTable.DANGER_BG_HOVER);

    /// <inheritdoc cref="SeedColorTable.DANGER_BG_PRESSED"/>
    public static readonly Color DangerBgPressed = ToColor(SeedColorTable.DANGER_BG_PRESSED);

    /// <inheritdoc cref="SeedColorTable.DANGER_FG"/>
    public static readonly Color DangerFg = ToColor(SeedColorTable.DANGER_FG);

    // ── リンク風ボタン ──────────────────────────────────────

    /// <inheritdoc cref="SeedColorTable.LINK_FG"/>
    public static readonly Color LinkFg = ToColor(SeedColorTable.LINK_FG);

    /// <inheritdoc cref="SeedColorTable.LINK_FG_HOVER"/>
    public static readonly Color LinkFgHover = ToColor(SeedColorTable.LINK_FG_HOVER);

    /// <inheritdoc cref="SeedColorTable.LINK_FG_PRESSED"/>
    public static readonly Color LinkFgPressed = ToColor(SeedColorTable.LINK_FG_PRESSED);

    /// <inheritdoc cref="SeedColorTable.LINK_FG_DISABLED"/>
    public static readonly Color LinkFgDisabled = ToColor(SeedColorTable.LINK_FG_DISABLED);

    // ── アイコン専用ボタン ──────────────────────────────────

    /// <inheritdoc cref="SeedColorTable.ICON_OVERLAY_HOVER"/>
    public static readonly Color IconOverlayHover = ToColor(SeedColorTable.ICON_OVERLAY_HOVER);

    /// <inheritdoc cref="SeedColorTable.ICON_OVERLAY_PRESSED"/>
    public static readonly Color IconOverlayPressed = ToColor(SeedColorTable.ICON_OVERLAY_PRESSED);

    /// <inheritdoc cref="SeedColorTable.ICON_FG"/>
    public static readonly Color IconFg = ToColor(SeedColorTable.ICON_FG);

    /// <inheritdoc cref="SeedColorTable.ICON_FG_HOVER"/>
    public static readonly Color IconFgHover = ToColor(SeedColorTable.ICON_FG_HOVER);

    /// <inheritdoc cref="SeedColorTable.ICON_FG_DISABLED"/>
    public static readonly Color IconFgDisabled = ToColor(SeedColorTable.ICON_FG_DISABLED);

    // ── トグルの ON 状態 ────────────────────────────────────

    /// <inheritdoc cref="SeedColorTable.TOGGLE_BG_CHECKED"/>
    public static readonly Color ToggleBgChecked = ToColor(SeedColorTable.TOGGLE_BG_CHECKED);

    /// <inheritdoc cref="SeedColorTable.TOGGLE_BG_CHECKED_HOVER"/>
    public static readonly Color ToggleBgCheckedHover = ToColor(SeedColorTable.TOGGLE_BG_CHECKED_HOVER);

    /// <inheritdoc cref="SeedColorTable.TOGGLE_FG_CHECKED"/>
    public static readonly Color ToggleFgChecked = ToColor(SeedColorTable.TOGGLE_FG_CHECKED);

    // ── ダイアログの文字と入力欄 ────────────────────────────

    /// <inheritdoc cref="SeedColorTable.SURFACE_WINDOW"/>
    public static readonly Color WindowSurface = ToColor(SeedColorTable.SURFACE_WINDOW);

    /// <inheritdoc cref="SeedColorTable.SURFACE_DIALOG"/>
    public static readonly Color DialogSurface = ToColor(SeedColorTable.SURFACE_DIALOG);

    /// <inheritdoc cref="SeedColorTable.FIELD_BG"/>
    public static readonly Color FieldBg = ToColor(SeedColorTable.FIELD_BG);

    /// <inheritdoc cref="SeedColorTable.FIELD_BORDER"/>
    public static readonly Color FieldBorder = ToColor(SeedColorTable.FIELD_BORDER);

    /// <inheritdoc cref="SeedColorTable.DIALOG_TEXT"/>
    public static readonly Color DialogText = ToColor(SeedColorTable.DIALOG_TEXT);

    /// <summary>ダイアログの一覧で、選択されている行の背景。</summary>
    public static readonly Color DialogListSelectionBg =
        ToColor(SeedColorTable.DIALOG_LIST_SELECTION_BG);

    /// <summary>ダイアログの一覧で、マウスが乗っている行の背景。</summary>
    public static readonly Color DialogListHoverBg =
        ToColor(SeedColorTable.DIALOG_LIST_HOVER_BG);

    /// <inheritdoc cref="SeedColorTable.DIALOG_DIM_TEXT"/>
    public static readonly Color DialogDimText = ToColor(SeedColorTable.DIALOG_DIM_TEXT);

    /// <inheritdoc cref="SeedColorTable.DIALOG_SUCCESS_TEXT"/>
    public static readonly Color DialogSuccessText = ToColor(SeedColorTable.DIALOG_SUCCESS_TEXT);

    /// <inheritdoc cref="SeedColorTable.DIALOG_ERROR_TEXT"/>
    public static readonly Color DialogErrorText = ToColor(SeedColorTable.DIALOG_ERROR_TEXT);

    // ── マージエディタ ──────────────────────────────────────

    /// <inheritdoc cref="SeedColorTable.MERGE_BLOCK_BG"/>
    public static readonly Color MergeBlockBg = ToColor(SeedColorTable.MERGE_BLOCK_BG);

    /// <inheritdoc cref="SeedColorTable.MERGE_BLOCK_BORDER"/>
    public static readonly Color MergeBlockBorder = ToColor(SeedColorTable.MERGE_BLOCK_BORDER);

    /// <inheritdoc cref="SeedColorTable.MERGE_ADDED_BG"/>
    public static readonly Color MergeAddedBg = ToColor(SeedColorTable.MERGE_ADDED_BG);

    /// <inheritdoc cref="SeedColorTable.MERGE_REMOVED_BG"/>
    public static readonly Color MergeRemovedBg = ToColor(SeedColorTable.MERGE_REMOVED_BG);

    /// <inheritdoc cref="SeedColorTable.MERGE_PADDING_STROKE"/>
    public static readonly Color MergePaddingStroke = ToColor(SeedColorTable.MERGE_PADDING_STROKE);

    // ── スクリプトエディタの通知帯（ディスク追従）──────────

    /// <inheritdoc cref="SeedColorTable.NOTICE_BAR_BG"/>
    public static readonly Color NoticeBarBg = ToColor(SeedColorTable.NOTICE_BAR_BG);

    /// <inheritdoc cref="SeedColorTable.NOTICE_BAR_BORDER"/>
    public static readonly Color NoticeBarBorder = ToColor(SeedColorTable.NOTICE_BAR_BORDER);

    /// <inheritdoc cref="SeedColorTable.NOTICE_BAR_ICON"/>
    public static readonly Color NoticeBarIcon = ToColor(SeedColorTable.NOTICE_BAR_ICON);

    /// <summary>
    /// 色表の 16 進文字列を WPF の <see cref="Color"/> へ変換する。
    /// </summary>
    /// <param name="hex">"#RRGGBB" または "#AARRGGBB"。</param>
    /// <returns>変換した色。</returns>
    private static Color ToColor(string hex)
    {
        var (a, r, g, b) = SeedColorTable.Parse(hex);
        return Color.FromArgb(a, r, g, b);
    }
}
