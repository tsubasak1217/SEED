// ============================================================
//  RunRequestConfig.cs — 設定 JSON（--config）を AndroidRunRequest として読む
//
//  キーは AndroidRunRequest の JsonPropertyName（snake_case）。相対パス（project / assets_dir / log_file / keystore）は
//  設定 JSON のあるフォルダからの相対として絶対パスへ直す（どこから実行しても同じ意味になるように）。
//  署名のパスワードは JSON から読まない（AndroidRunRequest.SigningSecrets は JsonIgnore。環境変数か対話の入力で渡す）。
//  goal はサブコマンドで決まるので、書いてあっても使わない。
// ============================================================

using System;
using System.IO;
using System.Text.Json;
using SEEDEditor.Android.Pipeline;

namespace SEEDEditor.Tools.SeedAndroid;

/// <summary>設定 JSON の読み込み。</summary>
public static class RunRequestConfig
{
    /// <summary>読み込みの設定（コメント・末尾のカンマを許す）。</summary>
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// 設定 JSON を読む。
    /// </summary>
    /// <param name="path">設定 JSON のパス。</param>
    /// <param name="error">読めなかった理由（成功時は null）。</param>
    /// <returns>指定（失敗時は null）。</returns>
    public static AndroidRunRequest? Load(string path, out string? error)
    {
        error = null;
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath))
        {
            error = $"設定 JSON が見つかりません: {fullPath}";
            return null;
        }
        AndroidRunRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<AndroidRunRequest>(File.ReadAllText(fullPath), ReadOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            error = $"設定 JSON を読めません（{fullPath}）: {ex.Message}";
            return null;
        }
        if (request is null)
        {
            error = $"設定 JSON が空です: {fullPath}";
            return null;
        }

        var baseDir = Path.GetDirectoryName(fullPath)!;
        return request with
        {
            ProjectDir   = Absolute(baseDir, request.ProjectDir),
            AssetsDir    = Absolute(baseDir, request.AssetsDir),
            LogFile      = Absolute(baseDir, request.LogFile),
            // 配布用の署名のキーストア（段階D。パスワードは JSON に書かない＝読まない）
            KeystorePath = Absolute(baseDir, request.KeystorePath),
        };
    }

    /// <summary>相対パスを設定 JSON のフォルダからの絶対パスにする（null・空はそのまま）。</summary>
    /// <param name="baseDir">設定 JSON のフォルダ。</param>
    /// <param name="path">パス。</param>
    /// <returns>絶対パス。</returns>
    private static string? Absolute(string baseDir, string? path) =>
        string.IsNullOrWhiteSpace(path) ? path : Path.GetFullPath(Path.Combine(baseDir, path));
}
