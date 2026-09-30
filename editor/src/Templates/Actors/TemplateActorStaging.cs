// ============================================================
//  TemplateActorStaging.cs — ランタイムへ渡す「まっさらな木」の一時ファイル
//
//  【役割】
//  まっさらにしたテンプレートの JSON を、ランタイムが読める一時ファイルへ書き出す。
//  ランタイムは ADD_TEMPLATE_ACTOR:{…},{このパス} を受けて、.actor の唯一の読み込み口
//  （runtime の core::app_base::actor_file）から読む。版の変換もそこで行われる。
//
//  【なぜプロジェクトの外（OS の一時フォルダ）なのか】
//  プロジェクトへ .actor を作らない約束（利用者の決定。docs/template_library.md §9）を守るため。
//  cache/ のようなプロジェクト内の隠し場所にも置かない。
//
//  【掃除】
//  ランタイムは受け取った次のフレームで読むので、ファイルはすぐ不要になる。
//  ただしエディタは「読み終えた」合図を受け取らないため、その場では消さず、
//  次に書き出すときに StaleAge より古いものをまとめて消す。
// ============================================================

using System;
using System.IO;
using System.Text;

namespace SEEDEditor.Templates.Actors;

/// <summary>
/// まっさらな木の一時ファイルの書き出しと掃除。状態を持たない静的ユーティリティ。
/// </summary>
public static class TemplateActorStaging
{
    /// <summary>OS の一時フォルダの下に作るフォルダ名。</summary>
    public const string StagingFolderName = "seed_template_actors";

    /// <summary>これより古い一時ファイルは次の書き出しのときに消す（ランタイムが読むのは直後の 1 フレーム）。</summary>
    public static readonly TimeSpan StaleAge = TimeSpan.FromHours(1);

    /// <summary>書き出しの文字コード（BOM なしの UTF-8。ランタイムは BOM ありでも読めるが付けない）。</summary>
    private static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    /// <summary>既定の一時フォルダ（%TEMP%\seed_template_actors）。</summary>
    public static string DefaultDirectory => Path.Combine(Path.GetTempPath(), StagingFolderName);

    /// <summary>
    /// まっさらな木の JSON を一時ファイルへ書き出す。
    /// </summary>
    /// <param name="json">まっさらにした木の JSON テキスト。</param>
    /// <param name="templateRelPath">
    /// 元のテンプレートの相対パス（拡張子 .actor / .actor2d をそろえるためだけに使う）。
    /// </param>
    /// <param name="directory">書き出し先のフォルダ（省略時は <see cref="DefaultDirectory"/>。テストで差し替える）。</param>
    /// <returns>書き出したファイルの絶対パス（毎回ちがう名前）。</returns>
    public static string Stage(string json, string templateRelPath, string? directory = null)
    {
        var dir = directory ?? DefaultDirectory;
        Directory.CreateDirectory(dir);
        CleanupStale(dir, DateTime.UtcNow);

        var extension = Path.GetExtension(templateRelPath);
        if (string.IsNullOrEmpty(extension)) extension = TemplateActorCatalogFormat.TemplateExtensions[0];

        // 同時に何件追加しても取り違えないよう、毎回ちがう名前にする
        var path = Path.Combine(dir, Guid.NewGuid().ToString("N") + extension);
        File.WriteAllText(path, json, FileEncoding);
        return path;
    }

    /// <summary>
    /// 一時フォルダの中の古いファイルを消す（消せないものは次の機会に回す）。
    /// </summary>
    /// <param name="directory">一時フォルダ。</param>
    /// <param name="nowUtc">いまの時刻（UTC）。テストで固定できるよう引数で受ける。</param>
    /// <returns>消したファイル数。</returns>
    public static int CleanupStale(string directory, DateTime nowUtc)
    {
        if (!Directory.Exists(directory)) return 0;
        int removed = 0;
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            try
            {
                if (nowUtc - File.GetLastWriteTimeUtc(file) < StaleAge) continue;
                File.Delete(file);
                removed++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // ランタイムが読んでいる最中などで消せなければ、次の書き出しのときにまた試す
            }
        }
        return removed;
    }
}
