namespace SEEDEditor.Runtime;

/// <summary>
/// ランタイム（Rust 側）が WGSL シェーディングアセットの検証で返した診断 1 件。
///
/// エディタ UI 層（AvalonEdit のオフセット・波線）に依存しない純粋な受信データであり、
/// 「行番号 → オフセット」の変換や表示ラベルの整形は表示側（ScriptEditorPanel）が行う。
/// これにより IPC 層と表示層の責務を分離している。
/// </summary>
/// <param name="Message">エラーメッセージ本文（ランタイムが生成した文字列）。</param>
/// <param name="Line">
/// アセットソース内の 1 始まり行番号。null は「アセット外で検出された／行が特定できない」ことを表す。
/// </param>
/// <param name="Variant">検出フェーズなどの種別文字列（例 "rt_on"）。ツールチップの識別子に使う。</param>
public sealed record WgslDiagnostic(string Message, int? Line, string Variant);
