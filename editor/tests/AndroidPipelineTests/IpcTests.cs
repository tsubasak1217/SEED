using System.Net;
using System.Net.Sockets;
using System.Text;
using SEEDEditor.Android;
using SEEDEditor.Android.Adb;
using SEEDEditor.Android.Ipc;
using SEEDEditor.Android.Pipeline;
using SEEDEditor.Android.Steps;
using SEEDEditor.Ipc;
using SEEDEditor.Tools.SeedAndroid;
using SpriteRigTests;

namespace AndroidPipelineTests;

/// <summary>
/// 段階D-1: 端末のアプリとエディタをつなぐ IPC（adb forward ＋ TCP）。ポートの決め方、adb forward の引数と出力の読み方、
/// am start の extra（seed.ipc_port）、行の送受信（IpcLineChannel）、挨拶までのやり直し（ループバックの偽のランタイム）、
/// スクリーンショットの応答の読み方、SeedAndroid の pause / resume / screenshot の引数。端末・adb は使わない。
/// </summary>
public static class IpcTests
{
    /// <summary>テストのアプリ ID。</summary>
    private const string AppId = "com.seedengine.runtime";

    /// <summary>テストの接続トークン（起動の工程が端末へ渡すもの）。</summary>
    private const string Token = "0123456789abcdef0123456789abcdef";

    /// <summary>待ち合わせの上限（遅い PC でも通るよう長めにとる。通常は数ミリ秒で満たされる）。</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    /// <summary>接続のやり直しを素早く確かめる時間の決まり（上限 3 秒・やり直し 50 ms・挨拶 300 ms）。</summary>
    private static readonly AndroidIpcTimings FastTimings = new(
        TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(300), TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(1));

    /// <summary>テストを登録する。</summary>
    /// <param name="harness">テストランナー。</param>
    public static void Register(TestHarness harness)
    {
        harness.Add("IPC のポート: 指定なし＝既定 52735・0＝使わない・範囲外は誤り（40000 台を避けた既定）", ResolvesDevicePort);
        harness.Add("adb forward: tcp:0 tcp:<端末> で張り、出力の数字を PC 側のポートとして読む・--remove tcp:<PC 側>", BuildsForwardArguments);
        harness.Add("起動の extra: seed.ipc_port を 10 進の文字列で渡す（シーンと並べる・0 の指定なら渡さない）・キーはランタイムと一致", LaunchExtrasCarryIpcPort);
        harness.Add("行の送受信: 1 行ずつ前後の空白を落として届き、受け手の例外で止まらず、相手が閉じると Closed", LineChannelSendsAndReceives);
        harness.Add("接続: 最初の行で HELLO:<トークン> を示し、挨拶（READY:）が届くまでやり直す（起動の途中は閉じられる）・挨拶の後の行も届く", HandshakeRetriesUntilGreeting);
        harness.Add("接続: 待ち受けていない／挨拶が来ない（別の接続がつながっている）は時間内に理由付きで諦める", HandshakeGivesUpWithReason);
        harness.Add("接続トークン: 断られたら（IPC_DENIED:）やり直さず理由付きで諦める", HandshakeStopsOnDenial);
        harness.Add("接続トークン: 128 ビットの乱数の 16 進（毎回違う）・書式の検査・起動の extra に入れて表示では伏せる", TokenIsCreatedPassedAndMasked);
        harness.Add("接続トークン: 起動の記録（run_state.json の ipc_launches）の往復と、端末・アプリ ID での引き当て", RunStateRecordsIpcLaunch);
        harness.Add("スクリーンショット: 端末の書き先（アプリのキャッシュ）と応答（DONE の幅・高さ・ERROR の理由）の読み方", ParsesScreenshotReply);
        harness.Add("SeedAndroid: pause / resume / screenshot・--ipc-port（0〜65535）・--out は screenshot だけ・auto は使えない", ParsesControlArguments);
        harness.Add("写し（§20.17）: 端末の書き先・run-as の引数・SNAPSHOT_SCENE の応答待ち（失敗・切断・時間切れは理由付き）・SeedAndroid の snapshot と --out", SnapshotRequestAndArguments);
    }

