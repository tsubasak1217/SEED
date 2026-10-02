// ============================================================
//  ScreenPreviewIpc.cs — 画面プレビューの命令の組み立てと応答の解釈（エディタ ⇔ ランタイム）
//
//  【ワイヤ形式】（正典は docs/editor_screen_preview.md §8 と runtime/src/engine/core/app_base/ipc.rs）
//  エディタ → ランタイム:
//    PREVIEW_PREFAB:{world_line},{parent_dfs},{json}
//        json = {"prefab":"assets://..","under":"Screens","frame":"assets://..","frame_body":"Body","layer_bias":10000}
//        prefab だけ必須。frame は枠なしなら出さない、layer_bias は 0 なら出さない。
//        json はパスにカンマを含みうるので**必ず最後**に置く（ランタイムは先頭から 2 つだけ区切る）。
//    PREVIEW_CLEAR:{world_line},{dfs}        … dfs を含むプレビューを 1 つ消す（根でも中でもよい）
//    PREVIEW_CLEAR_ALL:{world_line}          … その世界線のプレビューを全部消す
//    PREVIEW_REFRESH_PATH:{path}             … 中身か枠がそのプレハブのプレビューを作り直す（絶対 or assets://）
//  ランタイム → エディタ:
//    HIERARCHY_QUIET                         … 直後の HIERARCHY 1 通は未保存の印を付けない（単独の行）
//    PREVIEW_ADDED:{world_line},{root_dfs}
//    PREVIEW_CLEARED:{count}
//    PREVIEW_REFRESHED:{count},{path}        … path は送ったまま（カンマを含みうるので最初のカンマで区切る）
//    PREVIEW_ERROR:{message}
//
//  【WPF 非依存】
//  単体テスト（editor/tests/ScreenPreviewTests）がリンクして試すので WPF 型を使わない。状態も持たない。
// ============================================================

using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SEEDEditor.Preview;

/// <summary>
/// 画面プレビューの IPC の組み立てと解釈。状態を持たない。
/// </summary>
public static class ScreenPreviewIpc
{
    // ── 命令の頭（エディタ → ランタイム。ランタイムの ipc.rs と一致させる）──────

    /// <summary>プレビューを作る命令の頭。</summary>
    public const string PreviewPrefabPrefix = "PREVIEW_PREFAB:";

    /// <summary>プレビューを 1 つ消す命令の頭。</summary>
    public const string ClearPrefix = "PREVIEW_CLEAR:";

    /// <summary>世界線のプレビューを全部消す命令の頭。</summary>
    public const string ClearAllPrefix = "PREVIEW_CLEAR_ALL:";

    /// <summary>プレハブのパスでプレビューを作り直す命令の頭。</summary>
    public const string RefreshPathPrefix = "PREVIEW_REFRESH_PATH:";

    // ── 応答の頭（ランタイム → エディタ。ランタイムの editor_preview/wire.rs と一致させる）──

    /// <summary>直後の HIERARCHY 1 通は未保存の印を付けない、という知らせ（単独の行）。</summary>
    public const string HierarchyQuiet = "HIERARCHY_QUIET";

    /// <summary>プレビューを作った応答の頭。</summary>
    public const string AddedPrefix = "PREVIEW_ADDED:";

    /// <summary>プレビューを消した応答の頭。</summary>
    public const string ClearedPrefix = "PREVIEW_CLEARED:";

    /// <summary>プレビューを作り直した応答の頭。</summary>
    public const string RefreshedPrefix = "PREVIEW_REFRESHED:";

    /// <summary>失敗・拒否の応答の頭。</summary>
    public const string ErrorPrefix = "PREVIEW_ERROR:";

    // ── json の欄名（ランタイムの PreviewPrefabRequest と一致させる）──────────

    /// <summary>中身のプレハブの欄。</summary>
    public const string JsonPrefab = "prefab";

    /// <summary>差し込む子のパスの欄。</summary>
    public const string JsonUnder = "under";

