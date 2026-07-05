using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// ブレークポイントの永続化ストア。ファイルパス（正規化）ごとに行番号の集合を
/// editor/settings/breakpoints.json に保存し、次回起動時に復元する。
///
/// 将来のデバッガバックエンドは、ここに保存された「ファイル→行」をアタッチ時の
/// ブレークポイント設定として利用する。
/// </summary>
public sealed class BreakpointStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private readonly string _filePath;
    // 正規化パス → 行番号リスト
    private readonly Dictionary<string, List<int>> _byPath;

    public BreakpointStore(string settingsDir)
    {
        _filePath = Path.Combine(settingsDir, "breakpoints.json");
        _byPath   = Load(_filePath);
    }

    /// <summary>指定ファイルのブレークポイント行を取得する（無ければ空）。</summary>
    public IReadOnlyList<int> Get(string filePath)
        => _byPath.TryGetValue(Normalize(filePath), out var lines) ? lines : Array.Empty<int>();

    /// <summary>永続化されている全ファイルのブレークポイント（正規化パス→行）。デバッガのアタッチ用。</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<int>> All()
        => _byPath.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<int>)kv.Value);

    /// <summary>指定ファイルのブレークポイント行を設定する（空なら削除）。呼び出し後に Save する。</summary>
    public void Set(string filePath, IEnumerable<int> lines)
    {
        var key  = Normalize(filePath);
        var list = lines.Distinct().OrderBy(n => n).ToList();
        if (list.Count == 0) _byPath.Remove(key);
        else                 _byPath[key] = list;
    }

    /// <summary>現在の内容をディスクへ保存する。</summary>
    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_byPath, JsonOpts));
        }
        catch (Exception ex)
        {
            EditorLog.Write($"ブレークポイントの保存に失敗: {ex.Message}");
        }
    }

    private static Dictionary<string, List<int>> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return new();
            return JsonSerializer.Deserialize<Dictionary<string, List<int>>>(File.ReadAllText(path))
                ?? new();
        }
        catch
        {
            return new();
        }
    }

    private static string Normalize(string path)
    {
        try { path = Path.GetFullPath(path); } catch { }
        return path.Replace('/', '\\').ToLowerInvariant();
    }
}
