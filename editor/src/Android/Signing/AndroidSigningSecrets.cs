// ============================================================
//  AndroidSigningSecrets.cs — 配布用（release）の署名のパスワード（メモリの中だけで持つ）
//
//  【出どころ】（優先の順。決め方は AndroidSigningResolver）
//    1. 指定（AndroidRunRequest.SigningSecrets）… エディタはエディタ設定の保護保存（DPAPI。Settings/AndroidSigningSecretStore）から、
//       SeedAndroid は対話の入力（コンソールでパスワードを打つ）から入れる
//    2. 環境変数 SEED_ANDROID_KEYSTORE_PASSWORD（キーストア）/ SEED_ANDROID_KEY_PASSWORD（キー。無ければキーストアと同じ）
//       … CI・SeedAndroid を対話なしで使うとき
//  キーのパスワードを省くとキーストアのパスワードを使う（keytool が既定で作る PKCS12 のキーストアは、キーとキーストアの
//  パスワードが同じでなければならない。keytool は違う -keypass を無視する）。
//
//  【漏らさないための決まり】
//    - ファイル・JSON・ログ・Gradle のコマンドラインへ書かない（Gradle へは環境変数 ORG_GRADLE_PROJECT_seed.signing.* で渡す。
//      keytool へは -storepass:env で子プロセスの環境変数から読ませる）
//    - ToString は伏せ字を返す（record の自動の ToString やログに混ざっても中身が出ない）
//    - 指紋（step_stamps.json）にも入れない（ハッシュも残さない。弱いパスワードを総当たりで戻されないように）
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;

namespace SEEDEditor.Android.Signing;

/// <summary>署名のパスワード（キーストアとキー）。</summary>
public sealed class AndroidSigningSecrets
{
    /// <summary>キーストアのパスワードを入れる環境変数（SeedAndroid・CI）。</summary>
    public const string KeystorePasswordVariable = "SEED_ANDROID_KEYSTORE_PASSWORD";

    /// <summary>キーのパスワードを入れる環境変数（省略時はキーストアのパスワード）。</summary>
    public const string KeyPasswordVariable = "SEED_ANDROID_KEY_PASSWORD";

    /// <summary>ログ・画面でパスワードの代わりに出す伏せ字。</summary>
    public const string Mask = "********";

    /// <summary>キーストアのパスワード。</summary>
    public string KeystorePassword { get; }

    /// <summary>キーのパスワード（PKCS12 ではキーストアと同じ）。</summary>
    public string KeyPassword { get; }

    /// <summary>どこから来たか（ログ用。値は含まない）。</summary>
    public string Origin { get; }

    /// <summary>
    /// パスワードを指定して作る。
    /// </summary>
    /// <param name="keystorePassword">キーストアのパスワード（空は不可）。</param>
    /// <param name="keyPassword">キーのパスワード（null・空ならキーストアのパスワード）。</param>
    /// <param name="origin">どこから来たか（ログ用）。</param>
    /// <exception cref="ArgumentException">キーストアのパスワードが空のとき。</exception>
    public AndroidSigningSecrets(string keystorePassword, string? keyPassword, string origin)
    {
        if (string.IsNullOrEmpty(keystorePassword))
        {
            throw new ArgumentException("キーストアのパスワードが空です。", nameof(keystorePassword));
        }
        KeystorePassword = keystorePassword;
        KeyPassword = string.IsNullOrEmpty(keyPassword) ? keystorePassword : keyPassword;
        Origin = origin;
    }

    /// <summary>
    /// 環境変数から読む（キーストアのパスワードが無ければ null）。
    /// </summary>
    /// <param name="getEnvironmentVariable">環境変数の読み口（単体テストで差し替える）。</param>
    /// <returns>パスワード（無ければ null）。</returns>
    public static AndroidSigningSecrets? FromEnvironment(Func<string, string?> getEnvironmentVariable)
    {
        var keystorePassword = getEnvironmentVariable(KeystorePasswordVariable);
        if (string.IsNullOrEmpty(keystorePassword)) return null;
        var keyPassword = getEnvironmentVariable(KeyPasswordVariable);
        var origin = string.IsNullOrEmpty(keyPassword)
            ? $"環境変数 {KeystorePasswordVariable}"
            : $"環境変数 {KeystorePasswordVariable} / {KeyPasswordVariable}";
        return new AndroidSigningSecrets(keystorePassword, keyPassword, origin);
    }

    /// <summary>中身を出さない（record の ToString・ログに混ざっても漏れないように）。</summary>
    /// <returns>伏せ字と出どころ。</returns>
    public override string ToString() => $"{Mask}（{Origin}）";
}
