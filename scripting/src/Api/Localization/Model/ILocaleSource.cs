namespace SEED.Localization;

// ============================================================
//  ILocaleSource.cs — 多言語のデータファイルの読み込み元（差し替えの口）
//
//  実行中は AssetLocaleSource（SEED.Assets = PAK 同梱・実ファイルのどちらでも読める）。
//  テスト（editor/tests/LocalizationTests）は辞書や一時フォルダの読み込み元へ差し替えて、Assets 無しで LocaleCatalog を検査する。
// ============================================================

/// <summary>多言語のデータファイルの読み込み元。</summary>
public interface ILocaleSource
{
    /// <summary>テキストを読む。</summary>
    /// <param name="path">パス（"assets://locale/ja.json" など）。</param>
    /// <param name="text">本文（読めなければ空）。</param>
    /// <returns>読めたら true。</returns>
    bool TryReadText(string path, out string text);

    /// <summary>
    /// 最終更新の印（値に意味は無く、前と違うかだけを見る。取れなければ 0 ＝ 変わらない扱い）。
    /// </summary>
    /// <param name="path">パス。</param>
    /// <returns>更新の印。</returns>
    long GetModifiedTime(string path);
}
