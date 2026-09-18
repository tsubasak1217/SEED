// ============================================================
//  MergeValidation.cs — 書き戻す前の最終検査
//
//  【なぜ検査が要るのか】
//  マージエディタの結果は **アセットそのもの** を上書きする。壊れたものを
//  書き戻すと、次にランタイムがそのシーンを読めなくなる（起動しない）。
//  Lore に解決を頼む前に、こちらで確実に落とせるものは落とす。
//
//  【検査 1: 印が残っていないこと】
//  印が残ったまま解決を頼むと、Lore は
//  「[Warn] Cannot resolve path with conflict markers still present」を出して
//  **何もしないのに成功（rc=0）を返す**（実機で確認済み）。
//  「解決しました」と言いながら何も変わらない、が最悪なのでここで止める。
//
//  【検査 2: JSON として読めること + 同一オブジェクト内でキーが重複しないこと】
//  SEED のアセットはほとんどが JSON（.scene / .actor / .mat …）。
//  「両方を取り込む」でよくある事故が **キーの重複** で、
//  System.Text.Json の既定は重複を弾かず後勝ちで読んでしまうため、
//  構文解析だけでは見つからない。Utf8JsonReader で自前に数える。
//  （ランタイム側の serde は重複でエラーになるため、放置すると
//    「エディタでは開けるがゲームが落ちる」ファイルができる。）
//
//  【依存】
//  WPF にも LoreVcs にも依存しない（System.Text.Json は BCL なのでテストでも使える）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SEEDEditor.VersionControl.Merge;

/// <summary>
/// 検査の結果（不変）。
/// </summary>
/// <param name="IsValid">書き戻してよいか。</param>
/// <param name="Message">駄目な理由（よいときは空文字）。</param>
public readonly record struct MergeValidationResult(bool IsValid, string Message)
{
    /// <summary>問題なし、を表す結果。</summary>
    public static MergeValidationResult Ok => new(true, string.Empty);

    /// <summary>理由つきの不合格を作る。</summary>
    /// <param name="message">駄目な理由。</param>
    public static MergeValidationResult Invalid(string message) => new(false, message);
}

/// <summary>
/// 書き戻す前の検査。
/// </summary>
public static class MergeValidation
{
    /// <summary>印が 1 つも見つからなかったことを表す行番号。</summary>
    public const int NO_MARKER_LINE = 0;

    /// <summary>
    /// 中身が JSON である拡張子。
    ///
    /// <para>
    /// ★正典は <c>runtime/src/engine/core/migration/kind.rs</c> のアセット形式表
    /// （C# 側の写しは <c>editor/src/Migration/AssetFormat.cs</c>）だが、
    /// **あの表は「形式名 → 版」であって拡張子を持たない**。
    /// 拡張子との対応はここが唯一の置き場になる。形式を足したらここにも足すこと。
    /// </para>
    /// </summary>
    public static readonly IReadOnlyList<string> JSON_EXTENSIONS = new[]
    {
        ".json",        // 各種設定（terrain の layers / props / cover_materials など）
        ".scene",       // シーン
        ".actor",       // アクタ（プレハブ・3D）
        ".actor2d",     // アクタ（プレハブ・2D）
        ".anim",        // アニメーションクリップ
        ".mat",         // マテリアル
        ".postfx",      // ポストエフェクトチェーン
        ".inputmap",    // 入力アクションマップ
        ".sprite_mesh", // 2D スプライトメッシュ
    };

    /// <summary>
    /// このパスの中身は JSON か（拡張子で判断する）。
    /// </summary>
    /// <param name="path">相対でも絶対でもよいパス。</param>
    public static bool IsJson(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;

        var extension = Path.GetExtension(path);
        if (extension.Length == 0) return false;

        foreach (var known in JSON_EXTENSIONS)
        {
            if (string.Equals(extension, known, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 書き戻してよいかを検査する（印の残存 → JSON の構文 → キーの重複 の順）。
    /// </summary>
    /// <param name="path">対象のパス（拡張子だけを見る。相対でも絶対でもよい）。</param>
    /// <param name="text">書き戻そうとしている中身。</param>
    public static MergeValidationResult Validate(string? path, string text)
    {
        var markerLine = FindMarkerLine(text);
        if (markerLine != NO_MARKER_LINE)
        {
            return MergeValidationResult.Invalid(string.Format(
                VersionControlMessages.MERGE_VALIDATE_MARKERS_REMAIN_FORMAT, markerLine));
        }

        return IsJson(path) ? ValidateJson(text) : MergeValidationResult.Ok;
    }

    /// <summary>
    /// 印が残っている最初の行番号を返す（1 始まり）。無ければ
    /// <see cref="NO_MARKER_LINE"/>。
    /// </summary>
    /// <param name="text">調べるテキスト。</param>
    public static int FindMarkerLine(string? text)
    {
        if (string.IsNullOrEmpty(text)) return NO_MARKER_LINE;

        var lines = MergeTextLines.Split(text);
        for (var i = 0; i < lines.Count; i++)
        {
            if (ConflictMarkerDocument.IsAnyMarkerLine(lines[i].Text)) return i + 1;
        }
        return NO_MARKER_LINE;
    }

    /// <summary>
    /// JSON として厳密に読めるか、同じオブジェクトの中でキーが重複していないかを見る。
    /// </summary>
    /// <param name="text">調べるテキスト。</param>
    private static MergeValidationResult ValidateJson(string text)
    {
        var utf8 = Encoding.UTF8.GetBytes(text);

        // 1) 構文。コメントも末尾カンマも許さない（ランタイムの serde と同じ厳しさ）。
        try
        {
            using var document = JsonDocument.Parse(utf8, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling     = JsonCommentHandling.Disallow,
            });
        }
        catch (JsonException ex)
        {
            return MergeValidationResult.Invalid(string.Format(
                VersionControlMessages.MERGE_VALIDATE_JSON_BROKEN_FORMAT, ex.Message));
        }

        // 2) キーの重複。構文解析は通ってしまうのでここで別に数える。
        var duplicate = FindDuplicateKey(utf8);
        return duplicate is null
            ? MergeValidationResult.Ok
            : MergeValidationResult.Invalid(string.Format(
                VersionControlMessages.MERGE_VALIDATE_JSON_DUPLICATE_KEY_FORMAT, duplicate));
    }

    /// <summary>
    /// 同一オブジェクトの中で 2 回現れたキーを 1 つ返す（無ければ null）。
    /// </summary>
    /// <param name="utf8">UTF-8 に符号化した JSON。</param>
    private static string? FindDuplicateKey(byte[] utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling     = JsonCommentHandling.Disallow,
        });

        // 入れ子のオブジェクトごとに「見たキー」を持つ。配列は素通し。
        var scopes = new Stack<HashSet<string>>();

        while (reader.Read())
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    scopes.Push(new HashSet<string>(StringComparer.Ordinal));
                    break;

                case JsonTokenType.EndObject:
                    if (scopes.Count > 0) scopes.Pop();
                    break;

                case JsonTokenType.PropertyName:
                    var name = reader.GetString() ?? string.Empty;
                    if (scopes.Count > 0 && !scopes.Peek().Add(name)) return name;
                    break;
            }
        }

        return null;
    }
}
