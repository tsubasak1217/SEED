// ============================================================
//  Base64Url.cs — base64url（パディングなし）の相互変換
//
//  【役割】
//  契約（docs/seed_accounts.md 2 章）で、公開鍵・署名・nonce は
//  **base64url のパディングなし** と決まっている。素の Convert.ToBase64String は
//  `+` `/` `=` を使う別の表現なので、そのまま送ると相手側で復号できない。
//  変換をこの 1 か所へ閉じ込め、呼び出し側で置換処理を書かせない。
//
//  【なぜ標準 API を使わないのか】
//  .NET には Base64Url 相当の公開 API が .NET 9 で入ったが
//  （System.Buffers.Text.Base64Url）、このファイルは
//  net9.0 / net9.0-windows の双方からリンクされ、かつ挙動を
//  テストで固定したいので、依存の少ない自前実装にしてある。
//  処理は短く、ホットパスでもない。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;

namespace SEEDEditor.Accounts.Crypto;

/// <summary>
/// base64url（RFC 4648 §5、パディングなし）の変換。
/// </summary>
public static class Base64Url
{
    /// <summary>標準 base64 の 62 番目の文字。</summary>
    private const char BASE64_CHAR_62 = '+';

    /// <summary>標準 base64 の 63 番目の文字。</summary>
    private const char BASE64_CHAR_63 = '/';

    /// <summary>base64url の 62 番目の文字。</summary>
    private const char BASE64URL_CHAR_62 = '-';

    /// <summary>base64url の 63 番目の文字。</summary>
    private const char BASE64URL_CHAR_63 = '_';

    /// <summary>base64 のパディング文字。</summary>
    private const char PADDING_CHAR = '=';

    /// <summary>base64 の 1 ブロックの文字数（この倍数になるまでパディングする）。</summary>
    private const int BASE64_BLOCK_LENGTH = 4;

    /// <summary>
    /// バイト列を base64url（パディングなし）へ変換する。
    /// </summary>
    /// <param name="bytes">変換するバイト列。</param>
    /// <returns>base64url 文字列。</returns>
    public static string Encode(ReadOnlySpan<byte> bytes)
        => Convert.ToBase64String(bytes)
                  .TrimEnd(PADDING_CHAR)
                  .Replace(BASE64_CHAR_62, BASE64URL_CHAR_62)
                  .Replace(BASE64_CHAR_63, BASE64URL_CHAR_63);

    /// <summary>
    /// base64url（パディングの有無を問わない）をバイト列へ戻す。
    /// </summary>
    /// <param name="value">base64url 文字列。</param>
    /// <returns>復元したバイト列。</returns>
    /// <exception cref="FormatException">文字列が base64url として読めないとき。</exception>
    public static byte[] Decode(string? value)
    {
        if (string.IsNullOrEmpty(value)) return Array.Empty<byte>();

        // base64url → 標準 base64 へ戻し、4 の倍数になるまでパディングを補う。
        var standard = value
            .Replace(BASE64URL_CHAR_62, BASE64_CHAR_62)
            .Replace(BASE64URL_CHAR_63, BASE64_CHAR_63);

        var remainder = standard.Length % BASE64_BLOCK_LENGTH;
        if (remainder != 0) standard += new string(PADDING_CHAR, BASE64_BLOCK_LENGTH - remainder);

        return Convert.FromBase64String(standard);
    }

    /// <summary>
    /// base64url として読めるかどうかを、例外を投げずに判定する。
    /// </summary>
    /// <param name="value">判定する文字列。</param>
    /// <param name="bytes">読めた場合のバイト列。読めなければ空。</param>
    /// <returns>読めたら真。</returns>
    public static bool TryDecode(string? value, out byte[] bytes)
    {
        try
        {
            bytes = Decode(value);
            return true;
        }
        catch (FormatException)
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }
}
