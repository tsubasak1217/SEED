// ============================================================
//  SeedAccount.cs — アカウントのモデル（表に出す身元 / 秘密を持つ実体）
//
//  【役割】
//  「名前 ＋ 公開鍵」という**画面へ出してよい情報**（AccountIdentity）と、
//  「それに秘密鍵を足したもの」（SeedAccount）を型として分ける。
//
//  【なぜ 2 つに分けるのか】
//  画面・ログ・DTO へ渡す値と、署名に使う値を同じ型にすると、
//  うっかり秘密鍵ごと渡す経路ができてしまう。**型で防ぐ**。
//  AccountIdentity は秘密を一切持たないので、どこへ渡しても安全。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using SEEDEditor.Accounts.Crypto;

namespace SEEDEditor.Accounts.Model;

/// <summary>
/// 画面へ出してよいアカウント情報（秘密を含まない）。
/// </summary>
/// <param name="Name">アカウント名（契約 2 章の規則を満たす）。</param>
/// <param name="PublicKeyBase64Url">公開鍵（SEC1 非圧縮点）の base64url。</param>
public readonly record struct AccountIdentity(string Name, string PublicKeyBase64Url)
{
    /// <summary>アカウントが無い状態。</summary>
    public static AccountIdentity None => new(string.Empty, string.Empty);

    /// <summary>実体があるか（名前と公開鍵が揃っているか）。</summary>
    public bool HasValue
        => !string.IsNullOrWhiteSpace(Name) && !string.IsNullOrWhiteSpace(PublicKeyBase64Url);
}

/// <summary>
/// 秘密鍵を持つアカウント。プロセス内で 1 つだけ保持し、終了時に破棄する。
/// </summary>
public sealed class SeedAccount : IDisposable
{
    /// <summary>鍵ペア（秘密鍵を持つ）。</summary>
    private readonly AccountKeyPair _keyPair;

    /// <summary>破棄済みの印。</summary>
    private bool _disposed;

    /// <summary>アカウント名。</summary>
    public string Name { get; }

    /// <summary>公開鍵（SEC1 非圧縮点）の base64url。</summary>
    public string PublicKeyBase64Url => _keyPair.PublicKeyBase64Url;

    /// <summary>画面へ出してよい身元。</summary>
    public AccountIdentity Identity => new(Name, PublicKeyBase64Url);

    /// <summary>
    /// 名前と鍵ペアを指定して生成する。鍵ペアの所有権はこの型が持つ
    /// （<see cref="Dispose"/> で一緒に破棄する）。
    /// </summary>
    /// <param name="name">アカウント名。</param>
    /// <param name="keyPair">鍵ペア。</param>
    public SeedAccount(string name, AccountKeyPair keyPair)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException(AccountMessages.NAME_EMPTY, nameof(name));

        Name     = name;
        _keyPair = keyPair ?? throw new ArgumentNullException(nameof(keyPair));
    }

    /// <summary>
    /// ログインチャレンジへ署名する。
    /// </summary>
    /// <param name="challengeId">サーバが返した challenge_id。</param>
    /// <param name="nonce">サーバが返した nonce。</param>
    /// <returns>署名の base64url。</returns>
    public string SignLoginChallenge(string challengeId, string nonce)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _keyPair.Sign(LoginSignaturePayload.Build(challengeId, nonce));
    }

    /// <summary>
    /// 秘密鍵を PKCS#8 で書き出す（保管・書き出し専用）。
    ///
    /// <para>
    /// ★平文の秘密鍵が返る。呼び出し側は必ず包んでから保存し、
    /// 使い終わったらゼロクリアすること。
    /// </para>
    /// </summary>
    public byte[] ExportPkcs8PrivateKey()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _keyPair.ExportPkcs8PrivateKey();
    }

    /// <summary>秘密鍵を解放する。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _keyPair.Dispose();
    }
}
