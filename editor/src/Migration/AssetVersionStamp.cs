// ============================================================
//  AssetVersionStamp.cs — 保存時に版をトップレベルの先頭へ刻む
//
//  【役割】
//  「版の欄を、既に有っても無くても、必ずトップレベルの**先頭**に置く」だけを担当する。
//  先頭に置くのは、差分を見たときに版がすぐ分かるようにするため
//  （docs/asset_migration.md 6.5 (1)）。ランタイムの一括アップグレードも
//  同じ位置へ差し込むので、位置を合わせておくと不要な差分が出ない。
//
//  【値をハードコードしないこと】
//  刻む値は必ず <see cref="AssetFormat.CurrentVersion"/> から取る。
//  版を上げたときに直す場所を 1 か所（AssetFormat.cs の表）に保つため。
//
//  【JsonNode を使う書き手向け】
//  <see cref="System.Text.Json.Nodes.JsonObject"/> は挿入順を保つが、
//  先頭へ差し込む API が無いので、版 → 既存の欄の順で**組み直す**。
//  Utf8JsonWriter で手書きする書き手（AnimClipIO）は、
//  最初に版の欄を書けばよいのでこのクラスを使わない。
//
//  【依存】
//  WPF に依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Text.Json.Nodes;

namespace SEEDEditor.Migration;

/// <summary>
/// 版の刻印（状態を持たない静的クラス）。
/// </summary>
public static class AssetVersionStamp
{
    /// <summary>
    /// 版の欄を先頭に置いた**新しい** JSON オブジェクトを作る。
    ///
    /// <para>
    /// 元のオブジェクトは変更しない（同じドキュメントを 2 回直列化しても
    /// 結果が変わらないようにするため）。既に版の欄があれば現行版で置き換える。
    /// </para>
    /// </summary>
    /// <param name="root">元のルートオブジェクト。</param>
    /// <param name="format">対象の形式（欄名と現行版の出所）。</param>
    /// <returns>版の欄が先頭に入った新しいオブジェクト。</returns>
    public static JsonObject WithVersionFirst(JsonObject root, AssetFormat format)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(format);

        var key     = format.VersionKeyName;
        var stamped = new JsonObject
        {
            // 先に入れることで、挿入順＝出力順の先頭になる。
            [key] = JsonValue.Create(format.CurrentVersion),
        };

        foreach (var pair in root)
        {
            // 元にあった版の欄は落とす（先頭に入れ直した方が正）。
            if (string.Equals(pair.Key, key, StringComparison.Ordinal)) continue;

            // JsonNode は 1 つの親にしか属せないため、複製して詰める。
            stamped[pair.Key] = pair.Value?.DeepClone();
        }
        return stamped;
    }
}