    /// <summary>枠のプレハブの欄。</summary>
    public const string JsonFrame = "frame";

    /// <summary>枠の中の差し込み先の欄。</summary>
    public const string JsonFrameBody = "frame_body";

    /// <summary>根のレイヤーの底上げの欄。</summary>
    public const string JsonLayerBias = "layer_bias";

    // ── 書式 ────────────────────────────────────────────────

    /// <summary>欄の区切り。</summary>
    private const char FieldSeparator = ',';

    /// <summary>1 行の命令に載せられない文字（IPC は 1 行 = 1 通）。</summary>
    private static readonly char[] LineBreaks = ['\r', '\n'];

    // ============================================================
    //  組み立て（エディタ → ランタイム）
    // ============================================================

    /// <summary>
    /// プレビューを作る命令を組み立てる。
    /// </summary>
    /// <param name="worldLine">差し込み先の世界線。</param>
    /// <param name="parentDfs">親の DFS 番号（0 以上）。</param>
    /// <param name="request">命令の中身。</param>
    /// <returns>ランタイムへ送る 1 行。</returns>
    /// <exception cref="ArgumentException">中身のプレハブが空、または親の番号が負のとき。</exception>
    public static string BuildPreviewPrefab(uint worldLine, int parentDfs, ScreenPreviewRequest request)
    {
        if (parentDfs < 0)
            throw new ArgumentException("親の DFS 番号は 0 以上でなければなりません", nameof(parentDfs));
        if (string.IsNullOrWhiteSpace(request.Prefab))
            throw new ArgumentException("中身のプレハブが空です", nameof(request));

        return PreviewPrefabPrefix
             + Number(worldLine) + FieldSeparator
             + Number(parentDfs) + FieldSeparator
             + BuildRequestJson(request);
    }

    /// <summary>
    /// 命令の中身を json にする（System.Text.Json。パスの '\'・'"'・日本語は JSON として正しくエスケープされ、
    /// 行は ASCII だけになる。ランタイムの serde_json は \uXXXX をそのまま読む）。
    /// </summary>
    /// <param name="request">命令の中身。</param>
    /// <returns>1 行の json。</returns>
    public static string BuildRequestJson(ScreenPreviewRequest request)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString(JsonPrefab, request.Prefab.Trim());
            writer.WriteString(JsonUnder, request.Under ?? "");
            // 枠なし（null・空白だけ）は欄ごと出さない（ランタイムは「無し = 枠なし」）
            if (!string.IsNullOrWhiteSpace(request.Frame))
                writer.WriteString(JsonFrame, request.Frame.Trim());
            writer.WriteString(JsonFrameBody, request.FrameBody ?? "");
            // 底上げ 0 は欄ごと出さない（ランタイムは「無し = 付けない」）
            if (request.LayerBias != 0)
                writer.WriteNumber(JsonLayerBias, request.LayerBias);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// dfs を含むプレビューを 1 つ消す命令を組み立てる。
    /// </summary>
    /// <param name="worldLine">世界線。</param>
    /// <param name="dfs">プレビューの根、または中のノードの DFS 番号（0 以上）。</param>
    /// <returns>ランタイムへ送る 1 行。</returns>
    /// <exception cref="ArgumentException">番号が負のとき。</exception>
    public static string BuildClear(uint worldLine, int dfs)
    {
        if (dfs < 0) throw new ArgumentException("DFS 番号は 0 以上でなければなりません", nameof(dfs));
        return ClearPrefix + Number(worldLine) + FieldSeparator + Number(dfs);
    }

    /// <summary>
    /// 世界線のプレビューを全部消す命令を組み立てる。
    /// </summary>
    /// <param name="worldLine">世界線。</param>
    /// <returns>ランタイムへ送る 1 行。</returns>
    public static string BuildClearAll(uint worldLine) => ClearAllPrefix + Number(worldLine);

