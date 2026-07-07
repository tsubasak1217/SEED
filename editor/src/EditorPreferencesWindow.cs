using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace SEEDEditor;

/// <summary>
/// エディタ全体の環境設定ウィンドウ（メニューバー「編集 → 環境設定...」から開く）。
///
/// 特定パネルに属さない操作系の好み（タッチパッドスクロール係数など）を編集し、
/// <see cref="EditorPreferences"/>（editor_preferences.json）へ保存する。
/// 今後、環境設定の項目が増えたらこのウィンドウへ行を追加していく。
/// </summary>
public sealed class EditorPreferencesWindow : Window
{
    // ── ダークタイトルバー用 P/Invoke ─────────────────────────
    private const int DwmwaUseImmersiveDarkMode = 20;
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);

    // ── レイアウト定数 ────────────────────────────────────────
    private const double WindowW = 420;
    private const double WindowH = 330;
    private const double LabelColumnWidth = 140;

    // ── 配色（他の設定ウィンドウと統一したダークテーマ）────────
    private static readonly Brush Bg     = new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E));
    private static readonly Brush FieldBg= new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
    private static readonly Brush Text   = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly Brush Dim    = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));
    private static readonly Brush Border = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));

    /// <summary>スクロール係数の入力フィールド。</summary>
    private readonly TextBox _tbScrollScale;

    /// <summary>「選択でシーンタブを自動切替」のチェックボックス。</summary>
    private readonly CheckBox _cbSceneTabAutoSwitch;

    public EditorPreferencesWindow()
    {
        Title       = "環境設定";
        Width       = WindowW;
        Height      = WindowH;
        Background  = Bg;
        ResizeMode  = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        // ── スクロール係数の入力行 ──
        _tbScrollScale = MakeField(
            EditorPreferences.Instance.TouchpadScrollScale.ToString("0.##", CultureInfo.InvariantCulture));

        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = "エディタ全体の操作設定", Foreground = Text, FontSize = 14,
            FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 0, 12),
        });
        panel.Children.Add(MakeRow("スクロール係数", _tbScrollScale));
        panel.Children.Add(new TextBlock
        {
            Text = $"タッチパッド（精密スクロール）の縦・横スクロールの強さ（{EditorPreferences.ScrollScaleMin}〜{EditorPreferences.ScrollScaleMax}）。\n"
                 + "1.0 が標準で、小さくするほどゆっくりスクロールします。\n"
                 + "全パネル・全ウィンドウに適用されます。物理マウスホイールには影響しません。",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });

        // ── シーンタブ自動切替のチェックボックス行 ──
        _cbSceneTabAutoSwitch = new CheckBox
        {
            Content    = "選択でシーンタブを自動切替",
            IsChecked  = EditorPreferences.Instance.SceneTabAutoSwitch,
            Foreground = Text,
            FontSize   = 12,
            Margin     = new Thickness(0, 16, 0, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        panel.Children.Add(_cbSceneTabAutoSwitch);
        panel.Children.Add(new TextBlock
        {
            Text = "ヒエラルキーで選択したアクターの種類（2D/3D）に合わせて、\n"
                 + "ビューポートのシーンタブ（3Dシーン/2Dシーン）を自動で切り替えます。",
            Foreground = Dim, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
        });

        // ── OK / キャンセル ──
        var btnRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(16, 0, 16, 12),
        };
        var okBtn     = MakeButton("保存");
        var cancelBtn = MakeButton("キャンセル");
        okBtn.Click += (_, _) => { ApplyAndSave(); DialogResult = true; Close(); };
        cancelBtn.Click += (_, _) => { DialogResult = false; Close(); };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(okBtn);

        var root = new DockPanel();
        DockPanel.SetDock(btnRow, Dock.Bottom);
        root.Children.Add(btnRow);
        root.Children.Add(panel);
        Content = root;

        // ダークタイトルバーを適用する
        SourceInitialized += (_, _) =>
        {
            int dark = 1;
            DwmSetWindowAttribute(new WindowInteropHelper(this).Handle,
                DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
        };
    }

    /// <summary>入力値を検証して EditorPreferences へ反映・保存する（範囲外はクランプ）。</summary>
    private void ApplyAndSave()
    {
        if (double.TryParse(_tbScrollScale.Text.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out var scale))
        {
            EditorPreferences.Instance.TouchpadScrollScale =
                Math.Clamp(scale, EditorPreferences.ScrollScaleMin, EditorPreferences.ScrollScaleMax);
        }
        // シーンタブ自動切替の設定を反映する
        EditorPreferences.Instance.SceneTabAutoSwitch = _cbSceneTabAutoSwitch.IsChecked == true;
        EditorPreferences.Save();
    }

    // ── UI 部品生成 ───────────────────────────────────────────

    /// <summary>ダークテーマの入力フィールドを生成する。</summary>
    private static TextBox MakeField(string initial) => new()
    {
        Text            = initial,
        Background      = FieldBg,
        Foreground      = Text,
        BorderBrush     = Border,
        Padding         = new Thickness(4, 2, 4, 2),
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    /// <summary>「ラベル | 入力フィールド」の 1 行を生成する。</summary>
    private static Grid MakeRow(string label, UIElement field)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(LabelColumnWidth) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var lb = new TextBlock
        {
            Text = label, Foreground = Text, FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(lb, 0);    row.Children.Add(lb);
        Grid.SetColumn(field, 1); row.Children.Add(field);
        return row;
    }

    /// <summary>ダークテーマのボタンを生成する。</summary>
    private static Button MakeButton(string caption) => new()
    {
        Content    = caption,
        Background = FieldBg,
        Foreground = Text,
        BorderBrush= Border,
        Padding    = new Thickness(14, 4, 14, 4),
        Margin     = new Thickness(8, 0, 0, 0),
    };
}
