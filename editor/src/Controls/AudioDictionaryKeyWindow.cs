using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SEEDEditor.Controls;

/// <summary>
/// 音声辞書のキーを選ぶ小さなモーダルウィンドウ。
///
/// AudioComponent の音源欄へ「AudioDictionary を持つアクタ」をドロップしたときに開き、
/// その辞書が持つキー（<c>グループ名/用途名</c>）を<b>グループごとにまとめて</b>提示する。
///
/// <see cref="ReferenceSelectorWindow"/> とは別クラスにしている理由:
/// あちらは「文字列の 1 次元リストを多段で選ばせる」汎用ウィンドウで、
/// グループ見出し（選択不可の行）を表現できない。
/// 辞書は「グループでまとめて見せる」ことが要件そのものなので、
/// 見出し付きリストを持つ専用ウィンドウを用意する。
/// </summary>
internal sealed class AudioDictionaryKeyWindow : Window
{
    // ── レイアウト定数（マジックナンバー回避）────────────────────
    private const double WindowWidth         = 360;
    private const double ListMaxHeight       = 320;
    private const double ContentMargin       = 12;
    private const double DescriptionFontSize = 11;
    private const double GroupHeaderFontSize = 11;
    private const double KeyFontSize         = 12;
    private const double PathFontSize        = 10;
    private const double GroupHeaderTopGap   = 6;
    private const double RowIndent           = 10;

    // ── 配色（エディタ全体のダークテーマに合わせる）──────────────
    private static readonly Brush BrushWindowBg   = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly Brush BrushListBg     = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
    private static readonly Brush BrushText       = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly Brush BrushSubText    = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly Brush BrushGroupText  = new SolidColorBrush(Color.FromRgb(0x7E, 0xC8, 0xE3));
    private static readonly Brush BrushBorder     = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));

    /// <summary>選択された行の項目（キャンセル時は null）。</summary>
    private AudioDictionaryCatalog.KeyRow? _result;

    /// <summary>キー行を並べるリスト（グループ見出しは IsEnabled=false の項目で表す）。</summary>
    private readonly ListBox _listBox;

    private AudioDictionaryKeyWindow(
        Window? owner, string actorName,
        IReadOnlyList<AudioDictionaryCatalog.KeyGroup> keyGroups)
    {
        Width                 = WindowWidth;
        SizeToContent         = SizeToContent.Height;
        Owner                 = owner;
        WindowStartupLocation = owner is null
            ? WindowStartupLocation.CenterScreen
            : WindowStartupLocation.CenterOwner;
        Background            = BrushWindowBg;
        ResizeMode            = ResizeMode.NoResize;
        Title                 = "音声辞書のキーを選択";

        var root = new StackPanel { Margin = new Thickness(ContentMargin) };

        root.Children.Add(new TextBlock
        {
            Text = $"「{actorName}」の音声辞書には "
                   + $"{AudioDictionaryCatalog.CountKeys(keyGroups)} 件のキーがあります。\n"
                   + "この音源に使うキーを選択してください。",
            Foreground   = BrushText,
            FontSize     = DescriptionFontSize,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 8),
        });

        _listBox = new ListBox
        {
            Background  = BrushListBg,
            Foreground  = BrushText,
            BorderBrush = BrushBorder,
            MaxHeight   = ListMaxHeight,
            Margin      = new Thickness(0, 0, 0, 8),
        };
        _listBox.MouseDoubleClick += (_, _) => Commit();

        // ── グループ見出し + キー行を積む ──
        // 見出しは IsEnabled=false の ListBoxItem にして「選べない行」にする
        //（見出しを選んで確定 → 解決できないキーが保存される、という事故を構造的に防ぐ）。
        var isFirstGroup = true;
        foreach (var group in keyGroups)
        {
            _listBox.Items.Add(new ListBoxItem
            {
                Content = new TextBlock
                {
                    Text       = group.GroupName,
                    Foreground = BrushGroupText,
                    FontSize   = GroupHeaderFontSize,
                    FontWeight = FontWeights.Bold,
                },
                IsEnabled = false,
                Focusable = false,
                Margin    = new Thickness(0, isFirstGroup ? 0 : GroupHeaderTopGap, 0, 0),
            });
            isFirstGroup = false;

            foreach (var row in group.Rows)
            {
                // 用途名（主表示）とパス（副表示）を 2 行で見せる。
                // 保存されるのはキー文字列だが、選ぶ人が知りたいのは「どのファイルか」なので
                // パスも必ず出す。
                var content = new StackPanel { Margin = new Thickness(RowIndent, 0, 0, 0) };
                content.Children.Add(new TextBlock
                {
                    Text       = row.Usage,
                    Foreground = BrushText,
                    FontSize   = KeyFontSize,
                });
                content.Children.Add(new TextBlock
                {
                    Text         = row.Path,
                    Foreground   = BrushSubText,
                    FontSize     = PathFontSize,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                });

                _listBox.Items.Add(new ListBoxItem
                {
                    Content = content,
                    Tag     = row,
                    ToolTip = $"{row.Key}\n{row.Path}",
                });
            }
        }
        root.Children.Add(_listBox);

        var okButton = new Button { Content = "選択", Padding = new Thickness(12, 4, 12, 4) };
        okButton.Click += (_, _) => Commit();
        var cancelButton = new Button
        {
            Content = "キャンセル",
            Padding = new Thickness(12, 4, 12, 4),
            Margin  = new Thickness(0, 0, 6, 0),
        };
        cancelButton.Click += (_, _) => Close();

        var buttons = new StackPanel
        {
            Orientation         = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        buttons.Children.Add(cancelButton);
        buttons.Children.Add(okButton);
        root.Children.Add(buttons);

        Content = root;

        // Enter で確定 / Esc でキャンセル
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Return or Key.Enter) { Commit(); e.Handled = true; }
            else if (e.Key == Key.Escape)         { Close();  e.Handled = true; }
        };
    }

    /// <summary>
    /// キー選択ウィンドウを開き、選ばれたキー行を返す（キャンセル・候補なしは null）。
    /// </summary>
    /// <param name="owner">親ウィンドウ（中央寄せに使う）。</param>
    /// <param name="actorName">辞書を持つアクタの名前（説明文に出す）。</param>
    /// <param name="keyGroups">グループごとにまとめたキー一覧。</param>
    public static AudioDictionaryCatalog.KeyRow? Show(
        Window? owner, string actorName,
        IReadOnlyList<AudioDictionaryCatalog.KeyGroup> keyGroups)
    {
        if (keyGroups.Count == 0) return null;
        var win = new AudioDictionaryKeyWindow(owner, actorName, keyGroups);
        win.ShowDialog();
        return win._result;
    }

    /// <summary>選択中の行を確定してウィンドウを閉じる（見出し行・未選択では何もしない）。</summary>
    private void Commit()
    {
        if (_listBox.SelectedItem is ListBoxItem { Tag: AudioDictionaryCatalog.KeyRow row })
        {
            _result = row;
            Close();
        }
    }
}