    /// <summary>
    /// プレハブのパスでプレビューを作り直す命令を組み立てる。
    /// </summary>
    /// <param name="path">保存したプレハブ（絶対パス or assets://）。</param>
    /// <returns>ランタイムへ送る 1 行。</returns>
    /// <exception cref="ArgumentException">パスが空、または改行を含む（1 行の命令に載らない）とき。</exception>
    public static string BuildRefreshPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(LineBreaks) >= 0)
            throw new ArgumentException("パスが命令に載せられない形です", nameof(path));
        return RefreshPathPrefix + path;
    }

    // ============================================================
    //  解釈（ランタイム → エディタ）
    // ============================================================

    /// <summary>「直後の HIERARCHY 1 通は未保存の印を付けない」知らせか。</summary>
    /// <param name="message">受け取った 1 行。</param>
    /// <returns>知らせなら true。</returns>
    public static bool IsHierarchyQuiet(string message) =>
        string.Equals(message, HierarchyQuiet, StringComparison.Ordinal);

    /// <summary>
    /// PREVIEW_ADDED:{world_line},{root_dfs} を読む。
    /// </summary>
    /// <param name="message">受け取った 1 行。</param>
    /// <param name="worldLine">世界線。</param>
    /// <param name="rootDfs">作った根の DFS 番号。</param>
    /// <returns>この応答として読めたら true。</returns>
    public static bool TryParseAdded(string message, out uint worldLine, out int rootDfs)
    {
        worldLine = 0;
        rootDfs   = 0;
        if (!message.StartsWith(AddedPrefix, StringComparison.Ordinal)) return false;

        var parts = message[AddedPrefix.Length..].Split(FieldSeparator);
        return parts.Length == 2
            && uint.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out worldLine)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out rootDfs);
    }

    /// <summary>
    /// PREVIEW_CLEARED:{count} を読む。
    /// </summary>
    /// <param name="message">受け取った 1 行。</param>
    /// <param name="count">消したプレビューの数（0 もある）。</param>
    /// <returns>この応答として読めたら true。</returns>
    public static bool TryParseCleared(string message, out int count)
    {
        count = 0;
        return message.StartsWith(ClearedPrefix, StringComparison.Ordinal)
            && int.TryParse(message[ClearedPrefix.Length..], NumberStyles.None, CultureInfo.InvariantCulture, out count);
    }

    /// <summary>
    /// PREVIEW_REFRESHED:{count},{path} を読む（path はカンマを含みうるので最初のカンマで区切る）。
    /// </summary>
    /// <param name="message">受け取った 1 行。</param>
    /// <param name="count">作り直した根の数（0 もある）。</param>
    /// <param name="path">送ったパス（そのまま返ってくる）。</param>
    /// <returns>この応答として読めたら true。</returns>
    public static bool TryParseRefreshed(string message, out int count, out string path)
    {
        count = 0;
        path  = "";
        if (!message.StartsWith(RefreshedPrefix, StringComparison.Ordinal)) return false;

        var payload = message[RefreshedPrefix.Length..];
        var comma   = payload.IndexOf(FieldSeparator);
        if (comma < 0) return false;
        if (!int.TryParse(payload[..comma], NumberStyles.None, CultureInfo.InvariantCulture, out count)) return false;
        path = payload[(comma + 1)..];
        return true;
    }

    /// <summary>
    /// PREVIEW_ERROR:{message} を読む。
    /// </summary>
    /// <param name="message">受け取った 1 行。</param>
    /// <param name="reason">失敗・拒否の理由（利用者向けの文）。</param>
    /// <returns>この応答として読めたら true。</returns>
    public static bool TryParseError(string message, out string reason)
    {
        reason = "";
        if (!message.StartsWith(ErrorPrefix, StringComparison.Ordinal)) return false;
        reason = message[ErrorPrefix.Length..];
        return true;
    }

    // ============================================================
    //  小道具
    // ============================================================

    /// <summary>数を命令の書式（カルチャに依らない 10 進）にする。</summary>
    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
