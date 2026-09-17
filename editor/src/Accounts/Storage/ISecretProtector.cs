// ============================================================
//  ISecretProtector.cs — 秘密鍵を「この PC・この利用者だけ」に縛る境界
//
//  【役割】
//  PKCS#8 の秘密鍵をディスクへ置く前に包む／読むときに解く、という
//  2 つの操作だけの細い境界。既定の実装は Windows の DPAPI（CurrentUser）。
//
//  【なぜ境界にするのか】
//  ・保管層（AccountFileStore）の「tmp → rename」「JSON の読み書き」といった
//    ロジックを、暗号の実装に触れずにテストできる。
//  ・将来 Windows 以外や、別の保護方式（TPM・OS キーチェーン）へ移すときに
//    この 1 ファイルだけを足せば済む。
//
//  【守ること】
//  実装は「包めなかった」「解けなかった」を **例外で** 伝える。
//  黙って平文を返すような実装を足してはいけない。
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（単体テストへそのままリンクできる）。
// ============================================================

namespace SEEDEditor.Accounts.Storage;

/// <summary>
/// 秘密をこの PC・この利用者に縛って包む／解く。
/// </summary>
public interface ISecretProtector
{
    /// <summary>
    /// 平文を包む。
    /// </summary>
    /// <param name="plaintext">包む対象（PKCS#8 の秘密鍵）。</param>
    /// <returns>包んだバイト列（そのままディスクへ置いてよい）。</returns>
    byte[] Protect(byte[] plaintext);

    /// <summary>
    /// 包まれたバイト列を解く。
    /// </summary>
    /// <param name="protectedBytes">包まれたバイト列。</param>
    /// <returns>平文。</returns>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// 別の PC・別の利用者で包まれていた場合など、解けないとき。
    /// </exception>
    byte[] Unprotect(byte[] protectedBytes);
}
