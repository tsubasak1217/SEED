// ============================================================
//  AndroidKeystoreTool.cs — JDK の keytool で配布用の鍵（キーストア）を作る・開けるか確かめる（段階D。docs/android.md §24）
//
//  【作る（keystore create・パッケージ化ウィンドウの「新しいキーストアを作る」）】
//    keytool -genkeypair -keystore <パス> -storetype PKCS12 -alias <別名> -keyalg RSA -keysize 2048 -validity 10000
//            -dname <名前> -storepass:env <変数> -keypass:env <変数>
//    鍵の長さ・有効期間は Android の公式の手順（アップロード鍵の作り方）と同じ値（AndroidKeystoreDefaults）。
//    既にあるファイルは上書きしない（アップロード鍵を失うと、Google Play に鍵の再設定を頼むまで更新を出せない）。
//  【確かめる（release のビルドの準備）】
//    keytool -list -v -keystore <パス> -alias <別名> -storepass:env <変数>
//    パスワード・別名の誤りを、数分かかる Rust のビルドより前に分かるようにする。証明書の持ち主・SHA-256・有効期限をログへ出す。
//
//  パスワードは子プロセスの環境変数だけで渡す（-storepass:env。コマンドラインにもログにも出さない）。
//  出力の言語は JVM の既定のロケールで変わるので、-J-Duser.language=en で英語に固定して読む。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Processes;

namespace SEEDEditor.Android.Signing;

/// <summary>新しい鍵の既定値（Android の公式の手順と同じ）。</summary>
public static class AndroidKeystoreDefaults
{
    /// <summary>キーストアの形式（JDK 9 以降の keytool の既定。JKS より新しい標準形式）。</summary>
    public const string StoreType = "PKCS12";

    /// <summary>鍵の方式。</summary>
    public const string KeyAlgorithm = "RSA";

    /// <summary>鍵の長さ（ビット）。</summary>
    public const int KeySizeBits = 2048;

    /// <summary>証明書の有効期間（日。約 27 年。Google Play は 25 年以上を勧める）。</summary>
    public const int ValidityDays = 10000;

    /// <summary>別名の既定値（アップロード鍵）。</summary>
    public const string DefaultAlias = "upload";

    /// <summary>証明書の名前（dname）を指定しないときの CN。</summary>
    public const string DefaultCommonName = "SEED Upload Key";

    /// <summary>パスワードの最短の長さ（keytool が 6 文字未満を拒む）。</summary>
    public const int MinPasswordLength = 6;
}

/// <summary>キーストアの中の証明書の要点（ログ・画面用）。</summary>
/// <param name="Alias">別名。</param>
/// <param name="EntryType">項目の種類（署名に使えるのは PrivateKeyEntry）。</param>
/// <param name="Owner">持ち主（証明書の名前）。</param>
/// <param name="Sha256">証明書の SHA-256 の指紋（Google Play のアップロード鍵の照合に使う）。</param>
/// <param name="ValidUntil">有効期限（keytool の表記のまま）。</param>
public sealed record AndroidKeystoreCertificate(string? Alias, string? EntryType, string? Owner, string? Sha256, string? ValidUntil)
{
    /// <summary>署名に使える項目か（秘密鍵を持つ）。</summary>
    public bool IsPrivateKey => string.Equals(EntryType, AndroidKeystoreTool.PrivateKeyEntryType, StringComparison.OrdinalIgnoreCase);

    /// <summary>ログ用の一行。</summary>
    /// <returns>説明。</returns>
    public string Describe() => $"証明書 {Owner ?? "（不明）"}・SHA-256 {Sha256 ?? "（不明）"}・有効期限 {ValidUntil ?? "（不明）"}";
}

/// <summary>新しいキーストアの指定。</summary>
/// <param name="KeystorePath">作るファイル（絶対パス。在ってはいけない）。</param>
/// <param name="KeyAlias">別名。</param>
/// <param name="Password">パスワード（キーストアとキーで同じ。PKCS12 の決まり）。</param>
/// <param name="CommonName">証明書の名前（CN。空なら既定値）。</param>
/// <param name="AssetsRoot">アセットルート（この中には作らない。無ければ null）。</param>
public sealed record AndroidKeystoreCreateRequest(string KeystorePath, string KeyAlias, AndroidSigningSecrets Password, string? CommonName, string? AssetsRoot)
{
    /// <summary>中身を出さない（パスワードは AndroidSigningSecrets の伏せ字）。</summary>
    /// <returns>説明。</returns>
    public override string ToString() => $"{KeystorePath}（別名 {KeyAlias}・パスワード {Password}）";
}

/// <summary>keytool の呼び出し。</summary>
public static class AndroidKeystoreTool
{
    /// <summary>署名に使える項目の種類（keytool の表記）。</summary>
    public const string PrivateKeyEntryType = "PrivateKeyEntry";

