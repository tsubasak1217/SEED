using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;

namespace SEEDEditor.Runtime;

/// <summary>
/// 実行中の Visual Studio を指定プロセス（SEED.exe）へアタッチさせる支援ユーティリティ。
///
/// スクリプトは埋め込み PDB 付きでコンパイルされるため、VS をアタッチすれば
/// .cs ファイルにブレークポイントを張ってステップ実行・変数確認ができる。
///
/// 実行中の VS が COM の実行オブジェクトテーブル(ROT)経由で見つかれば自動アタッチを
/// 試みる。見つからない/失敗した場合は呼び出し側が手動アタッチ手順を案内する。
/// </summary>
public static class VisualStudioAttach
{
    /// <summary>
    /// 実行中の Visual Studio 群を走査し、指定 PID のプロセスへアタッチを試みる。
    /// 成功したら true。VS 未起動・アタッチ失敗時は false（message に理由）。
    /// </summary>
    public static bool TryAttach(int processId, out string message)
    {
        var instances = GetRunningDteInstances();
        if (instances.Count == 0)
        {
            message = "実行中の Visual Studio が見つかりませんでした。";
            return false;
        }

        foreach (dynamic dte in instances)
        {
            try
            {
                dynamic debugger = dte.Debugger;
                dynamic locals   = debugger.LocalProcesses;
                foreach (dynamic proc in locals)
                {
                    int pid = (int)proc.ProcessID;
                    if (pid == processId)
                    {
                        proc.Attach();
                        message = $"Visual Studio をプロセス {processId} にアタッチしました。";
                        return true;
                    }
                }
            }
            catch
            {
                // この VS インスタンスでは失敗 → 次を試す
            }
        }

        message = $"対象プロセス(PID={processId})を Visual Studio のプロセス一覧に見つけられませんでした。";
        return false;
    }

    // ── ROT から DTE(Visual Studio) インスタンスを列挙する ────

    private static List<object> GetRunningDteInstances()
    {
        var result = new List<object>();
        IRunningObjectTable? rot = null;
        IEnumMoniker?        enumMoniker = null;
        try
        {
            if (GetRunningObjectTable(0, out rot) != 0 || rot is null) return result;
            rot.EnumRunning(out enumMoniker);
            if (enumMoniker is null) return result;

            var monikers = new IMoniker[1];
            IntPtr fetched = IntPtr.Zero;
            while (enumMoniker.Next(1, monikers, fetched) == 0)
            {
                try
                {
                    if (CreateBindCtx(0, out IBindCtx ctx) != 0) continue;
                    monikers[0].GetDisplayName(ctx, null, out string name);
                    // VisualStudio.DTE.x.y:pid の形式のモニカーのみ対象
                    if (name is not null && name.StartsWith("!VisualStudio.DTE", StringComparison.Ordinal))
                    {
                        if (rot.GetObject(monikers[0], out object obj) == 0 && obj is not null)
                            result.Add(obj);
                    }
                }
                catch { /* このモニカーはスキップ */ }
            }
        }
        catch { /* ROT アクセス失敗 → 空リスト */ }
        finally
        {
            if (enumMoniker is not null) Marshal.ReleaseComObject(enumMoniker);
            if (rot is not null)         Marshal.ReleaseComObject(rot);
        }
        return result;
    }

    [DllImport("ole32.dll")]
    private static extern int GetRunningObjectTable(int reserved, out IRunningObjectTable prot);

    [DllImport("ole32.dll")]
    private static extern int CreateBindCtx(int reserved, out IBindCtx ppbc);
}
