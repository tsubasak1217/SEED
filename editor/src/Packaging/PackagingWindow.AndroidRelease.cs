// ============================================================
//  PackagingWindow.AndroidRelease.cs — パッケージ化ウィンドウの Android の配布用（release）の欄（段階D。docs/android.md §24）
//
//  【欄】（Android を選んだときの設定ペイン。選んだビルドの種類に関係の無い欄は出さない）
//    ビルドの種類 … 開発用（デバッグ署名の APK）/ 配布用（release。アップロード鍵で署名）
//    形式         … APK / AAB（配布用だけ出す）
//    署名         … キーストアの場所・別名（packaging_settings.json の android.signing）・パスワード（エディタの保護保存。
//                   Settings/AndroidSigningSecretStore。DPAPI）・新しいキーストアを作る（keytool。中核の Signing/AndroidKeystoreTool）
//    アイコン     … プロジェクト設定の android.icon / icon_background の今の値（設定はプロジェクト設定ウィンドウ）
//    Google Play の要件 … 「要件を確認」（中核の Release/AndroidRequirementsCheckRunner）と、ビルドの結果の一覧
//  手順の中身は中核（editor/src/Android/。SeedAndroid と共通）。ここは入力を集めて呼び、結果を並べるだけ。
//
//  パスワードはプロジェクトのファイルにもログにも書かない。「保存」でこの PC のこの Windows ユーザーだけが解ける形で
//  editor/settings/android_signing_secrets.json へ置き、ビルドのときに取り出して中核へ渡す（無ければ中核が環境変数を見る）。
// ============================================================

using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SEEDEditor.Android.Icons;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Release;
using SEEDEditor.Android.Signing;
using SEEDEditor.Android.Toolchain;
using SEEDEditor.AndroidRun;
using SEEDEditor.Project;
using SEEDEditor.ProjectSettings;
using SEEDEditor.Settings;

namespace SEEDEditor.Packaging;

public partial class PackagingWindow
{
    // ── 見た目 ────────────────────────────────────────────

    /// <summary>要件の一覧の判定のアイコンの一辺（px）。</summary>
    private const double RequirementIconSize = 12.0;

    /// <summary>要件の一覧のアイコンと文の間（px）。</summary>
    private const double RequirementIconGap = 6.0;

    /// <summary>要件の一覧の行の間（px）。</summary>
    private const double RequirementRowSpacing = 3.0;

    /// <summary>要件の一覧の文字の大きさ。</summary>
    private const double RequirementFontSize = 10.5;

    /// <summary>ボタンどうしの間（px）。</summary>
    private const double ActionButtonSpacing = 6.0;

    /// <summary>設定行の下の余白（他の行と同じ）。</summary>
    private const double SettingRowBottomMargin = 4.0;

    /// <summary>設定行の上の余白（他の行と同じ）。</summary>
    private const double SettingRowTopMargin = 2.0;

    /// <summary>合格の色。</summary>
    private static readonly SolidColorBrush RequirementPassBrush = new(Color.FromRgb(0x55, 0xBB, 0x77));

    /// <summary>知らせの色。</summary>
    private static readonly SolidColorBrush RequirementInfoBrush = new(Color.FromRgb(0x88, 0x88, 0x88));

    /// <summary>注意の色。</summary>
    private static readonly SolidColorBrush RequirementWarningBrush = new(Color.FromRgb(0xFF, 0xCC, 0x66));

    /// <summary>不合格の色。</summary>
    private static readonly SolidColorBrush RequirementFailureBrush = new(Color.FromRgb(0xFF, 0x88, 0x88));

    /// <summary>パスワードの状態の文の色（保存済み）。</summary>
    private static readonly SolidColorBrush PasswordSavedBrush = RequirementPassBrush;

    // ── 文言 ──────────────────────────────────────────────

