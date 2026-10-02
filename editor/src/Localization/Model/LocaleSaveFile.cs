// ============================================================
//  LocaleSaveFile.cs — 保存で書くファイル 1 つ（値だけの型。LocaleTableModel.PendingWrites が作る）
// ============================================================

namespace SEEDEditor.Localization.Model;

/// <summary>保存で書くファイル 1 つ。</summary>
/// <param name="FileName">置き場の中のファイル名（"index.json"・"en.json"）。</param>
/// <param name="Text">書く中身（UTF-8・BOM 無しで書く）。</param>
public sealed record LocaleSaveFile(string FileName, string Text);
