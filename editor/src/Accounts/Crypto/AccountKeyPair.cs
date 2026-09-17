// ============================================================
//  AccountKeyPair.cs — 利用者の鍵ペア（ECDSA P-256）
//
//  【役割】
//  契約（docs/seed_accounts.md 2 章）が定める鍵と署名の形式を、この 1 か所で扱う。
//    ・鍵    … ECDSA P-256（.NET 標準の ECDsa。外部ライブラリ不要）
//    ・公開鍵 … SEC1 非圧縮点（65 バイト: 0x04 ‖ X ‖ Y）の base64url
//    ・署名   … SHA-256 ＋ IEEE P1363 固定長（r ‖ s の 64 バイト）の base64url
//
//  【なぜ P1363 なのか（間違えると必ず失敗する）】
//  .NET の既定は **DER（ASN.1）形式**で、長さが可変（70〜72 バイト程度）になる。
//  契約は固定長 64 バイトなので、`DSASignatureFormat.IeeeP1363FixedFieldConcatenation`
//  を明示しないとサーバ側の検証が通らない。しかも「動くように見えて時々通る」
//  ような壊れ方はせず、**常に失敗する**（気づけるのが救い）。
//
//  【秘密鍵の扱い】
//  ・この型は秘密鍵をメモリに持つ。IDisposable を必ず使い、破棄する。
//  ・ログ・例外メッセージ・画面へ秘密鍵を出さない。
//  ・ディスクへ書くのは保管層（AccountFileStore）だけで、必ず DPAPI で包む。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Security.Cryptography;

namespace SEEDEditor.Accounts.Crypto;

/// <summary>
/// 利用者の鍵ペア（ECDSA P-256）。秘密鍵を持つので必ず破棄すること。
/// </summary>
public sealed class AccountKeyPair : IDisposable
{
    /// <summary>署名・検証に使うハッシュアルゴリズム（契約 2 章）。</summary>
    private static readonly HashAlgorithmName SignatureHash = HashAlgorithmName.SHA256;

    /// <summary>
    /// 署名の形式（契約 2 章）。
    /// **既定の DER にすると長さが可変になり、必ず検証に失敗する。**
    /// </summary>
    private const DSASignatureFormat SignatureFormat =
        DSASignatureFormat.IeeeP1363FixedFieldConcatenation;

    /// <summary>実体。秘密鍵を保持する。</summary>
    private readonly ECDsa _ecdsa;

    /// <summary>破棄済みの印。</summary>
    private bool _disposed;

    /// <summary>公開鍵の base64url 表現（SEC1 非圧縮点）。</summary>
    public string PublicKeyBase64Url { get; }

    /// <summary>
    /// 既存の <see cref="ECDsa"/> から生成する（内部用）。
    /// </summary>
    /// <param name="ecdsa">所有権ごと受け取る鍵。</param>
    private AccountKeyPair(ECDsa ecdsa)
    {
        _ecdsa             = ecdsa;
        PublicKeyBase64Url = Base64Url.Encode(ExportPublicKeySec1(ecdsa));
    }

    // ── 生成・復元 ──────────────────────────────────────────

    /// <summary>
    /// 新しい鍵ペアを作る（P-256）。
    /// </summary>
    /// <returns>生成した鍵ペア。</returns>
    public static AccountKeyPair Create()
        => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>
    /// PKCS#8（暗号化なし）のバイト列から復元する。
    /// </summary>
    /// <param name="pkcs8PrivateKey">PKCS#8 形式の秘密鍵。</param>
    /// <returns>復元した鍵ペア。</returns>
    /// <exception cref="CryptographicException">形式が不正なとき。</exception>
    public static AccountKeyPair FromPkcs8(ReadOnlySpan<byte> pkcs8PrivateKey)
    {
        var ecdsa = ECDsa.Create();
        try
        {
            ecdsa.ImportPkcs8PrivateKey(pkcs8PrivateKey, out _);
            return new AccountKeyPair(ecdsa);
        }
        catch
        {
            // 取り込みに失敗したら、ここで作った ECDsa を漏らさない。
            ecdsa.Dispose();
            throw;
        }
    }

    /// <summary>
    /// 秘密鍵を PKCS#8（暗号化なし）で書き出す。
    ///
    /// <para>
    /// ★戻り値は平文の秘密鍵。呼び出し側は必ず
    /// DPAPI かパスフレーズで包んでから保存し、使い終わったら
    /// <see cref="CryptographicOperations.ZeroMemory"/> で消すこと。
    /// </para>
    /// </summary>
    /// <returns>PKCS#8 のバイト列。</returns>
    public byte[] ExportPkcs8PrivateKey()
    {
        ThrowIfDisposed();
        return _ecdsa.ExportPkcs8PrivateKey();
    }

    // ── 署名・検証 ──────────────────────────────────────────

