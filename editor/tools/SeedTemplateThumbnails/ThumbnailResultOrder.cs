// ============================================================
//  ThumbnailResultOrder.cs — 件ごとの結果をカタログに現れた順へ並べる（純粋な処理）
//
//  【なぜ鍵がライブラリ相対パスか（docs/reviews/2026-10-02_code_review.md #19）】
//  以前はテンプレートのファイル名（拡張子なし）を鍵にした辞書を ToDictionary で作っていたので、
//  templates/actors と templates/ui の両方に button.actor があると、PNG を書き終えた後に ArgumentException で落ち、
//  結果の一覧も終了コードも出なかった。カタログの読み込みはライブラリ相対パスで重複を除く（TemplateActorCatalog）ので、
//  鍵もライブラリ相対パスにすれば重ならない。念のため同じ鍵が 2 度来ても落ちない（最初の位置を採る）作りにしてある。
// ============================================================

using SEEDEditor.Packaging.Collect;

namespace SEEDEditor.Tools.SeedTemplateThumbnails;

/// <summary>結果をカタログに現れた順へ並べる。</summary>
public static class ThumbnailResultOrder
{
    /// <summary>
    /// 結果をカタログに現れた順（撮る対象のエントリの順）へ並べる。カタログに無い鍵の結果は最後（来た順のまま）。
    /// </summary>
    /// <typeparam name="T">結果の型。</typeparam>
    /// <param name="entryRelPaths">撮る対象のエントリのライブラリ相対パス（カタログに現れた順）。</param>
    /// <param name="results">件ごとの結果（撮った順。窓の大きさのまとまりごとなのでカタログの順と違う）。</param>
    /// <param name="relPathOf">結果からライブラリ相対パスを取り出す。</param>
    /// <returns>並べ替えた結果（同じ位置どうしは来た順を保つ）。</returns>
    public static List<T> Sort<T>(IReadOnlyList<string> entryRelPaths, IEnumerable<T> results, Func<T, string> relPathOf)
    {
        // 大文字小文字はライブラリの読み込みと同じ比べ方（Windows に合わせて無視）。同じ鍵は最初の位置を採る（落ちない）
        var order = new Dictionary<string, int>(AssetPathUtil.PathComparer);
        for (int i = 0; i < entryRelPaths.Count; i++) order.TryAdd(entryRelPaths[i], i);
        return results.OrderBy(r => order.TryGetValue(relPathOf(r), out var position) ? position : int.MaxValue).ToList();
    }
}
