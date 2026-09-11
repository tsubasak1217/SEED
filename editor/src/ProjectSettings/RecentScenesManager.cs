using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SEEDEditor.ProjectSettings;

/// <summary>
/// 最近開いたシーン（.scene）の一覧を管理する。
///
/// <para>
/// 保存先は <c>{SettingsDir}/recent_scenes.json</c>（.scene 絶対パスの文字列配列）。
/// 起動時のシーン復元（MainWindow.FileOps の TryLoadLastScene）が使う。
/// </para>
///
/// <para>
/// かつてこのクラスは <c>RecentProjectsManager</c> という名前で、同じ内容を
/// <c>recent_projects.json</c> へ書いていた。プロジェクト概念の導入で
/// その名前・ファイル名は「最近開いたプロジェクト」
/// （<see cref="SEEDEditor.Project.RecentProjectsStore"/>）が使うようになったため、
/// 実態に合わせて改名した。旧ファイルの中身は
/// <see cref="SEEDEditor.Project.RecentProjectsStore.MigrateLegacySceneList"/> が
/// recent_scenes.json へ移す。読み書きの前に必ずその移行を先に走らせること
/// （移行前に新形式で上書きすると、旧一覧が失われるため）。
/// </para>
/// </summary>
public static class RecentScenesManager
{
    /// <summary>保存ファイル名。</summary>
    public const string FILE_NAME = "recent_scenes.json";

    /// <summary>保持する最大件数（古いものから捨てる）。</summary>
    private const int MaxRecentScenes = 10;

    /// <summary>JSON の書き出し設定。</summary>
    private static readonly JsonSerializerOptions SerializeOptions = new() { WriteIndented = true };

    /// <summary>設定フォルダ（エディタ本体側。プロジェクトを跨いで共有する）。</summary>
    private static string SettingsDir => SEEDEditor.Settings.EditorPaths.SettingsDir;

    /// <summary>保存ファイルの絶対パス。</summary>
    private static string SettingsFilePath => Path.Combine(SettingsDir, FILE_NAME);

    /// <summary>
    /// 最近開いたシーンの一覧を新しい順に返す。
    /// ファイルが無い・壊れている場合は空リスト。
    /// </summary>
    public static List<string> LoadRecentScenes()
    {
        // 旧 recent_projects.json からの移行を先に済ませる
        // （このクラスが読む recent_scenes.json は移行の出力先そのもの）。
        SEEDEditor.Project.RecentProjectsStore.MigrateLegacySceneList(SettingsDir);

        var path = SettingsFilePath;
        if (!File.Exists(path)) return new List<string>();
        try
        {
            return JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path))
                   ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>
    /// シーンを一覧の先頭へ追加する（既にあれば先頭へ引き上げる）。
    /// </summary>
    /// <param name="path">.scene のパス（内部で絶対パス化する）。</param>
    public static void AddScene(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var scenes   = LoadRecentScenes();
        var fullPath = Path.GetFullPath(path);

        // 重複を除去し、リストの先頭に追加
        scenes.RemoveAll(p => string.Equals(SafeFullPath(p), fullPath, StringComparison.OrdinalIgnoreCase));
        scenes.Insert(0, fullPath);

        // 最大数に制限
        if (scenes.Count > MaxRecentScenes) scenes = scenes.Take(MaxRecentScenes).ToList();

        Save(scenes);
    }

    /// <summary>一覧をファイルへ書き出す。</summary>
    private static void Save(List<string> scenes)
    {
        Directory.CreateDirectory(SettingsDir);
        File.WriteAllText(SettingsFilePath, JsonSerializer.Serialize(scenes, SerializeOptions));
    }

    /// <summary>例外を投げずに絶対パスへ正規化する（不正なら入力をそのまま返す）。</summary>
    private static string SafeFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }
}