    /// <summary>
    /// UTF-8 の文字列へ署名し、base64url（64 バイトの P1363）で返す。
    /// </summary>
    /// <param name="payload">署名対象の文字列（<see cref="LoginSignaturePayload"/> が作る）。</param>
    /// <returns>署名の base64url。</returns>
    public string Sign(string payload)
    {
        ThrowIfDisposed();

        var data      = System.Text.Encoding.UTF8.GetBytes(payload ?? string.Empty);
        var signature = _ecdsa.SignData(data, SignatureHash, SignatureFormat);
        return Base64Url.Encode(signature);
    }

    /// <summary>
    /// 公開鍵だけで署名を検証する（自分の署名の自己検査とテストに使う）。
    /// </summary>
    /// <param name="publicKeyBase64Url">SEC1 非圧縮点の base64url。</param>
    /// <param name="payload">署名対象の文字列。</param>
    /// <param name="signatureBase64Url">署名の base64url。</param>
    /// <returns>検証できたら真。形式が不正な場合も偽（例外は投げない）。</returns>
    public static bool Verify(
        string? publicKeyBase64Url, string payload, string? signatureBase64Url)
    {
        if (!Base64Url.TryDecode(publicKeyBase64Url, out var publicKey))  return false;
        if (!Base64Url.TryDecode(signatureBase64Url, out var signature))  return false;

        // 長さが契約どおりでなければ、検証にかけるまでもなく不正。
        if (publicKey.Length != AccountSettings.PUBLIC_KEY_BYTE_LENGTH) return false;
        if (signature.Length != AccountSettings.SIGNATURE_BYTE_LENGTH)  return false;
        if (publicKey[0] != AccountSettings.PUBLIC_KEY_UNCOMPRESSED_PREFIX) return false;

        try
        {
            // 65 バイトを X / Y へ切り分けて公開鍵だけの ECDsa を作る。
            var coordinate = AccountSettings.P256_COORDINATE_BYTE_LENGTH;
            var x = new byte[coordinate];
            var y = new byte[coordinate];
            Array.Copy(publicKey, 1,              x, 0, coordinate);
            Array.Copy(publicKey, 1 + coordinate, y, 0, coordinate);

            using var ecdsa = ECDsa.Create(new ECParameters
            {
                Curve = ECCurve.NamedCurves.nistP256,
                Q     = new ECPoint { X = x, Y = y },
            });

            var data = System.Text.Encoding.UTF8.GetBytes(payload ?? string.Empty);
            return ecdsa.VerifyData(data, signature, SignatureHash, SignatureFormat);
        }
        catch (CryptographicException)
        {
            // 曲線上に無い点など。不正な公開鍵は「検証できない」で片付ける。
            return false;
        }
    }

    // ── 内部ヘルパー ────────────────────────────────────────

    /// <summary>
    /// 公開鍵を SEC1 非圧縮点（`0x04 ‖ X ‖ Y` の 65 バイト）へ書き出す。
    ///
    /// <para>
    /// ★X / Y は「先頭のゼロを詰めない 32 バイト」でなければならない。
    /// .NET の <c>ExportParameters</c> は P-256 なら 32 バイトで返すが、
    /// 実装が短く返した場合に備えて **右詰めで 32 バイトへ揃える**。
    /// ここを間違えると、たまに（256 回に 1 回程度）長さの違う公開鍵ができ、
    /// 「普段は動くのに時々ログインできない」という最悪の壊れ方をする。
    /// </para>
    /// </summary>
    /// <param name="ecdsa">対象の鍵。</param>
    private static byte[] ExportPublicKeySec1(ECDsa ecdsa)
    {
        var parameters = ecdsa.ExportParameters(includePrivateParameters: false);
        var coordinate = AccountSettings.P256_COORDINATE_BYTE_LENGTH;

        var result = new byte[AccountSettings.PUBLIC_KEY_BYTE_LENGTH];
        result[0] = AccountSettings.PUBLIC_KEY_UNCOMPRESSED_PREFIX;

        CopyRightAligned(parameters.Q.X, result, destinationOffset: 1,              length: coordinate);
        CopyRightAligned(parameters.Q.Y, result, destinationOffset: 1 + coordinate, length: coordinate);

        return result;
    }

    /// <summary>
    /// 座標のバイト列を、固定長の枠へ右詰めで写す（不足分は先頭ゼロ）。
    /// </summary>
    /// <param name="source">元のバイト列（null 可）。</param>
    /// <param name="destination">書き込み先。</param>
    /// <param name="destinationOffset">書き込み先の開始位置。</param>
    /// <param name="length">枠の長さ。</param>
    private static void CopyRightAligned(
        byte[]? source, byte[] destination, int destinationOffset, int length)
    {
        if (source is null || source.Length == 0) return;

        if (source.Length > length)
        {
            // 想定より長い（先頭に余分なゼロが付いている等）。末尾 length バイトを採る。
            Array.Copy(source, source.Length - length, destination, destinationOffset, length);
            return;
        }

        Array.Copy(
            source, 0,
            destination, destinationOffset + (length - source.Length),
            source.Length);
    }

    /// <summary>破棄済みなら例外を投げる。</summary>
    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    /// <summary>秘密鍵を解放する。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _ecdsa.Dispose();
    }
}
