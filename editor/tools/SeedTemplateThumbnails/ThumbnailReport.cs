// ============================================================
//  ThumbnailReport.cs — 1 件ごとの結果と、最後に出す一覧
// ============================================================

using System.Globalization;
using SEEDEditor.Tools.SeedTemplateThumbnails.Imaging;

namespace SEEDEditor.Tools.SeedTemplateThumbnails;

/// <summary>1 件の結果の種類。</summary>
public enum ThumbnailOutcome
{
    /// <summary>見本の画像を書いた。</summary>
    Written,
    /// <summary>カタログの指示で撮らなかった（thumbnail_sample.skip）。</summary>
    Skipped,
    /// <summary>撮れなかった（理由つき）。</summary>
    Failed,
}

/// <summary>1 件の結果。</summary>
/// <param name="Name">テンプレートのファイル名（拡張子なし）。</param>
/// <param name="DisplayName">カタログの表示名。</param>
/// <param name="Outcome">結果の種類。</param>
/// <param name="Detail">飛ばした・撮れなかった理由（書けたときは空）。</param>
/// <param name="OutputPath">書いた画像の絶対パス（書けなければ null）。</param>
/// <param name="Bytes">書いた画像のバイト数。</param>
/// <param name="Size">書いた画像の一辺（画素）。</param>
/// <param name="Stats">書いた画像の中身の量（書けなければ null）。</param>
/// <param name="Warnings">書けたが気を付けること（見つからない参照・ログの誤り・中身が少ない等）。</param>
/// <param name="Elapsed">この件にかかった時間。</param>
public sealed record ThumbnailResult(
    string Name,
    string DisplayName,
    ThumbnailOutcome Outcome,
    string Detail,
    string? OutputPath,
    long Bytes,
    int Size,
    ThumbnailStats? Stats,
    IReadOnlyList<string> Warnings,
    TimeSpan Elapsed);

/// <summary>結果の一覧の書き出し。</summary>
public static class ThumbnailReport
{
    /// <summary>名前の列の幅（文字）。</summary>
    private const int NameColumnWidth = 22;

    /// <summary>割合を百分率にする倍率。</summary>
    private const double Percent = 100.0;

    /// <summary>
    /// 結果の一覧を出す。
    /// </summary>
    /// <param name="results">件ごとの結果。</param>
    /// <param name="total">全体の時間。</param>
    /// <param name="write">出力先。</param>
    public static void Print(IReadOnlyList<ThumbnailResult> results, TimeSpan total, Action<string> write)
    {
        int written = results.Count(r => r.Outcome == ThumbnailOutcome.Written);
        int skipped = results.Count(r => r.Outcome == ThumbnailOutcome.Skipped);
        int failed = results.Count(r => r.Outcome == ThumbnailOutcome.Failed);
        int warned = results.Count(r => r.Outcome == ThumbnailOutcome.Written && r.Warnings.Count > 0);

        write("");
        write("==== 見本の画像の生成の結果 ====");
        write($"書けた {written} 件（うち注意あり {warned} 件）/ 飛ばした {skipped} 件 / 撮れなかった {failed} 件 / 所要 {total.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} 秒");
        foreach (var r in results)
        {
            string name = r.Name.PadRight(NameColumnWidth);
            switch (r.Outcome)
            {
                case ThumbnailOutcome.Written:
                    string mark = r.Warnings.Count > 0 ? "[WARN]" : "[ OK ]";
                    string content = r.Stats is { } s
                        ? $"中身 {(s.ContentRatio * Percent).ToString("F1", CultureInfo.InvariantCulture)}%{(s.TouchesEdge ? "・縁まで" : "")}"
                        : "";
                    write($"  {mark} {name} {r.Size}x{r.Size}  {r.Bytes.ToString("N0", CultureInfo.InvariantCulture),7} B  {content}  " +
                          $"({r.Elapsed.TotalSeconds.ToString("F1", CultureInfo.InvariantCulture)} 秒)  {r.OutputPath}");
                    foreach (var w in r.Warnings) write($"         ! {w}");
                    break;
                case ThumbnailOutcome.Skipped:
                    write($"  [SKIP] {name} {r.Detail}");
                    break;
                default:
                    write($"  [FAIL] {name} {r.Detail}");
                    foreach (var w in r.Warnings) write($"         ! {w}");
                    break;
            }
        }
    }
}
