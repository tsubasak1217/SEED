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
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Win32;
using SEEDEditor.Packaging.Collect;
using SEEDEditor.Packaging.Pak;
using SEEDEditor.Packaging.Runtime;
using SEEDEditor.Packaging.Scripts;

namespace SEEDEditor.Packaging;

/// <summary>
/// パッケージ化ウィンドウ。左ペインでターゲットプラットフォームを選択し、
/// 右ペインでプラットフォーム別のビルド設定を編集、ビルドボタンで cargo build を
/// 実行して実行ファイル・アセット（assets.pak）を出力フォルダへ書き出す。
/// </summary>
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

    /// <summary>
    /// runtime/src パス。エンジンに焼き込まれた assets:// 参照を拾って
    /// 収録の起点に加えるために AssetCollector へ渡す。
    /// </summary>
    private readonly string _runtimeSrcPath;

    private PackagingData  _data;
    private TargetPlatform _selectedPlatform = TargetPlatform.Windows;
    private Border?        _selectedPlatformBorder;
    private bool           _isBuilding;

    /// <summary>ゲーム名入力フィールド（設定ペイン共通ヘッダー部分）。</summary>
    private TextBox?       _tbGameName;

    // ── プラットフォームメタ一覧 ─────────────────────────────

    private static readonly List<PlatformInfo> Platforms = [
        new(TargetPlatform.Windows,
            "Windows", "Icon.Platform.Windows",
            PlatformAvailability.Available),
        new(TargetPlatform.macOS,
            "macOS", "Icon.Platform.MacOS",
            PlatformAvailability.RequiresOtherOS,
            "macOS 上でのビルドが必要です（CI / GitHub Actions を推奨）"),
        new(TargetPlatform.Android,
            "Android", "Icon.Platform.Android",
            PlatformAvailability.RequiresSetup,
            "Android NDK と cargo-ndk のセットアップが必要です"),
        new(TargetPlatform.iOS,
            "iOS", "Icon.Platform.iOS",
            PlatformAvailability.RequiresOtherOS,
            "macOS + Xcode 上でのビルドが必要です"),
        new(TargetPlatform.PlayStation5,
            "PlayStation 5", "Icon.Platform.PlayStation",
            PlatformAvailability.RequiresLicense,
            "Sony Interactive Entertainment のライセンス契約が必要です"),
        new(TargetPlatform.NintendoSwitch,
            "Nintendo Switch", "Icon.Platform.Switch",
            PlatformAvailability.RequiresLicense,
            "Nintendo のライセンス契約が必要です"),
    ];

    // ── ブラシ ───────────────────────────────────────────────

    /// <summary>プラットフォーム一覧行のアイコン一辺サイズ（px）。</summary>
    private const double PlatformIconSize = 18.0;

    /// <summary>可用性バッジ内アイコンの一辺サイズ（px）。バッジ径 18 に収まる大きさ。</summary>
    private const double BadgeIconSize = 11.0;

    /// <summary>設定ペイン見出しのアイコン一辺サイズ（px）。</summary>
    private const double SectionHeaderIconSize = 20.0;

    /// <summary>設定行のラベル列の幅（px）。</summary>
    private const double SettingLabelColumnWidth = 110.0;

    /// <summary>文字列リストを 1 行で表示するときの区切り。</summary>
    private const string ListSeparatorForDisplay = ", ";

    /// <summary>文字列リストの入力を分解するときの区切り文字。</summary>
    private static readonly char[] ListSeparatorChars = [',', ';', '\n', '\r'];

    private static readonly SolidColorBrush BrushSelected  = new(Color.FromRgb(0x1A, 0x2A, 0x3A));
    private static readonly SolidColorBrush BrushAvailable = new(Color.FromRgb(0x33, 0x99, 0x55));
    private static readonly SolidColorBrush BrushWarn      = new(Color.FromRgb(0xAA, 0x88, 0x22));
    private static readonly SolidColorBrush BrushDisabled  = new(Color.FromRgb(0x55, 0x55, 0x55));

    // ── コンストラクタ ───────────────────────────────────────

    public PackagingWindow(string assetsPath)
    {
        InitializeComponent();
        _assetsPath  = assetsPath;
        // runtime フォルダ（cargo のワークスペース）はエンジン側の資産であり、
        // プロジェクト（assets）とは別の場所にある。プロジェクト概念の導入で
        // assets からの相対では辿れなくなったため、ランタイム exe の位置から求める。
        _runtimePath    = ResolveRuntimeDir();
        _runtimeSrcPath = Path.Combine(_runtimePath, "src");

        var settingsPath = Path.Combine(assetsPath, "packaging_settings.json");
        _data = PackagingData.LoadFrom(settingsPath);
    }

    /// <summary>
    /// cargo build を実行する runtime フォルダ（エンジンのワークスペース）を解決する。
    ///
    /// <para>
    /// 判定はランタイム exe の場所から行う。開発配置では
    /// <c>runtime/target/&lt;構成&gt;/SEED.exe</c> なので 2 階層上が runtime/。
    /// それ以外（exe の隣に配置されたリリース形態など）は exe のフォルダを返す
    /// （その形態では cargo build 自体が動かないが、ここで例外にはしない）。
    /// </para>
    /// <para>
    /// 出力フォルダ名は以前 "debug" / "release" で決め打ちしていたが、
    /// ランタイムのビルド構成に develop が加わって決め打ちが外れるようになったため、
    /// Cargo.toml の実在で判定する <see cref="SEEDEditor.Runtime.BuildConfig.RuntimeSourceDirLocator"/>
    /// へ一本化した。
    /// </para>
    /// </summary>
    private static string ResolveRuntimeDir()
    {
        var exePath = MainWindow.RuntimeExePath;

        // runtime/target/<構成>/SEED.exe の形なら runtime/ が返る
        var sourceDir = SEEDEditor.Runtime.BuildConfig.RuntimeSourceDirLocator.FromExePath(exePath);
        if (sourceDir is not null) return sourceDir;

        // 配布形態（exe の隣に SEED.exe）: exe のフォルダ。パスが取れなければカレント。
        var exeDir = Path.GetDirectoryName(exePath);
        return string.IsNullOrEmpty(exeDir) ? Directory.GetCurrentDirectory() : exeDir;
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
        var (badgeColor, badgeIconKey) = info.Availability switch
        {
            PlatformAvailability.Available       => (BrushAvailable, "Icon.Apply"),
            PlatformAvailability.RequiresSetup   => (BrushWarn,      "Icon.Settings"),
            PlatformAvailability.RequiresOtherOS => (BrushWarn,      "Icon.Warning"),
            PlatformAvailability.RequiresLicense => (BrushDisabled,  "Icon.Close"),
            _ => (BrushDisabled, "Icon.Info"),
        };

        var badge = new Border
        {
            Width           = 18,
            Height          = 18,
            CornerRadius    = new CornerRadius(9),
            Background      = badgeColor,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment   = VerticalAlignment.Center,
            Child = BuildBadgeIcon(badgeIconKey),
        };

        var sp = new StackPanel { Orientation = Orientation.Horizontal };
        var platformIcon = SEEDEditor.Controls.AppIcon.Create(info.IconKey, PlatformIconSize);
        platformIcon.Margin            = new Thickness(0, 0, 8, 0);
        platformIcon.VerticalAlignment = VerticalAlignment.Center;
        sp.Children.Add(platformIcon);
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
        SettingsPane.Children.Add(BuildPlatformSectionHeader(meta.IconKey, meta.DisplayName));

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

        // ── .NET ランタイムの同梱（実際にパッケージを作れるプラットフォームのみ） ──
        // 配布先に .NET が入っていないとスクリプトが 1 つも動かないため既定は ON。
        if (meta.Availability != PlatformAvailability.RequiresLicense)
        {
            SettingsPane.Children.Add(BuildCheckRow(
                ".NET ランタイムを同梱", _data.BundleDotnetRuntime,
                v => _data.BundleDotnetRuntime = v,
                "スクリプト実行に必要な .NET を dotnet/ フォルダごと配布物へ入れます（約 75 MB 増）。\n" +
                "OFF にすると配布先の PC に .NET 9 のインストールが必要になり、\n" +
                "未インストールの環境ではスクリプトが動かないまま起動します。"));
        }

        // ── アセット収録設定（実際にパッケージを作れるプラットフォームのみ） ──
        if (meta.Availability != PlatformAvailability.RequiresLicense)
            BuildAssetCollectionSettings();

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
        AddOutputFolderRows(TargetPlatform.Windows, _data.Windows.OutputPath,
            path => _data.Windows.OutputPath = path);

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
        AddOutputFolderRows(TargetPlatform.macOS, _data.MacOs.OutputPath,
            path => _data.MacOs.OutputPath = path);

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
        AddOutputFolderRows(TargetPlatform.Android, _data.Android.OutputPath,
            path => _data.Android.OutputPath = path);

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
        AddOutputFolderRows(TargetPlatform.iOS, _data.Ios.OutputPath,
            path => _data.Ios.OutputPath = path);

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

    /// <summary>プラットフォーム設定ペインの見出し（アイコン＋名前）を作る。</summary>
    /// <param name="iconKey">Icons.xaml のアイコンキー。</param>
    /// <param name="displayName">プラットフォーム表示名。</param>
    private static UIElement BuildPlatformSectionHeader(string iconKey, string displayName)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, 12),
        };
        var icon = SEEDEditor.Controls.AppIcon.Create(iconKey, SectionHeaderIconSize);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin            = new Thickness(0, 0, 8, 0);
        sp.Children.Add(icon);
        sp.Children.Add(new TextBlock
        {
            Text              = displayName,
            FontSize          = 16,
            FontWeight        = FontWeights.Bold,
            Foreground        = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            VerticalAlignment = VerticalAlignment.Center,
        });
        return sp;
    }

    /// <summary>可用性バッジ（丸い色付き背景）の中に載せる白いアイコンを作る。</summary>
    /// <param name="iconKey">Icons.xaml のアイコンキー。</param>
    private static UIElement BuildBadgeIcon(string iconKey)
    {
        var icon = SEEDEditor.Controls.AppIcon.Create(iconKey, BadgeIconSize);
        icon.SetBrush(Brushes.White);
        icon.HorizontalAlignment = HorizontalAlignment.Center;
        icon.VerticalAlignment   = VerticalAlignment.Center;
        return icon;
    }

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
    /// <summary>
    /// 「出力フォルダ」行と、未設定時の既定出力先ヒントをまとめて設定ペインへ追加する。
    ///
    /// 出力先が空でもビルドできる（<see cref="PackagingOutputDefaults"/> が
    /// &lt;ProjectRoot&gt;/build/&lt;platform&gt; を既定にする）ことを、
    /// 実際のパスとともに利用者へ見せる。
    /// </summary>
    /// <param name="platform">対象プラットフォーム。</param>
    /// <param name="currentPath">現在設定されている出力先（空なら未設定）。</param>
    /// <param name="onChanged">入力が変わったときに設定へ書き戻すコールバック。</param>
    private void AddOutputFolderRows(
        TargetPlatform platform, string currentPath, Action<string> onChanged)
    {
        SettingsPane.Children.Add(BuildFolderRow("出力フォルダ", currentPath, onChanged));
        SettingsPane.Children.Add(BuildInfoBlock(PackagingOutputDefaults.HintFor(platform)));
    }

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

    // ── アセット収録設定 ─────────────────────────────────────

    /// <summary>
    /// 収録アセットの絞り込み設定 UI を構築する（全プラットフォーム共通）。
    ///
    /// 参照グラフから外れるアセットを救う「追加同梱フォルダ」と、
    /// 参照解決を捨てて従来どおり全部入れる「全ファイル同梱」が逃げ道になる。
    /// </summary>
    private void BuildAssetCollectionSettings()
    {
        var assets = _data.Assets;

        SettingsPane.Children.Add(BuildSectionSubHeader("アセット収録"));

        SettingsPane.Children.Add(BuildInfoBlock(
            "既定では project_settings.json のシーンから参照を辿り、\n" +
            "到達したアセットだけを assets.pak へ入れます。\n" +
            "除外は「参照されていないものの掃除」であり、参照されていれば\n" +
            "除外設定に当たっていても同梱されます（ログに警告が出ます）。"));

        // 全ファイル同梱トグル（従来の挙動へ戻す緊急避難）
        SettingsPane.Children.Add(BuildCheckRow(
            "全ファイル同梱", assets.IncludeAllFiles,
            v => assets.IncludeAllFiles = v,
            "参照解決を行わず、アセットフォルダの全ファイルを入れます（サイズは大きくなります）"));

        // 除外・追加の各リスト（カンマ区切り）
        SettingsPane.Children.Add(BuildListRow(
            "除外フォルダ", assets.ExcludedFolders,
            "パスのどこかの階層名が一致したら除外します（* が使えます）"));
        SettingsPane.Children.Add(BuildListRow(
            "除外拡張子", assets.ExcludedExtensions,
            "この拡張子のファイルは、参照されていなければ除外します"));
        SettingsPane.Children.Add(BuildListRow(
            "除外ファイル名", assets.ExcludedFileNames,
            "OS が作るゴミファイルなどの除外（* が使えます）"));
        SettingsPane.Children.Add(BuildListRow(
            "追加同梱フォルダ", assets.AdditionalFolders,
            "参照グラフで辿れないアセットを丸ごと入れる逃げ道（アセットルートからの相対パス）"));
        SettingsPane.Children.Add(BuildListRow(
            "常時同梱拡張子", assets.AlwaysIncludedExtensions,
            "参照の有無に関わらず入れる拡張子。既定は空です（スクリプトは SEEDUserScripts.dll へ事前コンパイルして同梱するため .cs は不要）"));
    }

    /// <summary>
    /// 文字列リストをカンマ区切りで編集する行を構築する。
    /// 編集内容はその場でリストへ書き戻す（保存はビルド実行時にまとめて行う）。
    /// </summary>
    /// <param name="label">行のラベル。</param>
    /// <param name="target">編集対象のリスト（この場で中身を差し替える）。</param>
    /// <param name="tooltip">ホバー時の説明。</param>
    /// <returns>構築した行。</returns>
    private UIElement BuildListRow(string label, List<string> target, string tooltip)
    {
        var tb = new TextBox
        {
            Style      = (Style)Resources["SettingTextBox"],
            Text       = string.Join(ListSeparatorForDisplay, target),
            ToolTip    = tooltip,
        };
        tb.TextChanged += (_, _) =>
        {
            // カンマ区切りを分解し、空要素を落としてリストへ書き戻す
            var items = tb.Text
                .Split(ListSeparatorChars, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();
            target.Clear();
            target.AddRange(items);
        };

        return BuildLabeledRow(label, tb, tooltip);
    }

    /// <summary>真偽値をチェックボックスで編集する行を構築する。</summary>
    /// <param name="label">行のラベル。</param>
    /// <param name="current">初期値。</param>
    /// <param name="onChanged">変更時のコールバック。</param>
    /// <param name="tooltip">ホバー時の説明。</param>
    /// <returns>構築した行。</returns>
    private UIElement BuildCheckRow(string label, bool current, Action<bool> onChanged, string tooltip)
    {
        var cb = new CheckBox
        {
            IsChecked         = current,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground        = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            ToolTip           = tooltip,
        };
        cb.Checked   += (_, _) => onChanged(true);
        cb.Unchecked += (_, _) => onChanged(false);
        return BuildLabeledRow(label, cb, tooltip);
    }

    /// <summary>ラベル + 任意コントロールの 2 列行を作る（設定行の共通レイアウト）。</summary>
    /// <param name="label">左のラベル文字列。</param>
    /// <param name="content">右に置くコントロール。</param>
    /// <param name="tooltip">ラベルにも付ける説明。</param>
    /// <returns>構築した行。</returns>
    private UIElement BuildLabeledRow(string label, UIElement content, string tooltip)
    {
        var grid = new Grid { Margin = new Thickness(0, 2, 0, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(SettingLabelColumnWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var lbl = new TextBlock
        {
            Style   = (Style)Resources["SettingLabel"],
            Text    = label,
            ToolTip = tooltip,
        };
        Grid.SetColumn(lbl, 0);
        Grid.SetColumn(content, 1);
        grid.Children.Add(lbl);
        grid.Children.Add(content);
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

    /// <summary>
    /// 現在選択中のプラットフォームの出力パスを返す。
    ///
    /// 設定が空のときは <c>&lt;ProjectRoot&gt;/build/&lt;platform&gt;</c> を既定として使う
    /// （既定値の決定は <see cref="PackagingOutputDefaults"/> が一手に担う）。
    /// </summary>
    private string GetCurrentOutputPath()
    {
        var configured = _selectedPlatform switch
        {
            TargetPlatform.Windows => _data.Windows.OutputPath,
            TargetPlatform.macOS   => _data.MacOs.OutputPath,
            TargetPlatform.Android => _data.Android.OutputPath,
            TargetPlatform.iOS     => _data.Ios.OutputPath,
            _ => "",
        };
        return PackagingOutputDefaults.ResolveForCurrentProject(configured, _selectedPlatform);
    }

    /// <summary>非同期でビルドを実行する。</summary>
    private async Task RunBuildAsync(TargetPlatform platform, string outputPath)
    {
        AppendLog($"═══ ビルド開始: {Platforms.Find(p => p.Platform == platform)!.DisplayName} ═══");
        AppendLog($"出力先: {outputPath}");
        AppendLog("");

        SetStatus("cargo build を実行中...");
        SetProgress(ProgressCargoStart);

        // cargo ターゲットとバイナリ名を決定する
        var (cargoTarget, binaryName, buildArgs) = platform switch
        {
            TargetPlatform.Windows when _data.Windows.Arch == WindowsArch.Arm64 =>
                ("aarch64-pc-windows-msvc", "SEED.exe",
                 BuildArgs(_data.Windows.BuildType, "aarch64-pc-windows-msvc")),
            // x64 Windows をホストが x64 Windows のときにビルドする場合だけ --target を付けない。
            // --target を付けると cargo は target/<triple>/release/ を使い、
            // 普段の `cargo run` が使う target/release/ とビルドキャッシュを共有しない
            // （同じコードを 2 回フルビルドすることになる）。
            TargetPlatform.Windows when IsHostWindowsX64() =>
                ("", "SEED.exe", BuildArgs(_data.Windows.BuildType, target: null)),
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

        // 各フェーズの所要時間を測る（どこが遅いのかを毎回ログに残す）
        var phaseWatch = Stopwatch.StartNew();
        var totalWatch = Stopwatch.StartNew();

        var exitCode = await RunCargoAsync(buildArgs);
        if (exitCode != 0)
        {
            AppendLog($"\n❌ ビルド失敗 (exit code: {exitCode})");
            SetStatus("ビルド失敗");
            SetProgress(0);
            return;
        }
        LogPhase("cargo build", phaseWatch);

        SetProgress(ProgressAfterCargo);
        SetStatus("ファイルをコピー中...");

        // ゲーム名サブフォルダを作成する（出力先は {outputPath}/{gameName}/）
        var gameName   = GetGameName();
        var gameOutDir = Path.Combine(outputPath, gameName);

        // 出力ディレクトリを作成してファイルをコピーする
        try
        {
            Directory.CreateDirectory(gameOutDir);
            AppendLog($"出力フォルダ: {gameOutDir}");

            // ── 旧レイアウトの残骸を掃除する ──────────────────────
            //
            // 以前は SEEDScripting.dll / Microsoft.CodeAnalysis*.dll /
            // SEEDUserScripts.dll / *.deps.json / dotnet/ を **出力フォルダ直下** へ
            // 置いていた。同じフォルダへ再パッケージすると、それらが直下に残ったまま
            // bin/ にも同じものが並ぶ。ランタイムは bin/ しか見ないので実行はできるが、
            // 「exe の隣に DLL が散らかる」状態が消えず、利用者から見て新旧の区別が
            // 付かなくなるため、bin/ を作る前にここで消す。
            //
            // caches / logs / saved は利用者データ（セーブ・ログ）なので対象外
            // （判定は PackageLayout.IsLegacyLeftover* に閉じてある）。
            PackageLayout.RemoveLegacyLayout(gameOutDir, AppendLog);

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
            LogPhase("バイナリのコピー", phaseWatch);

            // ── ユーザースクリプトの事前コンパイル ──────────────
            //
            // cargo build の後・PAK の前に行う。PAK には .cs を入れず、
            // ここで作った SEEDUserScripts.dll を配布物として同梱するため、
            // コンパイルに失敗したら「スクリプトが動かないパッケージ」が
            // できてしまう。それは配ってはいけないので、その場で中止する。
            SetStatus("ユーザースクリプトを事前コンパイル中...");
            AppendLog("");
            AppendLog("── ユーザースクリプトの事前コンパイル ──");

            ScriptPackagingResult scriptResult = null!;
            await Task.Run(() =>
            {
                scriptResult = ScriptPackager.Run(_runtimePath, _assetsPath, gameOutDir, LogFromWorker);
            });
            LogPhase("スクリプトの事前コンパイル", phaseWatch);

            if (!scriptResult.Success)
            {
                ScriptPackager.LogErrors(scriptResult, AppendLog);
                AppendLog("");
                AppendLog("❌ スクリプトが動かないパッケージは作らないため、ここで中止します。");
                SetStatus("スクリプトのコンパイル失敗");
                SetProgress(0);
                return;
            }
            SetProgress(ProgressAfterScripts);

            // ── .NET ランタイムの同梱 ────────────────────────────
            //
            // スクリプトの事前コンパイルの直後に行う。同梱するバージョンは
            // 直前のフェーズが出力へコピーした SEEDScripting.runtimeconfig.json から読むため、
            // この順序に依存している（前後を入れ替えるならバージョンの読み取り元も変えること）。
            //
            // 検出できなくてもパッケージ化は止めない（.NET が入った PC でなら動く配布物にはなる）。
            await BundleDotnetRuntimeAsync(gameOutDir, phaseWatch);
            SetProgress(ProgressAfterDotnetRuntime);

            // アセットを PAK ファイルにまとめる
            await PackAssetsAsync(gameOutDir, phaseWatch);

            SetProgress(ProgressComplete);
            AppendLog("");
            AppendLog($"✅ ビルド完了: {gameOutDir}（合計 {totalWatch.Elapsed.TotalSeconds:F1} 秒）");
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

    /// <summary>
    /// cargo build の引数を組み立てる。
    /// </summary>
    /// <param name="buildType">Release / Debug。</param>
    /// <param name="target">
    /// ターゲットトリプル。null / 空なら --target を付けない
    /// （ホストと同じターゲット。通常の target/&lt;profile&gt;/ を使うのでキャッシュを共有できる）。
    /// </param>
    /// <returns>cargo へ渡す引数文字列。</returns>
    private static string BuildArgs(BuildType buildType, string? target)
    {
        var profile   = buildType == BuildType.Release ? " --release" : "";
        var targetArg = string.IsNullOrEmpty(target) ? "" : $" --target {target}";
        return $"build{profile}{targetArg}";
    }

    /// <summary>
    /// ホスト環境が x64 Windows かを判定する。
    ///
    /// 真なら x64 Windows 向けビルドは --target 無しで通常のビルドキャッシュを使える。
    /// 注意: 既定ツールチェインが *-pc-windows-gnu の場合は ABI が変わるが、
    /// このプロジェクトは msvc 前提のため判定はアーキテクチャのみで行う。
    /// </summary>
    /// <returns>x64 Windows なら true。</returns>
    private static bool IsHostWindowsX64() =>
        RuntimeInformation.IsOSPlatform(OSPlatform.Windows) &&
        RuntimeInformation.OSArchitecture == Architecture.X64;

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
    //  アセット収集と PAK 書き出し
    //
    //  収録ファイルの決定は AssetCollector（参照グラフの閉包）、
    //  バイナリの書き出しは PakWriter（ストリーミング）に委譲する。
    //  ここは「進捗と結果を UI へ流す」だけを担当する。
    // ────────────────────────────────────────────────────────────

    /// <summary>cargo build 開始時に表示する進捗（％）。</summary>
    private const int ProgressCargoStart = 10;

    /// <summary>cargo build 完了時の進捗（％）。</summary>
    private const int ProgressAfterCargo = 60;

    /// <summary>ユーザースクリプトの事前コンパイル完了時の進捗（％）。</summary>
    private const int ProgressAfterScripts = 65;

    /// <summary>.NET ランタイム同梱の完了時の進捗（％）。</summary>
    private const int ProgressAfterDotnetRuntime = 68;

    /// <summary>全工程完了時の進捗（％）。</summary>
    private const int ProgressComplete = 100;

    /// <summary>PAK 書き出し中に割り当てる進捗の下限値（％）。</summary>
    private const int ProgressPakStart = 70;

    /// <summary>PAK 書き出し中に割り当てる進捗の上限値（％）。</summary>
    private const int ProgressPakEnd = 95;

    /// <summary>欠落参照をログへ列挙する最大件数（多すぎるとログが読めなくなる）。</summary>
    private const int MaxLoggedMissingReferences = 50;

    /// <summary>除外ルールに当たったまま同梱したファイルをログへ列挙する最大件数。</summary>
    private const int MaxLoggedExcludedButIncluded = 20;

    /// <summary>バイト数を MB 表記へ直すための除数。</summary>
    private const double BytesPerMegabyte = 1024.0 * 1024.0;

    /// <summary>
    /// 収録アセットを決定し、assets.pak にまとめて出力先へ書き出す。
    /// </summary>
    /// <param name="outputDir">出力フォルダ（ここに assets.pak を作る）。</param>
    /// <param name="phaseWatch">フェーズ所要時間の計測用ストップウォッチ。</param>
    /// <summary>
    /// .NET ランタイムを出力フォルダへ同梱する（設定が ON のときだけ）。
    ///
    /// <para>
    /// 検出に失敗しても中止しない。同梱が無いパッケージは
    /// 「.NET がインストールされた PC でなら動く」ものなので、
    /// 作らせない理由にはならない（ログには必ず理由を残す）。
    /// </para>
    /// </summary>
    /// <param name="gameOutDir">パッケージ出力フォルダ。</param>
    /// <param name="phaseWatch">フェーズ所要時間の計測用ストップウォッチ。</param>
    private async Task BundleDotnetRuntimeAsync(string gameOutDir, Stopwatch phaseWatch)
    {
        if (!_data.BundleDotnetRuntime)
        {
            AppendLog("");
            AppendLog("── .NET ランタイムの同梱: スキップ（設定 OFF）──");
            AppendLog("  配布先の PC に .NET のインストールが必要なパッケージになります。");
            return;
        }

        SetStatus(".NET ランタイムを同梱中...");
        AppendLog("");
        AppendLog("── .NET ランタイムの同梱 ──");

        DotnetBundleResult bundle = null!;
        await Task.Run(() => bundle = DotnetRuntimeBundler.Run(gameOutDir, LogFromWorker));
        LogPhase(".NET ランタイムの同梱", phaseWatch);

        if (!bundle.Bundled)
        {
            AppendLog($"⚠ {bundle.SkipReason}");
        }
    }

    private async Task PackAssetsAsync(string outputDir, Stopwatch phaseWatch)
    {
        var pakPath = Path.Combine(outputDir, "assets.pak");

        // ── フェーズ: 収録ファイルの決定 ────────────────────────
        SetStatus("収録アセットを収集中...");
        AppendLog("");
        AppendLog("── 収録アセットの収集 ──");

        AssetCollectionResult result = null!;
        await Task.Run(() =>
        {
            var collector = new AssetCollector(_assetsPath, _data.Assets, _runtimeSrcPath, LogFromWorker);
            result = collector.Collect();
        });
        LogPhase("収録アセットの収集", phaseWatch);
        ReportCollection(result);
        SetProgress(ProgressPakStart);

        if (result.Included.Count == 0)
        {
            AppendLog("❌ 収録対象が 0 件です。project_settings.json の start_scene / scenes を確認してください。");
            return;
        }

        // ── フェーズ: PAK 書き出し ──────────────────────────────
        SetStatus("assets.pak を書き出し中...");
        AppendLog("");
        AppendLog($"PAK 作成: {pakPath}");

        PakWriteStats stats = default;
        await Task.Run(() =>
        {
            stats = PakWriter.Write(pakPath, _assetsPath, result.Included, LogFromWorker, ReportPakProgress);
        });
        LogPhase("PAK 書き出し", phaseWatch);

        AppendLog($"✓ assets.pak 作成完了: {stats.EntryCount} ファイル / {ToMegabytes(stats.TotalBytes):F1} MB");
        if (stats.SizeMismatchCount > 0)
            AppendLog($"⚠ 収集後にサイズが変わったファイル: {stats.SizeMismatchCount} 件（0 埋め / 切り捨てで整合させました）");
    }

    /// <summary>収集結果（件数・サイズ・欠落・警告）をログへ書き出す。</summary>
    /// <param name="result">AssetCollector の結果。</param>
    private void ReportCollection(AssetCollectionResult result)
    {
        AppendLog($"収録: {result.Included.Count} ファイル / {ToMegabytes(result.IncludedBytes):F1} MB");
        AppendLog($"除外: {result.ExcludedFileCount} ファイル / {ToMegabytes(result.ExcludedBytes):F1} MB " +
                  $"（アセット全体 {result.TotalFileCount} ファイル / {ToMegabytes(result.TotalBytes):F1} MB）");

        // 実体の無いシーン登録
        foreach (var scene in result.MissingScenes)
            AppendLog($"⚠ 登録シーンの実体がありません: {scene}");

        // 除外ルールに当たっているが参照されたので入れたもの（設定見直しの材料）
        if (result.IncludedDespiteExclusion.Count > 0)
        {
            AppendLog($"⚠ 除外ルールに一致するが参照されているため同梱: {result.IncludedDespiteExclusion.Count} ファイル");
            foreach (var path in result.IncludedDespiteExclusion.Take(MaxLoggedExcludedButIncluded))
                AppendLog($"    {path}");
            if (result.IncludedDespiteExclusion.Count > MaxLoggedExcludedButIncluded)
                AppendLog($"    …ほか {result.IncludedDespiteExclusion.Count - MaxLoggedExcludedButIncluded} ファイル");
        }

        // 参照はあるが実体が無いパス（パッケージ版で読み込み失敗になる箇所）
        if (result.MissingReferences.Count > 0)
        {
            AppendLog($"❌ 参照先が見つからないパス: {result.MissingReferences.Count} 件");
            foreach (var m in result.MissingReferences.Take(MaxLoggedMissingReferences))
                AppendLog($"    {m.ReferencePath}  ← {m.SourceRelPath}");
            if (result.MissingReferences.Count > MaxLoggedMissingReferences)
                AppendLog($"    …ほか {result.MissingReferences.Count - MaxLoggedMissingReferences} 件");
        }
    }

    /// <summary>PAK 書き出しの進捗を UI へ反映する（ワーカースレッドから呼ばれる）。</summary>
    /// <param name="p">書き出し進捗。</param>
    private void ReportPakProgress(PakWriteProgress p)
    {
        var ratio   = p.TotalBytes > 0 ? (double)p.BytesWritten / p.TotalBytes : 1.0;
        var percent = ProgressPakStart + (int)((ProgressPakEnd - ProgressPakStart) * ratio);
        Dispatcher.BeginInvoke(() =>
        {
            SetProgress(percent);
            SetStatus($"assets.pak 書き出し中 {p.FilesWritten}/{p.TotalFiles} ファイル " +
                      $"({ToMegabytes(p.BytesWritten):F0}/{ToMegabytes(p.TotalBytes):F0} MB)");
        });
    }

    /// <summary>ワーカースレッドからのログを UI スレッドへ流す。</summary>
    /// <param name="line">ログ 1 行。</param>
    private void LogFromWorker(string line) => Dispatcher.Invoke(() => AppendLog(line));

    /// <summary>フェーズの所要秒数をログへ書き、ストップウォッチを次のフェーズ用に測り直す。</summary>
    /// <param name="phaseName">フェーズ名。</param>
    /// <param name="watch">計測用ストップウォッチ（呼び出し後に再スタートする）。</param>
    private void LogPhase(string phaseName, Stopwatch watch)
    {
        AppendLog($"[時間] {phaseName}: {watch.Elapsed.TotalSeconds:F1} 秒");
        watch.Restart();
    }

    /// <summary>バイト数を MB へ変換する。</summary>
    /// <param name="bytes">バイト数。</param>
    /// <returns>MB 単位の値。</returns>
    private static double ToMegabytes(long bytes) => bytes / BytesPerMegabyte;

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
