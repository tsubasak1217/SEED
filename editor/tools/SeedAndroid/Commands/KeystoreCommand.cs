// ============================================================
//  KeystoreCommand.cs — keystore create（配布用の鍵＝アップロード鍵のキーストアを作る。段階D。docs/android.md §24）
//
//  中身は中核の Signing/AndroidKeystoreTool.CreateAsync（keytool -genkeypair。パッケージ化ウィンドウの「新しいキーストアを作る」と共通）。
//  パスワードは環境変数 SEED_ANDROID_KEYSTORE_PASSWORD、無ければ対話で 2 回聞く（コマンドラインには書かない）。
//  既にあるファイルは上書きしない。--project を付けると、そのアセットフォルダの中に作ろうとしたときに止める。
//  プロジェクトの設定（packaging_settings.json）は書き換えない（使うときに --keystore / --key-alias か設定ファイルで指定する）。
// ============================================================

using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Signing;
using SEEDEditor.Android.Toolchain;

namespace SEEDEditor.Tools.SeedAndroid.Commands;

/// <summary>keystore create。</summary>
public static class KeystoreCommand
{
    /// <summary>
    /// キーストアを作り、証明書の要点と保管の注意を書く。
    /// </summary>
    /// <param name="toolchain">道具の場所。</param>
    /// <param name="line">コマンドラインの指定。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>終了コード。</returns>
    public static async Task<int> RunAsync(AndroidToolchain toolchain, SeedAndroidCommandLine line, CancellationToken cancellationToken)
    {
        var keystorePath = Path.GetFullPath(line.KeystorePath!);
        var alias = string.IsNullOrWhiteSpace(line.KeyAlias) ? AndroidKeystoreDefaults.DefaultAlias : line.KeyAlias.Trim();
        var assetsRoot = line.ProjectDir is null ? null : AndroidProjectResolver.Resolve(line.ProjectDir, null)?.Folder.AssetsRoot;

        // 対話で聞く前に、作れない指定（既にある・アセットの中）を弾く（パスワードを 2 回打たせてから断らない）
        var placeholder = new AndroidSigningSecrets(new string('x', AndroidKeystoreDefaults.MinPasswordLength), null, "検査用");
        if (AndroidKeystoreTool.ValidateCreate(new AndroidKeystoreCreateRequest(keystorePath, alias, placeholder, line.CertificateName, assetsRoot)) is { } problem)
        {
            Console.Error.WriteLine($"エラー: {problem}");
            return SeedAndroidExitCodes.InvalidRequest;
        }

        var secrets = AndroidSigningSecrets.FromEnvironment(Environment.GetEnvironmentVariable);
        if (secrets is null)
        {
            secrets = ConsoleSecretPrompt.AskNew(out var promptError);
            if (secrets is null)
            {
                Console.Error.WriteLine($"エラー: {promptError}");
                return SeedAndroidExitCodes.InvalidRequest;
            }
        }

        Console.Out.WriteLine($"キーストアを作ります: {keystorePath}（別名 {alias}・{AndroidKeystoreDefaults.StoreType}・{AndroidKeystoreDefaults.KeyAlgorithm} " +
                              $"{AndroidKeystoreDefaults.KeySizeBits}・{AndroidKeystoreDefaults.ValidityDays} 日・パスワード {secrets}）");
        var certificate = await AndroidKeystoreTool.CreateAsync(
            toolchain.RequireKeytool(),
            new AndroidKeystoreCreateRequest(keystorePath, alias, secrets, line.CertificateName, assetsRoot),
            output => Console.Out.WriteLine("  " + output),
            cancellationToken);

        Console.Out.WriteLine();
        Console.Out.WriteLine($"作りました: {keystorePath}");
        Console.Out.WriteLine($"  別名 {alias}・{certificate.Describe()}");
        Console.Out.WriteLine();
        Console.Out.WriteLine("これが配布用の鍵（Google Play の Play App Signing では「アップロード鍵」）です。");
        Console.Out.WriteLine("  - キーストアとパスワードを別々の安全な場所にも控えてください（失うと Google Play に鍵の再設定を頼むまで更新を出せません）。");
        Console.Out.WriteLine("  - リポジトリ・プロジェクトのアセットフォルダに置かないでください。");
        Console.Out.WriteLine("  - 使うときは build --variant release --keystore <このファイル> --key-alias " + alias +
                              "（または packaging_settings.json の android.signing）。docs/android.md §24。");
        return SeedAndroidExitCodes.Success;
    }
}