    /// <summary>keytool へパスワードを渡す子プロセスの環境変数（-storepass:env / -keypass:env で読ませる）。</summary>
    public const string PasswordVariable = "SEED_KEYTOOL_PASSWORD";

    /// <summary>出力を英語に固定する JVM の指定（ロケールで出力の見出しが変わるため）。</summary>
    public static readonly IReadOnlyList<string> EnglishOutputArguments = new[] { "-J-Duser.language=en", "-J-Duser.country=US" };

    /// <summary>「別名」の行の見出し。</summary>
    private const string AliasLabel = "Alias name:";

    /// <summary>「項目の種類」の行の見出し。</summary>
    private const string EntryTypeLabel = "Entry type:";

    /// <summary>「持ち主」の行の見出し。</summary>
    private const string OwnerLabel = "Owner:";

    /// <summary>「SHA-256 の指紋」の行の見出し。</summary>
    private const string Sha256Label = "SHA256:";

    /// <summary>「有効期間」の行の見出し（Valid from: … until: …）。</summary>
    private const string ValidFromLabel = "Valid from:";

    /// <summary>有効期間の行のうち、期限の前の語。</summary>
    private const string UntilLabel = "until:";

    /// <summary>keytool のエラーの行頭。</summary>
    private const string ErrorPrefix = "keytool error:";

    /// <summary>
    /// 新しいキーストアを作る（既にあれば作らない）。作った後に中身を読み直して返す。
    /// </summary>
    /// <param name="keytool">keytool.exe。</param>
    /// <param name="request">指定。</param>
    /// <param name="log">出力の受け手（子プロセスの行。パスワードは含まれない）。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>作った鍵の証明書の要点。</returns>
    /// <exception cref="AndroidPipelineException">指定の誤り・keytool の失敗。</exception>
    public static async Task<AndroidKeystoreCertificate> CreateAsync(
        string keytool, AndroidKeystoreCreateRequest request, Action<string>? log, CancellationToken cancellationToken)
    {
        var problem = ValidateCreate(request);
        if (problem is not null) throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest, problem);

        var directory = Path.GetDirectoryName(request.KeystorePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

        var arguments = new List<string>
        {
            "-genkeypair", "-keystore", request.KeystorePath, "-storetype", AndroidKeystoreDefaults.StoreType,
            "-alias", request.KeyAlias, "-keyalg", AndroidKeystoreDefaults.KeyAlgorithm,
            "-keysize", AndroidKeystoreDefaults.KeySizeBits.ToString(CultureInfo.InvariantCulture),
            "-validity", AndroidKeystoreDefaults.ValidityDays.ToString(CultureInfo.InvariantCulture),
            "-dname", DistinguishedName(request.CommonName),
            "-storepass:env", PasswordVariable, "-keypass:env", PasswordVariable,
        };
        arguments.AddRange(EnglishOutputArguments);
        var capture = await RunAsync(keytool, arguments, request.Password.KeystorePassword, cancellationToken).ConfigureAwait(false);
        foreach (var line in capture.StandardOutput.Concat(capture.StandardError)) log?.Invoke(line);
        if (capture.ExitCode != 0 || !File.Exists(request.KeystorePath))
        {
            throw new AndroidPipelineException(AndroidFailureKind.Build,
                $"keytool でキーストアを作れませんでした（終了コード {capture.ExitCode}）: {ErrorLine(capture)}");
        }
        return await ReadAsync(keytool, request.KeystorePath, request.KeyAlias, request.Password, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// 新しいキーストアの指定を確かめる（純粋な処理。問題が無ければ null）。
    /// </summary>
    /// <param name="request">指定。</param>
    /// <returns>問題の説明。</returns>
    public static string? ValidateCreate(AndroidKeystoreCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.KeystorePath)) return "作るキーストアの場所を指定してください。";
        if (File.Exists(request.KeystorePath) || Directory.Exists(request.KeystorePath))
        {
            return $"{request.KeystorePath} は既にあります。キーストアは上書きしません（配布用の鍵を失うと更新を出せなくなるため）。別の名前にしてください。";
        }
        if (request.AssetsRoot is { } assetsRoot && AndroidSigningResolver.IsUnder(request.KeystorePath, assetsRoot))
        {
            return $"キーストアをアセットフォルダの中（{request.KeystorePath}）には作りません。アセットは pak に入って配られ得るため、プロジェクトの外を指定してください。";
        }
        if (string.IsNullOrWhiteSpace(request.KeyAlias)) return "キーの別名を指定してください。";
        if (request.Password.KeystorePassword.Length < AndroidKeystoreDefaults.MinPasswordLength)
        {
            return $"パスワードは {AndroidKeystoreDefaults.MinPasswordLength} 文字以上にしてください（keytool の決まり）。";
        }
        return null;
    }

