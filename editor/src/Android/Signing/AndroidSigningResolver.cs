// ============================================================
//  AndroidSigningResolver.cs — 配布用（release）の署名に使う鍵を決める（段階D。docs/android.md §24）
//
//  【決め方】
//    キーストア … 指定（SeedAndroid の --keystore・エディタの画面。相対パスはカレントフォルダから）
//                 → packaging_settings.json の android.signing.keystore_path（相対パスはプロジェクトのルートから）
//    別名       … 指定（--key-alias）→ android.signing.key_alias
//    パスワード … 指定（エディタの保護保存・SeedAndroid の対話の入力）→ 環境変数 SEED_ANDROID_KEYSTORE_PASSWORD /
//                 SEED_ANDROID_KEY_PASSWORD（AndroidSigningSecrets）
//  どれかが無ければ、デバッグ署名にも無署名にもせず、何を指定すればよいかを書いたエラーにする（ビルドは始めない）。
//
//  【キーストアの置き場の決まり】
//    - アセットルートの中は不可（「全ファイル同梱」や参照で pak に入り、ゲームと一緒に配られ得る）
//    - プロジェクトのフォルダの中は警告（プロジェクトをバージョン管理に入れると鍵も入り得る。プロジェクトの外を勧める）
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using SEEDEditor.Android.Pipeline;

namespace SEEDEditor.Android.Signing;

/// <summary>配布用の署名の鍵を決める。</summary>
public static class AndroidSigningResolver
{
    /// <summary>指定（コマンドライン・エディタの画面）から来たキーストアの出どころの表示。</summary>
    public const string RequestOrigin = "指定";

    /// <summary>プロジェクトの設定ファイルから来たキーストアの出どころの表示。</summary>
    public const string ProjectSettingsOrigin = "packaging_settings.json の android.signing";

    /// <summary>キーストアが無いときの説明（何を指定すればよいか）。</summary>
    public const string MissingKeystoreMessage =
        "配布用（release）の署名に使うキーストアが指定されていません。SeedAndroid は --keystore <キーストア> --key-alias <別名>、" +
        "エディタはパッケージ化ウィンドウの「署名」で指定してください（鍵がまだ無ければ SeedAndroid の keystore create か、" +
        "パッケージ化ウィンドウの「新しいキーストアを作る」で作れます。docs/android.md §24）。デバッグ署名の配布物は作りません。";

    /// <summary>別名が無いときの説明。</summary>
    public const string MissingAliasMessage =
        "キーの別名が指定されていません（SeedAndroid の --key-alias、または packaging_settings.json の android.signing.key_alias。" +
        "パッケージ化ウィンドウの「署名」の「別名」）。";

    /// <summary>パスワードが無いときの説明。</summary>
    public const string MissingPasswordMessage =
        "キーストアのパスワードがありません。エディタはパッケージ化ウィンドウの「署名」でパスワードを保存し、SeedAndroid は環境変数 " +
        AndroidSigningSecrets.KeystorePasswordVariable + "（キーのパスワードが違えば " + AndroidSigningSecrets.KeyPasswordVariable +
        "）を設定するか、対話で入力してください。パスワードはファイルにもコマンドラインにも書きません。";

