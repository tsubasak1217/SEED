// ============================================================
//  AdbClient.cs — adb の呼び出し（端末の一覧・インストール・run-as での転送と消去・起動・停止・logcat・
//                 エミュレータの AVD 名と起動の完了の確認）
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

    /// <summary>起動が終わった（パッケージマネージャ等が使える）ことを表すシステムプロパティ。</summary>
    public const string BootCompletedProperty = "sys.boot_completed";

    /// <summary><see cref="BootCompletedProperty"/> の「起動が終わった」の値。</summary>
    private const string BootCompletedValue = "1";

    /// <summary>adb emu（エミュレータのコンソール）の応答の終わりの行。</summary>
    private const string EmulatorConsoleOkLine = "OK";

    /// <summary>端末のシェルで単一引用符の中に単一引用符を入れるための置き換え（' → '\''）。</summary>
    private const string ShellSingleQuoteEscape = "'\\''";

    /// <summary>am start の文字列の extra の指定。</summary>
    private const string AmStringExtraOption = "--es";

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

    /// <summary>run-as でフォルダを消したときに端末が返す文字列。</summary>
    private const string RemovedMarker = "removed";

    /// <summary>
    /// アプリの内部データフォルダの中のフォルダを、あれば消して「消した」と返すスクリプト（{0} = 内部データフォルダからの相対パス）。
    /// 無ければ何も出さない（exec-out は端末側の終了コードを返さないことがあるので、出力で見分ける）。
    /// </summary>
    private const string RemoveIfExistsScriptFormat = "if [ -e {0} ]; then rm -rf {0} && echo " + RemovedMarker + "; fi";

    /// <summary>相対パスの区切り（端末のパス）。</summary>
    private const char RemotePathSeparator = '/';

    /// <summary>親フォルダを表す区切り（アプリのデータフォルダの外へ出るので受け付けない）。</summary>
    private const string RemoteParentSegment = "..";

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

    /// <summary>
    /// run-as でアプリの権限になり、アプリの内部データフォルダの中のフォルダを消す（あれば。段階C-4: run の起動の前に
    /// push で置いた DLL の上書き files/bin を消すのに使う）。デバッグ版の APK だけが使える。
    /// run-as の作業フォルダはそのアプリのデータフォルダ（/data/user/0/&lt;アプリ ID&gt;）で、権限もそのアプリのものなので、
    /// 消せるのは <paramref name="applicationId"/> のアプリの中だけ（<paramref name="remoteDir"/> は相対パスに限る）。
    /// </summary>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="applicationId">アプリ ID（検査済みの値）。</param>
    /// <param name="remoteDir">消すフォルダ（内部データフォルダからの相対。例 files/bin）。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>消したら true、もともと無ければ false。</returns>
    /// <exception cref="AdbCommandException">消せなかった・run-as が失敗したとき（アプリが入っていない・デバッグ版でない等）。</exception>
    public async Task<bool> RunAsRemoveDirectoryAsync(
        string serial, string applicationId, string remoteDir, CancellationToken cancellationToken)
    {
        var capture = await ChildProcessRunner.CaptureAsync(
            Spec(serial, RunAsRemoveArguments(applicationId, remoteDir)), cancellationToken).ConfigureAwait(false);
        var removed = ParseRunAsRemoveOutput(capture.StandardOutputText);
        if (capture.ExitCode != 0 || removed is null)
        {
            throw new AdbCommandException(
                $"run-as で {remoteDir} を消せませんでした（終了コード {capture.ExitCode}。デバッグ版の APK が入っているか確認してください）: {capture.AllOutputText}".TrimEnd());
        }
        return removed.Value;
    }

    /// <summary>
    /// run-as でフォルダを消す adb の引数を作る（純粋な処理）: exec-out run-as &lt;ID&gt; sh -c 'if [ -e &lt;dir&gt; ]; then rm -rf &lt;dir&gt; &amp;&amp; echo removed; fi'。
    /// exec-out は引数を 1 つずつ端末へ渡すので、スクリプトは 1 引数のまま渡す。
    /// </summary>
    /// <param name="applicationId">アプリ ID。</param>
    /// <param name="remoteDir">消すフォルダ（内部データフォルダからの相対。絶対パス・.. は受け付けない）。</param>
    /// <returns>adb の引数（-s は含まない）。</returns>
    /// <exception cref="ArgumentException">remoteDir が空・絶対パス・.. を含むとき（アプリのデータフォルダの外を指さないように）。</exception>
    public static string[] RunAsRemoveArguments(string applicationId, string remoteDir)
    {
        if (string.IsNullOrWhiteSpace(remoteDir) || remoteDir.StartsWith(RemotePathSeparator)
            || remoteDir.Split(RemotePathSeparator).Contains(RemoteParentSegment))
        {
            throw new ArgumentException($"消すフォルダはアプリのデータフォルダからの相対パスにしてください（{RemoteParentSegment} も使えません）: {remoteDir}", nameof(remoteDir));
        }
        var script = string.Format(System.Globalization.CultureInfo.InvariantCulture, RemoveIfExistsScriptFormat, remoteDir);
        return new[] { "exec-out", "run-as", applicationId, "sh", "-c", script };
    }

    /// <summary>
    /// run-as でフォルダを消した出力を読む（純粋な処理）。exec-out は端末の標準エラーも同じ出力に混ぜて返すので、
    /// 「removed」の行があれば消した（rm が成功したときだけ echo する）、何も無ければもともと無かった、
    /// それ以外（run-as・rm のエラーの文言だけ）は失敗とみなす。
    /// </summary>
    /// <param name="output">adb の標準出力。</param>
    /// <returns>消したら true、無かったら false、失敗なら null。</returns>
    public static bool? ParseRunAsRemoveOutput(string output)
    {
        var lines = output.Split('\n').Select(line => line.Trim()).Where(line => line.Length > 0).ToList();
        if (lines.Count == 0) return false;
        return lines.Contains(RemovedMarker, StringComparer.Ordinal) ? true : null;
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
    /// アプリのプロセス ID（pidof &lt;アプリ ID&gt;）。動いていなければ空。
    /// エディタの実行（段階C-2）が「端末でアプリが終わったか」を見張るのに使う（自分のアプリだけを問い合わせる）。
    /// </summary>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="applicationId">アプリ ID（検査済みの値。英数字・_・. だけなので adb shell にそのまま渡せる）。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>プロセス ID の並び（動いていなければ空）。</returns>
    /// <exception cref="AdbCommandException">adb 自体が失敗したとき（端末が外れた等。標準エラーに理由が出る）。</exception>
    public async Task<IReadOnlyList<int>> GetProcessIdsAsync(string serial, string applicationId, CancellationToken cancellationToken)
    {
        var capture = await ChildProcessRunner.CaptureAsync(
            Spec(serial, "shell", "pidof", applicationId), cancellationToken).ConfigureAwait(false);
        var pids = ParseProcessIds(capture.StandardOutputText);
        if (pids.Count > 0) return pids;

        // pidof は見つからないとき終了コード 1 で何も出さない。adb 自体の失敗（device not found 等）は標準エラーに理由が出る
        if (capture.ExitCode != 0 && capture.StandardError.Any(line => line.Trim().Length > 0))
        {
            throw new AdbCommandException($"pidof {applicationId} が失敗しました（{serial}・終了コード {capture.ExitCode}）: {capture.AllOutputText}");
        }
        return Array.Empty<int>();
    }

    /// <summary>
    /// pidof の出力（空白区切りの数字。例 "12345" / "12345 12400"）をプロセス ID の並びにする（純粋な処理）。
    /// 数字でない語（エラーの文言など）は読み飛ばす。
    /// </summary>
    /// <param name="output">pidof の標準出力。</param>
    /// <returns>プロセス ID の並び（出力の順）。</returns>
    public static IReadOnlyList<int> ParseProcessIds(string output) =>
        output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(token => int.TryParse(token, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var pid) ? pid : 0)
            .Where(pid => pid > 0)
            .ToArray();

    /// <summary>
    /// Activity を起動して表示されるまで待つ（am start -W -n。起動オプションは --es の extra で渡す）。
    /// 出力（LaunchState・TotalTime 等）を返す。
    /// </summary>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="component">&lt;アプリ ID&gt;/&lt;Activity の完全修飾名&gt;。</param>
    /// <param name="extras">文字列の extra（起動オプション。無ければ空）。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>am start の出力。</returns>
    /// <exception cref="AdbCommandException">起動できなかったとき。</exception>
    public async Task<IReadOnlyList<string>> StartActivityAsync(
        string serial, string component, IReadOnlyList<AdbIntentExtra> extras, CancellationToken cancellationToken)
    {
        var capture = await ChildProcessRunner.CaptureAsync(
            Spec(serial, AmStartArguments(component, extras)), cancellationToken).ConfigureAwait(false);
        var lines = capture.StandardOutput.Concat(capture.StandardError).ToList();
        if (capture.ExitCode != 0 || lines.Any(line => line.TrimStart().StartsWith(AmStartErrorPrefix, StringComparison.Ordinal)))
        {
            throw new AdbCommandException($"am start が失敗しました（終了コード {capture.ExitCode}）: {string.Join(" / ", lines)}");
        }
        return lines;
    }

    /// <summary>
    /// am start の adb の引数を作る（純粋な処理）: shell am start -W -n &lt;component&gt; [--es &lt;キー&gt; '&lt;値&gt;' …]。
    /// adb shell は 2 つ目以降の引数を空白でつないで端末のシェルに解釈させるので、値は単一引用符で囲む
    /// （空白・日本語・記号を含むシーンのパスもそのまま 1 つの引数として届く）。
    /// </summary>
    /// <param name="component">&lt;アプリ ID&gt;/&lt;Activity の完全修飾名&gt;（検査済みの値。引用しない）。</param>
    /// <param name="extras">文字列の extra。</param>
    /// <returns>adb の引数（-s は含まない）。</returns>
    public static string[] AmStartArguments(string component, IReadOnlyList<AdbIntentExtra> extras)
    {
        var arguments = new List<string> { "shell", "am", "start", "-W", "-n", component };
        foreach (var extra in extras)
        {
            arguments.Add(AmStringExtraOption);
            arguments.Add(extra.Key);
            arguments.Add(ShellQuote(extra.Value));
        }
        return arguments.ToArray();
    }

    /// <summary>
    /// 端末のシェル（sh）へ 1 つの語として渡すために単一引用符で囲む（中の ' は '\'' にする。純粋な処理）。
    /// </summary>
    /// <param name="value">値。</param>
    /// <returns>引用した値。</returns>
    public static string ShellQuote(string value) =>
        "'" + value.Replace("'", ShellSingleQuoteEscape, StringComparison.Ordinal) + "'";

    /// <summary>
    /// エミュレータの AVD 名（adb -s &lt;シリアル&gt; emu avd name）。新しく起動したエミュレータを見分けるのに使う
    /// （同じ PC で別の AVD が動いていることがあるため、現れた emulator-* の AVD 名を照合する）。
    /// </summary>
    /// <param name="serial">エミュレータのシリアル（emulator-5554 等）。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>AVD 名（コンソールにつながらない・読めなければ null）。</returns>
    public async Task<string?> GetEmulatorAvdNameAsync(string serial, CancellationToken cancellationToken)
    {
        var capture = await ChildProcessRunner.CaptureAsync(Spec(serial, "emu", "avd", "name"), cancellationToken).ConfigureAwait(false);
        return capture.ExitCode != 0 ? null : ParseEmulatorAvdName(capture.StandardOutput);
    }

    /// <summary>
    /// adb emu avd name の出力（1 行目が AVD 名、最後に OK）から AVD 名を取り出す（純粋な処理）。
    /// </summary>
    /// <param name="lines">標準出力の行。</param>
    /// <returns>AVD 名（無ければ null）。</returns>
    public static string? ParseEmulatorAvdName(IEnumerable<string> lines) =>
        lines.Select(line => line.Trim())
            .FirstOrDefault(line => line.Length > 0 && !string.Equals(line, EmulatorConsoleOkLine, StringComparison.Ordinal));

    /// <summary>
    /// 起動が終わったか（getprop sys.boot_completed が 1）。adb の状態が device になっても、起動の途中は
    /// パッケージマネージャが無くて install が失敗するので、エミュレータはこれを待ってから使う。
    /// </summary>
    /// <param name="serial">端末のシリアル。</param>
    /// <param name="cancellationToken">中断の合図。</param>
    /// <returns>起動が終わっていれば true（問い合わせに失敗したら false）。</returns>
    public async Task<bool> IsBootCompletedAsync(string serial, CancellationToken cancellationToken)
    {
        try
        {
            var value = await GetPropertyAsync(serial, BootCompletedProperty, cancellationToken).ConfigureAwait(false);
            return string.Equals(value, BootCompletedValue, StringComparison.Ordinal);
        }
        catch (AdbCommandException)
        {
            // offline のうちは getprop が失敗する（起動の途中）
            return false;
        }
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
