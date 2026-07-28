using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SEEDEditor.Panels;

/// <summary>
/// Inspector 等でファイルパス参照 UI（ラベル・パス表示・ドラッグ&amp;ドロップ・参照ボタン）を
/// 組み立てる静的ヘルパー。ドロップ受付は指定拡張子のみに制限する。
/// </summary>
internal static class FileRefBuilder
{
    /// <summary>
    /// ラベル・パス表示・参照ボタン（＋任意でクリアボタン）をまとめたファイル参照行を生成する。
    /// acceptedExtensions に含まれる拡張子のファイルのみドロップを受け付ける。
    /// </summary>
    /// <param name="onClear">
    /// 非 null のとき、行末に「×」クリアボタンを表示し、押下時にこのデリゲートを呼ぶ
    /// （参照を未設定へ戻す用途）。null のときクリアボタンは表示しない。
    /// 既存の呼び出し側をそのままコンパイルできるよう省略可能引数にしている。
    /// </param>
    public static UIElement Build(
        string label,
        string? currentPath,
        string[] acceptedExtensions,
        Func<string?> browseFn,
        Action<string> onPathSet,
        Action? onClear = null)
    {
        var hasPath = !string.IsNullOrEmpty(currentPath);
        var display = hasPath ? Path.GetFileName(currentPath) : "（未設定）";

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(48) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        // クリアボタン用の列（onClear が null のときは幅 0 のまま使われない）
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var lbl = new TextBlock
        {
            Text              = label,
            Foreground        = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            FontSize          = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var normalBorder = hasPath
            ? new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46))
            : new SolidColorBrush(Color.FromRgb(0x55, 0x44, 0x22));

        var pathBox = new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
            BorderBrush     = normalBorder,
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 2, 4, 2),
            CornerRadius    = new CornerRadius(2),
            Margin          = new Thickness(2, 0, 2, 0),
            AllowDrop       = true,
            ToolTip         = hasPath
                ? currentPath
                : "Project パネルまたはエクスプローラーから " + string.Join(" / ", acceptedExtensions) + " をドロップ",
        };

        var pathText = new TextBlock
        {
            Text              = display,
            Foreground        = hasPath
                ? new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC))
                : new SolidColorBrush(Color.FromRgb(0x88, 0x66, 0x44)),
            FontSize          = 11,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        pathBox.Child = pathText;

        void SetHover(bool active)
        {
            pathBox.Background  = new SolidColorBrush(active
                ? Color.FromRgb(0x1A, 0x33, 0x1A)
                : Color.FromRgb(0x1A, 0x1A, 0x1A));
            pathBox.BorderBrush = active
                ? new SolidColorBrush(Color.FromRgb(0x44, 0xAA, 0x44))
                : normalBorder;
        }

        pathBox.DragEnter += (_, e) =>
        {
            if (ExtractPath(e, acceptedExtensions) != null)
            {
                SetHover(true);
                // ドラッグ元が許可するエフェクトに合わせる（Move のみ許可の場合は Copy にすると Drop が発火しない）
                e.Effects = (e.AllowedEffects & DragDropEffects.Copy) != 0
                    ? DragDropEffects.Copy
                    : DragDropEffects.Move;
            }
            else e.Effects = DragDropEffects.None;
            e.Handled = true;
        };
        pathBox.DragOver += (_, e) =>
        {
            if (ExtractPath(e, acceptedExtensions) != null)
            {
                SetHover(true);
                e.Effects = (e.AllowedEffects & DragDropEffects.Copy) != 0
                    ? DragDropEffects.Copy
                    : DragDropEffects.Move;
            }
            else e.Effects = DragDropEffects.None;
            e.Handled = true;
        };
        pathBox.DragLeave += (_, e) =>
        {
            SetHover(false);
            e.Handled = true;
        };
        pathBox.Drop += (_, e) =>
        {
            SetHover(false);
            var path = ExtractPath(e, acceptedExtensions);
            if (path != null) onPathSet(path);
            e.Handled = true;
        };

        var browseBtn = new Button
        {
            Content           = "参照",
            Background        = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            Foreground        = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)),
            BorderBrush       = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
            BorderThickness   = new Thickness(1),
            FontSize          = 10,
            Padding           = new Thickness(6, 2, 6, 2),
            Cursor            = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            Template          = BuildButtonTemplate(),
        };
        browseBtn.Click += (_, _) =>
        {
            var path = browseFn();
            if (path != null) onPathSet(path);
        };

        Grid.SetColumn(lbl,       0); grid.Children.Add(lbl);
        Grid.SetColumn(pathBox,   1); grid.Children.Add(pathBox);
        Grid.SetColumn(browseBtn, 2); grid.Children.Add(browseBtn);

        // クリアボタン（指定があるときのみ）: 参照を未設定へ戻す
        if (onClear != null)
        {
            var clearBtn = new Button
            {
                Content           = "×",
                Background        = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
                Foreground        = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB)),
                BorderBrush       = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44)),
                BorderThickness   = new Thickness(1),
                FontSize          = 10,
                Padding           = new Thickness(6, 2, 6, 2),
                Margin            = new Thickness(2, 0, 0, 0),
                Cursor            = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Template          = BuildButtonTemplate(),
                ToolTip           = "参照を解除する",
                // 未設定のときは押しても意味がないため無効化する
                IsEnabled         = hasPath,
            };
            clearBtn.Click += (_, _) => onClear();
            Grid.SetColumn(clearBtn, 3); grid.Children.Add(clearBtn);
        }

        return grid;
    }

    private static string? ExtractPath(DragEventArgs e, string[] exts)
    {
        if (e.Data.GetDataPresent("SEEDProjectPaths") &&
            e.Data.GetData("SEEDProjectPaths") is string[] seedPaths &&
            seedPaths.Length == 1 &&
            exts.Any(ext => Path.GetExtension(seedPaths[0]).Equals(ext, StringComparison.OrdinalIgnoreCase)))
            return seedPaths[0];

        if (e.Data.GetDataPresent(DataFormats.FileDrop) &&
            e.Data.GetData(DataFormats.FileDrop) is string[] files &&
            files.Length == 1 &&
            exts.Any(ext => Path.GetExtension(files[0]).Equals(ext, StringComparison.OrdinalIgnoreCase)))
            return files[0];

        return null;
    }

    internal static ControlTemplate BuildButtonTemplate()
    {
        var t  = new ControlTemplate(typeof(Button));
        var bd = new FrameworkElementFactory(typeof(Border));
        bd.SetBinding(Border.BackgroundProperty,
            new System.Windows.Data.Binding("Background")
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        bd.SetBinding(Border.BorderBrushProperty,
            new System.Windows.Data.Binding("BorderBrush")
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        bd.SetBinding(Border.BorderThicknessProperty,
            new System.Windows.Data.Binding("BorderThickness")
            { RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent) });
        bd.SetValue(Border.CornerRadiusProperty, new CornerRadius(2));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(ContentPresenter.HorizontalAlignmentProperty, HorizontalAlignment.Center);
        cp.SetValue(ContentPresenter.VerticalAlignmentProperty,   VerticalAlignment.Center);
        bd.AppendChild(cp);
        t.VisualTree = bd;
        return t;
    }
}
