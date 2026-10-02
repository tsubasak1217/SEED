// ============================================================
//  LocaleRow.cs — 文字列表の行 1 つ（値だけの型。LocaleTableModel.BuildRows が作る）
// ============================================================

using System.Collections.Generic;
using System.Linq;
using SEED.Localization;

namespace SEEDEditor.Localization.Model;

/// <summary>文字列表の行 1 つ（キー 1 つと言語ごとの升目）。</summary>
/// <param name="Key">平たいキー（"menu.start"）。</param>
/// <param name="Kind">行の種類。</param>
/// <param name="GroupKey">複数形のまとまりの名前（複数形にかかわる行だけ。ほかは null）。</param>
/// <param name="Category">複数形の形（<see cref="LocaleRowKind.PluralForm"/> だけ。ほかは null）。</param>
/// <param name="Cells">言語ごとの升目（言語の一覧の順）。</param>
/// <remarks>キーの説明（"_" で始まる鍵）は行ごとに集めると重いので、選んだ行の分だけ LocaleTableModel.CommentsFor で引く。</remarks>
public sealed record LocaleRow(
    string Key,
    LocaleRowKind Kind,
    string? GroupKey,
    PluralCategory? Category,
    IReadOnlyList<LocaleCell> Cells)
{
    /// <summary>未訳の升目の数。</summary>
    public int MissingCount => Cells.Count(c => c.IsMissing);

    /// <summary>未訳の升目があるか。</summary>
    public bool HasMissing => Cells.Any(c => c.IsMissing);

    /// <summary>
    /// 名前の変更・複数形の形の追加の対象にする「論理のキー」（複数形にかかわる行はまとまりの名前、ほかはキー）。
    /// </summary>
    public string LogicalKey => GroupKey ?? Key;
}