    /// <summary>配布用の説明（画面に明記する）。</summary>
    private const string AndroidReleaseNote =
        "配布用（release）: debuggable にせず、INTERNET 権限を入れず、アップロード鍵で署名します（Rust は常に --release）。" +
        "Google Play へ出すなら形式を AAB にします。鍵が無い・開けないときはビルドを始めません（デバッグ署名の配布物は作りません）。";

    /// <summary>鍵の保管の注意。</summary>
    private const string AndroidKeystoreSafetyNote =
        "キーストアは Google Play の「アップロード鍵」になります。失うと Google に鍵の再設定を頼むまで更新を出せません。\n" +
        "・キーストアとパスワードは別々の安全な場所にも控える\n" +
        "・リポジトリ・プロジェクトのアセットフォルダには置かない（アセットの中は使えません）\n" +
        "・パスワードはこの画面の「保存」（この PC のこの Windows ユーザーだけが解ける形で editor/settings に保存）か、" +
        "環境変数 SEED_ANDROID_KEYSTORE_PASSWORD（SeedAndroid・CI）で渡す。プロジェクトのファイルには書かない";

    /// <summary>アイコンの設定の置き場の説明。</summary>
    private const string AndroidIconNote =
        "アイコンはプロジェクト設定 → 解像度設定 → 「Android アプリ情報（モバイル）」の「アイコン」「アイコンの背景色」で設定します。\n" +
        "ビルドのときに各密度の mipmap とアダプティブアイコン（前景＋背景色）を生成します（512x512 以上の正方形の PNG を推奨）。";

    // ── 入力欄（表示中だけ非 null）──────────────────────────

    /// <summary>キーストアの場所の入力欄。</summary>
    private TextBox? _tbKeystorePath;

    /// <summary>別名の入力欄。</summary>
    private TextBox? _tbKeyAlias;

    /// <summary>パスワードの入力欄。</summary>
    private PasswordBox? _pbKeystorePassword;

    /// <summary>新しいキーストアのパスワードの確認の入力欄。</summary>
    private PasswordBox? _pbKeystoreConfirm;

    /// <summary>新しいキーストアの証明書の名前（CN）の入力欄。</summary>
    private TextBox? _tbCertificateName;

    /// <summary>パスワードの保存の状態の表示。</summary>
    private TextBlock? _passwordStatus;

    /// <summary>要件の一覧を並べる所。</summary>
    private StackPanel? _androidRequirementsPanel;

    /// <summary>最後に出した要件の一覧（ペインを作り直しても出し直す）。</summary>
    private AndroidRequirementReport? _androidLastReport;

    /// <summary>最後の一覧の出どころ（「要件を確認」か「ビルド」と配布物）。</summary>
    private string? _androidLastReportCaption;

    /// <summary>keytool・要件の確認が動いている間は true（ボタンを重ねて押させない）。</summary>
    private bool _androidToolBusy;

    /// <summary>署名のパスワードの保護保存（editor/settings/android_signing_secrets.json・DPAPI）。</summary>
    private static AndroidSigningSecretStore SigningSecretStore { get; } =
        new(Path.Combine(EditorPaths.SettingsDir, AndroidSigningSecretStore.FileName), AndroidSigningDpapiProtector.Instance);

    // ── ビルドの種類と形式 ─────────────────────────────────

    /// <summary>「ビルドの種類」と（配布用のときだけ）「形式」の行を足す。選び直したらペインを作り直す（関係の無い欄を出さない）。</summary>
    private void AddAndroidVariantRows()
    {
        SettingsPane.Children.Add(BuildComboRow("ビルドの種類",
            AndroidApkOutput.VariantChoices.Select(choice => choice.Label).ToArray(),
            AndroidApkOutput.LabelFor(_data.Android.Variant),
            v =>
            {
                var variant = AndroidApkOutput.VariantFor(v);
                if (variant == _data.Android.Variant) return;
                _data.Android.Variant = variant;
                // AAB は配布用だけ。開発用へ戻したら APK にする
                if (variant == AndroidBuildVariant.Debug) _data.Android.Format = AndroidPackageFormat.Apk;
                Dispatcher.BeginInvoke(() => BuildSettingsPane(TargetPlatform.Android));
            }));
        if (_data.Android.Variant != AndroidBuildVariant.Release) return;

        SettingsPane.Children.Add(BuildComboRow("形式",
            AndroidApkOutput.FormatChoices.Select(choice => choice.Label).ToArray(),
            AndroidApkOutput.LabelFor(_data.Android.Format),
            v => _data.Android.Format = AndroidApkOutput.FormatFor(v)));
        SettingsPane.Children.Add(BuildNoteBlock(AndroidReleaseNote, PlatformAvailability.RequiresSetup));
    }

