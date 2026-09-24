// ============================================================
//  AdbClient.cs — adb の呼び出し（端末の一覧・インストール・run-as での転送・起動・停止・logcat）
//
//  【触ってよいもの】
//  端末は共用・私物のことがある。ここから行う操作は「自分のアプリ（アプリ ID を引数で受け取る）と、そのデータフォルダ」
//  だけに閉じる（logcat -c・再起動・他のアプリの操作は持たない）。logcat は起動直前の端末の時刻からの分だけを読む。
//
//  【引数の渡り方（adb の仕様）】
//  - adb shell は 2 つ目以降の引数を空白でつないで端末のシェルに解釈させる。空白を含むものは内側で引用する。
//  - adb exec-in / exec-out は 2 つ目以降の引数を 1 つずつ引用して渡す。sh -c のスクリプトは 1 引数で渡す。
//  - adb logcat は引数を 1 つずつ引用して渡すので、空白を含む時刻（-T "09-24 17:00:00.000"）もそのまま渡せる。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SEEDEditor.Android.Processes;

namespace SEEDEditor.Android.Adb;

/// <summary>adb の操作が失敗した。</summary>
public sealed class AdbCommandException : Exception
{
    /// <summary>理由を指定して生成する。</summary>
    /// <param name="message">利用者へ見せる説明。</param>
    public AdbCommandException(string message) : base(message) { }
}

/// <summary>adb の呼び出し。</summary>
public sealed class AdbClient
{
    /// <summary>端末の ABI の並び（優先順）を持つシステムプロパティ。</summary>
    public const string AbiListProperty = "ro.product.cpu.abilist";

    /// <summary>pm path の出力の行頭（package:/data/app/…/base.apk）。</summary>
    private const string PackagePathPrefix = "package:";

    /// <summary>転送の確認で端末が返す文字列。</summary>
    private const string CheckOkMarker = "ok";

    /// <summary>am start の出力で失敗を表す行頭（"Error: Activity class … does not exist." 等）。</summary>
    private const string AmStartErrorPrefix = "Error";

    /// <summary>転送先のフォルダを作り直して tar を展開し、他のユーザーから読めないよう権限を絞るスクリプト（{0} = 置き場）。</summary>
    private const string ExtractScriptFormat =
        "rm -rf {0} && mkdir -p {0} && tar -xf - -C {0} && chmod -R u+rwX,go-rwx {0}";

    /// <summary>転送後に「置けたか」を確かめるスクリプト（{0} = 確かめるファイル）。</summary>
    private const string CheckScriptFormat = "test -f {0} && echo " + CheckOkMarker;

    /// <summary>adb の実行ファイル。</summary>
    public string AdbPath { get; }

    /// <summary>adb の場所を指定して作る。</summary>
    /// <param name="adbPath">adb.exe の絶対パス。</param>
    public AdbClient(string adbPath)
    {
        AdbPath = adbPath;
    }

