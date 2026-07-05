using System;
using System.IO;

namespace SEEDEditor.Debugger;

/// <summary>
/// マネージドデバッガ netcoredbg（OSS・DAP 対応）の実行ファイルを探す。
///
/// 探索順:
///   1. 環境変数 SEED_NETCOREDBG（フルパス）
///   2. エディタ同梱の tools\netcoredbg\netcoredbg.exe（実行ディレクトリ基準）
///   3. リポジトリ直下 tools\netcoredbg\netcoredbg.exe（開発時、親を遡って探す）
///   4. PATH 上の netcoredbg(.exe)
///
/// 見つからなければ null を返す。バイナリは大きい（数MB）ためリポジトリには含めず、
/// ユーザーが上記いずれかに配置する運用（配置方法は docs/scripting_debugger.md 参照）。
/// </summary>
public static class NetcoredbgLocator
{
    private const string ExeName = "netcoredbg.exe";
    private const string EnvVar  = "SEED_NETCOREDBG";

    // 同梱・リポジトリ内での相対配置
    private static readonly string[] RelativeCandidates =
    {
        Path.Combine("tools", "netcoredbg", ExeName),
        Path.Combine("netcoredbg", ExeName),
        ExeName,
    };

    /// <summary>netcoredbg.exe のフルパスを返す（見つからなければ null）。</summary>
    public static string? Locate()
    {
        // 1) 環境変数
        var env = Environment.GetEnvironmentVariable(EnvVar);
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;

        // 2)+3) 実行ディレクトリから親を遡り、相対候補を探す
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            foreach (var rel in RelativeCandidates)
            {
                var candidate = Path.Combine(dir.FullName, rel);
                if (File.Exists(candidate)) return candidate;
            }
            dir = dir.Parent;
        }

        // 4) PATH 上を探す
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var p in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(p)) continue;
            try
            {
                var candidate = Path.Combine(p.Trim(), ExeName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { /* 不正な PATH 要素はスキップ */ }
        }

        return null;
    }
}
