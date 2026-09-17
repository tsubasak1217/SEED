// ============================================================
//  DpapiSecretProtector.cs — DPAPI（CurrentUser）で秘密鍵を包む実装
//
//  【役割】
//  契約（docs/seed_accounts.md 2 章）の
//  「PKCS#8 を DPAPI（CurrentUser）で暗号化して保存」を実装する。
//
//  【DPAPI の性質（利用者への影響）】
//  ・包んだデータは **同じ Windows ユーザー・同じ PC** でしか解けない。
//    → 別の PC へ移すには「書き出し／読み込み」を使う（パスフレーズ方式）。
//    → ファイルをコピーしただけでは動かない。これは仕様であり、
//      盗まれても使えないという利点でもある。
//  ・Windows のパスワードをリセットすると解けなくなることがある。
//    そのときも「書き出したファイル」から復帰できる。
//
//  【追加エントロピー】
//  用途を表す固定文字列（AccountSettings.DPAPI_ENTROPY_SEED）を混ぜる。
//  同じ PC の別アプリが、同じ利用者の権限で偶然復号できてしまうのを避けるため。
//  **この文字列を変えると、それ以前に保存したアカウントは読めなくなる。**
//
//  【依存】
//  System.Security.Cryptography.ProtectedData（NuGet）。Windows 専用 API なので
//  ここ 1 ファイルへ閉じ込め、他の層は ISecretProtector しか見ない。
// ============================================================

using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace SEEDEditor.Accounts.Storage;

/// <summary>
/// Windows の DPAPI（CurrentUser スコープ）で秘密を包む実装。
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretProtector : ISecretProtector
{
    /// <summary>追加エントロピー（用途を混ぜる固定値）。</summary>
    private static readonly byte[] Entropy =
        Encoding.UTF8.GetBytes(AccountSettings.DPAPI_ENTROPY_SEED);

    /// <summary>プロセスで共有してよい既定の実装（状態を持たない）。</summary>
    public static DpapiSecretProtector Instance { get; } = new();

    /// <summary>平文を DPAPI で包む。</summary>
    /// <param name="plaintext">包む対象。</param>
    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    }

    /// <summary>DPAPI で包まれたバイト列を解く。</summary>
    /// <param name="protectedBytes">包まれたバイト列。</param>
    public byte[] Unprotect(byte[] protectedBytes)
    {
        ArgumentNullException.ThrowIfNull(protectedBytes);
        return ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
    }
}