    /// <summary>
    /// 署名の鍵を決める（パスワードは指定 → 環境変数）。足りなければ理由付きの例外。
    /// </summary>
    /// <param name="inputs">材料。</param>
    /// <param name="getEnvironmentVariable">環境変数の読み口（null なら実際の環境変数。単体テストで差し替える）。</param>
    /// <returns>決まった鍵。</returns>
    /// <exception cref="AndroidPipelineException">キーストア・別名・パスワードのどれかが無い・キーストアが使えない場所にあるとき。</exception>
    public static AndroidSigningConfig Resolve(AndroidSigningInputs inputs, Func<string, string?>? getEnvironmentVariable = null)
    {
        var env = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
        var warnings = new List<string>();

        // ── キーストア ──
        var (keystorePath, origin) = ResolveKeystorePath(inputs);
        if (keystorePath is null) throw Invalid(MissingKeystoreMessage);
        if (!File.Exists(keystorePath))
        {
            throw Invalid($"キーストアがありません: {keystorePath}（{origin}）。場所を直すか、鍵がまだ無ければ作ってください（docs/android.md §24）。");
        }
        if (inputs.AssetsRoot is { } assetsRoot && IsUnder(keystorePath, assetsRoot))
        {
            throw Invalid($"キーストアがアセットフォルダの中にあります: {keystorePath}。アセットは pak に入ってゲームと一緒に配られ得るので、" +
                          "アセットフォルダの外（プロジェクトの外を勧めます）へ移し、場所を指定し直してください。");
        }
        if (inputs.ProjectRoot is { } projectRoot && IsUnder(keystorePath, projectRoot))
        {
            warnings.Add($"キーストアがプロジェクトのフォルダの中にあります（{keystorePath}）。プロジェクトをバージョン管理に入れているなら" +
                         "鍵が入らないようにしてください（鍵はプロジェクトの外に保管し、別の場所にも控えを取っておくことを勧めます）。");
        }

        // ── 別名 ──
        var alias = FirstNonBlank(inputs.RequestKeyAlias, inputs.ProjectSettings?.KeyAlias);
        if (alias is null) throw Invalid(MissingAliasMessage);

        // ── パスワード ──
        var secrets = inputs.RequestSecrets ?? AndroidSigningSecrets.FromEnvironment(env);
        if (secrets is null) throw Invalid(MissingPasswordMessage);

        return new AndroidSigningConfig(keystorePath, alias, secrets, origin, warnings);
    }

    /// <summary>
    /// キーストアの場所を決める（指定 → 設定ファイル。どちらも無ければ null）。
    /// </summary>
    /// <param name="inputs">材料。</param>
    /// <returns>絶対パスと出どころ。</returns>
    public static (string? Path, string Origin) ResolveKeystorePath(AndroidSigningInputs inputs)
    {
        if (!string.IsNullOrWhiteSpace(inputs.RequestKeystorePath))
        {
            return (Path.GetFullPath(inputs.RequestKeystorePath.Trim()), RequestOrigin);
        }
        var configured = inputs.ProjectSettings?.KeystorePath;
        if (string.IsNullOrWhiteSpace(configured)) return (null, ProjectSettingsOrigin);
        return (ResolveConfiguredPath(configured, inputs.ProjectRoot), ProjectSettingsOrigin);
    }

    /// <summary>
    /// 設定ファイルに書かれたキーストアのパスを絶対パスにする（相対パスはプロジェクトのルートから。ルートが無ければカレントから）。
    /// </summary>
    /// <param name="configured">書かれたパス。</param>
    /// <param name="projectRoot">プロジェクトのルート（無ければ null）。</param>
    /// <returns>絶対パス。</returns>
    public static string ResolveConfiguredPath(string configured, string? projectRoot)
    {
        var trimmed = configured.Trim();
        return Path.IsPathRooted(trimmed) || projectRoot is null
            ? Path.GetFullPath(trimmed)
            : Path.GetFullPath(Path.Combine(projectRoot, trimmed));
    }

    /// <summary>
    /// パスがフォルダの中（直下を含む）にあるか（大文字小文字を区別しない。Windows のパス）。
    /// </summary>
    /// <param name="path">調べるパス。</param>
    /// <param name="directory">フォルダ。</param>
    /// <returns>中にあれば true。</returns>
    public static bool IsUnder(string path, string directory)
    {
        var full = Path.GetFullPath(path);
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>空でない最初の値（前後の空白を落とす。どれも空なら null）。</summary>
    /// <param name="values">候補。</param>
    /// <returns>値。</returns>
    private static string? FirstNonBlank(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value)) return value.Trim();
        }
        return null;
    }

    /// <summary>指定の誤りの例外を作る。</summary>
    /// <param name="message">説明。</param>
    /// <returns>例外。</returns>
    private static AndroidPipelineException Invalid(string message) => new(AndroidFailureKind.InvalidRequest, message);
}
