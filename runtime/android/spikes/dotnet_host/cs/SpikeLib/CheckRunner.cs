// =============================================================================
// CheckRunner.cs
// 個々のチェックを実行して OK/NG・所要時間・詳細を 1 行ずつ集める。
// 出力はタブ区切りのテキスト（Rust 側はそのまま表示するだけ）。
//   INFO  <key>  <value>
//   CHECK <name> <OK|NG> <ms> <detail>
// =============================================================================
using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace SpikeLib;

/// <summary>チェック結果の収集器。</summary>
internal sealed class CheckRunner
{
    /// <summary>レポート本体。</summary>
    private readonly StringBuilder _report = new();

    /// <summary>NG の件数。</summary>
    public int FailureCount { get; private set; }

    /// <summary>情報行（検証ではなく環境の記録）を追加する。</summary>
    public void Info(string key, object? value)
    {
        _report.Append("INFO\t").Append(key).Append('\t').Append(Sanitize(value?.ToString() ?? "(null)")).Append('\n');
    }

    /// <summary>
    /// チェックを 1 件実行する。body が例外なく返れば OK、例外なら NG。
    /// body は「(合否, 詳細文字列)」を返す。合否 false も NG とする。
    /// </summary>
    public void Run(string name, Func<(bool ok, string detail)> body)
    {
        var sw = Stopwatch.StartNew();
        bool ok;
        string detail;
        try
        {
            (ok, detail) = body();
        }
        catch (Exception ex)
        {
            ok = false;
            detail = $"EXCEPTION {ex.GetType().FullName}: {ex.Message}";
        }
        sw.Stop();
        if (!ok) FailureCount++;
        _report.Append("CHECK\t").Append(name).Append('\t').Append(ok ? "OK" : "NG").Append('\t')
               .Append(sw.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)).Append("ms\t")
               .Append(Sanitize(detail)).Append('\n');
    }

    /// <summary>集計行を付けてレポート文字列を返す。</summary>
    public string Finish()
    {
        _report.Append("SUMMARY\tfailures=").Append(FailureCount).Append('\n');
        return _report.ToString();
    }

    /// <summary>1 行に収めるため改行とタブを可視化する。</summary>
    private static string Sanitize(string text) => text.Replace("\r", "").Replace("\n", " | ").Replace("\t", " ");
}