    // ── 署名 ──────────────────────────────────────────────

    /// <summary>「署名」の欄（配布用だけ）。</summary>
    private void AddAndroidSigningSection()
    {
        SettingsPane.Children.Add(BuildSectionSubHeader("署名（配布用。アップロード鍵）"));
        var signing = _data.Android.Signing;

        // キーストアの場所（参照はファイルを選ぶ。まだ無いファイル名も指定できる＝新しく作るとき）
        _tbKeystorePath = new TextBox { Style = (Style)Resources["SettingTextBox"], Text = signing.KeystorePath };
        _tbKeystorePath.TextChanged += (_, _) =>
        {
            signing.KeystorePath = _tbKeystorePath.Text.Trim();
            RefreshPasswordStatus();
        };
        var browse = new Button { Style = (Style)Resources["BrowseBtn"], ToolTip = "キーストアを選ぶ（新しく作るならファイル名を入力）" };
        browse.Click += (_, _) => BrowseKeystore();
        SettingsPane.Children.Add(BuildLabeledRow("キーストア", WithTrailing(_tbKeystorePath, browse),
            "キーストアのファイル（絶対パスか、プロジェクトのルートからの相対パス）。packaging_settings.json の android.signing.keystore_path"));

        _tbKeyAlias = new TextBox { Style = (Style)Resources["SettingTextBox"], Text = signing.KeyAlias };
        _tbKeyAlias.TextChanged += (_, _) =>
        {
            signing.KeyAlias = _tbKeyAlias.Text.Trim();
            RefreshPasswordStatus();
        };
        SettingsPane.Children.Add(BuildLabeledRow("別名", _tbKeyAlias,
            $"キーストアの中のキーの別名（keytool の -alias。新しく作るときの既定は {AndroidKeystoreDefaults.DefaultAlias}）"));

        // パスワード（保存・消す）と、その状態
        _pbKeystorePassword = new PasswordBox { Style = (Style)Resources["SettingPasswordBox"] };
        var save = new Button { Content = "保存", Style = (Style)Resources["PkgBtn"], Margin = new Thickness(ActionButtonSpacing, 0, 0, 0) };
        save.Click += (_, _) => SaveAndroidPassword();
        var forget = new Button { Content = "消す", Style = (Style)Resources["PkgBtn"], Margin = new Thickness(ActionButtonSpacing, 0, 0, 0) };
        forget.Click += (_, _) => ForgetAndroidPassword();
        SettingsPane.Children.Add(BuildLabeledRow("パスワード", WithTrailing(_pbKeystorePassword, save, forget),
            "キーストアのパスワード。「保存」でこの PC のこの Windows ユーザーだけが解ける形で保存します（DPAPI）"));
        _passwordStatus = new TextBlock { FontSize = RequirementFontSize, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(SettingLabelColumnWidth, 0, 0, SettingRowBottomMargin) };
        SettingsPane.Children.Add(_passwordStatus);
        RefreshPasswordStatus();

        // 新しいキーストアを作る（上のキーストアの場所・別名・パスワードを使う）
        _pbKeystoreConfirm = new PasswordBox { Style = (Style)Resources["SettingPasswordBox"] };
        SettingsPane.Children.Add(BuildLabeledRow("確認用", _pbKeystoreConfirm, "新しく作るときだけ: パスワードをもう一度"));
        _tbCertificateName = new TextBox { Style = (Style)Resources["SettingTextBox"], Text = GetGameName() };
        SettingsPane.Children.Add(BuildLabeledRow("証明書の名前", _tbCertificateName,
            $"新しく作るときだけ: 証明書の CN（空なら {AndroidKeystoreDefaults.DefaultCommonName}。利用者には見えない）"));
        var create = new Button { Content = "この場所に新しいキーストアを作る", Style = (Style)Resources["PkgBtn"], HorizontalAlignment = HorizontalAlignment.Left };
        create.Click += async (_, _) => await CreateKeystoreAsync(create);
        SettingsPane.Children.Add(BuildLabeledRow(string.Empty, create,
            $"keytool で作ります（{AndroidKeystoreDefaults.StoreType}・{AndroidKeystoreDefaults.KeyAlgorithm} {AndroidKeystoreDefaults.KeySizeBits}・" +
            $"{AndroidKeystoreDefaults.ValidityDays} 日）。既にあるファイルは上書きしません。作ったらパスワードを保存します"));
        SettingsPane.Children.Add(BuildInfoBlock(AndroidKeystoreSafetyNote));
    }

