// ============================================================
//  LocaleCell.cs — 文字列表の升目 1 つ（値だけの型。LocaleTableModel.BuildRows が作る）
// ============================================================

namespace SEEDEditor.Localization.Model;

/// <summary>文字列表の升目 1 つ（あるキーのある言語の文）。</summary>
/// <param name="Code">言語のコード。</param>
/// <param name="Text">文（null・欠けは null。数・真偽値は書いたままの字句）。</param>
/// <param name="State">状態（未訳か・要らないか）。</param>
/// <param name="Note">状態の説明（升目のツールチップに出す。文があれば空）。</param>
public readonly record struct LocaleCell(string Code, string? Text, LocaleCellState State, string Note)
{
    /// <summary>未訳に数えるか。</summary>
    public bool IsMissing => State.IsMissing();
}
