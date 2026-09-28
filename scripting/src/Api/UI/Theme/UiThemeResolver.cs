using System;
using System.Collections.Generic;

namespace SEED.UI;

// ============================================================
//  UiThemeResolver.cs — テーマの継承（extends）を解いて鎖を作る（W2-9。docs/ui_theme.md §3。純粋な計算）
//
//  葉のテーマから extends をたどって基のテーマを読み、最後は必ず組み込みの既定のテーマ（根）に着く
//  （extends を書かないテーマの基も既定のテーマ＝書いていないトークンは既定の値に落ちる）。
//  次のときは警告してそこで鎖を切り、既定のテーマへつなぐ（例外で止めない）:
//    - 基のファイルが読めない・壊れた JSON
//    - 輪（A → B → A）
//    - 深すぎる（MaxDepth 段）
//  ファイルを読む口は引数（テストは手元の表・実行中は Assets.TryReadText）。
// ============================================================

/// <summary>テーマの継承を解く。</summary>
public static class UiThemeResolver
{
    /// <summary>葉から数えた鎖の最大の段数（組み込みの既定のテーマを除く）。</summary>
    public const int MaxDepth = 16;

    /// <summary>
    /// 葉のテーマから鎖を作る。
    /// </summary>
    /// <param name="leaf">読み込んだテーマ。</param>
    /// <param name="builtIn">組み込みの既定のテーマ（根）。</param>
    /// <param name="readText">パス → JSON（読めなければ null）。</param>
    public static UiThemeDefinition Build(UiThemeSource leaf, UiThemeSource builtIn, Func<string, string?> readText)
    {
        var warnings = new List<string>();
        var leafToRoot = new List<UiThemeSource> { leaf };
        if (leaf.Origin != UiThemePaths.BuiltInOrigin)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { leaf.Origin };
            var current = leaf;
            while (true)
            {
                string? extends = current.Extends;
                if (extends is null || UiThemePaths.IsBuiltIn(extends)) break;
                string path = UiThemePaths.Resolve(extends, current.Origin);
                if (!visited.Add(path))
                {
                    warnings.Add($"{current.Origin}: extends: 基のテーマが輪になっています（{path}）。既定のテーマへつなぎます");
                    break;
                }
                if (leafToRoot.Count >= MaxDepth)
                {
                    warnings.Add($"{current.Origin}: extends: 基のテーマが深すぎます（{MaxDepth} 段まで）。既定のテーマへつなぎます");
                    break;
                }
                string? json = readText(path);
                if (json is null)
                {
                    warnings.Add($"{current.Origin}: extends: 基のテーマを読めません（{path}）。既定のテーマへつなぎます");
                    break;
                }
                var parent = UiThemeSource.Parse(json, path);
                if (!parent.IsValid)
                {
                    warnings.Add($"{path}: 基のテーマの JSON が壊れています（{parent.Error}）。既定のテーマへつなぎます");
                    break;
                }
                leafToRoot.Add(parent);
                current = parent;
            }
            leafToRoot.Add(builtIn);
        }
        leafToRoot.Reverse();
        return new UiThemeDefinition(leafToRoot, warnings);
    }
}