    /// <summary>入力欄の右にボタンを並べた行の中身を作る。</summary>
    private static UIElement WithTrailing(Control input, params Button[] buttons)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(input, 0);
        grid.Children.Add(input);
        for (var i = 0; i < buttons.Length; i++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Grid.SetColumn(buttons[i], i + 1);
            grid.Children.Add(buttons[i]);
        }
        return grid;
    }

    /// <summary>キーストアのファイルを選ぶ（まだ無いファイル名も入力できる）。</summary>
    private void BrowseKeystore()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "キーストアを選ぶ（新しく作るならファイル名を入力）",
            CheckFileExists = false,
            Filter = "キーストア (*.jks;*.keystore;*.p12)|*.jks;*.keystore;*.p12|すべてのファイル (*.*)|*.*",
        };
        var current = ResolveKeystorePathForUi();
        if (current is not null && Directory.Exists(Path.GetDirectoryName(current))) dialog.InitialDirectory = Path.GetDirectoryName(current);
        if (dialog.ShowDialog(this) == true && _tbKeystorePath is not null) _tbKeystorePath.Text = dialog.FileName;
    }

    /// <summary>プロジェクトのルート（キーストアの相対パスの基準。中核と同じ ProjectFolderResolver の規則）。</summary>
    private string? AndroidProjectRoot()
    {
        var projectDir = string.IsNullOrWhiteSpace(ProjectContext.RootDir) ? _assetsPath : ProjectContext.RootDir;
        return ProjectFolderResolver.Resolve(projectDir, out _)?.ProjectRoot;
    }

    /// <summary>入力されたキーストアの絶対パス（空なら null。相対パスはプロジェクトのルートから＝中核と同じ）。</summary>
    private string? ResolveKeystorePathForUi()
    {
        var configured = _data.Android.Signing.KeystorePath;
        if (string.IsNullOrWhiteSpace(configured)) return null;
        try
        {
            return AndroidSigningResolver.ResolveConfiguredPath(configured, AndroidProjectRoot());
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    /// <summary>入力された別名（空なら null）。</summary>
    private string? KeyAliasForUi() =>
        string.IsNullOrWhiteSpace(_data.Android.Signing.KeyAlias) ? null : _data.Android.Signing.KeyAlias.Trim();

    /// <summary>パスワードの保存の状態を出し直す。</summary>
    private void RefreshPasswordStatus()
    {
        if (_passwordStatus is null) return;
        var keystore = ResolveKeystorePathForUi();
        var alias = KeyAliasForUi();
        if (keystore is null || alias is null)
        {
            _passwordStatus.Text = "キーストアと別名を入れると、保存したパスワードの有無を出します。";
            _passwordStatus.Foreground = RequirementInfoBrush;
            return;
        }
        var saved = SigningSecretStore.Has(keystore, alias);
        var fromEnvironment = AndroidSigningSecrets.FromEnvironment(Environment.GetEnvironmentVariable) is not null;
        _passwordStatus.Text = saved
            ? $"パスワード: 保存済み（{AndroidSigningSecretStore.Origin}）"
            : fromEnvironment
                ? $"パスワード: 未保存（環境変数 {AndroidSigningSecrets.KeystorePasswordVariable} を使います）"
                : "パスワード: 未保存（配布用のビルドには「保存」が要ります）";
        _passwordStatus.Foreground = saved || fromEnvironment ? PasswordSavedBrush : RequirementWarningBrush;
        if (!File.Exists(keystore)) _passwordStatus.Text += $"。キーストア {keystore} はまだありません（下の「新しいキーストアを作る」で作れます）";
    }

    /// <summary>入力したパスワードを保護保存する（入力欄は空にする）。</summary>
    private void SaveAndroidPassword()
    {
        var keystore = ResolveKeystorePathForUi();
        var alias = KeyAliasForUi();
        var password = _pbKeystorePassword?.Password ?? string.Empty;
        if (keystore is null || alias is null || password.Length == 0)
        {
            AppendLog("パスワードを保存するには、キーストア・別名・パスワードを入れてください。");
            return;
        }
        SigningSecretStore.Save(keystore, alias, new AndroidSigningSecrets(password, null, AndroidSigningSecretStore.Origin));
        _pbKeystorePassword!.Clear();
        SaveSettings();
        AppendLog($"キーストア {keystore}（別名 {alias}）のパスワードを保存しました（{AndroidSigningSecretStore.Origin}）。");
        RefreshPasswordStatus();
    }

    /// <summary>保存したパスワードを消す。</summary>
    private void ForgetAndroidPassword()
    {
        var keystore = ResolveKeystorePathForUi();
        var alias = KeyAliasForUi();
        if (keystore is null || alias is null) return;
        AppendLog(SigningSecretStore.Remove(keystore, alias)
            ? $"キーストア {keystore}（別名 {alias}）の保存したパスワードを消しました。"
            : "保存したパスワードはありません。");
        RefreshPasswordStatus();
    }

    /// <summary>保存したパスワードを取り出す（無ければ null＝中核が環境変数を見る）。</summary>
    private AndroidSigningSecrets? LoadStoredAndroidSecrets()
    {
        var keystore = ResolveKeystorePathForUi();
        var alias = KeyAliasForUi();
        return keystore is null || alias is null ? null : SigningSecretStore.Load(keystore, alias);
    }

    /// <summary>
    /// 新しいキーストアを keytool で作る（場所・別名・パスワードは上の欄。作ったらパスワードを保存し、設定を保存する）。
    /// </summary>
    /// <param name="button">押されたボタン（作っている間は押せなくする）。</param>
    private async Task CreateKeystoreAsync(Button button)
    {
        if (_androidToolBusy || _isBuilding) return;
        var keystore = ResolveKeystorePathForUi();
        if (keystore is null)
        {
            AppendLog("新しいキーストアを作る場所（ファイル名）を「キーストア」に入れてください（プロジェクトの外を勧めます）。");
            return;
        }
        if (KeyAliasForUi() is null && _tbKeyAlias is not null) _tbKeyAlias.Text = AndroidKeystoreDefaults.DefaultAlias;
        var alias = KeyAliasForUi()!;
        var password = _pbKeystorePassword?.Password ?? string.Empty;
        if (!string.Equals(password, _pbKeystoreConfirm?.Password ?? string.Empty, StringComparison.Ordinal))
        {
            AppendLog("パスワードと確認用が一致しません。");
            return;
        }
        if (password.Length < AndroidKeystoreDefaults.MinPasswordLength)
        {
            AppendLog($"パスワードは {AndroidKeystoreDefaults.MinPasswordLength} 文字以上にしてください。");
            return;
        }

        var toolchain = AndroidToolchain.Detect();
        var secrets = new AndroidSigningSecrets(password, null, AndroidSigningSecretStore.Origin);
        var request = new AndroidKeystoreCreateRequest(keystore, alias, secrets, _tbCertificateName?.Text, _assetsPath);
        AppendLog("");
        AppendLog($"═══ キーストアの作成: {keystore}（別名 {alias}） ═══");
        _androidToolBusy = true;
        button.IsEnabled = false;
        try
        {
            var certificate = await Task.Run(() => AndroidKeystoreTool.CreateAsync(
                toolchain.RequireKeytool(), request, line => Dispatcher.Invoke(() => AppendLog("  " + line)), CancellationToken.None));
            SigningSecretStore.Save(keystore, alias, secrets);
            _pbKeystorePassword?.Clear();
            _pbKeystoreConfirm?.Clear();
            SaveSettings();
            AppendLog($"作りました: {keystore}（別名 {alias}・{certificate.Describe()}）。パスワードを保存しました。");
            AppendLog("キーストアとパスワードを別の安全な場所にも控えてください（アップロード鍵を失うと更新を出せなくなります）。");
        }
        catch (AndroidPipelineException ex)
        {
            AppendLog("キーストアを作れませんでした: " + ex.Message);
        }
        finally
        {
            _androidToolBusy = false;
            button.IsEnabled = true;
            RefreshPasswordStatus();
        }
    }

    // ── アイコン ──────────────────────────────────────────

    /// <summary>「アイコン」の欄（プロジェクト設定の今の値と、誤りがあればその説明）。</summary>
    private void AddAndroidIconSection()
    {
        SettingsPane.Children.Add(BuildSectionSubHeader("アイコン"));
        var android = AndroidProjectSettingsReader.Read(_assetsPath).Android;
        var configured = AndroidAppSettings.NormalizeText(android?.Icon);
        var errors = LauncherIconSettings.Validate(android, _assetsPath);
        var background = AndroidAppSettings.NormalizeText(android?.IconBackground) ?? RgbaColor.White.ToAndroidHex();
        var current = configured is null
            ? "今の設定: なし（ランチャーにはシステムの既定のアイコンが出ます）"
            : $"今の設定: {configured}（背景色 {background}）";
        SettingsPane.Children.Add(BuildInfoBlock(current + (errors.Count == 0 ? string.Empty : "\n" + string.Join("\n", errors)),
            isWarning: errors.Count > 0));
        SettingsPane.Children.Add(BuildInfoBlock(AndroidIconNote));
    }

    // ── Google Play の要件 ─────────────────────────────────

    /// <summary>「Google Play の要件」の欄（配布用だけ）: 確かめるボタンと、最後の一覧。</summary>
    private void AddAndroidRequirementsSection()
    {
        SettingsPane.Children.Add(BuildSectionSubHeader("Google Play の要件"));
        var check = new Button { Content = "要件を確認", Style = (Style)Resources["PkgBtn"], HorizontalAlignment = HorizontalAlignment.Left };
        check.Click += async (_, _) => await CheckAndroidRequirementsAsync(check);
        SettingsPane.Children.Add(BuildLabeledRow(string.Empty, check,
            "ビルドをせずに、設定（targetSdk・versionCode・署名・アイコン・アプリ ID）と前回の配布物（debuggable・権限・16 KB・署名）を確かめます"));
        _androidRequirementsPanel = new StackPanel { Margin = new Thickness(0, SettingRowTopMargin, 0, SettingRowBottomMargin) };
        SettingsPane.Children.Add(_androidRequirementsPanel);
        ShowAndroidRequirements(_androidLastReport, _androidLastReportCaption);
    }

    /// <summary>
    /// ビルドをせずに要件を確かめる（中核の AndroidRequirementsCheckRunner。パスワードは保護保存から）。
    /// </summary>
    /// <param name="button">押されたボタン（確かめている間は押せなくする）。</param>
    private async Task CheckAndroidRequirementsAsync(Button button)
    {
        if (_androidToolBusy || _isBuilding) return;
        SaveSettings();
        var engine = AndroidEnginePaths.Locate(AppContext.BaseDirectory, Environment.CurrentDirectory, _runtimePath);
        if (engine is null)
        {
            AppendLog("エラー: " + AndroidRunEnvironment.NoEngineReason);
            return;
        }
        var projectDir = string.IsNullOrWhiteSpace(ProjectContext.RootDir) ? _assetsPath : ProjectContext.RootDir;
        var request = AndroidEditorRunRequests.ForReleasePackage(
            projectDir, AndroidApkOutput.AbisFor(_data.Android.Arch), _data.Android.Format, null, null, LoadStoredAndroidSecrets());
        AppendLog("");
        AppendLog("═══ Google Play の要件の確認 ═══");
        _androidToolBusy = true;
        button.IsEnabled = false;
        try
        {
            var toolchain = AndroidToolchain.Detect();
            var result = await Task.Run(() => AndroidRequirementsCheckRunner.RunAsync(
                engine, toolchain, request, null, line => Dispatcher.Invoke(() => AppendLog(line)), CancellationToken.None));
            foreach (var item in result.Report.Items) AppendLog("  " + item.Describe());
            ShowAndroidRequirements(result.Report,
                "要件を確認" + (result.ArtifactPath is null ? "（配布物はまだありません）" : $"（{Path.GetFileName(result.ArtifactPath)}）"));
        }
        catch (AndroidPipelineException ex)
        {
            AppendLog("要件を確かめられませんでした: " + ex.Message);
        }
        finally
        {
            _androidToolBusy = false;
            button.IsEnabled = true;
        }
    }

    /// <summary>要件の一覧を並べ直す（覚えておき、ペインを作り直しても出す）。</summary>
    /// <param name="report">一覧（無ければ案内だけ）。</param>
    /// <param name="caption">出どころ。</param>
    private void ShowAndroidRequirements(AndroidRequirementReport? report, string? caption)
    {
        _androidLastReport = report;
        _androidLastReportCaption = caption;
        if (_androidRequirementsPanel is null) return;
        _androidRequirementsPanel.Children.Clear();
        if (report is null)
        {
            _androidRequirementsPanel.Children.Add(new TextBlock
            {
                Text = "まだ確かめていません（配布用のビルドの後にも出します）。",
                Foreground = RequirementInfoBrush,
                FontSize = RequirementFontSize,
            });
            return;
        }
        _androidRequirementsPanel.Children.Add(new TextBlock
        {
            Text = $"{caption}: {report.Summary()}",
            Foreground = report.HasFailures ? RequirementFailureBrush : RequirementPassBrush,
            FontSize = RequirementFontSize,
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, RequirementRowSpacing),
        });
        foreach (var item in report.Items) _androidRequirementsPanel.Children.Add(BuildRequirementRow(item));
    }

    /// <summary>要件の 1 行（判定のアイコン＋見出しと説明。色は判定ごと）。</summary>
    private static UIElement BuildRequirementRow(AndroidRequirementItem item)
    {
        var (iconKey, brush) = item.Severity switch
        {
            AndroidRequirementSeverity.Pass    => ("Icon.Apply", RequirementPassBrush),
            AndroidRequirementSeverity.Info    => ("Icon.Info", RequirementInfoBrush),
            AndroidRequirementSeverity.Warning => ("Icon.Warning", RequirementWarningBrush),
            _                                  => ("Icon.Close", RequirementFailureBrush),
        };
        var icon = SEEDEditor.Controls.AppIcon.Create(iconKey, RequirementIconSize);
        icon.SetBrush(brush);
        icon.VerticalAlignment = VerticalAlignment.Top;
        icon.Margin = new Thickness(0, SettingRowTopMargin, RequirementIconGap, 0);

        var grid = new Grid { Margin = new Thickness(0, 0, 0, RequirementRowSpacing) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var text = new TextBlock
        {
            Text = $"[{AndroidRequirementReport.Label(item.Severity)}] {item.Title}: {item.Detail}",
            Foreground = brush,
            FontSize = RequirementFontSize,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(icon, 0);
        Grid.SetColumn(text, 1);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        return grid;
    }
}