    /// <summary>
    /// 一時停止中の写し（docs/android.md §20.17）の中核の部品: 端末の書き先・run-as の引数・通信路での応答待ち・SeedAndroid の引数。
    /// 取り出し（run-as）まで通る成功の流れは実機で確かめる（adb が要るため）。ここでは adb に届く前に終わる流れを確かめる。
    /// </summary>
    private static void SnapshotRequestAndArguments()
    {
        Check.Equal("/data/user/0/com.seedengine.runtime/cache/seed_ipc_snapshot.scene", AndroidIpcSnapshot.DevicePath(AppId),
            "アプリのキャッシュの絶対パス（ランタイムは .scene だけを受け付ける）");
        Check.Equal("exec-out run-as com.seedengine.runtime cat cache/seed_ipc_snapshot.scene",
            string.Join(" ", AdbClient.RunAsReadFileArguments(AppId, AndroidRuntimeContract.RemoteSnapshotPath)), "run-as で読む");

        // 端末が書き出せなかった（SNAPSHOT_FAILED）: 理由付きの失敗・受け手を外す
        var failing = new SnapshotLink(command => command.StartsWith(SEEDEditor.SceneSnapshot.SceneSnapshotWire.SnapshotScenePrefix, StringComparison.Ordinal)
            ? "SNAPSHOT_FAILED:/data/x.scene|シーンが読み込まれていません"
            : null);
        var unusedAdb = new AdbClient("adb-is-not-called");
        var failed = CatchIpcAsync(AndroidIpcSnapshot.FetchAsync(unusedAdb, "SERIAL", AppId, failing, Path.Combine(Path.GetTempPath(), "never.scene"),
            TimeSpan.FromSeconds(5), CancellationToken.None));
        Check.True(failed.Message.Contains("シーンが読み込まれていません"), $"端末の理由を伝える: {failed.Message}");
        Check.Equal($"SNAPSHOT_SCENE:{AndroidIpcSnapshot.DevicePath(AppId)}", failing.Sent.Single(), "命令の書式");
        Check.Equal(0, failing.Subscribers, "応答の受け手を外す");

        // 応答の前に切れた・時間切れ・送れない
        SnapshotLink? closing = null;
        closing = new SnapshotLink(_ =>
        {
            // 命令を受け取った直後に端末のアプリが終わった
            closing!.Disconnect();
            return null;
        });
        var closed = CatchIpcAsync(AndroidIpcSnapshot.FetchAsync(unusedAdb, "SERIAL", AppId, closing, "x.scene", TimeSpan.FromSeconds(5), CancellationToken.None));
        Check.Equal(AndroidIpcFailureKind.Disconnected, closed.Kind, "応答の前に切れた");
        Check.True(closed.Message.Contains("応答の前に"), $"切れた理由: {closed.Message}");
        var silent = new SnapshotLink(_ => null);
        var timedOut = CatchIpcAsync(AndroidIpcSnapshot.FetchAsync(unusedAdb, "SERIAL", AppId, silent, "x.scene", TimeSpan.FromMilliseconds(50), CancellationToken.None));
        Check.Equal(AndroidIpcFailureKind.NoReply, timedOut.Kind, "時間切れ");
        Check.True(timedOut.Message.Contains("書き出しませんでした"), $"時間切れの理由: {timedOut.Message}");
        var notSent = CatchIpcAsync(AndroidIpcSnapshot.FetchAsync(unusedAdb, "SERIAL", AppId, new SnapshotLink(_ => null) { FailSend = true }, "x.scene",
            TimeSpan.FromSeconds(5), CancellationToken.None));
        Check.Equal(AndroidIpcFailureKind.Disconnected, notSent.Kind, "送れない");

        // SeedAndroid: snapshot（--out を使える・auto は使えない・使い方に載る）
        var snapshot = SeedAndroidArguments.Parse(new[] { "snapshot", "--serial", "2B011JEGR02535", "--out", "a.scene" });
        Check.True(snapshot.Error is null, $"snapshot: {snapshot.Error}");
        Check.Equal(SeedAndroidCommand.Snapshot, snapshot.CommandLine!.Command, "サブコマンド");
        Check.Equal("a.scene", snapshot.CommandLine.OutputPath, "--out");
        Check.True(SeedAndroidArguments.Parse(new[] { "snapshot", "--serial", "auto" }).Error is not null, "auto は使えない");
        Check.True(SeedAndroidArguments.OperatesRunningDevice(SeedAndroidCommand.Snapshot), "動いているアプリだけを操作する");
        Check.True(SeedAndroidArguments.Usage.Contains("snapshot"), "使い方に載せる");
    }

