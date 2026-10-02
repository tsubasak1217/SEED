// ============================================================
//  LocaleComment.cs — キーの説明 1 つ（値だけの型）
//
//  言語の表の "_" で始まる鍵（_about・_comment・_source など）は実行中には読まれない説明。
//  文字列表のパネルは、選んだキーを包むオブジェクトの説明を近い順に並べて見せる
//  （集めるのは LocaleJsonDocument.CommentsFor）。
// ============================================================

namespace SEEDEditor.Localization.Json;

/// <summary>キーの説明 1 つ（どこの説明の鍵か・文）。</summary>
/// <param name="Location">説明の鍵の平たい道筋（"ui._about"）。</param>
/// <param name="Text">説明の文。</param>
public readonly record struct LocaleComment(string Location, string Text);
