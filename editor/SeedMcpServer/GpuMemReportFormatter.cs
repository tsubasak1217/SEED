// ============================================================
//  GpuMemReportFormatter.cs — seed_gpu_mem_report の応答（GPU メモリの内訳の JSON）を要約の表へ整形する
//
//  【役割】
//  エディタの gpu_mem_report コマンドは {"ok":true,"path":"…","report":{内訳の JSON}} を返す。
//  内訳の JSON はランタイムの renderer/gpu_mem/report.rs（GpuMemReport の serde）が正典で、
//  そのままでは長いので、AI が最初に見る「合計・分類ごとの合計・上位 N 件」の表を前に付ける
//  （seed_profile の要約表と同じ流儀）。完全な JSON は必ず後ろに付けて情報を落とさない。
//
//  【単体テスト】
//  editor/tests/AiSafetyTests がこのファイルをリンクして試す（MCP サーバーの Program.cs は
//  トップレベル文なので外から呼べない。整形はここへ分けてある）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace SeedMcpServer;

/// <summary>
/// GPU メモリの内訳（gpu_mem_report の応答）を要約の表＋完全な JSON のテキストにする。状態を持たない。
/// </summary>
internal static class GpuMemReportFormatter
{
    // ── 表示 ─────────────────────────────────────────────────────

    /// <summary>上位の表に出す件数の既定（ランタイムが JSON に入れる件数 TOP_ENTRY_COUNT と同じ）。</summary>
    public const int DEFAULT_TOP = 20;

    /// <summary>上位の表に出す件数の下限。</summary>
    public const int MIN_TOP = 1;

    /// <summary>1 MiB のバイト数（表示の単位。ランタイムのログと同じ MiB）。</summary>
    private const double BYTES_PER_MIB = 1024.0 * 1024.0;

    /// <summary>MiB の表示の書式。</summary>
    private const string MIB_FORMAT = "0.00";

    /// <summary>完全な JSON の前に置く見出し（seed_profile と同じ書き方）。</summary>
    public const string FULL_JSON_HEADER = "── 完全な JSON ──";

    // ── 応答の欄（エディタの gpu_mem_report と、ランタイムの GpuMemReport の serde の名前）──

    /// <summary>エディタの応答: 内訳の JSON。</summary>
    private const string KEY_REPORT = "report";

    /// <summary>エディタの応答: 内訳を書いたファイルのパス。</summary>
    private const string KEY_PATH = "path";