    /// <summary>非同期の AndroidIpcException を受け止める（投げなければ失敗）。</summary>
    private static AndroidIpcException CatchIpcAsync(Task task)
    {
        try
        {
            task.Wait(TimeSpan.FromSeconds(10));
        }
        catch (AggregateException ex) when (ex.InnerException is AndroidIpcException ipc)
        {
            return ipc;
        }
        throw new AssertionException("AndroidIpcException が投げられませんでした");
    }

    /// <summary>送った命令に決めた行を返す偽の通信路（写しのテスト用。端末・adb を使わない）。</summary>
    private sealed class SnapshotLink(Func<string, string?> reply) : IAndroidIpcLink
    {
        /// <summary>切れると完了する。</summary>
        private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>受け手。</summary>
        private Action<string>? _received;

        /// <summary>送った命令。</summary>
        public List<string> Sent { get; } = new();

        /// <summary>送るのを失敗させるか。</summary>
        public bool FailSend { get; init; }

        /// <summary>受け手の数（外し忘れの確認用）。</summary>
        public int Subscribers => _received?.GetInvocationList().Length ?? 0;

        /// <inheritdoc />
        public event Action<string>? MessageReceived
        {
            add => _received += value;
            remove => _received -= value;
        }

        /// <inheritdoc />
        public Task Closed => _closed.Task;

        /// <inheritdoc />
        public bool Send(string command)
        {
            if (FailSend || _closed.Task.IsCompleted) return false;
            Sent.Add(command);
            if (reply(command) is { } line) _received?.Invoke(line);
            return true;
        }

        /// <summary>切れたことにする。</summary>
        public void Disconnect() => _closed.TrySetResult();

        /// <inheritdoc />
        public Task CloseAsync(bool detach)
        {
            _closed.TrySetResult();
            return Task.CompletedTask;
        }
    }

    /// <summary>ポートの決め方。</summary>
    private static void ResolvesDevicePort()
    {
        var defaultPort = AndroidIpcSettings.DefaultDevicePort;
        Check.Equal(52735, defaultPort, "既定のポート");
        Check.True(defaultPort / 10000 != 4, "40000 台（本番の Lore のポートの並び）を避ける");
        Check.Equal<int?>(AndroidIpcSettings.DefaultDevicePort, AndroidIpcSettings.ResolveDevicePort(null), "指定なしは既定");
        Check.Equal<int?>(null, AndroidIpcSettings.ResolveDevicePort(AndroidIpcSettings.DisabledPort), "0 は使わない");
        Check.Equal<int?>(51000, AndroidIpcSettings.ResolveDevicePort(51000), "指定のポート");
        Check.True(AndroidIpcSettings.Validate(null) is null && AndroidIpcSettings.Validate(0) is null && AndroidIpcSettings.Validate(65535) is null, "正しい指定");
        Check.True(AndroidIpcSettings.Validate(-1) is not null && AndroidIpcSettings.Validate(65536) is not null, "範囲外は誤り");
    }

    /// <summary>adb forward の引数と出力。</summary>
    private static void BuildsForwardArguments()
    {
        Check.Equal("forward tcp:0 tcp:52735", string.Join(" ", AdbClient.ForwardArguments(52735)), "PC 側は adb に選ばせる");
        Check.Equal("forward --remove tcp:49537", string.Join(" ", AdbClient.RemoveForwardArguments(49537)), "自分が張った PC 側のポートだけ外す");
        Check.Equal<int?>(49537, AdbClient.ParseForwardedPort("49537\r\n"), "adb が返すのは選んだポートの数字 1 行");
        Check.Equal<int?>(49537, AdbClient.ParseForwardedPort("* daemon started successfully\n49537\n"), "adb のサーバーの起動の行は読み飛ばす");
        Check.Equal<int?>(null, AdbClient.ParseForwardedPort(""), "出力が無ければ読めない");
        Check.Equal<int?>(null, AdbClient.ParseForwardedPort("0\n70000\nerror: device offline"), "0・範囲外・エラーの文言はポートではない");
        Check.Equal("exec-out run-as com.seedengine.runtime cat cache/seed_ipc_screenshot.png",
            string.Join(" ", AdbClient.RunAsReadFileArguments(AppId, AndroidRuntimeContract.RemoteScreenshotPath)), "run-as で読む");
        foreach (var bad in new[] { "", "/data/local/tmp/x.png", "../other/x.png", "cache/../../x" })
        {
            var thrown = false;
            try
            {
                AdbClient.RunAsReadFileArguments(AppId, bad);
            }
            catch (ArgumentException)
            {
                thrown = true;
            }
            Check.True(thrown, $"アプリのデータフォルダの外は読まない: '{bad}'");
        }
    }

