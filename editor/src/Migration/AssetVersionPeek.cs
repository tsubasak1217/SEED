// ============================================================
//  AssetVersionPeek.cs — 版の欄だけを安く覗き見る
//
//  【役割】
//  「このファイルは現行版か、古いか、未来版か」だけを、本体を組み立てずに判定する。
//  現行版（＝ほぼ全部のファイル）は変換が要らないので、そこで SEED.exe を
//  起動しないことがこの層の存在理由である。
//
//  【安さの担保】
//  <see cref="System.Text.Json.Utf8JsonReader"/> でトップレベルの欄だけを走査し、
//  入れ子の値は <c>TrySkip</c> で読み飛ばす。JsonDocument を作ると
//  ファイル全体がオブジェクト木になるため、ここでは使わない。
//
//  【ランタイムと同じ解釈にすること】
//  runtime/src/engine/core/migration/runner.rs の <c>interpret_version</c> と
//  同じ規則で解釈する:
//    ・欄が無い            → 1 版（暗黙の既定）
//    ・非負整数            → その値
//    ・それ以外（小数・文字列・null・負数）→ 形式違い（エラー）
//    ・トップレベルがオブジェクトでない → 形式違い（エラー）
//
//  【依存】
//  WPF に依存しない（単体テストへそのままリンクできる）。
// ============================================================

using System;
using System.Text;
using System.Text.Json;

namespace SEEDEditor.Migration;

// ── 結果 ─────────────────────────────────────────────────────────

/// <summary>版の覗き見の結末。</summary>
public enum AssetVersionPeekStatus
{
    /// <summary>版を読み取れた（欄が無い場合の暗黙の 1 版を含む）。</summary>
    Ok,

    /// <summary>JSON として読めなかった（壊れている）。</summary>
    NotJson,

    /// <summary>トップレベルがオブジェクトではない（配列・数値など）。</summary>
    NotObject,

    /// <summary>版の欄はあるが、非負整数ではない。</summary>
    InvalidVersion,
}

/// <summary>
/// 版の覗き見の結果（不変）。
/// </summary>
/// <param name="Status">結末。</param>
/// <param name="Version">
/// 読み取れた版（<see cref="AssetVersionPeekStatus.Ok"/> のときだけ意味を持つ）。
/// </param>
/// <param name="HasVersionField">
/// 版の欄が**物理的に**書かれていたか。
/// 「欄が無い＝1 版」という暗黙の規約と、「欄に 1 と書いてある」を区別するために持つ。
/// </param>
/// <param name="Detail">失敗したときの補足（ログ用）。成功時は空。</param>
public readonly record struct AssetVersionPeekResult(
    AssetVersionPeekStatus Status,
    int Version,
    bool HasVersionField,
    string Detail)
{
    /// <summary>版を読み取れたか。</summary>
    public bool IsOk => Status == AssetVersionPeekStatus.Ok;
}

// ── 覗き見 ───────────────────────────────────────────────────────

/// <summary>
/// JSON テキストから版の欄だけを読み取る（状態を持たない静的クラス）。
/// </summary>
public static class AssetVersionPeek
{
    /// <summary>JSON の先頭に付きうる BOM（エディタや外部ツールが付けることがある）。</summary>
    private const char Bom = '﻿';

    /// <summary>トップレベルの欄が居る深さ（ルートオブジェクトの直下）。</summary>
    private const int TopLevelDepth = 1;

    /// <summary>
    /// 版の欄だけを読み取る。
    /// </summary>
    /// <param name="json">対象の JSON テキスト（先頭 BOM は許容する）。</param>
    /// <param name="format">対象の形式（版の欄名を決める）。</param>
    /// <returns>覗き見の結果。</returns>
    public static AssetVersionPeekResult Read(string? json, AssetFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);

        if (string.IsNullOrWhiteSpace(json))
            return Failure(AssetVersionPeekStatus.NotJson, "内容が空です");

        var text  = json.TrimStart(Bom);
        var bytes = Encoding.UTF8.GetBytes(text);

        try
        {
            return ReadFromUtf8(bytes, format);
        }
        catch (JsonException e)
        {
            return Failure(AssetVersionPeekStatus.NotJson, e.Message);
        }
    }

    /// <summary>
    /// UTF-8 バイト列から版の欄を探す本体。
    /// </summary>
    /// <param name="utf8">BOM を除いた JSON の UTF-8 バイト列。</param>
    /// <param name="format">対象の形式。</param>
    private static AssetVersionPeekResult ReadFromUtf8(byte[] utf8, AssetFormat format)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            // 版の欄より前にコメントや末尾カンマがあっても覗き見を諦めない。
            // （厳密な検証は Rust 側の変換段が行うので、ここでは寛容でよい）
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        });

        // ── 先頭がオブジェクトであること ──
        if (!reader.Read())
            return Failure(AssetVersionPeekStatus.NotJson, "トークンがありません");
        if (reader.TokenType != JsonTokenType.StartObject)
            return Failure(AssetVersionPeekStatus.NotObject, $"先頭が {reader.TokenType} です");

        var versionKey = format.VersionKeyName;

        // ── トップレベルの欄を順に見る ──
        while (reader.Read())
        {
            // ルートオブジェクトが閉じた＝版の欄は無かった。
            if (reader.TokenType == JsonTokenType.EndObject && reader.CurrentDepth == 0) break;

            // 入れ子の中の同名キーに反応しないよう、深さ 1 の欄名だけを見る。
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != TopLevelDepth)
                continue;

            var isVersionField = reader.ValueTextEquals(versionKey);

            // 値へ進む。
            if (!reader.Read())
                return Failure(AssetVersionPeekStatus.NotJson, "欄の値がありません");

            if (!isVersionField)
            {
                // 対象外の欄。オブジェクト・配列なら丸ごと読み飛ばす（ここが「安さ」の要）。
                if (!reader.TrySkip())
                    return Failure(AssetVersionPeekStatus.NotJson, "値を読み飛ばせませんでした");
                continue;
            }

            // 版の欄を見つけた。非負整数だけを版として認める。
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var version) || version < 0)
            {
                return Failure(
                    AssetVersionPeekStatus.InvalidVersion,
                    $"{versionKey} が非負整数ではありません");
            }
            return new AssetVersionPeekResult(
                AssetVersionPeekStatus.Ok, version, HasVersionField: true, Detail: string.Empty);
        }

        // 欄が無いファイルは 1 版とみなす（docs/asset_migration.md 1 章）。
        return new AssetVersionPeekResult(
            AssetVersionPeekStatus.Ok,
            AssetFormats.IMPLICIT_FIRST_VERSION,
            HasVersionField: false,
            Detail: string.Empty);
    }

    /// <summary>失敗の結果を作る（版は 0 で意味を持たない）。</summary>
    /// <param name="status">失敗の種類。</param>
    /// <param name="detail">補足。</param>
    private static AssetVersionPeekResult Failure(AssetVersionPeekStatus status, string detail)
        => new(status, 0, HasVersionField: false, Detail: detail);
}