    /// <summary>
    /// エディタの応答を整形する。整形できない形（失敗の応答・スキーマの変更）なら元の文字列をそのまま返す。
    /// </summary>
    /// <param name="editorReply">エディタの gpu_mem_report の応答。</param>
    /// <param name="top">上位の表に出す件数（範囲外は丸める）。</param>
    /// <returns>要約の表＋完全な JSON。整形できなければ元の文字列。</returns>
    public static string Format(string editorReply, int top)
    {
        try
        {
            using var doc = JsonDocument.Parse(editorReply);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(KEY_REPORT, out var report)
                || report.ValueKind != JsonValueKind.Object)
                return editorReply;

            var sb = new StringBuilder();
            AppendTotals(sb, report, root.TryGetProperty(KEY_PATH, out var path) ? path.GetString() : null);
            AppendCategories(sb, report);
            AppendTop(sb, report, Math.Max(MIN_TOP, top));
            sb.Append(FULL_JSON_HEADER).Append('\n').Append(editorReply);
            return sb.ToString();
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException)
        {
            // 失敗の応答（JSON でない ERROR 文）・想定外の形でも、元の情報は必ず返す。
            return editorReply;
        }
    }

    /// <summary>合計の行（理由・文脈〈描画の構成〉・追跡・wgpu-hal・実際の確保・スワップチェイン）を書く。</summary>
    /// <param name="sb">書き先。</param>
    /// <param name="report">内訳の JSON。</param>
    /// <param name="path">内訳を書いたファイルのパス（無ければ null）。</param>
    private static void AppendTotals(StringBuilder sb, JsonElement report, string? path)
    {
        sb.Append("── GPU メモリの内訳 ──\n");
        sb.Append("理由: ").Append(Str(report, "reason")).Append("  フレーム: ").Append(Num(report, "frame").ToString("0", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("文脈: ").Append(Str(report, "context")).Append('\n');
        sb.Append("追跡した資源（生存の推定）: ").Append(Mib(Num(report, "tracked_bytes")))
          .Append("（テクスチャ ").Append(Mib(Num(report, "texture_bytes")))
          .Append(" / バッファ ").Append(Mib(Num(report, "buffer_bytes")))
          .Append("、").Append(Num(report, "resource_count").ToString("0", CultureInfo.InvariantCulture)).Append(" 件）\n");

        // wgpu-hal の計数（取れなければ null）
        if (report.TryGetProperty("hal", out var hal) && hal.ValueKind == JsonValueKind.Object)
        {
            var total = Num(hal, "buffer_bytes") + Num(hal, "texture_bytes") + Num(hal, "acceleration_structure_bytes");
            sb.Append("wgpu-hal の確保ブロック: ").Append(Mib(total))
              .Append("（確保 ").Append(Num(hal, "memory_allocations").ToString("0", CultureInfo.InvariantCulture)).Append(" 回）\n");
        }
        else
        {
            sb.Append("wgpu-hal の確保ブロック: 取れませんでした\n");
        }

        // 実際の確保（Vulkan の VK_EXT_memory_budget。DX12 などでは null）
        sb.Append("実際の確保（ヒープの合計）: ")
          .Append(report.TryGetProperty("heap_usage_bytes", out var heap) && heap.ValueKind == JsonValueKind.Number
              ? Mib(heap.GetDouble())
              : "取れませんでした（Vulkan の VK_EXT_memory_budget が無い・DX12 など）")
          .Append('\n');

        if (report.TryGetProperty("swapchain", out var sc) && sc.ValueKind == JsonValueKind.Object)
        {
            sb.Append("スワップチェイン（見積り・別枠）: ")
              .Append(Num(sc, "width").ToString("0", CultureInfo.InvariantCulture)).Append('x')
              .Append(Num(sc, "height").ToString("0", CultureInfo.InvariantCulture)).Append(' ')
              .Append(Str(sc, "format")).Append(" × ").Append(Num(sc, "image_count").ToString("0", CultureInfo.InvariantCulture))
              .Append(" 枚 = ").Append(Mib(Num(sc, "bytes"))).Append('\n');
        }
        if (!string.IsNullOrEmpty(path)) sb.Append("ファイル: ").Append(path).Append('\n');
        sb.Append('\n');
    }

    /// <summary>分類ごとの合計の表（ランタイムが大きい順に並べた順のまま）を書く。</summary>
    /// <param name="sb">書き先。</param>
    /// <param name="report">内訳の JSON。</param>
    private static void AppendCategories(StringBuilder sb, JsonElement report)
    {
        if (!report.TryGetProperty("categories", out var cats) || cats.ValueKind != JsonValueKind.Array) return;
        sb.Append("── 分類ごとの合計（大きい順）──\n");
        sb.Append("       MiB   件数  分類\n");
        foreach (var c in cats.EnumerateArray())
        {
            sb.Append(Mib(Num(c, "bytes"), withUnit: false).PadLeft(10)).Append(' ')
              .Append(Num(c, "count").ToString("0", CultureInfo.InvariantCulture).PadLeft(6)).Append("  ")
              .Append(Str(c, "name")).Append(" (").Append(Str(c, "key")).Append(")\n");
        }
        sb.Append('\n');
    }

    /// <summary>上位 N 件の表（大きい順）を書く。分類はランタイムの日本語の名前へ引き直す。</summary>
    /// <param name="sb">書き先。</param>
    /// <param name="report">内訳の JSON。</param>
    /// <param name="top">出す件数。</param>
    private static void AppendTop(StringBuilder sb, JsonElement report, int top)
    {
        if (!report.TryGetProperty("top", out var entries) || entries.ValueKind != JsonValueKind.Array) return;

        // 上位の各行の category は列挙子の名前（GBuffer など）。分類の表から日本語の名前を引く。
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (report.TryGetProperty("categories", out var cats) && cats.ValueKind == JsonValueKind.Array)
            foreach (var c in cats.EnumerateArray())
                names[Str(c, "category")] = Str(c, "name");

        var rows = entries.EnumerateArray().Take(top).ToList();
        sb.Append("── 上位 ").Append(rows.Count).Append(" 件（大きい順）──\n");
        sb.Append("       MiB  種類      分類 / ラベル / 詳細 / 作った場所\n");
        foreach (var e in rows)
        {
            var category = Str(e, "category");
            sb.Append(Mib(Num(e, "bytes"), withUnit: false).PadLeft(10)).Append("  ")
              .Append(Str(e, "kind").PadRight(8)).Append("  ")
              .Append(names.TryGetValue(category, out var jp) ? jp : category).Append(" / ")
              .Append(Str(e, "label")).Append(" / ")
              .Append(Str(e, "detail")).Append(" / ")
              .Append(Str(e, "site_file")).Append(':').Append(Num(e, "site_line").ToString("0", CultureInfo.InvariantCulture))
              .Append('\n');
        }
        sb.Append('\n');
    }

    // ── 小道具 ───────────────────────────────────────────────────

    /// <summary>数値の欄を読む（無い・数値でなければ 0）。</summary>
    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0.0;

    /// <summary>文字列の欄を読む（無ければ空文字。文字列以外は JSON の表記のまま）。</summary>
    private static string Str(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Null   => "",
            _                    => v.GetRawText(),
        };
    }

    /// <summary>バイト数を MiB の表記にする。</summary>
    /// <param name="bytes">バイト数。</param>
    /// <param name="withUnit">末尾に " MiB" を付けるか。</param>
    private static string Mib(double bytes, bool withUnit = true)
    {
        var text = (bytes / BYTES_PER_MIB).ToString(MIB_FORMAT, CultureInfo.InvariantCulture);
        return withUnit ? text + " MiB" : text;
    }
}
