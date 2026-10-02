namespace SEED.Localization;

// ============================================================
//  SaveDataLocaleStore.cs — 選んだ言語を SEED.SaveData へ保存する保存先（実行中の既定）
//
//  言語を選び直すのは利用者の操作（めったに起きない）なので、書いたらすぐ SaveData.Save() でディスクへ書き出す
//  （Android は背面のアプリを予告なく終わらせることがあるため）。SaveData.Batch の中なら Batch の終わりに書かれる。
//  LocaleCatalog は同じ値を書き直さない（起動のたびに書き出さない）。
// ============================================================

/// <summary>SEED.SaveData へ保存する保存先。</summary>
internal sealed class SaveDataLocaleStore : ILocaleStore
{
    /// <summary>保存していないときの読み取りの既定値（Has で先に確かめるので使われない）。</summary>
    private const string Unset = "";

    /// <inheritdoc />
    public string? GetString(string key) => SaveData.Has(key) ? SaveData.GetString(key, Unset) : null;

    /// <inheritdoc />
    public void SetString(string key, string value)
    {
        SaveData.SetString(key, value);
        SaveData.Save();
    }

    /// <inheritdoc />
    public void Delete(string key)
    {
        if (!SaveData.DeleteKey(key)) return;
        SaveData.Save();
    }
}
