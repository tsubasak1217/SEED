// ============================================================
//  PackagingWindow.xaml.cs — パッケージ化ウィンドウ
//
//  【概要】
//  プラットフォームごとのビルド設定を管理し、
//  cargo build を呼び出してゲームをパッケージ化する。
//
//  【対応プラットフォーム】
//  ・Windows   — このマシンから直接ビルド可能
//  ・macOS     — macOS 上でのビルドが必要（CI / osxcross）
//  ・Android   — Android NDK + cargo-ndk が必要
//  ・iOS       — macOS + Xcode でのビルドが必要
//  ・PS5       — ライセンス契約が必要
//  ・Switch    — ライセンス契約が必要
// ============================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;

namespace SEEDEditor.Packaging;

public partial class PackagingWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    // ── フィールド ───────────────────────────────────────────

    /// <summary>プロジェクトの Assets フォルダパス。</summary>
    private readonly string _assetsPath;

    /// <summary>runtime フォルダパス（cargo build の実行ディレクトリ）。</summary>
    private readonly string _runtimePath;

    private PackagingData  _data;
    private TargetPlatform _selectedPlatform = TargetPlatform.Windows;
    private Border?        _selectedPlatformBorder;
    private bool           _isBuilding;

    /// <summary>ゲーム名入力フィールド（設定ペイン共通ヘッダー部分）。</summary>
    private TextBox?       _tbGameName;

    // ── プラットフォームメタ一覧 ─────────────────────────────

    private static readonly List<PlatformInfo> Platforms = [
        new(TargetPlatform.Windows,
            "Windows", "🖥",
            PlatformAvailability.Available),
        new(TargetPlatform.macOS,
            "macOS", "🍎",
            PlatformAvailability.RequiresOtherOS,
            "macOS 上でのビルドが必要です（CI / GitHub Actions を推奨）"),
        new(TargetPlatform.Android,
            "Android", "🤖",
            PlatformAvailability.RequiresSetup,
            "Android NDK と cargo-ndk のセットアップが必要です"),
        new(TargetPlatform.iOS,
            "iOS", "📱",
            PlatformAvailability.RequiresOtherOS,
            "macOS + Xcode 上でのビルドが必要です"),
        new(TargetPlatform.PlayStation5,
            "PlayStation 5", "🎮",
            PlatformAvailability.RequiresLicense,
            "Sony Interactive Entertainment のライセンス契約が必要です"),
        new(TargetPlatform.NintendoSwitch,
            "Nintendo Switch", "🕹",
            PlatformAvailability.RequiresLicense,
            "Nintendo のライセンス契約が必要です"),
    ];

    // ── ブラシ ───────────────────────────────────────────────

    private static readonly SolidColorBrush BrushSelected  = new(Color.FromRgb(0x1A, 0x2A, 0x3A));
    private static readonly SolidColorBrush BrushAvailable = new(Color.FromRgb(0x33, 0x99, 0x55));
    private static readonly SolidColorBrush BrushWarn      = new(Color.FromRgb(0xAA, 0x88, 0x22));
    private static readonly SolidColorBrush BrushDisabled  = new(Color.FromRgb(0x55, 0x55, 0x55));

    // ── コンストラクタ ───────────────────────────────────────

    public PackagingWindow(string assetsPath)
    {
        InitializeComponent();
        _assetsPath  = assetsPath;
        // runtime フォルダはプロジェクトルート直下にある想定
        _runtimePath = Path.GetFullPath(Path.Combine(assetsPath, "..", "..", "runtime"));

        var settingsPath = Path.Combine(assetsPath, "packaging_settings.json");
        _data = PackagingData.LoadFrom(settingsPath);
    }

    /// <summary>
    /// 有効なゲーム名を取得する。
    /// 優先順: パッケージ設定の game_name → project_settings.json の game_name → assetsの親フォルダ名
    /// </summary>
    private string GetGameName()
    {
        // 1. パッケージ設定の入力値を最優先する
        if (!string.IsNullOrWhiteSpace(_data.GameName))
            return SanitizeFileName(_data.GameName);

        // 2. project_settings.json のゲーム名
        var projectSettingsPath = Path.Combine(_assetsPath, "project_settings.json");
        if (File.Exists(projectSettingsPath))
        {
            try
            {
                var json = File.ReadAllText(projectSettingsPath);
                var doc  = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("game_name", out var prop))
                {
                    var name = prop.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) return SanitizeFileName(name);
                }
            }
            catch { /* パース失敗時はフォールバック */ }
        }

        // 3. フォールバック: assets の親フォルダ名
        return SanitizeFileName(Path.GetFileName(Path.GetDirectoryName(_assetsPath)) ?? "Game");
    }

    /// <summary>ファイル名として使えない文字を除去・置換する。</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new System.Text.StringBuilder();
        foreach (var c in name)
            sb.Append(invalid.Contains(c) ? '_' : c);
        var result = sb.ToString().Trim();
        return string.IsNullOrEmpty(result) ? "Game" : result;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        int dark = 1;
        DwmSetWindowAttribute(helper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        BuildPlatformList();
        SelectPlatform(TargetPlatform.Windows);
    }

    // ── プラットフォーム一覧 ─────────────────────────────────

    /// <summary>左ペインのプラットフォーム一覧を構築する。</summary>
    private void BuildPlatformList()
    {
        PlatformList.Children.Clear();
        foreach (var info in Platforms)
        {
            var border = BuildPlatformRow(info);
            PlatformList.Children.Add(border);
        }
    }

    /// <summary>プラットフォーム 1 行分の UI を構築する。</summary>
    private Border BuildPlatformRow(PlatformInfo info)
    {
        // 可用性バッジ色
        var (badgeColor, badgeText) = info.Availability switch
        {
            PlatformAvailability.Available       => (BrushAvailable, "✓"),
            PlatformAvailability.RequiresSetup   => (BrushWarn,      "⚙"),
            PlatformAvailability.RequiresOtherOS => (BrushWarn,      "⚠"),
            PlatformAvailability.RequiresLicense => (BrushDisabled,  "✕"),
            _ => (BrushDisabled, "?"),
        };

        var badge = new Border
        {
            Width           = 18,
            Height          = 18,
            CornerRadius    = new CornerRadius(9),
            Background      = badgeColor,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text              = badgeText,
                FontSize          = 9,
                Foreground        = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
            },
        };

        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        sp.Children.Add(new TextBlock
        {
            Text              = info.Icon,
            FontSize          = 18,
            Margin            = new Thickness(0, 0, 8, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        sp.Children.Add(new TextBlock
        {
            Text              = info.DisplayName,
            FontSize          = 12,
            Foreground        = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            VerticalAlignment = VerticalAlignment.Center,
        });

        var inner = new Grid();
        inner.Children.Add(sp);
        inner.Children.Add(badge);

        var border = new Border
        {
            Padding    = new Thickness(10, 8, 10, 8),
            Background = Brushes.Transparent,
            Cursor     = Cursors.Hand,
            Child      = inner,
            Tag        = info,
        };
        border.MouseEnter += (_, _) =>
        {
            if (border != _selectedPlatformBorder)
                border.Background = new SolidColorBrush(Color.FromRgb(0x28, 0x28, 0x28));
        };
        border.MouseLeave += (_, _) =>
        {
            if (border != _selectedPlatformBorder) border.Background = Brushes.Transparent;
        };
        border.MouseLeftButtonDown += (_, _) => SelectPlatform(info.Platform);

        return border;
    }

    /// <summary>プラットフォームを選択して右ペインを更新する。</summary>
    private void SelectPlatform(TargetPlatform platform)
    {
        _selectedPlatform = platform;

        // ハイライト更新
        if (_selectedPlatformBorder != null) _selectedPlatformBorder.Background = Brushes.Transparent;
        foreach (var child in PlatformList.Children)
        {
            if (child is Border b && b.Tag is PlatformInfo info && info.Platform == platform)
            {
                _selectedPlatformBorder = b;
                b.Background = BrushSelected;
                break;
            }
        }

        // 右ペイン更新
        BuildSettingsPane(platform);

        // ビルドボタンの有効/無効（ライセンス必須は無効）
        var meta = Platforms.Find(p => p.Platform == platform)!;
        BtnBuild.IsEnabled = meta.Availability != PlatformAvailability.RequiresLicense && !_isBuilding;
    }

    // ── 設定ペイン ───────────────────────────────────────────

    /// <summary>右ペインを選択プラットフォームの設定 UI に切り替える。</summary>
    private void BuildSettingsPane(TargetPlatform platform)
    {
        SettingsPane.Children.Clear();

        var meta = Platforms.Find(p => p.Platform == platform)!;

        // プラットフォームタイトル
        SettingsPane.Children.Add(BuildSectionHeader(meta.Icon + "  " + meta.DisplayName));

        // 注記（利用不可・要セットアップの場合）
        if (!string.IsNullOrEmpty(meta.Note))
        {
            SettingsPane.Children.Add(BuildNoteBlock(meta.Note, meta.Availability));
        }

        // ── ゲーム名（全プラットフォーム共通） ─────────────────
        SettingsPane.Children.Add(BuildSectionSubHeader("共通設定"));

        _tbGameName = new TextBox
        {
            Style = (Style)Resources["SettingTextBox"],
            // 保存済み値があればそれを、なければ推定値を初期値として表示する
            Text  = !string.IsNullOrEmpty(_data.GameName) ? _data.GameName : GetGameName(),
        };
        _tbGameName.TextChanged += (_, _) => _data.GameName = _tbGameName.Text.Trim();

        var gameNameGrid = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        gameNameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        gameNameGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var gameNameLabel = new TextBlock { Style = (Style)Resources["SettingLabel"], Text = "ゲーム名" };
        Grid.SetColumn(gameNameLabel, 0);
        Grid.SetColumn(_tbGameName, 1);
        gameNameGrid.Children.Add(gameNameLabel);
        gameNameGrid.Children.Add(_tbGameName);
        SettingsPane.Children.Add(gameNameGrid);

        // 各プラットフォームの設定 UI
        switch (platform)
        {
            case TargetPlatform.Windows:
                BuildWindowsSettings();
                break;
            case TargetPlatform.macOS:
                BuildMacOsSettings();
                break;
            case TargetPlatform.Android:
                BuildAndroidSettings();
                break;
            case TargetPlatform.iOS:
                BuildIosSettings();
                break;
            case TargetPlatform.PlayStation5:
            case TargetPlatform.NintendoSwitch:
                BuildLicenseRequiredPane(meta);
                break;
        }
    }

    // ── Windows 設定 ─────────────────────────────────────────

    private void BuildWindowsSettings()
    {
        SettingsPane.Children.Add(BuildSectionSubHeader("出力設定"));

        // 出力フォルダ
        SettingsPane.Children.Add(BuildFolderRow("出力フォルダ", _data.Windows.OutputPath,
            path => _data.Windows.OutputPath = path));

        SettingsPane.Children.Add(BuildSectionSubHeader("ビルド設定"));

        // ビルド種別
        SettingsPane.Children.Add(BuildComboRow("ビルド種別",
            ["Release", "Debug"],
            _data.Windows.BuildType == BuildType.Debug ? "Debug" : "Release",
            v => _data.Windows.BuildType = v == "Debug" ? BuildType.Debug : BuildType.Release));

        // アーキテクチャ
        SettingsPane.Children.Add(BuildComboRow("アーキテクチャ",
            ["x64 (x86_64)", "Arm64"],
            _data.Windows.Arch == WindowsArch.Arm64 ? "Arm64" : "x64 (x86_64)",
            v => _data.Windows.Arch = v == "Arm64" ? WindowsArch.Arm64 : WindowsArch.X64));

        SettingsPane.Children.Add(BuildSectionSubHeader("ビルド手順"));
        SettingsPane.Children.Add(BuildInfoBlock(
            "1. cargo build --release でランタイムをコンパイル\n" +
            "2. 出力フォルダに実行ファイルとアセットをコピー\n" +
            "3. 出力フォルダを配布先に渡す"));
    }

    // ── macOS 設定 ──────────────────────────────────────────

    private void BuildMacOsSettings()
    {
        SettingsPane.Children.Add(BuildSectionSubHeader("出力設定"));
        SettingsPane.Children.Add(BuildFolderRow("出力フォルダ", _data.MacOs.OutputPath,
            path => _data.MacOs.OutputPath = path));

        SettingsPane.Children.Add(BuildSectionSubHeader("ビルド設定"));
        SettingsPane.Children.Add(BuildComboRow("ビルド種別",
            ["Release", "Debug"],
            _data.MacOs.BuildType == BuildType.Debug ? "Debug" : "Release",
            v => _data.MacOs.BuildType = v == "Debug" ? BuildType.Debug : BuildType.Release));
        SettingsPane.Children.Add(BuildComboRow("アーキテクチャ",
            ["Arm64 (Apple Silicon)", "x64 (Intel)", "Universal Binary"],
            _data.MacOs.Arch switch
            {
                MacArch.X64 => "x64 (Intel)",
                MacArch.Universal => "Universal Binary",
                _ => "Arm64 (Apple Silicon)",
            },
            v => _data.MacOs.Arch = v switch
            {
                "x64 (Intel)"      => MacArch.X64,
                "Universal Binary" => MacArch.Universal,
                _                  => MacArch.Arm64,
            }));

        SettingsPane.Children.Add(BuildSectionSubHeader("CI ビルドについて"));
        SettingsPane.Children.Add(BuildInfoBlock(
            "macOS 向けビルドには macOS 環境が必要です。\n" +
            "GitHub Actions の macos-latest ランナーを使用した\n" +
            "CI/CD パイプラインでのビルドを推奨します。\n\n" +
            "設定を保存後、CI 側で以下のコマンドを実行：\n" +
            "  cargo build --release --target aarch64-apple-darwin"));
    }

    // ── Android 設定 ─────────────────────────────────────────

    private void BuildAndroidSettings()
    {
        SettingsPane.Children.Add(BuildSectionSubHeader("出力設定"));
        SettingsPane.Children.Add(BuildFolderRow("出力フォルダ", _data.Android.OutputPath,
            path => _data.Android.OutputPath = path));

        SettingsPane.Children.Add(BuildSectionSubHeader("ビルド設定"));
        SettingsPane.Children.Add(BuildComboRow("ビルド種別",
            ["Release", "Debug"],
            _data.Android.BuildType == BuildType.Debug ? "Debug" : "Release",
            v => _data.Android.BuildType = v == "Debug" ? BuildType.Debug : BuildType.Release));
        SettingsPane.Children.Add(BuildComboRow("アーキテクチャ",
            ["arm64-v8a", "x86_64"],
            _data.Android.Arch == AndroidArch.X86_64 ? "x86_64" : "arm64-v8a",
            v => _data.Android.Arch = v == "x86_64" ? AndroidArch.X86_64 : AndroidArch.Arm64V8a));

        SettingsPane.Children.Add(BuildSectionSubHeader("NDK 設定"));
        SettingsPane.Children.Add(BuildFolderRow("Android NDK パス", _data.Android.NdkPath,
            path => _data.Android.NdkPath = path));

        SettingsPane.Children.Add(BuildSectionSubHeader("セットアップ手順"));
        SettingsPane.Children.Add(BuildInfoBlock(
            "1. Android NDK をインストール\n" +
            "2. cargo install cargo-ndk\n" +
            "3. rustup target add aarch64-linux-android\n" +
            "4. NDK パスを上記フィールドに設定\n\n" +
            "ビルドコマンド例：\n" +
            "  cargo ndk --target arm64-v8a build --release"));
    }

    // ── iOS 設定 ─────────────────────────────────────────────

    private void BuildIosSettings()
    {
        SettingsPane.Children.Add(BuildSectionSubHeader("出力設定"));
        SettingsPane.Children.Add(BuildFolderRow("出力フォルダ", _data.Ios.OutputPath,
            path => _data.Ios.OutputPath = path));

        SettingsPane.Children.Add(BuildSectionSubHeader("ビルド設定"));
        SettingsPane.Children.Add(BuildComboRow("ビルド種別",
            ["Release", "Debug"],
            _data.Ios.BuildType == BuildType.Debug ? "Debug" : "Release",
            v => _data.Ios.BuildType = v == "Debug" ? BuildType.Debug : BuildType.Release));

        SettingsPane.Children.Add(BuildSectionSubHeader("CI ビルドについて"));
        SettingsPane.Children.Add(BuildInfoBlock(
            "iOS 向けビルドには macOS + Xcode が必要です。\n" +
            "GitHub Actions の macos-latest ランナーを推奨します。\n\n" +
            "rustup target add aarch64-apple-ios\n" +
            "cargo build --release --target aarch64-apple-ios"));
    }

    // ── ライセンス必須プラットフォーム ───────────────────────

    private void BuildLicenseRequiredPane(PlatformInfo meta)
    {
        SettingsPane.Children.Add(BuildSectionSubHeader("ライセンスについて"));

        var contactInfo = meta.Platform switch
        {
            TargetPlatform.PlayStation5 =>
                "Sony Interactive Entertainment (SIE)\n" +
                "PlayStation Partner Program への登録が必要です。\n" +
                "https://partners.playstation.net/",
            TargetPlatform.NintendoSwitch =>
                "Nintendo Developer Portal\n" +
                "Nintendo Switch 開発者プログラムへの参加が必要です。\n" +
                "https://developer.nintendo.com/",
            _ => "",
        };

        SettingsPane.Children.Add(BuildInfoBlock(
            $"{meta.DisplayName} 向けの開発には、プラットフォームホルダーとの\n" +
            $"ライセンス契約および専用 SDK の入手が必要です。\n\n" +
            $"{contactInfo}",
            isWarning: true));
    }

    // ── UI ヘルパー ──────────────────────────────────────────

    private static UIElement BuildSectionHeader(string title)
    {
        return new TextBlock
        {
            Text       = title,
            FontSize   = 16,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            Margin     = new Thickness(0, 0, 0, 12),
        };
    }

    private static UIElement BuildSectionSubHeader(string title)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 12, 0, 8) };
        sp.Children.Add(new TextBlock
        {
            Text       = title,
            FontSize   = 11,
            FontWeight = FontWeights.Bold,
            Foreground = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88)),
            Margin     = new Thickness(0, 0, 0, 4),
        });
        sp.Children.Add(new Border
        {
            Height          = 1,
            Background      = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
        });
        return sp;
    }

    private static UIElement BuildNoteBlock(string note, PlatformAvailability avail)
    {
        var bg = avail == PlatformAvailability.RequiresLicense
            ? Color.FromRgb(0x3A, 0x1A, 0x1A)
            : Color.FromRgb(0x3A, 0x33, 0x1A);
        var fg = avail == PlatformAvailability.RequiresLicense
            ? Color.FromRgb(0xFF, 0x88, 0x88)
            : Color.FromRgb(0xFF, 0xCC, 0x66);

        return new Border
        {
            Background      = new SolidColorBrush(bg),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x55, 0x44, 0x22)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(10, 8, 10, 8),
            Margin          = new Thickness(0, 0, 0, 8),
            Child = new TextBlock
            {
                Text       = note,
                FontSize   = 11,
                Foreground = new SolidColorBrush(fg),
                TextWrapping = TextWrapping.Wrap,
            },
        };
    }

    private static UIElement BuildInfoBlock(string text, bool isWarning = false)
    {
        var fg = isWarning
            ? Color.FromRgb(0xFF, 0x88, 0x88)
            : Color.FromRgb(0x88, 0x88, 0x88);

        return new Border
        {
            Background      = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22)),
            BorderBrush     = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33)),
            BorderThickness = new Thickness(1),
            CornerRadius    = new CornerRadius(4),
            Padding         = new Thickness(10, 8, 10, 8),
            Margin          = new Thickness(0, 4, 0, 4),
            Child = new TextBlock
            {
                Text         = text,
                FontFamily   = new FontFamily("Consolas"),
                FontSize     = 10,
                Foreground   = new SolidColorBrush(fg),
                TextWrapping = TextWrapping.Wrap,
            },
        };
    }

    /// <summary>フォルダパス入力行（テキストボックス＋参照ボタン）を構築する。</summary>
    private UIElement BuildFolderRow(string label, string currentPath, Action<string> onChanged)
    {
        var tb = new TextBox
        {
            Style = (Style)Resources["SettingTextBox"],
            Text  = currentPath,
        };
        tb.TextChanged += (_, _) => onChanged(tb.Text.Trim());

        var btn = new Button { Style = (Style)Resources["BrowseBtn"] };
        btn.Click += (_, _) =>
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog
            {
                Title            = label + " フォルダを選択",
                InitialDirectory = (!string.IsNullOrEmpty(tb.Text) && Directory.Exists(tb.Text))
                                   ? tb.Text : null,
            };
            if (dlg.ShowDialog() == true)
            {
                tb.Text = dlg.FolderName;
                onChanged(dlg.FolderName);
            }
        };

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });

        var lbl = new TextBlock { Style = (Style)Resources["SettingLabel"], Text = label };
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(tb,  1);
        Grid.SetColumn(btn, 2);
        grid.Children.Add(lbl);
        grid.Children.Add(tb);
        grid.Children.Add(btn);
        return grid;
    }

    /// <summary>コンボボックス選択行を構築する。</summary>
    private UIElement BuildComboRow(string label, string[] options, string current, Action<string> onChanged)
    {
        var cb = new ComboBox { Style = (Style)Resources["SettingCombo"] };
        foreach (var opt in options) cb.Items.Add(opt);
        cb.SelectedItem = current;
        cb.SelectionChanged += (_, _) =>
        {
            if (cb.SelectedItem is string v) onChanged(v);
        };

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock { Style = (Style)Resources["SettingLabel"], Text = label };
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(cb,  1);
        grid.Children.Add(lbl);
        grid.Children.Add(cb);
        return grid;
    }

    // ── ビルド実行 ───────────────────────────────────────────

    private async void OnBuild(object sender, RoutedEventArgs e)
    {
        if (_isBuilding) return;

        // 出力パス検証
        var outputPath = GetCurrentOutputPath();
        if (string.IsNullOrWhiteSpace(outputPath))
        {
            AppendLog("❌ 出力フォルダが設定されていません。");
            return;
        }

        // 設定を保存してからビルドを開始する
        SaveSettings();

        _isBuilding            = true;
        BtnBuild.IsEnabled     = false;
        BuildProgress.Value    = 0;
        BuildProgress.Visibility = Visibility.Visible;

        try
        {
            await RunBuildAsync(_selectedPlatform, outputPath);
        }
        finally
        {
            _isBuilding        = false;
            BtnBuild.IsEnabled = true;
        }
    }

    /// <summary>現在選択中のプラットフォームの出力パスを返す。</summary>
    private string GetCurrentOutputPath() => _selectedPlatform switch
    {
        TargetPlatform.Windows => _data.Windows.OutputPath,
        TargetPlatform.macOS   => _data.MacOs.OutputPath,
        TargetPlatform.Android => _data.Android.OutputPath,
        TargetPlatform.iOS     => _data.Ios.OutputPath,
        _ => "",
    };

    /// <summary>非同期でビルドを実行する。</summary>
    private async Task RunBuildAsync(TargetPlatform platform, string outputPath)
    {
        AppendLog($"═══ ビルド開始: {Platforms.Find(p => p.Platform == platform)!.DisplayName} ═══");
        AppendLog($"出力先: {outputPath}");
        AppendLog("");

        SetStatus("cargo build を実行中...");
        SetProgress(10);

        // cargo ターゲットとバイナリ名を決定する
        var (cargoTarget, binaryName, buildArgs) = platform switch
        {
            TargetPlatform.Windows when _data.Windows.Arch == WindowsArch.Arm64 =>
                ("aarch64-pc-windows-msvc", "SEED.exe",
                 BuildArgs(_data.Windows.BuildType, "aarch64-pc-windows-msvc")),
            TargetPlatform.Windows =>
                ("x86_64-pc-windows-msvc", "SEED.exe",
                 BuildArgs(_data.Windows.BuildType, "x86_64-pc-windows-msvc")),
            TargetPlatform.macOS when _data.MacOs.Arch == MacArch.X64 =>
                ("x86_64-apple-darwin", "SEED",
                 BuildArgs(_data.MacOs.BuildType, "x86_64-apple-darwin")),
            TargetPlatform.macOS when _data.MacOs.Arch == MacArch.Universal =>
                ("", "SEED", "--release"),  // Universal は別途処理
            TargetPlatform.macOS =>
                ("aarch64-apple-darwin", "SEED",
                 BuildArgs(_data.MacOs.BuildType, "aarch64-apple-darwin")),
            TargetPlatform.Android when _data.Android.Arch == AndroidArch.X86_64 =>
                ("x86_64-linux-android", "libSEED.so",
                 BuildArgs(_data.Android.BuildType, "x86_64-linux-android")),
            TargetPlatform.Android =>
                ("aarch64-linux-android", "libSEED.so",
                 BuildArgs(_data.Android.BuildType, "aarch64-linux-android")),
            TargetPlatform.iOS =>
                ("aarch64-apple-ios", "SEED",
                 BuildArgs(_data.Ios.BuildType, "aarch64-apple-ios")),
            _ => ("", "", ""),
        };

        // cargo build を実行する
        var profileDir = (_data.Windows.BuildType == BuildType.Release
            && platform == TargetPlatform.Windows) ? "release" : "debug";
        profileDir = platform switch
        {
            TargetPlatform.macOS    => _data.MacOs.BuildType    == BuildType.Release ? "release" : "debug",
            TargetPlatform.Android  => _data.Android.BuildType  == BuildType.Release ? "release" : "debug",
            TargetPlatform.iOS      => _data.Ios.BuildType      == BuildType.Release ? "release" : "debug",
            _ => profileDir,
        };

        var exitCode = await RunCargoAsync(buildArgs);
        if (exitCode != 0)
        {
            AppendLog($"\n❌ ビルド失敗 (exit code: {exitCode})");
            SetStatus("ビルド失敗");
            SetProgress(0);
            return;
        }

        SetProgress(70);
        SetStatus("ファイルをコピー中...");

        // ゲーム名サブフォルダを作成する（出力先は {outputPath}/{gameName}/）
        var gameName   = GetGameName();
        var gameOutDir = Path.Combine(outputPath, gameName);

        // 出力ディレクトリを作成してファイルをコピーする
        try
        {
            Directory.CreateDirectory(gameOutDir);
            AppendLog($"出力フォルダ: {gameOutDir}");

            // バイナリのコピー
            var targetDir    = string.IsNullOrEmpty(cargoTarget) ? profileDir : $"{cargoTarget}/{profileDir}";
            var binarySource = Path.Combine(_runtimePath, "target", targetDir, binaryName);

            if (File.Exists(binarySource))
            {
                // Windows / macOS / iOS は実行ファイルをゲーム名にリネームする
                // Android (.so) はシステムが名前を参照するためリネームしない
                var renamedBinary = platform switch
                {
                    TargetPlatform.Windows => $"{gameName}.exe",
                    TargetPlatform.macOS   => gameName,
                    TargetPlatform.iOS     => gameName,
                    _                      => binaryName,
                };
                var binaryDest = Path.Combine(gameOutDir, renamedBinary);
                File.Copy(binarySource, binaryDest, overwrite: true);
                AppendLog($"✓ {binaryName} → {renamedBinary}");
            }
            else
            {
                AppendLog($"⚠ バイナリが見つかりません: {binarySource}");
            }

            // アセットを PAK ファイルにまとめる
            await PackAssetsAsync(gameOutDir);

            SetProgress(100);
            AppendLog("");
            AppendLog($"✅ ビルド完了: {gameOutDir}");
            SetStatus($"ビルド完了 → {gameOutDir}");

            // エクスプローラーでビルド出力フォルダを開く
            Process.Start("explorer.exe", gameOutDir);
        }
        catch (Exception ex)
        {
            AppendLog($"❌ コピー失敗: {ex.Message}");
            SetStatus("コピー失敗");
        }
    }

    private static string BuildArgs(BuildType buildType, string target) =>
        buildType == BuildType.Release
            ? $"build --release --target {target}"
            : $"build --target {target}";

    /// <summary>cargo コマンドを非同期実行し、ログに出力しながら終了コードを返す。</summary>
    private async Task<int> RunCargoAsync(string args)
    {
        AppendLog($"$ cargo {args}");
        AppendLog("");

        var psi = new ProcessStartInfo("cargo", args)
        {
            WorkingDirectory        = _runtimePath,
            RedirectStandardOutput  = true,
            RedirectStandardError   = true,
            UseShellExecute         = false,
            CreateNoWindow          = true,
            StandardOutputEncoding  = Encoding.UTF8,
            StandardErrorEncoding   = Encoding.UTF8,
        };

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.Start();

        // stdout / stderr を並行して読む
        var outTask = ReadStreamAsync(proc.StandardOutput);
        var errTask = ReadStreamAsync(proc.StandardError);
        await Task.WhenAll(outTask, errTask);
        await proc.WaitForExitAsync();
        return proc.ExitCode;
    }

    /// <summary>StreamReader を行ごとに読みながら UI ログへ追記する。</summary>
    private async Task ReadStreamAsync(StreamReader reader)
    {
        while (true)
        {
            var line = await reader.ReadLineAsync();
            if (line is null) break;
            Dispatcher.Invoke(() => AppendLog(line));
        }
    }

    // ────────────────────────────────────────────────────────────
    //  PAK パッカー
    //
    //  【バイナリ形式】
    //  [Header - 12 bytes]
    //    magic:       "SEED" (4 bytes)
    //    version:     1      (u32 LE)
    //    entry_count: N      (u32 LE)
    //
    //  [Entry Table - N entries]
    //    path_len: u32 LE
    //    path:     UTF-8 bytes (assets ルートからの相対パス、'/' 区切り)
    //    offset:   u64 LE (ファイル先頭からのバイト位置)
    //    size:     u64 LE
    //
    //  [Data Section]
    //    各ファイルの生バイトを連結
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// アセットフォルダを assets.pak にまとめて出力先に書き出す。
    /// テキストファイル（.json, .scene, .actor, .inputmap）内の絶対パスを
    /// 仮想パス（assets://...）に変換してから格納する。
    /// </summary>
    private async Task PackAssetsAsync(string outputDir)
    {
        var pakPath = Path.Combine(outputDir, "assets.pak");
        AppendLog($"PAK 作成: {pakPath}");

        await Task.Run(() => WritePak(pakPath));
        AppendLog("✓ assets.pak 作成完了");
    }

    /// <summary>PAK ファイルを同期的に書き出す（Task.Run 内から呼ぶ）。</summary>
    private void WritePak(string pakPath)
    {
        // アセットフォルダ内の全ファイルを列挙する
        var allFiles = Directory.GetFiles(_assetsPath, "*", SearchOption.AllDirectories);

        // アセットルートのパスプレフィックス（末尾 '/'）
        var assetsPrefix = _assetsPath.TrimEnd('/', '\\') + Path.DirectorySeparatorChar;

        // 各エントリの (相対パス, バイトデータ) を収集する
        var entries = new List<(string RelPath, byte[] Data)>(allFiles.Length);
        foreach (var file in allFiles)
        {
            // 相対パスを '/' 区切りで取得する
            var relPath = file.StartsWith(assetsPrefix, StringComparison.OrdinalIgnoreCase)
                ? file[assetsPrefix.Length..].Replace('\\', '/')
                : Path.GetFileName(file);

            byte[] data;
            var ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".json" or ".scene" or ".actor" or ".inputmap")
            {
                // テキストファイルは絶対パスを仮想パスへ書き換えてから格納する
                var text = File.ReadAllText(file, System.Text.Encoding.UTF8);
                text = RewritePathsToVirtual(text);
                data = System.Text.Encoding.UTF8.GetBytes(text);
            }
            else
            {
                data = File.ReadAllBytes(file);
            }

            entries.Add((relPath, data));
        }

        // オフセット計算: ヘッダー + エントリテーブルの合計サイズ
        // Header: 4 + 4 + 4 = 12 bytes
        // Per entry: 4 (path_len) + pathBytes + 8 (offset) + 8 (size) = 20 + pathBytes
        long dataOffset = 12; // ヘッダー
        foreach (var (relPath, _) in entries)
            dataOffset += 4 + System.Text.Encoding.UTF8.GetByteCount(relPath) + 8 + 8;

        using var fs = new FileStream(pakPath, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs, System.Text.Encoding.UTF8, leaveOpen: false);

        // ── ヘッダー書き込み ─────────────────────────────────
        bw.Write((byte)'S'); bw.Write((byte)'E'); bw.Write((byte)'E'); bw.Write((byte)'D');
        bw.Write((uint)1);                           // version
        bw.Write((uint)entries.Count);               // entry_count

        // ── エントリテーブル書き込み ─────────────────────────
        long currentOffset = dataOffset;
        foreach (var (relPath, data) in entries)
        {
            var pathBytes = System.Text.Encoding.UTF8.GetBytes(relPath);
            bw.Write((uint)pathBytes.Length);
            bw.Write(pathBytes);
            bw.Write((ulong)currentOffset);
            bw.Write((ulong)data.Length);
            currentOffset += data.Length;
        }

        // ── データセクション書き込み ─────────────────────────
        foreach (var (_, data) in entries)
            bw.Write(data);

        Dispatcher.Invoke(() => AppendLog($"  → {entries.Count} ファイルを格納"));
    }

    /// <summary>
    /// テキスト内のアセット絶対パスを仮想パス（assets://...）へ書き換える。
    /// JSON 文字列リテラル内の絶対パスが対象。
    /// </summary>
    private string RewritePathsToVirtual(string text)
    {
        // アセットルートの正規化（'\\' → '/'）
        var root = _assetsPath.TrimEnd('/', '\\').Replace('\\', '/') + "/";
        // JSON では '\' が '\\' にエスケープされているため両方置換する
        var rootEscaped = root.Replace("/", "\\/");

        text = text.Replace(root,        VirtualPath.Scheme);
        text = text.Replace(rootEscaped, VirtualPath.Scheme);
        // バックスラッシュ区切りの絶対パスも対応する
        var rootBs        = _assetsPath.TrimEnd('/', '\\') + "\\";
        var rootBsEscaped = rootBs.Replace("\\", "\\\\");
        text = text.Replace(rootBs,        VirtualPath.Scheme);
        text = text.Replace(rootBsEscaped, VirtualPath.Scheme);
        return text;
    }

    // ── 設定の保存 ───────────────────────────────────────────

    private void SaveSettings()
    {
        // TextBox の現在値を _data へ反映してから保存する
        if (_tbGameName != null) _data.GameName = _tbGameName.Text.Trim();
        var path = Path.Combine(_assetsPath, "packaging_settings.json");
        _data.SaveTo(path);
    }

    // ── ログ / 進捗 ──────────────────────────────────────────

    private void AppendLog(string line)
    {
        LogText.Text += line + "\n";
        LogScroll.ScrollToEnd();
    }

    private void OnClearLog(object sender, RoutedEventArgs e)
    {
        LogText.Text = "";
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void SetProgress(int value)
    {
        BuildProgress.Value = value;
        BuildProgress.Visibility = value > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}