    /// <summary>つながっている端末の一覧（adb devices -l）。</summary>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>端末の一覧。</returns>
    public async Task<IReadOnlyList<AdbDevice>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        var capture = await ChildProcessRunner.CaptureAsync(Spec(null, "devices", "-l"), cancellationToken).ConfigureAwait(false);
        if (capture.ExitCode != 0)
        {
            throw new AdbCommandException($"adb devices が失敗しました（終了コード {capture.ExitCode}）: {capture.AllOutputText}");
        }
        return AdbDeviceListParser.Parse(capture.StandardOutputText);
    }

    /// <summary>端末のシステムプロパティを読む（getprop）。</summary>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="name">プロパティ名。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>値（前後の空白を落としたもの。無ければ空）。</returns>
    public async Task<string> GetPropertyAsync(string serial, string name, CancellationToken cancellationToken)
    {
        var capture = await ChildProcessRunner.CaptureAsync(Spec(serial, "shell", "getprop", name), cancellationToken).ConfigureAwait(false);
        if (capture.ExitCode != 0)
        {
            throw new AdbCommandException($"getprop {name} が失敗しました（{serial}・終了コード {capture.ExitCode}）: {capture.AllOutputText}");
        }
        return capture.StandardOutputText.Trim();
    }

    /// <summary>
    /// logcat -T に渡す「今」の端末の時刻（共用の端末で logcat -c をせず、今回分だけを取り出すため）。
    /// </summary>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>時刻の文字列（例 "09-25 14:03:12.000"）。</returns>
    public async Task<string> GetLogcatSinceAsync(string serial, CancellationToken cancellationToken)
    {
        // adb shell は引数をつないで端末のシェルへ渡すので、空白を含む書式は内側で引用する
        var command = $"date '{AndroidRuntimeContract.LogcatSinceDateFormat}'";
        var capture = await ChildProcessRunner.CaptureAsync(Spec(serial, "shell", command), cancellationToken).ConfigureAwait(false);
        var since = capture.StandardOutputText.Trim();
        if (capture.ExitCode != 0 || since.Length == 0)
        {
            throw new AdbCommandException($"端末の時刻を読めません（{serial}・終了コード {capture.ExitCode}）: {capture.AllOutputText}");
        }
        return since;
    }

    /// <summary>
    /// 入っているアプリの APK の場所（pm path）。入っていなければ null。
    /// 場所はインストールのたびに変わる（Android 11 以降は /data/app/~~乱数==/&lt;ID&gt;-乱数==/base.apk）ので、
    /// 「前回自分が入れたものがそのまま入っているか」の目印に使える。
    /// </summary>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="applicationId">アプリ ID。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>base.apk の場所（入っていなければ null）。</returns>
    public async Task<string?> GetInstalledApkPathAsync(string serial, string applicationId, CancellationToken cancellationToken)
    {
        var capture = await ChildProcessRunner.CaptureAsync(Spec(serial, "shell", "pm", "path", applicationId), cancellationToken).ConfigureAwait(false);
        // 入っていなければ終了コード 1 で何も出さない
        return capture.StandardOutput
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(PackagePathPrefix, StringComparison.Ordinal))
            .Select(line => line[PackagePathPrefix.Length..])
            .OrderBy(path => path, StringComparer.Ordinal)
            .FirstOrDefault();
    }

    /// <summary>APK を入れる（adb install -r。アプリのデータは残す）。</summary>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="apkPath">APK のパス。</param>
    /// <param name="onLine">adb の出力 1 行ごとに呼ばれる。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>終了コード。</returns>
    public Task<int> InstallAsync(
        string serial, string apkPath, Action<ChildProcessStream, string>? onLine, CancellationToken cancellationToken) =>
        ChildProcessRunner.RunAsync(Spec(serial, "install", "-r", apkPath), onLine, cancellationToken);

    /// <summary>
    /// run-as でアプリの権限になり、tar のストリームをアプリの内部データフォルダへ展開する（前回分は消してから置き直す）。
    /// デバッグ版の APK だけが使える。展開の後、確かめるファイルがあるかを見る（exec-in は端末側の失敗を
    /// 終了コードで返さないことがあるため）。
    /// </summary>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="applicationId">アプリ ID。</param>
    /// <param name="remoteDir">送り先（内部データフォルダからの相対。例 files/assets）。</param>
    /// <param name="checkFile">送り先の中に必ずあるはずのファイル名。</param>
    /// <param name="writeTar">tar のストリームを書く処理。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <exception cref="AdbCommandException">転送できなかったとき。</exception>
    public async Task RunAsExtractTarAsync(
        string serial,
        string applicationId,
        string remoteDir,
        string checkFile,
        Func<Stream, CancellationToken, Task> writeTar,
        CancellationToken cancellationToken)
    {
        var extract = string.Format(System.Globalization.CultureInfo.InvariantCulture, ExtractScriptFormat, remoteDir);
        var capture = await ChildProcessRunner.CaptureAsync(
            Spec(serial, "exec-in", "run-as", applicationId, "sh", "-c", extract), cancellationToken, writeTar).ConfigureAwait(false);
        if (capture.ExitCode != 0)
        {
            throw new AdbCommandException(
                $"run-as での転送が失敗しました（終了コード {capture.ExitCode}。デバッグ版の APK が入っているか確認してください）: {capture.AllOutputText}");
        }

        var check = string.Format(System.Globalization.CultureInfo.InvariantCulture, CheckScriptFormat, $"{remoteDir}/{checkFile}");
        var verify = await ChildProcessRunner.CaptureAsync(
            Spec(serial, "exec-out", "run-as", applicationId, "sh", "-c", check), cancellationToken).ConfigureAwait(false);
        if (verify.StandardOutputText.Trim() != CheckOkMarker)
        {
            throw new AdbCommandException(
                $"転送の後に {remoteDir}/{checkFile} が見つかりません（{capture.AllOutputText} {verify.AllOutputText}）".TrimEnd());
        }
    }

    /// <summary>アプリを止める（am force-stop。自分のアプリだけ）。</summary>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="applicationId">アプリ ID。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>終了コード。</returns>
    public async Task<int> ForceStopAsync(string serial, string applicationId, CancellationToken cancellationToken)
    {
        var capture = await ChildProcessRunner.CaptureAsync(
            Spec(serial, "shell", "am", "force-stop", applicationId), cancellationToken).ConfigureAwait(false);
        return capture.ExitCode;
    }

    /// <summary>
    /// Activity を起動して表示されるまで待つ（am start -W -n）。出力（LaunchState・TotalTime 等）を返す。
    /// </summary>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="component">&lt;アプリ ID&gt;/&lt;Activity の完全修飾名&gt;。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>am start の出力。</returns>
    /// <exception cref="AdbCommandException">起動できなかったとき。</exception>
    public async Task<IReadOnlyList<string>> StartActivityAsync(string serial, string component, CancellationToken cancellationToken)
    {
        var capture = await ChildProcessRunner.CaptureAsync(
            Spec(serial, "shell", "am", "start", "-W", "-n", component), cancellationToken).ConfigureAwait(false);
        var lines = capture.StandardOutput.Concat(capture.StandardError).ToList();
        if (capture.ExitCode != 0 || lines.Any(line => line.TrimStart().StartsWith(AmStartErrorPrefix, StringComparison.Ordinal)))
        {
            throw new AdbCommandException($"am start が失敗しました（終了コード {capture.ExitCode}）: {string.Join(" / ", lines)}");
        }
        return lines;
    }

    /// <summary>
    /// logcat を流す（中断されるまで）。-T で指定した時刻以降の分だけ、指定のタグだけを読む。
    /// </summary>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="since">この時刻以降（<see cref="GetLogcatSinceAsync"/> の値）。</param>
    /// <param name="filters">タグの絞り込み（例 SEED:V … *:S）。</param>
    /// <param name="onLine">1 行ごとに呼ばれる。</param>
    /// <param name="cancellationToken">止める合図（止めると adb logcat を終了させて戻る）。</param>
    /// <returns>終了コード（中断されたときは OperationCanceledException）。</returns>
    public Task<int> StreamLogcatAsync(
        string serial, string since, IReadOnlyList<string> filters, Action<string> onLine, CancellationToken cancellationToken)
    {
        var arguments = new List<string> { "logcat", "-v", "threadtime", "-T", since };
        arguments.AddRange(filters);
        return ChildProcessRunner.RunAsync(Spec(serial, arguments.ToArray()), (_, line) => onLine(line), cancellationToken);
    }

    /// <summary>adb の起動内容を作る（シリアルがあれば -s を付ける）。</summary>
    /// <param name="serial">端末のシリアル（null なら付けない）。</param>
    /// <param name="arguments">adb の引数。</param>
    /// <returns>起動内容。</returns>
    public ChildProcessSpec Spec(string? serial, params string[] arguments)
    {
        var all = new List<string>();
        if (!string.IsNullOrEmpty(serial))
        {
            all.Add("-s");
            all.Add(serial);
        }
        all.AddRange(arguments);
        return new ChildProcessSpec { FileName = AdbPath, Arguments = all };
    }
}