    /// <summary>起動の extra。</summary>
    private static void LaunchExtrasCarryIpcPort()
    {
        Check.Equal("seed.ipc_port", AndroidRuntimeContract.IpcPortExtraName, "extra の名前");
        Check.Equal("ipc_port", AndroidRuntimeContract.LaunchOptionIpcPortKey, "runtime/src/engine/platform/launch_options.rs の IPC_PORT_KEY と一致");

        var both = LaunchStep.LaunchExtras("scenes/Main.scene", 52735);
        Check.Equal(2, both.Count, "シーンとポート");
        Check.Equal("seed.scene", both[0].Key, "シーンが先");
        Check.Equal("seed.ipc_port", both[1].Key, "ポート");
        Check.Equal("52735", both[1].Value, "10 進の文字列（Java は文字列の extra だけを渡す）");
        var args = string.Join(" ", AdbClient.AmStartArguments("com.seedengine.runtime/com.seedengine.runtime.MainActivity", both));
        Check.True(args.EndsWith("--es seed.scene 'scenes/Main.scene' --es seed.ipc_port '52735'", StringComparison.Ordinal), $"am start: {args}");

        Check.Equal(1, LaunchStep.LaunchExtras(null, 52735).Count, "開始シーンでもポートは渡す");
        Check.Equal(0, LaunchStep.LaunchExtras(null, null).Count, "ポートを使わない指定なら何も渡さない");
        Check.Equal(0, LaunchStep.LaunchExtras(null).Count, "従来の呼び方（シーンだけ）も使える");
    }

    /// <summary>行の送受信。</summary>
    private static void LineChannelSendsAndReceives()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var client = new TcpClient();
        client.Connect(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        using var server = listener.AcceptTcpClient();
        var serverStream = server.GetStream();

        using var channel = new IpcLineChannel(client.GetStream(), "test");
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var calls = 0;
        channel.MessageReceived += line =>
        {
            // 最初の行で受け手が例外を投げても、次の行は届く
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("受け手の不具合");
            received.Enqueue(line);
        };
        channel.Start();

        var bytes = Encoding.UTF8.GetBytes("READY:0\n  FPS:59.9  \r\nSCREENSHOT_DONE:/data/x.png,1080,2400\n");
        serverStream.Write(bytes);
        WaitUntil(() => received.Count == 2, "2 行届く");
        Check.Equal("FPS:59.9", received.ElementAt(0), "前後の空白と \\r を落とす");

        channel.Send(RuntimeIpcCommands.Pause);
        Check.True(channel.TrySend(RuntimeIpcCommands.Resume), "送れる");
        var reader = new StreamReader(serverStream, Encoding.UTF8);
        Check.Equal("PAUSE", reader.ReadLine(), "1 行 1 命令");
        Check.Equal("RESUME", reader.ReadLine(), "順番どおり");

        server.Close();
        Check.True(channel.Closed.Wait(Wait), "相手が閉じると Closed が完了する");
        Check.True(channel.IsClosed, "閉じた");
    }

