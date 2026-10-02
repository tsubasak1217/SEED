namespace SEED.Localization;

// ============================================================
//  ILocaleStore.cs — 選んだ言語の保存先（差し替えの口）
//
//  実行中は SaveDataLocaleStore（SEED.SaveData のキー LocaleCatalog.LanguageSaveKey = "l10n.language"）。
//  テストは辞書の保存先へ差し替えて、保存するキーと値を確かめる。
// ============================================================

/// <summary>選んだ言語の保存先。</summary>
public interface ILocaleStore
{
    /// <summary>保存した文字列を読む。</summary>
    /// <param name="key">キー。</param>
    /// <returns>値（保存していなければ null）。</returns>
    string? GetString(string key);

    /// <summary>文字列を保存する（ディスクへ書き出すかは保存先が決める）。</summary>
    /// <param name="key">キー。</param>
    /// <param name="value">値。</param>
    void SetString(string key, string value);

    /// <summary>保存した値を消す（無ければ何もしない）。</summary>
    /// <param name="key">キー。</param>
    void Delete(string key);
}
