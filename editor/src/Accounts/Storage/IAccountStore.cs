// ============================================================
//  IAccountStore.cs — アカウントの保管の境界
//
//  【役割】
//  「読む・書く・あるか・どこにあるか」だけの細い境界。
//  上の層（AccountService / Hub の画面）は保存形式も DPAPI も知らない。
//
//  【例外の約束】
//  読み書きの失敗は <see cref="AccountStoreException"/> に畳んで投げる。
//  実装の都合の例外（IOException / CryptographicException / JsonException）を
//  そのまま上げると、呼び出し側が catch すべき型を列挙する羽目になる。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using SEEDEditor.Accounts.Model;

namespace SEEDEditor.Accounts.Storage;

/// <summary>
/// アカウントの保管・取り出しの失敗。<see cref="Exception.Message"/> は
/// そのまま利用者へ見せてよい日本語にする（秘密は含めない）。
/// </summary>
public sealed class AccountStoreException : Exception
{
    /// <summary>メッセージを指定して生成する。</summary>
    /// <param name="message">利用者向けの説明。</param>
    public AccountStoreException(string message) : base(message) { }

    /// <summary>メッセージと内側の例外を指定して生成する。</summary>
    /// <param name="message">利用者向けの説明。</param>
    /// <param name="inner">元の例外（ログ用。画面へは出さない）。</param>
    public AccountStoreException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// アカウントの保管。
/// </summary>
public interface IAccountStore
{
    /// <summary>アカウントファイルの絶対パス（画面の案内に使う）。</summary>
    string FilePath { get; }

    /// <summary>アカウントが保存されているか。</summary>
    bool Exists { get; }

    /// <summary>
    /// 保存されているアカウントを読む。
    /// </summary>
    /// <returns>アカウント（未作成なら null）。呼び出し側が破棄する。</returns>
    /// <exception cref="AccountStoreException">壊れている・復号できないとき。</exception>
    SeedAccount? Load();

    /// <summary>
    /// アカウントを保存する（tmp → rename で原子的に書く）。
    /// </summary>
    /// <param name="account">保存するアカウント。</param>
    /// <exception cref="AccountStoreException">書き込めないとき。</exception>
    void Save(SeedAccount account);
}