    /// <summary>挨拶が届くまでやり直す。</summary>
    private static void HandshakeRetriesUntilGreeting()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        // 偽のランタイム: 1 本目は挨拶せずに閉じる（adb forward の先で誰も待ち受けていない＝起動の途中）、
        // 2 本目は最初の行（HELLO:<トークン>）を読んでから挨拶する
        var fake = Task.Run(() =>
        {
            using (var first = listener.AcceptTcpClient()) { }
            var second = listener.AcceptTcpClient();
            var stream = second.GetStream();
            var hello = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1, leaveOpen: true).ReadLine();
            stream.Write(Encoding.UTF8.GetBytes("READY:0\nFPS:30.0\n"));
            return (second, hello);
        });

        var (client, channel) = AndroidIpcConnector.HandshakeWithRetryAsync(port, 52735, Token, FastTimings, CancellationToken.None).Result;
        using (client)
        using (channel)
        {
            var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
            channel.MessageReceived += received.Enqueue;
            var (second, hello) = fake.Result;
            Check.Equal($"HELLO:{Token}", hello, "最初の行で接続トークンを示す");
            second.GetStream().Write(Encoding.UTF8.GetBytes("SCREENSHOT_ERROR:test\n"));
            WaitUntil(() => received.Contains("SCREENSHOT_ERROR:test"), "挨拶の後の行も同じ受信で届く");
            Check.True(!channel.IsClosed, "つながったまま");
            second.Dispose();
        }
    }

    /// <summary>断られたらやり直さない。</summary>
    private static void HandshakeStopsOnDenial()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var accepted = 0;
        // 偽のランタイム: 最初の行を読んで IPC_DENIED:token を返して閉じる（何度来ても断る）
        using var stop = new CancellationTokenSource();
        var fake = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient connection;
                try
                {
                    connection = await listener.AcceptTcpClientAsync(stop.Token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                using (connection)
                {
                    Interlocked.Increment(ref accepted);
                    var stream = connection.GetStream();
                    new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1, leaveOpen: true).ReadLine();
                    stream.Write(Encoding.UTF8.GetBytes("IPC_DENIED:token\n"));
                    connection.Client.Shutdown(SocketShutdown.Send);
                }
            }
        });

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var error = CatchIpc(() => AndroidIpcConnector.HandshakeWithRetryAsync(port, 52735, "ffffffffffffffffffffffffffffffff", FastTimings, CancellationToken.None).Wait());
        stop.Cancel();
        fake.Wait(Wait);
        Check.Equal(AndroidIpcFailureKind.Denied, error.Kind, "断られた");
        Check.Equal(1, Volatile.Read(ref accepted), "断られたらやり直さない（接続は 1 回だけ）");
        Check.True(watch.Elapsed < FastTimings.ConnectTimeout, $"上限まで待たない（{watch.Elapsed.TotalSeconds:F2} 秒）");
        Check.True(error.Message.Contains("token") && error.Message.Contains("run / push"), $"理由と直し方: {error.Message}");
        Check.True(!error.Message.Contains("ffffffff"), "説明にトークンを含めない");
    }

    /// <summary>トークンの作り方・書式・起動の extra と表示。</summary>
    private static void TokenIsCreatedPassedAndMasked()
    {
        var first = AndroidIpcToken.Create();
        var second = AndroidIpcToken.Create();
        Check.Equal(AndroidIpcToken.RandomByteCount * 2, first.Length, "128 ビットを 16 進で 32 文字");
        Check.True(first != second, "毎回違う");
        Check.True(AndroidIpcToken.IsValid(first) && first.All(c => char.IsAsciiHexDigitLower(c) || char.IsAsciiDigit(c)), "16 進の小文字");
        Check.True(AndroidIpcToken.Validate(null) is null, "指定なしは中核が作るので正しい");
        foreach (var bad in new[] { "short", new string('a', AndroidIpcToken.MaxLength + 1), "0123456789abcdef;rm -rf /", "0123456789abcdef 0123" })
        {
            Check.True(!AndroidIpcToken.IsValid(bad), $"書式の誤り: {bad}");
            Check.True(AndroidIpcToken.Validate(bad) is { } message && !message.Contains(bad), "説明に値を含めない");
        }

        Check.Equal("ipc_token", AndroidRuntimeContract.LaunchOptionIpcTokenKey, "runtime/src/engine/platform/launch_options.rs の IPC_TOKEN_KEY と一致");
        Check.Equal("seed.ipc_token", AndroidRuntimeContract.IpcTokenExtraName, "extra の名前");
        var extras = LaunchStep.LaunchExtras("scenes/Main.scene", 52735, Token);
        Check.Equal("seed.scene,seed.ipc_port,seed.ipc_token", string.Join(",", extras.Select(extra => extra.Key)), "シーン・ポート・トークンの順");
        Check.Equal(Token, extras[2].Value, "実際に渡すのはトークンそのもの");
        Check.Equal(0, LaunchStep.LaunchExtras(null, null, Token).Count, "ポートを使わないならトークンも渡さない");

        var shown = string.Join(" ", AdbClient.AmStartArguments("com.seedengine.runtime/com.seedengine.runtime.MainActivity", LaunchStep.MaskSecrets(extras)));
        Check.True(!shown.Contains(Token) && shown.Contains("--es seed.ipc_token '***'"), $"表示では伏せる: {shown}");
        Check.True(shown.Contains("--es seed.ipc_port '52735'"), "ポートはそのまま");
    }

    /// <summary>起動の記録の往復。</summary>
    private static void RunStateRecordsIpcLaunch()
    {
        using var temp = new TempDir();
        var path = temp.Combine("cache/android/run_state.json");
        var state = new SEEDEditor.Android.State.AndroidRunState();
        state.IpcLaunches["2B011JEGR02535"] = new SEEDEditor.Android.State.AndroidIpcLaunchRecord
        {
            ApplicationId = AppId, IpcPort = 52735, IpcToken = Token, LaunchedAt = DateTimeOffset.Now,
        };
        state.EditorTarget = "auto";
        state.Save(path);

        var loaded = SEEDEditor.Android.State.AndroidRunState.Load(path);
        var record = loaded.IpcLaunchFor("2B011JEGR02535", AppId);
        Check.True(record is not null, "端末とアプリ ID で引ける");
        Check.Equal(Token, record!.IpcToken, "トークン");
        Check.Equal(52735, record.IpcPort, "ポート");
        Check.True(loaded.IpcLaunchFor("2B011JEGR02535", "com.example.other") is null, "別のアプリの記録は使わない");
        Check.True(loaded.IpcLaunchFor("emulator-5554", AppId) is null, "別の端末の記録は使わない");
        Check.Equal("auto", loaded.EditorTarget, "他の項目は保つ");
        Check.True(File.ReadAllText(path).Contains("\"ipc_launches\""), "キーは ipc_launches");

        // 古い記録（ipc_launches の無いもの）も読める
        File.WriteAllText(path, "{ \"format_version\": 1, \"editor_target\": \"pc\" }");
        var old = SEEDEditor.Android.State.AndroidRunState.Load(path);
        Check.Equal("pc", old.EditorTarget, "古い記録の他の項目");
        Check.Equal(0, old.IpcLaunches.Count, "ipc_launches が無ければ空");
    }

    /// <summary>時間内に諦める。</summary>
    private static void HandshakeGivesUpWithReason()
    {
        // 待ち受けていない: 受け付けてすぐ閉じ続ける（adb forward の先に誰もいない）
        using (var listener = new TcpListener(IPAddress.Loopback, 0))
        {
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var stop = new CancellationTokenSource();
            var closer = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    try
                    {
                        using var accepted = await listener.AcceptTcpClientAsync(stop.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            });
            var error = CatchIpc(() => AndroidIpcConnector.HandshakeWithRetryAsync(port, 52735, Token, FastTimings, CancellationToken.None).Wait());
            stop.Cancel();
            closer.Wait(Wait);
            Check.Equal(AndroidIpcFailureKind.NotListening, error.Kind, "閉じられ続けたら「待ち受けていない」");
            Check.True(error.Message.Contains("52735") && error.Message.Contains("APK"), $"何を確かめればよいか: {error.Message}");
        }

        // 挨拶が来ない: 受け付けない（OS の待ち行列で止まる＝別の接続がつながっている）
        using (var busy = new TcpListener(IPAddress.Loopback, 0))
        {
            busy.Start();
            var port = ((IPEndPoint)busy.LocalEndpoint).Port;
            var error = CatchIpc(() => AndroidIpcConnector.HandshakeWithRetryAsync(port, 52735, Token, FastTimings, CancellationToken.None).Wait());
            Check.Equal(AndroidIpcFailureKind.NoGreeting, error.Kind, "挨拶が来なければ「応答しない」");
            Check.True(error.Message.Contains("1 本だけ"), $"別の接続の可能性を伝える: {error.Message}");
        }
    }

    /// <summary>スクリーンショットの応答。</summary>
    private static void ParsesScreenshotReply()
    {
        Check.Equal("/data/user/0/com.seedengine.runtime/cache/seed_ipc_screenshot.png", AndroidIpcScreenshot.DevicePath(AppId),
            "アプリのキャッシュの絶対パス（ランタイムへ渡す）");
        Check.Equal("SCREENSHOT:game,/data/x.png", RuntimeIpcCommands.Screenshot(RuntimeIpcCommands.ScreenshotGameTarget, "/data/x.png"),
            "PC の Play と同じ命令の書式");
        Check.Equal((1080, 2400), AndroidIpcScreenshot.ParseReply("SCREENSHOT_DONE:/data/user/0/a/cache/x.png,1080,2400"), "幅と高さ");
        var error = CatchIpc(() => AndroidIpcScreenshot.ParseReply("SCREENSHOT_ERROR:最小化中は撮れません"));
        Check.True(error.Message.Contains("最小化中は撮れません"), "ランタイムの理由をそのまま伝える");
        CatchIpc(() => AndroidIpcScreenshot.ParseReply("SCREENSHOT_DONE:/x.png,wide,tall"));
        Check.True(RuntimeIpcCommands.IsScreenshotReply("SCREENSHOT_DONE:x,1,1") && RuntimeIpcCommands.IsScreenshotReply("SCREENSHOT_ERROR:x"), "応答の見分け");
        Check.True(!RuntimeIpcCommands.IsScreenshotReply("FPS:60.0") && RuntimeIpcCommands.IsReady("READY:0"), "他の行");
    }

    /// <summary>SeedAndroid の引数。</summary>
    private static void ParsesControlArguments()
    {
        var pause = SeedAndroidArguments.Parse(new[] { "pause", "--serial", "2B011JEGR02535", "--ipc-port", "51000" });
        Check.True(pause.Error is null && pause.CommandLine is not null, $"pause: {pause.Error}");
        Check.Equal(SeedAndroidCommand.Pause, pause.CommandLine!.Command, "サブコマンド");
        Check.Equal<int?>(51000, SeedAndroidArguments.ToRequest(pause.CommandLine, null).IpcPort, "--ipc-port は指定へ入る");
        Check.Equal<int?>(52000, SeedAndroidArguments.ToRequest(
            SeedAndroidArguments.Parse(new[] { "resume" }).CommandLine!, new AndroidRunRequest { IpcPort = 52000 }).IpcPort, "設定 JSON の ipc_port");
        Check.Equal(SeedAndroidCommand.Screenshot, SeedAndroidArguments.Parse(new[] { "screenshot", "--out", "a.png" }).CommandLine!.Command, "screenshot");
        Check.Equal<int?>(0, SeedAndroidArguments.Parse(new[] { "run", "--ipc-port", "0" }).CommandLine!.IpcPort, "run で 0 は使わない指定");

        Check.True(SeedAndroidArguments.Parse(new[] { "pause", "--ipc-port", "abc" }).Error is not null, "数字でない");
        Check.True(SeedAndroidArguments.Parse(new[] { "pause", "--ipc-port", "70000" }).Error is not null, "範囲外");
        Check.True(SeedAndroidArguments.Parse(new[] { "pause", "--out", "a.png" }).Error is not null, "--out は screenshot だけ");
        Check.True(SeedAndroidArguments.Parse(new[] { "screenshot", "--serial", "auto" }).Error is not null, "auto は端末を用意する経路なので使えない");
        Check.True(SeedAndroidArguments.OperatesRunningDevice(SeedAndroidCommand.Resume) && !SeedAndroidArguments.OperatesRunningDevice(SeedAndroidCommand.Run),
            "動いているアプリだけを操作するサブコマンド");
        Check.True(SeedAndroidArguments.Usage.Contains("pause") && SeedAndroidArguments.Usage.Contains("--ipc-port"), "使い方に載せる");
    }

    /// <summary>AndroidIpcException を受け止める（投げなければ失敗）。</summary>
    private static AndroidIpcException CatchIpc(Action body)
    {
        try
        {
            body();
        }
        catch (AggregateException ex) when (ex.InnerException is AndroidIpcException inner)
        {
            return inner;
        }
        catch (AndroidIpcException ex)
        {
            return ex;
        }
        throw new AssertionException("AndroidIpcException が投げられませんでした");
    }

    /// <summary>条件が満たされるまで待つ（満たされなければ失敗）。</summary>
    private static void WaitUntil(Func<bool> condition, string what)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.Elapsed > Wait) throw new AssertionException($"{Wait.TotalSeconds} 秒以内に {what} になりません");
            Thread.Sleep(5);
        }
    }
}
