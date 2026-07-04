using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// スクリプトエディタの未保存編集内容をディスクへ退避し、クラッシュ後に
/// 復元するためのストア。
///
/// 動作原理:
///   - 未保存の変更があるドキュメントの内容を、専用ディレクトリへ随時書き出す。
///   - 保存・タブクローズ時にはそのファイルの退避データを削除する。
///   - アプリを正常終了したときは <see cref="Clear"/> で全退避データを消す。
/// よって「退避データが残っている ＝ 前回が正常終了しなかった（クラッシュ）」
/// とみなせ、起動時に復元を提案できる。
/// </summary>
public sealed class ScriptRecoveryStore
{
    /// <summary>退避 1 件（元ファイルパスとその未保存内容）。</summary>
    public sealed record Entry(string FilePath, string Content);

    private readonly string _dir;

    public ScriptRecoveryStore(string dir)
    {
        _dir = dir;
        try { Directory.CreateDirectory(_dir); } catch { /* 失敗時は無効化される */ }
    }

    /// <summary>指定ファイルの未保存内容を退避する。</summary>
    public void Save(string filePath, string content)
    {
        try
        {
            var json = JsonSerializer.Serialize(new Entry(filePath, content));
            File.WriteAllText(PathFor(filePath), json, Encoding.UTF8);
        }
        catch { /* 退避失敗は無視（復元はベストエフォート） */ }
    }

    /// <summary>指定ファイルの退避データを削除する（保存・クローズ時）。</summary>
    public void Remove(string filePath)
    {
        try
        {
            var p = PathFor(filePath);
            if (File.Exists(p)) File.Delete(p);
        }
        catch { /* 無視 */ }
    }

    /// <summary>全退避データを削除する（正常終了時）。</summary>
    public void Clear()
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
            {
                try { File.Delete(f); } catch { /* 無視 */ }
            }
        }
        catch { /* 無視 */ }
    }

    /// <summary>退避データが 1 件でも存在するか（＝前回クラッシュの可能性）。</summary>
    public bool HasAny()
    {
        try { return Directory.Exists(_dir) && Directory.EnumerateFiles(_dir, "*.json").Any(); }
        catch { return false; }
    }

    /// <summary>全退避データを読み込む。壊れた項目はスキップする。</summary>
    public IReadOnlyList<Entry> LoadAll()
    {
        var list = new List<Entry>();
        try
        {
            if (!Directory.Exists(_dir)) return list;
            foreach (var f in Directory.EnumerateFiles(_dir, "*.json"))
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<Entry>(File.ReadAllText(f, Encoding.UTF8));
                    if (entry is not null && !string.IsNullOrEmpty(entry.FilePath))
                        list.Add(entry);
                }
                catch { /* 壊れた退避はスキップ */ }
            }
        }
        catch { /* 無視 */ }
        return list;
    }

    /// <summary>フルパスから退避ファイル名（安定なハッシュ）を生成する。</summary>
    private string PathFor(string filePath)
    {
        var key   = Path.GetFullPath(filePath).ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        var name  = Convert.ToHexString(bytes)[..16]; // 先頭 64bit で十分に一意
        return Path.Combine(_dir, name + ".json");
    }
}