    /// <summary>
    /// キーストアを開いて、別名の証明書の要点を読む（パスワード・別名の確かめ）。
    /// </summary>
    /// <param name="keytool">keytool.exe。</param>
    /// <param name="keystorePath">キーストア。</param>
    /// <param name="alias">別名。</param>
    /// <param name="secrets">パスワード。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>証明書の要点。</returns>
    /// <exception cref="AndroidPipelineException">開けない（パスワード・別名の誤り）・署名に使えない項目のとき。</exception>
    public static async Task<AndroidKeystoreCertificate> ReadAsync(
        string keytool, string keystorePath, string alias, AndroidSigningSecrets secrets, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "-list", "-v", "-keystore", keystorePath, "-alias", alias, "-storepass:env", PasswordVariable };
        arguments.AddRange(EnglishOutputArguments);
        var capture = await RunAsync(keytool, arguments, secrets.KeystorePassword, cancellationToken).ConfigureAwait(false);
        if (capture.ExitCode != 0)
        {
            throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest,
                $"キーストア {keystorePath} の別名 {alias} を開けません（パスワードと別名を確かめてください）: {ErrorLine(capture)}");
        }
        var certificate = ParseList(capture.StandardOutput);
        if (!certificate.IsPrivateKey)
        {
            throw new AndroidPipelineException(AndroidFailureKind.InvalidRequest,
                $"キーストア {keystorePath} の別名 {alias} は秘密鍵を持っていません（種類 {certificate.EntryType ?? "不明"}）。署名に使う鍵の別名を指定してください。");
        }
        return certificate;
    }

    /// <summary>
    /// keytool -list -v（英語）の出力から証明書の要点を読む（純粋な処理。最初の証明書だけ）。
    /// </summary>
    /// <param name="lines">出力の行。</param>
    /// <returns>要点（読めなかった項目は null）。</returns>
    public static AndroidKeystoreCertificate ParseList(IEnumerable<string> lines)
    {
        string? alias = null, entryType = null, owner = null, sha256 = null, validUntil = null;
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            alias ??= ValueAfter(line, AliasLabel);
            entryType ??= ValueAfter(line, EntryTypeLabel);
            owner ??= ValueAfter(line, OwnerLabel);
            sha256 ??= ValueAfter(line, Sha256Label);
            if (validUntil is null && line.StartsWith(ValidFromLabel, StringComparison.Ordinal))
            {
                var index = line.IndexOf(UntilLabel, StringComparison.Ordinal);
                if (index >= 0) validUntil = line[(index + UntilLabel.Length)..].Trim();
            }
        }
        return new AndroidKeystoreCertificate(alias, entryType, owner, sha256, validUntil);
    }

    /// <summary>
    /// 証明書の名前（dname）を作る（CN だけ。RFC 2253 の特別な文字はバックスラッシュで逃がす）。
    /// </summary>
    /// <param name="commonName">CN（空なら既定値）。</param>
    /// <returns>dname。</returns>
    public static string DistinguishedName(string? commonName)
    {
        var name = string.IsNullOrWhiteSpace(commonName) ? AndroidKeystoreDefaults.DefaultCommonName : commonName.Trim();
        var escaped = new StringBuilder();
        foreach (var c in name)
        {
            if (c is ',' or '+' or '"' or '\\' or '<' or '>' or ';' or '=') escaped.Append('\\');
            escaped.Append(c);
        }
        return "CN=" + escaped;
    }

    /// <summary>keytool をパスワード入りの環境変数付きで動かし、出力を集める。</summary>
    private static Task<ChildProcessCapture> RunAsync(
        string keytool, IReadOnlyList<string> arguments, string password, CancellationToken cancellationToken) =>
        ChildProcessRunner.CaptureAsync(new ChildProcessSpec
        {
            FileName = keytool,
            Arguments = arguments,
            Environment = new Dictionary<string, string?> { [PasswordVariable] = password },
        }, cancellationToken);

    /// <summary>行が見出しで始まれば、その後ろの値（前後の空白を落とす）。違えば null。</summary>
    private static string? ValueAfter(string line, string label) =>
        line.StartsWith(label, StringComparison.Ordinal) ? line[label.Length..].Trim() : null;

    /// <summary>keytool のエラーの行（無ければ出力の最後の行）。</summary>
    private static string ErrorLine(ChildProcessCapture capture)
    {
        var all = capture.StandardOutput.Concat(capture.StandardError).Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
        return all.FirstOrDefault(line => line.TrimStart().StartsWith(ErrorPrefix, StringComparison.OrdinalIgnoreCase))
               ?? all.LastOrDefault()
               ?? "（出力なし）";
    }
}
