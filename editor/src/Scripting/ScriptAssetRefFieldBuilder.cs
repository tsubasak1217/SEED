using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using SEEDEditor.Panels;
using static SEEDEditor.Scripting.ScriptFieldWidgets;

namespace SEEDEditor.Scripting;

/// <summary>
/// <c>[AssetReference("ttf", "otf")]</c> を付けた string フィールドの行を組み立てる。
///
/// 【行の構成】
/// ［パス表示（読み取り専用テキストボックス）］＋［参照ボタン］＋［× クリアボタン］。
/// Text コンポーネントの「フォント」行（<see cref="FileRefBuilder"/>）と同じ操作感になるよう、
/// ドロップ判定（<see cref="FileRefBuilder.ExtractDroppedPath"/>）とボタンのテンプレートは
/// あちらの部品をそのまま再利用している。ラベル列だけはスクリプトフィールド行の
/// 幅調停（<see cref="ScriptLabelColumnGroup"/>）に乗せる必要があるため、
/// 行の組み立て自体は <see cref="ScriptFieldWidgets.MakeRow"/> で行う。
///
/// 【保存する値】
/// <c>assets://</c> 仮想パス（アセットルート外のファイルは絶対パスのまま）。
/// 絶対パス → 仮想パスの変換は呼び出し側から渡される関数に委ねる
/// （アセットルートを知っているのはインスペクタ側だけのため）。
/// 変換関数が渡されない場合は参照・ドロップを受け付けず、現在値の表示のみになる。
/// </summary>
internal static class ScriptAssetRefFieldBuilder
{
    /// <summary>値が未設定のときにパス表示欄へ出す文言。</summary>
    private const string UnsetText = "（未設定）";

    /// <summary>クリア（×）ボタンのアイコンサイズ（px）。</summary>
    private const double ClearButtonIconSize = 10;

    /// <summary>ファイル選択ダイアログのタイトル。</summary>
    private const string DialogTitle = "ファイルを選択";

    /// <summary>ファイル選択ダイアログの「すべてのファイル」フィルタ。</summary>
    private const string DialogAllFilesFilter = "すべてのファイル|*.*";

    /// <summary>「参照」ボタンの文言。</summary>
    private const string BrowseButtonText = "参照";

    /// <summary>「参照」ボタンのフォントサイズ（px）。</summary>
    private const double BrowseButtonFontSize = 10;

    /// <summary>「参照」ボタンの内側余白。</summary>
    private static readonly Thickness BrowseButtonPadding = new(6, 2, 6, 2);

