// ============================================================
//  AndroidSigningDpapiProtector.cs — 配布用の署名のパスワードを DPAPI（CurrentUser）で包む（段階D。docs/android.md §24）
//
//  アカウントの秘密鍵を包む Accounts/Storage/DpapiSecretProtector と同じ仕組み（System.Security.Cryptography.ProtectedData。
//  エディタは WPF 経由でこれを持つ。SeedAndroid・単体テストはこのファイルを使わない）。追加エントロピーは用途ごとに別の
//  文字列にして、同じ PC の同じ利用者の別の用途の記録と混ざらないようにする。
//  **この文字列を変えると、それ以前に保存したパスワードは解けなくなる（入れ直しになる）。**
// ============================================================

using System;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using SEEDEditor.Accounts.Storage;

namespace SEEDEditor.Settings;

/// <summary>署名のパスワードを DPAPI（CurrentUser）で包む。</summary>
[SupportedOSPlatform("windows")]
public sealed class AndroidSigningDpapiProtector : ISecretProtector
{
    /// <summary>追加エントロピーの元（用途を表す固定の文字列）。</summary>
    private const string EntropySeed = "SEED.Editor.AndroidSigning.v1";

    /// <summary>追加エントロピー。</summary>
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes(EntropySeed);

    /// <summary>プロセスで共有してよい実装（状態を持たない）。</summary>
    public static AndroidSigningDpapiProtector Instance { get; } = new();

    /// <inheritdoc />
    public byte[] Protect(byte[] plaintext)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
    }

    /// <inheritdoc />
    public byte[] Unprotect(byte[] protectedBytes)
    {
        ArgumentNullException.ThrowIfNull(protectedBytes);
        return ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
    }
}
