using System;
using System.Collections.Generic;

namespace SEED;

/// <summary>
/// <c>[AssetReference]</c> を付けた <c>string</c> フィールドの
/// 「受け付ける拡張子」の正規化と照合をまとめた共通ヘルパー。
/// 属性側（SEEDEditor.Scripting.AssetReferenceAttribute）と
/// エディタ側の行ビルダーが共有する唯一の正典である。
///
/// 【正規化の規約】
/// 拡張子は「小文字・ドット無し」で保持する。利用者は "ttf" と ".TTF" の
/// どちらでも書けるが、内部表現を 1 つに決めておかないと照合が書き手の癖に依存してしまう。
/// ドロップ判定やファイルダイアログのフィルタを組むときは、
/// ToDotted でドット付きへ戻してから使う
/// （System.IO.Path.GetExtension がドット付きを返すため）。
/// </summary>
public static class ScriptAssetReference
{
    /// <summary>
    /// <c>[AssetReference]</c> 属性の型名。
    /// エディタの Roslyn コンパイルとランタイムの ALC コンパイルでは属性の
    /// アセンブリ ID が異なり得るため、型一致ではなくこの名前で照合すること。
    /// </summary>
    public const string AttributeName = "AssetReferenceAttribute";

    /// <summary>拡張子の区切り文字（ドット）。</summary>
    public const char ExtensionSeparator = '.';

    /// <summary>
    /// 拡張子の並びを「小文字・ドット無し・空要素なし・重複なし」へ正規化する。
    /// </summary>
    /// <param name="extensions">利用者が書いた拡張子の並び（null 可）。</param>
    /// <returns>正規化済みの配列（1 件も残らなければ空配列）。</returns>
    public static string[] Normalize(IEnumerable<string?>? extensions)
    {
        if (extensions is null) return Array.Empty<string>();

        var result = new List<string>();
        foreach (var raw in extensions)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            // 前後の空白と先頭のドットを落としてから小文字化する
            var ext = raw!.Trim().TrimStart(ExtensionSeparator).ToLowerInvariant();
            if (ext.Length == 0)      continue;
            if (result.Contains(ext)) continue;

            result.Add(ext);
        }
        return result.ToArray();
    }

    /// <summary>
    /// 正規化済みの拡張子をドット付きへ戻す（"ttf" → ".ttf"）。
    /// ドロップ判定（Path.GetExtension との比較）やダイアログのフィルタ用。
    /// </summary>
    /// <param name="extensions">正規化済みの拡張子（null・空なら空配列を返す）。</param>
    public static string[] ToDotted(IReadOnlyList<string>? extensions)
    {
        if (extensions is null || extensions.Count == 0) return Array.Empty<string>();

        var result = new string[extensions.Count];
        for (int i = 0; i < extensions.Count; i++)
            result[i] = ExtensionSeparator + extensions[i];
        return result;
    }
}
