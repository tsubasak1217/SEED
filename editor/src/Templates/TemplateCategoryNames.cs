// ============================================================
//  TemplateCategoryNames.cs — カテゴリ表示名の対応表（データ）
//
//  【役割】
//  テンプレートライブラリのトップレベルフォルダ名（= カテゴリ）を
//  UI に出す日本語表示名へ変換する、ただ 1 つの対応表。
//
//  【なぜデータ表にするか】
//  カテゴリはライブラリのフォルダ構成そのものなので、フォルダを 1 つ足せば
//  カテゴリが 1 つ増える。表示名だけをここへ 1 行足せば済むようにしておけば、
//  走査・インポートのロジックには一切手を入れずに済む。
//  表に無いフォルダはフォルダ名をそのまま表示するため、
//  「登録し忘れでカテゴリが消える」事故は起こらない。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.Templates;

/// <summary>
/// テンプレートライブラリのカテゴリ（トップレベルフォルダ）の表示名を引く対応表。
/// </summary>
public static class TemplateCategoryNames
{
    /// <summary>
    /// フォルダ名 → 表示名。
    /// <b>この並び順がそのまま UI のカテゴリ表示順になる</b>（Dictionary は挿入順を保つ）。
    /// 使用頻度の高い順に並べてある。
    /// </summary>
    private static readonly Dictionary<string, string> DisplayNameByFolder =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["scenes"]   = "シーン",
            ["actors"]   = "アクター",
            ["models"]   = "モデル",
            ["textures"] = "テクスチャ",
            ["shaders"]  = "シェーダ",
            ["skybox"]   = "スカイボックス",
            ["fonts"]    = "フォント",
            ["terrain"]  = "地形",
            ["input"]    = "入力設定",
            ["scripts"]  = "スクリプト",
        };

    /// <summary>
    /// カテゴリの表示名を返す。表に無いフォルダはフォルダ名をそのまま返す。
    /// </summary>
    /// <param name="folderName">ライブラリ直下のフォルダ名。</param>
    /// <returns>UI に出す表示名。</returns>
    public static string GetDisplayName(string folderName) =>
        DisplayNameByFolder.TryGetValue(folderName, out var name) ? name : folderName;

    /// <summary>
    /// 表に載っている並び順での位置を返す。載っていなければ表の件数（＝末尾）を返す。
    /// カテゴリの表示順を決めるソートキーに使う。
    /// </summary>
    /// <param name="folderName">ライブラリ直下のフォルダ名。</param>
    /// <returns>並び順のインデックス（小さいほど先頭）。</returns>
    public static int GetSortOrder(string folderName)
    {
        int index = 0;
        foreach (var key in DisplayNameByFolder.Keys)
        {
            if (string.Equals(key, folderName, StringComparison.OrdinalIgnoreCase)) return index;
            index++;
        }
        return DisplayNameByFolder.Count;   // 未知のフォルダは既知カテゴリの後ろへ
    }
}
