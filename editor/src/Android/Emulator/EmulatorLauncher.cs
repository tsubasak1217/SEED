// ============================================================
//  EmulatorLauncher.cs — Android Emulator（SDK の emulator.exe）の呼び出し（AVD の一覧・切り離した起動）
//
//  【起動のしかた】
//    emulator -avd <AVD> -gpu host
//  quick boot（スナップショットからの起動・閉じるときの保存）は AVD の既定のまま使う。エディタ・SeedAndroid が
//  終わっても動き続けるよう、切り離して起動する（Processes/DetachedProcess.cs。ハンドルを継承させない・コンソールの窓なし）。
//  新しく現れた emulator-* がどれかは、呼び出し側（AndroidDeviceProvisioner）が adb emu avd name で照合して決める。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Processes;

namespace SEEDEditor.Android.Emulator;

/// <summary>Android Emulator の呼び出し。</summary>
public sealed class EmulatorLauncher
{
    /// <summary>AVD の一覧を出す引数。</summary>
    private const string ListAvdsOption = "-list-avds";

    /// <summary>起動する AVD を指定する引数。</summary>
    private const string AvdOption = "-avd";

    /// <summary>ログに出すときの実行ファイルの名前。</summary>
    private const string DisplayName = "emulator";

    /// <summary>
    /// 起動の引数（AVD の指定の後ろ）。-gpu host は PC の GPU で描く（開発用の AVD の hw.gpu.mode と同じ。
    /// 段階0 から使っている設定。Vulkan の描画が速い）。quick boot は既定のまま（引数を足さない）。
    /// </summary>
    public static readonly IReadOnlyList<string> LaunchOptions = new[] { "-gpu", "host" };

    /// <summary>AVD の名前の形（avdmanager が許す文字だけ。一覧の出力に混ざるログの行と見分ける）。</summary>
    private static readonly Regex AvdNamePattern = new("^[A-Za-z0-9._-]+$", RegexOptions.CultureInvariant);

    /// <summary>emulator.exe の絶対パス。</summary>
    private readonly string _emulatorPath;

    /// <summary>emulator.exe の場所を指定して作る。</summary>
    /// <param name="emulatorPath">emulator.exe の絶対パス（AndroidToolchain.RequireEmulator）。</param>
    public EmulatorLauncher(string emulatorPath)
    {
        _emulatorPath = emulatorPath;
    }

    /// <summary>
    /// AVD の一覧（emulator -list-avds）。
    /// </summary>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>AVD の名前（出力の順）。</returns>
    /// <exception cref="AndroidPipelineException">一覧を取れなかったとき（道具の失敗）。</exception>
    public async Task<IReadOnlyList<string>> ListAvdsAsync(CancellationToken cancellationToken)
    {
        var spec = new ChildProcessSpec { FileName = _emulatorPath, Arguments = new[] { ListAvdsOption } };
        ChildProcessCapture capture;
        try
        {
            capture = await ChildProcessRunner.CaptureAsync(spec, cancellationToken).ConfigureAwait(false);
        }
        catch (ChildProcessStartException ex)
        {
            throw new AndroidPipelineException(AndroidFailureKind.Toolchain, ex.Message, ex);
        }
        if (capture.ExitCode != 0)
        {
            throw new AndroidPipelineException(AndroidFailureKind.Toolchain,
                $"emulator {ListAvdsOption} が失敗しました（終了コード {capture.ExitCode}）: {capture.AllOutputText}");
        }
        return ParseAvdList(capture.StandardOutput);
    }

    /// <summary>
    /// エミュレータを切り離して起動する（待たない）。
    /// </summary>
    /// <param name="avdName">AVD の名前。</param>
    /// <returns>起動した emulator.exe（終わったかを見るだけ。止めない）。</returns>
    /// <exception cref="ChildProcessStartException">起動できなかったとき。</exception>
    public IDetachedProcess Start(string avdName) =>
        DetachedProcess.Start(_emulatorPath, LaunchArguments(avdName), Path.GetDirectoryName(_emulatorPath));

    /// <summary>起動の引数（-avd &lt;AVD&gt; -gpu host。純粋な処理）。</summary>
    /// <param name="avdName">AVD の名前。</param>
    /// <returns>引数。</returns>
    public static IReadOnlyList<string> LaunchArguments(string avdName) =>
        new[] { AvdOption, avdName }.Concat(LaunchOptions).ToArray();

    /// <summary>起動のコマンドの表示（ログ用。例 emulator -avd seed_pixel6_api35 -gpu host）。</summary>
    /// <param name="avdName">AVD の名前。</param>
    /// <returns>表示。</returns>
    public static string Describe(string avdName) => string.Join(" ", new[] { DisplayName }.Concat(LaunchArguments(avdName)));

    /// <summary>
    /// emulator -list-avds の出力から AVD の名前を取り出す（純粋な処理）。空行・版によって混ざる
    /// ログの行（「INFO    | …」等。空白や | を含む）は読み飛ばし、同じ名前は 1 つにまとめる。
    /// </summary>
    /// <param name="lines">標準出力の行。</param>
    /// <returns>AVD の名前（出力の順）。</returns>
    public static IReadOnlyList<string> ParseAvdList(IEnumerable<string> lines) =>
        lines.Select(line => line.Trim())
            .Where(line => AvdNamePattern.IsMatch(line))
            .Distinct(StringComparer.Ordinal)
            .ToList();
}
