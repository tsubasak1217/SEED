namespace SEED.Localization;

// ============================================================
//  AssetLocaleSource.cs — 多言語のデータファイルを SEED.Assets から読む読み込み元（実行中の既定）
//
//  SEED.Assets（docs/scripting_api.md §7.75）は PAK 同梱・実ファイルのどちらでも同じ assets:// のパスで読める。
//  更新の印は Assets.GetModifiedTime（UNIX 秒。PAK 同梱・取れないときは 0 ＝ PollChanges は読み直さない）。
// ============================================================

/// <summary>SEED.Assets から読む読み込み元。</summary>
internal sealed class AssetLocaleSource : ILocaleSource
{
    /// <inheritdoc />
    public bool TryReadText(string path, out string text) => Assets.TryReadText(path, out text);

    /// <inheritdoc />
    public long GetModifiedTime(string path) => Assets.GetModifiedTime(path);
}