    /// <summary>
    /// アセット参照行を組む。
    /// </summary>
    /// <param name="field">フィールド情報（ラベル・説明）。</param>
    /// <param name="extensions">受け付ける拡張子（小文字・ドット無しの正規化済み。1 件以上）。</param>
    /// <param name="value">現在値（assets:// 仮想パス。空なら未設定）。</param>
    /// <param name="onChange">値の変更通知（保存する文字列をそのまま渡す）。</param>
    /// <param name="assetPathToVirtual">
    /// 絶対パス → 保存用パス（assets:// 仮想パス）の変換。
    /// null のときは参照ボタン・ドロップを無効化する（値を壊さない安全側）。
    /// </param>
    public static UIElement Build(
        ScriptFieldInfo       field,
        IReadOnlyList<string> extensions,
        string                value,
        Action<string>        onChange,
        Func<string, string>? assetPathToVirtual)
    {
        var hasPath = !string.IsNullOrEmpty(value);
        var canEdit = assetPathToVirtual is not null;

        // ドロップ判定・ダイアログのフィルタはドット付きの拡張子で行う
        // （Path.GetExtension がドット付きを返すため）。
        var dotted = SEED.ScriptAssetReference.ToDotted(extensions);

        // ── パス表示（読み取り専用）──────────────────────────
        var pathBox = MakeTextBox(hasPath ? Path.GetFileName(value) : UnsetText);
        pathBox.IsReadOnly   = true;
        pathBox.IsReadOnlyCaretVisible = false;
        pathBox.AllowDrop    = canEdit;
        pathBox.ToolTip      = hasPath
            ? value
            : "Project パネルまたはエクスプローラーから " + string.Join(" / ", dotted) + " をドロップ";

        // 値を確定してランタイムへ送る共通処理（参照ボタン・ドロップの両方から呼ぶ）
        void CommitAbsolutePath(string absolutePath)
        {
            if (assetPathToVirtual is null) return;
            onChange(assetPathToVirtual(absolutePath));
        }

        if (canEdit) AttachDrop(pathBox, dotted, CommitAbsolutePath);

        // ── 参照ボタン（ファイル選択ダイアログ）──────────────
        var browseBtn = new Button
        {
            Content           = BrowseButtonText,
            Background        = BrushButtonBg,
            Foreground        = BrushText,
            BorderBrush       = BrushButtonBorder,
            BorderThickness   = new Thickness(1),
            FontSize          = BrowseButtonFontSize,
            Padding           = BrowseButtonPadding,
            Margin            = new Thickness(3, 0, 0, 0),
            Cursor            = System.Windows.Input.Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Template          = FileRefBuilder.BuildButtonTemplate(),
            ToolTip           = "ファイルを選んで参照を設定する",
            IsEnabled         = canEdit,
        };
        browseBtn.Click += (_, _) =>
        {
            var dlg = new OpenFileDialog
            {
                Title  = DialogTitle,
                Filter = BuildDialogFilter(extensions),
            };
            if (dlg.ShowDialog() == true) CommitAbsolutePath(dlg.FileName);
        };

        // ── クリア（×）ボタン ────────────────────────────────
        var clearBtn = MakeIconButton(
            "Icon.Close", BrushText, "参照を解除する",
            () => onChange(string.Empty),
            iconSize: ClearButtonIconSize,
            isEnabled: hasPath);

        // ── 3 つを 1 つのコントロールへまとめる ───────────────
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(pathBox,   0); grid.Children.Add(pathBox);
        Grid.SetColumn(browseBtn, 1); grid.Children.Add(browseBtn);
        Grid.SetColumn(clearBtn,  2); grid.Children.Add(clearBtn);

        return MakeRow(field, null, grid);
    }

    /// <summary>
    /// パス表示欄へ「受け付ける拡張子のファイルだけを受け取るドロップ」を付ける。
    /// 拡張子が一致しないドロップはカーソルで拒否を示し、値も変えない。
    /// </summary>
    /// <param name="target">ドロップ先の要素。</param>
    /// <param name="dottedExtensions">受け付ける拡張子（ドット付き）。</param>
    /// <param name="onDropped">受理したときの絶対パス通知。</param>
    private static void AttachDrop(
        UIElement target, string[] dottedExtensions, Action<string> onDropped)
    {
        // ドラッグ中: 受け付けられるかどうかをカーソルで示す
        void UpdateEffect(DragEventArgs e)
        {
            e.Effects = FileRefBuilder.ExtractDroppedPath(e, dottedExtensions) is null
                ? DragDropEffects.None
                : DragDropEffects.Copy;
            e.Handled = true;
        }

        target.PreviewDragEnter += (_, e) => UpdateEffect(e);
        target.PreviewDragOver  += (_, e) => UpdateEffect(e);

        // 読み取り専用テキストボックス自身のドロップ処理より先に横取りする
        target.PreviewDrop += (_, e) =>
        {
            var dropped = FileRefBuilder.ExtractDroppedPath(e, dottedExtensions);
            if (dropped is not null) onDropped(dropped);
            e.Handled = true;
        };
    }

    /// <summary>
    /// 拡張子の並びからファイル選択ダイアログのフィルタ文字列を組む。
    /// 例: ttf, otf → "対応ファイル (*.ttf;*.otf)|*.ttf;*.otf|すべてのファイル|*.*"
    /// </summary>
    private static string BuildDialogFilter(IReadOnlyList<string> extensions)
    {
        var patterns = new List<string>(extensions.Count);
        foreach (var ext in extensions) patterns.Add("*." + ext);
        var joined = string.Join(";", patterns);

        return "対応ファイル (" + joined + ")|" + joined + "|" + DialogAllFilesFilter;
    }
}
