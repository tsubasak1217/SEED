using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SEEDEditor.ProjectSettings;

/// <summary>
/// 最近開いたプロジェクトのリストを管理する。
/// {SettingsDir}/recent_projects.json に保存される。
/// </summary>
public class RecentProjectsManager
{
    private static readonly string SettingsFilePath = Path.Combine(MainWindow.SettingsDir, "recent_projects.json");
    private const int MaxRecentProjects = 10;

    public static List<string> LoadRecentProjects()
    {
        if (!File.Exists(SettingsFilePath)) return new List<string>();
        try
        {
            var json = File.ReadAllText(SettingsFilePath);
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static void AddProject(string path)
    {
        var projects = LoadRecentProjects();
        var fullPath = Path.GetFullPath(path);

        // 重複を除去し、リストの先頭に追加
        projects.RemoveAll(p => string.Equals(Path.GetFullPath(p), fullPath, StringComparison.OrdinalIgnoreCase));
        projects.Insert(0, fullPath);

        // 最大数に制限
        if (projects.Count > MaxRecentProjects)
            projects = projects.Take(MaxRecentProjects).ToList();

        Save(projects);
    }

    private static void Save(List<string> projects)
    {
        var json = JsonSerializer.Serialize(projects, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(SettingsFilePath, json);
    }
}
