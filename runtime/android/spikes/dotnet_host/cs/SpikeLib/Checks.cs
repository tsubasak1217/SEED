// =============================================================================
// Checks.cs
// Android(linux-bionic) 上の CoreCLR で、SEED の ScriptAssemblyManager / ScriptBridge が
// 依存している機能（リフレクション・collectible ALC・ジェネリクス・JIT など）が動くかを 1 項目ずつ確認する。
// =============================================================================
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SpikeLib;

/// <summary>検証項目の本体。RunAll が全項目を順に実行してレポート文字列を返す。</summary>
internal static class Checks
{
    // ── プラグイン（collectible ALC）検証の定数 ──
    /// <summary>collectible ALC へ読み込む DLL のファイル名（SpikeLib.dll と同じフォルダに置く）。</summary>
    private const string PluginFileName = "SpikePlugin.dll";
    /// <summary>collectible ALC の名前。</summary>
    private const string PluginContextName = "SpikePluginContext";
    /// <summary>1 回目の Tick 入力。</summary>
    private const int PluginTickInput1 = 20;
    /// <summary>1 回目の期待値 = 20 * Speed(2.0) + Bias(1) + (履歴数1 - 1)。</summary>
    private const int PluginTickExpected1 = 41;
    /// <summary>リフレクションで上書きする Speed。</summary>
    private const float PluginSpeedOverride = 3.5f;
    /// <summary>2 回目の Tick 入力。</summary>
    private const int PluginTickInput2 = 10;
    /// <summary>2 回目の期待値 = 10 * 3.5 + 1 + (履歴数2 - 1)。</summary>
    private const int PluginTickExpected2 = 37;
    /// <summary>Unload 後に GC を回す最大回数。</summary>
    private const int MaxUnloadGcAttempts = 10;

    // ── 文字列・数値の検証データ ──
    /// <summary>UTF-8 往復に使う文字列（日本語・絵文字・ダイアクリティカル・半角カナを含む）。</summary>
    private const string Utf8Sample = "日本語テスト🐟 Ünïcödé ｶﾀｶﾅ";
    /// <summary>NFKC 正規化の入力（半角カナ濁点＋丸数字）。</summary>
    private const string NormalizeInput = "ｶﾞｷﾞ①";
    /// <summary>NFKC 正規化の期待値。</summary>
    private const string NormalizeExpected = "ガギ1";
    /// <summary>ジェネリクス検証で List に与える初期容量。</summary>
    private const int GenericListCapacity = 8;
    /// <summary>ジェネリクス検証で List に追加する要素数。</summary>
    private const int GenericListItemCount = 3;
    /// <summary>計算ループの反復回数（JIT されたネイティブコードかどうかの目安）。</summary>
    private const int ComputeLoopIterations = 20_000_000;
    /// <summary>GC 検証で確保する配列の数。</summary>
    private const int GcAllocationCount = 32;
    /// <summary>GC 検証で確保する配列 1 個のサイズ（1 MiB）。</summary>
    private const int GcAllocationBytes = 1 << 20;
    /// <summary>バイト → MiB の換算。</summary>
    private const double BytesPerMiB = 1024.0 * 1024.0;
    /// <summary>Parallel.For の上限（0..N-1 の総和を確認する）。</summary>
    private const int ParallelRange = 1000;
    /// <summary>Task.Delay の待ち時間。</summary>
    private const int DelayMs = 10;
    /// <summary>Task.Delay 完了待ちの上限。</summary>
    private static readonly TimeSpan DelayTimeout = TimeSpan.FromSeconds(5);

    // ── 環境情報 ──
    /// <summary>ロード済みライブラリの確認に使う procfs。</summary>
    private const string ProcMapsPath = "/proc/self/maps";
    /// <summary>表示する AppContext のキー（hostpolicy が設定するランタイムプロパティ）。</summary>
    private static readonly string[] AppContextKeys =
    {
        "RUNTIME_IDENTIFIER", "FX_DEPS_FILE", "APP_CONTEXT_DEPS_FILES", "APP_PATHS", "PROBING_DIRECTORIES",
        "NATIVE_DLL_SEARCH_DIRECTORIES", "PLATFORM_RESOURCE_ROOTS", "HOST_RUNTIME_CONTRACT",
        "System.Globalization.Invariant", "System.Globalization.PredefinedCulturesOnly",
        "System.GC.Server", "System.GC.Concurrent", "System.Runtime.TieredCompilation", "System.Runtime.TieredPGO",
    };
    /// <summary>表示する環境変数の接頭辞。</summary>
    private static readonly string[] EnvPrefixes = { "DOTNET_", "COREHOST_", "LD_", "TMPDIR", "HOME", "LANG", "LC_", "ANDROID_" };

    /// <summary>プラグイン DLL と作業ファイルを置くフォルダを上書きする環境変数。</summary>
    private const string WorkDirEnvName = "SPIKE_PLUGIN_DIR";

    /// <summary>
    /// プラグイン DLL・作業ファイルのフォルダ。
    /// バイト列から読み込んだ場合は Assembly.Location が空になるので、環境変数 → Location → BaseDirectory の順で決める。
    /// </summary>
    private static string WorkDirectory()
    {
        string? fromEnv = Environment.GetEnvironmentVariable(WorkDirEnvName);
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;
        string location = typeof(Checks).Assembly.Location;
        if (!string.IsNullOrEmpty(location)) return Path.GetDirectoryName(location) ?? AppContext.BaseDirectory;
        return AppContext.BaseDirectory;
    }

    /// <summary>実行する暗号チェックを選ぶ環境変数（"sha" / "rng" を含めると実行）。</summary>
    private const string CryptoEnvName = "SPIKE_CRYPTO";

    /// <summary>RunAll の呼び出し回数（1 回目と 2 回目の所要時間を比べるため）。</summary>
    private static int s_runCount;

    /// <summary>全チェックを実行してレポートを返す。</summary>
    public static string RunAll()
    {
        int run = Interlocked.Increment(ref s_runCount);
        var r = new CheckRunner();
        r.Info("run", run);
        AddRuntimeInfo(r);

        r.Run("reflection.GetTypes", CheckGetTypes);
        r.Run("reflection.CreateInstance", CheckCreateInstance);
        r.Run("reflection.GetFields.getset", CheckFields);
        r.Run("generics.MakeGenericType(List<struct>)", CheckMakeGenericType);
        // 読み込み～呼び出し（機能面）と、Unload 後に本当に回収されたか（メモリ面）を別項目で判定する
        PluginRunResult plugin = default;
        r.Run("alc.collectible.LoadFromStream+call", () => CheckCollectibleAlcLoad(out plugin));
        r.Run("alc.collectible.Unload(collected)", () => CheckCollectibleAlcUnload(plugin));
        r.Run("utf8.roundtrip", CheckUtf8RoundTrip);
        r.Run("console.WriteLine", () => CheckConsole(run));
        r.Run("logcat.__android_log_write", () => AndroidLog.Write(AndroidLog.PriorityInfo, $"hello from C# (SpikeLib) run={run} 日本語"));
        r.Run("pinvoke.libc.getpid", CheckLibcPInvoke);
        r.Run("time.DateTime.Now", CheckDateTimeNow);
        r.Run("time.FindSystemTimeZoneById(Asia/Tokyo)", CheckTimeZoneLookup);
        r.Run("culture.CurrentCulture", CheckCurrentCulture);
        r.Run("culture.GetCultureInfo(ja-JP)", CheckJapaneseCulture);
        r.Run("string.Normalize(FormKC)", CheckNormalize);
        r.Run("exception.managed.throw/filter/finally", CheckManagedException);
        r.Run("jit.DynamicMethod(Reflection.Emit)", CheckDynamicMethod);
        r.Run("jit.Expression.Compile", CheckExpressionCompile);
        r.Run("jit.computeLoop", CheckComputeLoop);
        r.Run("threads.Thread/Task/Parallel", CheckThreads);
        r.Run("gc.collect", CheckGc);
        r.Run("io.file.roundtrip", CheckFileIo);
        r.Run("env.paths", CheckEnvironmentPaths);
        // 暗号系はランタイム実装によってはプロセスごと落ちる（捕捉不能）ため、環境変数で選んだものだけ実行する
        string crypto = Environment.GetEnvironmentVariable(CryptoEnvName) ?? "";
        if (crypto.Contains("sha", StringComparison.Ordinal)) r.Run("crypto.SHA256", CheckSha256);
        if (crypto.Contains("rng", StringComparison.Ordinal)) r.Run("crypto.RandomNumberGenerator", CheckRandom);
        r.Run("random.Random.Shared+Guid.NewGuid", CheckNonCryptoRandom);
        r.Run("compression.Deflate", CheckDeflate);
        r.Run("json.System.Text.Json", CheckJson);

        // JIT が実際に動いた量（メソッド数・IL バイト数・コンパイル時間）
        r.Info("JitInfo.CompiledMethodCount", JitInfo.GetCompiledMethodCount(currentThread: false));
        r.Info("JitInfo.CompiledILBytes", JitInfo.GetCompiledILBytes(currentThread: false));
        r.Info("JitInfo.CompilationTimeMs", JitInfo.GetCompilationTime(currentThread: false).TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture));
        AddLoadedLibraryInfo(r);
        return r.Finish();
    }

    // ─────────────────────────────────────────────────────────────────────
    // 環境情報
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>ランタイム・OS・ホストが設定したプロパティ・環境変数を記録する。</summary>
    private static void AddRuntimeInfo(CheckRunner r)
    {
        r.Info("Environment.Version", Environment.Version);
        r.Info("FrameworkDescription", RuntimeInformation.FrameworkDescription);
        r.Info("OSDescription", RuntimeInformation.OSDescription);
        r.Info("RuntimeIdentifier", RuntimeInformation.RuntimeIdentifier);
        r.Info("ProcessArchitecture", RuntimeInformation.ProcessArchitecture);
        r.Info("OSArchitecture", RuntimeInformation.OSArchitecture);
        r.Info("OperatingSystem.IsAndroid", OperatingSystem.IsAndroid());
        r.Info("OperatingSystem.IsLinux", OperatingSystem.IsLinux());
        r.Info("ProcessorCount", Environment.ProcessorCount);
        r.Info("ProcessId", Environment.ProcessId);
        r.Info("ProcessPath", Environment.ProcessPath);
        r.Info("CoreLib.Location", typeof(object).Assembly.Location);
        r.Info("SpikeLib.Location", typeof(Checks).Assembly.Location);
        r.Info("SpikeLib.ALC", AssemblyLoadContext.GetLoadContext(typeof(Checks).Assembly));
        r.Info("AppContext.BaseDirectory", AppContext.BaseDirectory);
        foreach (string key in AppContextKeys)
        {
            r.Info($"AppContext[{key}]", AppContext.GetData(key));
        }
        string? tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        string[] tpaEntries = tpa?.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries) ?? Array.Empty<string>();
        r.Info("TPA.count", tpaEntries.Length);
        r.Info("TPA.first", tpaEntries.FirstOrDefault());
        r.Info("GCSettings.IsServerGC", GCSettings.IsServerGC);
        r.Info("GCSettings.LatencyMode", GCSettings.LatencyMode);
        r.Info("RuntimeFeature.IsDynamicCodeSupported", RuntimeFeature.IsDynamicCodeSupported);
        r.Info("RuntimeFeature.IsDynamicCodeCompiled", RuntimeFeature.IsDynamicCodeCompiled);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            string key = e.Key?.ToString() ?? "";
            if (EnvPrefixes.Any(p => key.StartsWith(p, StringComparison.Ordinal)))
            {
                r.Info("env." + key, e.Value);
            }
        }
    }

    /// <summary>
    /// /proc/self/maps から、実際にどのパスの .so が読み込まれたか（LD_LIBRARY_PATH 無しで解決できたか）と、
    /// 実行可能な匿名/二重マップ領域（JIT コードヒープの痕跡）を記録する。
    /// </summary>
    private static void AddLoadedLibraryInfo(CheckRunner r)
    {
        try
        {
            var libs = new SortedSet<string>(StringComparer.Ordinal);
            int execAnonymous = 0;
            int execDoubleMapped = 0;
            foreach (string line in File.ReadLines(ProcMapsPath))
            {
                // 形式: address perms offset dev inode [path]
                string[] parts = line.Split(' ', 6, StringSplitOptions.RemoveEmptyEntries);
                string perms = parts.Length > 1 ? parts[1] : "";
                string path = parts.Length >= 6 ? parts[5].Trim() : "";
                if (perms.Contains('x'))
                {
                    if (path.Length == 0) execAnonymous++;
                    else if (path.Contains("doublemapper", StringComparison.Ordinal)) execDoubleMapped++;
                }
                if (path.EndsWith(".so", StringComparison.Ordinal))
                {
                    libs.Add(path);
                }
            }
            r.Info("maps.execAnonymousRegions", execAnonymous);
            r.Info("maps.execDoubleMapperRegions", execDoubleMapped);
            r.Info("maps.sharedObjects.count", libs.Count);
            foreach (string lib in libs)
            {
                // システム標準のライブラリは量が多いので、.NET 関連・ICU・ログ・C++ 標準ライブラリ・アプリ配下だけ出す
                bool isSystem = lib.StartsWith("/system/", StringComparison.Ordinal) || lib.StartsWith("/apex/", StringComparison.Ordinal)
                                || lib.StartsWith("/vendor/", StringComparison.Ordinal);
                bool interesting = !isSystem || lib.Contains("icu", StringComparison.Ordinal) || lib.Contains("liblog", StringComparison.Ordinal)
                                   || lib.Contains("c++", StringComparison.Ordinal);
                if (interesting) r.Info("maps.so", lib);
            }
        }
        catch (Exception ex)
        {
            r.Info("maps.error", ex.Message);
        }
    }

    // ─────────────────────────────────────────────────────────────────────
    // リフレクション・ジェネリクス
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Assembly.GetTypes が全型を返すか。</summary>
    private static (bool, string) CheckGetTypes()
    {
        Type[] types = typeof(Checks).Assembly.GetTypes();
        bool hasComponent = types.Contains(typeof(SampleComponent));
        return (hasComponent, $"types={types.Length} hasSampleComponent={hasComponent}");
    }

    /// <summary>Activator.CreateInstance（型オブジェクト経由・型名解決経由）でインスタンスを作れるか。</summary>
    private static (bool, string) CheckCreateInstance()
    {
        object a = Activator.CreateInstance(typeof(SampleComponent))!;
        Type? byName = typeof(Checks).Assembly.GetType("SpikeLib.SampleComponent");
        object? b = byName is null ? null : Activator.CreateInstance(byName);
        bool ok = a is SampleComponent && b is IPluginComponent;
        return (ok, $"a={a.GetType().Name} b={b?.GetType().Name ?? "null"}");
    }

    /// <summary>GetFields で得た FieldInfo の SetValue/GetValue（float/int/string/struct）が往復するか。</summary>
    private static (bool, string) CheckFields()
    {
        const float speed = 4.25f;
        const int counter = 7;
        const string label = "ラベル";
        var offset = new SampleStruct(1, 2, 3);

        object comp = Activator.CreateInstance(typeof(SampleComponent))!;
        FieldInfo[] fields = typeof(SampleComponent).GetFields(BindingFlags.Public | BindingFlags.Instance);
        Dictionary<string, FieldInfo> byName = fields.ToDictionary(f => f.Name, StringComparer.Ordinal);
        byName[nameof(SampleComponent.Speed)].SetValue(comp, speed);
        byName[nameof(SampleComponent.Counter)].SetValue(comp, counter);
        byName[nameof(SampleComponent.Label)].SetValue(comp, label);
        byName[nameof(SampleComponent.Offset)].SetValue(comp, offset);

        float gotSpeed = (float)byName[nameof(SampleComponent.Speed)].GetValue(comp)!;
        int gotCounter = (int)byName[nameof(SampleComponent.Counter)].GetValue(comp)!;
        string gotLabel = (string)byName[nameof(SampleComponent.Label)].GetValue(comp)!;
        var gotOffset = (SampleStruct)byName[nameof(SampleComponent.Offset)].GetValue(comp)!;
        int tick = ((IPluginComponent)comp).Tick(1);

        bool ok = gotSpeed == speed && gotCounter == counter && gotLabel == label && gotOffset.Z == offset.Z && tick == 1 + counter;
        return (ok, $"fields={fields.Length} speed={gotSpeed} counter={gotCounter} label={gotLabel} offset={gotOffset} tick={tick}");
    }

    /// <summary>
    /// typeof(List&lt;&gt;).MakeGenericType(値型) → Activator.CreateInstance(型, 引数配列) → リフレクションで Add。
    /// 値型ジェネリクスの実行時インスタンス化は JIT が無いと動かない代表例。
    /// </summary>
    private static (bool, string) CheckMakeGenericType()
    {
        Type listType = typeof(List<>).MakeGenericType(typeof(SampleStruct));
        object list = Activator.CreateInstance(listType, new object[] { GenericListCapacity })!;
        MethodInfo add = listType.GetMethod("Add")!;
        for (int i = 0; i < GenericListItemCount; i++)
        {
            add.Invoke(list, new object[] { new SampleStruct(i, i * 2, i * 3) });
        }
        int count = (int)listType.GetProperty("Count")!.GetValue(list)!;
        int capacity = (int)listType.GetProperty("Capacity")!.GetValue(list)!;
        float sumZ = 0;
        foreach (object item in (IEnumerable)list)
        {
            sumZ += ((SampleStruct)item).Z;
        }

        Type dictType = typeof(Dictionary<,>).MakeGenericType(typeof(int), typeof(SampleStruct));
        var dict = (IDictionary)Activator.CreateInstance(dictType)!;
        dict[1] = new SampleStruct(1, 2, 3);

        const float expectedSumZ = 0 + 3 + 6;
        bool ok = count == GenericListItemCount && capacity == GenericListCapacity && sumZ == expectedSumZ && dict.Count == 1;
        return (ok, $"{listType.Name}[{listType.GenericTypeArguments[0].Name}] count={count} capacity={capacity} sumZ={sumZ} dict={dictType.Name} dictCount={dict.Count}");
    }

    // ─────────────────────────────────────────────────────────────────────
    // collectible AssemblyLoadContext（ScriptAssemblyManager の再現）
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>プラグイン読み込み結果（Unload 判定へ渡す弱参照）。</summary>
    private struct PluginRunResult
    {
        /// <summary>ALC オブジェクトへの弱参照。</summary>
        public WeakReference? AlcRef;

        /// <summary>
        /// プラグインの Assembly オブジェクトへの弱参照。
        /// ALC の管理オブジェクトだけは Unload が効かないランタイム（.NET 9 の Mono）でも回収されうるため、
        /// 「アセンブリが本当に解放されたか」はこちらで判定する。
        /// </summary>
        public WeakReference? AssemblyRef;
    }

    /// <summary>
    /// SpikePlugin.dll をバイト列で collectible ALC に LoadFromStream → 型探索 → 生成 → 呼び出し → フィールド書き換え → Unload 要求。
    /// </summary>
    private static (bool, string) CheckCollectibleAlcLoad(out PluginRunResult result)
    {
        result = default;
        string path = Path.Combine(WorkDirectory(), PluginFileName);
        if (!File.Exists(path)) return (false, "plugin not found: " + path);

        (WeakReference alcRef, WeakReference asmRef, bool ok, string detail) = LoadRunUnloadPlugin(path);
        result = new PluginRunResult { AlcRef = alcRef, AssemblyRef = asmRef };
        return (ok, detail);
    }

    /// <summary>Unload 後に GC を回し、ALC とプラグインの Assembly が実際に回収されたかを見る。</summary>
    private static (bool, string) CheckCollectibleAlcUnload(PluginRunResult plugin)
    {
        if (plugin.AlcRef is null || plugin.AssemblyRef is null) return (false, "load step failed");
        var sw = Stopwatch.StartNew();
        int attempts = 0;
        while ((plugin.AlcRef.IsAlive || plugin.AssemblyRef.IsAlive) && attempts < MaxUnloadGcAttempts)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            attempts++;
        }
        bool alcCollected = !plugin.AlcRef.IsAlive;
        bool assemblyCollected = !plugin.AssemblyRef.IsAlive;
        return (assemblyCollected, $"alcObjectCollected={alcCollected} assemblyCollected={assemblyCollected} gcAttempts={attempts} ms={sw.Elapsed.TotalMilliseconds:F1}");
    }

    /// <summary>
    /// ALC への強参照がこのメソッドの外へ漏れないよう、読み込み～Unload 要求までを閉じ込める（インライン化禁止）。
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference alcRef, WeakReference asmRef, bool ok, string detail) LoadRunUnloadPlugin(string path)
    {
        var sw = Stopwatch.StartNew();
        byte[] bytes = File.ReadAllBytes(path);
        double readMs = sw.Elapsed.TotalMilliseconds;

        var alc = new AssemblyLoadContext(PluginContextName, isCollectible: true);
        alc.Resolving += ResolveFromLoadedAssemblies;
        Assembly asm;
        using (var ms = new MemoryStream(bytes, writable: false))
        {
            asm = alc.LoadFromStream(ms);
        }
        double loadMs = sw.Elapsed.TotalMilliseconds - readMs;

        Type[] types = asm.GetTypes();
        Type compType = types.First(t => typeof(IPluginComponent).IsAssignableFrom(t) && !t.IsAbstract && !t.IsInterface);
        var comp = (IPluginComponent)Activator.CreateInstance(compType)!;
        int r1 = comp.Tick(PluginTickInput1);
        FieldInfo speed = compType.GetField("Speed", BindingFlags.Public | BindingFlags.Instance)!;
        speed.SetValue(comp, PluginSpeedOverride);
        int r2 = comp.Tick(PluginTickInput2);
        double runMs = sw.Elapsed.TotalMilliseconds - readMs - loadMs;

        bool ok = r1 == PluginTickExpected1 && r2 == PluginTickExpected2;
        string detail = $"bytes={bytes.Length} types={types.Length} comp={comp.Name} r1={r1}/{PluginTickExpected1} r2={r2}/{PluginTickExpected2} " +
                        $"pluginALC={AssemblyLoadContext.GetLoadContext(asm)?.Name} readMs={readMs:F1} loadMs={loadMs:F1} runMs={runMs:F1}";
        alc.Unload();
        return (new WeakReference(alc), new WeakReference(asm), ok, detail);
    }

    /// <summary>
    /// プラグインが参照する SpikeLib を、プロセス内で読み込み済みの同名アセンブリへ解決する
    /// （SEED の CreateLoadContext の Resolving と同じ方針。型の同一性を保つため）。
    /// </summary>
    private static Assembly? ResolveFromLoadedAssemblies(AssemblyLoadContext context, AssemblyName name) =>
        AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => string.Equals(a.GetName().Name, name.Name, StringComparison.Ordinal));

    // ─────────────────────────────────────────────────────────────────────
    // 文字列・コンソール・ログ・P/Invoke
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>UTF-8 のエンコード/デコード往復。</summary>
    private static (bool, string) CheckUtf8RoundTrip()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(Utf8Sample);
        string back = Encoding.UTF8.GetString(bytes);
        int runes = Utf8Sample.EnumerateRunes().Count();
        bool ok = back == Utf8Sample;
        return (ok, $"bytes={bytes.Length} utf16Chars={Utf8Sample.Length} runes={runes}");
    }

    /// <summary>Console.WriteLine / Console.Error.WriteLine の出力先（adb shell の出力に出るか）を確認する。</summary>
    private static (bool, string) CheckConsole(int run)
    {
        Console.WriteLine($"[SpikeLib] Console.WriteLine marker (stdout) run={run} 日本語");
        Console.Out.Flush();
        Console.Error.WriteLine($"[SpikeLib] Console.Error.WriteLine marker (stderr) run={run}");
        Console.Error.Flush();
        return (true, $"IsOutputRedirected={Console.IsOutputRedirected} IsErrorRedirected={Console.IsErrorRedirected} " +
                      $"OutputEncoding={Console.OutputEncoding.WebName} OutType={Console.Out.GetType().Name}");
    }

    /// <summary>DllImport("libc") が bionic の libc.so に解決されるか。</summary>
    private static (bool, string) CheckLibcPInvoke()
    {
        int pid = NativeMethods.GetPid();
        return (pid == Environment.ProcessId, $"getpid={pid} Environment.ProcessId={Environment.ProcessId}");
    }

    // ─────────────────────────────────────────────────────────────────────
    // 時刻・カルチャ
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>DateTime.Now（ローカルタイムゾーンの解決）が例外を出さないか。</summary>
    private static (bool, string) CheckDateTimeNow()
    {
        DateTime now = DateTime.Now;
        DateTime utc = DateTime.UtcNow;
        TimeZoneInfo local = TimeZoneInfo.Local;
        return (true, $"Now={now.ToString("O", CultureInfo.InvariantCulture)} UtcNow={utc.ToString("O", CultureInfo.InvariantCulture)} " +
                      $"Local.Id={local.Id} offset={local.GetUtcOffset(now)}");
    }

    /// <summary>IANA 名でタイムゾーンを引けるか（Android の tzdata を読めるか）。</summary>
    private static (bool, string) CheckTimeZoneLookup()
    {
        TimeZoneInfo tokyo = TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo");
        int zoneCount = TimeZoneInfo.GetSystemTimeZones().Count;
        bool ok = tokyo.BaseUtcOffset == TimeSpan.FromHours(9);
        return (ok, $"id={tokyo.Id} base={tokyo.BaseUtcOffset} systemZones={zoneCount}");
    }

    /// <summary>CurrentCulture での書式化・大文字化・比較が例外を出さないか。</summary>
    private static (bool, string) CheckCurrentCulture()
    {
        CultureInfo cc = CultureInfo.CurrentCulture;
        string number = 1234567.891.ToString("N2", cc);
        string date = new DateTime(2026, 9, 24, 15, 30, 0).ToString("F", cc);
        string upper = "straße äöü".ToUpper(cc);
        string lowerInv = "ÄÖÜ".ToLowerInvariant();
        int cmp = string.Compare("apple", "Banana", StringComparison.CurrentCulture);
        int cmpIgnoreCase = string.Compare("abc", "ABC", StringComparison.CurrentCultureIgnoreCase);
        bool invariant = AppContext.TryGetSwitch("System.Globalization.Invariant", out bool v) && v;
        return (true, $"Current='{cc.Name}' UI='{CultureInfo.CurrentUICulture.Name}' invariantSwitch={invariant} N2={number} F={date} " +
                      $"upper={upper} lowerInv={lowerInv} cmp(apple,Banana)={cmp} cmpIgnoreCase={cmpIgnoreCase}");
    }

    /// <summary>ja-JP カルチャを作れるか（Invariant モードでは作れないのが仕様）。</summary>
    private static (bool, string) CheckJapaneseCulture()
    {
        CultureInfo ja = CultureInfo.GetCultureInfo("ja-JP");
        string date = new DateTime(2026, 9, 24).ToString("D", ja);
        string currency = 1234.5m.ToString("C", ja);
        return (true, $"name={ja.Name} display={ja.DisplayName} D={date} C={currency}");
    }

    /// <summary>Unicode 正規化（NFKC）。</summary>
    private static (bool, string) CheckNormalize()
    {
        string kc = NormalizeInput.Normalize(NormalizationForm.FormKC);
        return (kc == NormalizeExpected, $"in={NormalizeInput} out={kc}");
    }

    // ─────────────────────────────────────────────────────────────────────
    // 例外・JIT
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>マネージド例外の throw → フィルタ → finally → catch の順序とスタックトレース（アンワインダの確認）。</summary>
    private static (bool, string) CheckManagedException()
    {
        var order = new List<string>();
        string? stack = null;
        try
        {
            try
            {
                order.Add("try");
                ThrowForCheck();
            }
            finally
            {
                order.Add("finally");
            }
        }
        catch (InvalidOperationException ex) when (Record(order, "filter"))
        {
            order.Add("catch");
            stack = ex.StackTrace;
        }
        string sequence = string.Join(">", order);
        bool hasFrame = stack?.Contains(nameof(ThrowForCheck), StringComparison.Ordinal) == true;
        // 2 パス方式の例外処理ではフィルタが finally より先に走る
        bool ok = sequence == "try>filter>finally>catch" && hasFrame;
        return (ok, $"order={sequence} stackHasThrowFrame={hasFrame}");
    }

    /// <summary>例外フィルタから呼ぶ記録用（常に true）。</summary>
    private static bool Record(List<string> order, string step)
    {
        order.Add(step);
        return true;
    }

    /// <summary>スタックトレースに現れることを確認するため、インライン化を禁止して投げる。</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowForCheck() => throw new InvalidOperationException("spike");

    /// <summary>Reflection.Emit の DynamicMethod を生成・実行できるか（= 実行時に IL をネイティブ化できる）。</summary>
    private static (bool, string) CheckDynamicMethod()
    {
        long before = JitInfo.GetCompiledMethodCount(currentThread: false);
        var dm = new DynamicMethod("SpikeDynamicMulAdd", typeof(int), new[] { typeof(int), typeof(int) }, typeof(Checks).Module);
        ILGenerator il = dm.GetILGenerator();
        // return a + b * 2;
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I4_2);
        il.Emit(OpCodes.Mul);
        il.Emit(OpCodes.Add);
        il.Emit(OpCodes.Ret);
        var fn = dm.CreateDelegate<Func<int, int, int>>();
        int result = fn(3, 4);
        long after = JitInfo.GetCompiledMethodCount(currentThread: false);
        const int expected = 11;
        return (result == expected && after > before, $"result={result}/{expected} jitCompiledMethods {before}->{after}");
    }

    /// <summary>式木のコンパイル（preferInterpretation=false）が本当にコンパイルされるか。</summary>
    private static (bool, string) CheckExpressionCompile()
    {
        ParameterExpression x = Expression.Parameter(typeof(int), "x");
        Expression<Func<int, int>> lambda = Expression.Lambda<Func<int, int>>(
            Expression.Add(Expression.Multiply(x, Expression.Constant(3)), Expression.Constant(1)), x);
        Func<int, int> fn = lambda.Compile(preferInterpretation: false);
        int result = fn(7);
        const int expected = 22;
        // コンパイル時は Closure、インタプリタ時は LightLambda が Target になる
        string target = fn.Target?.GetType().Name ?? "null";
        return (result == expected, $"result={result}/{expected} delegateTarget={target}");
    }

    /// <summary>単純な計算ループの所要時間（ネイティブ実行の目安。1 反復あたり ns を出す）。</summary>
    private static (bool, string) CheckComputeLoop()
    {
        var sw = Stopwatch.StartNew();
        long acc = 0;
        for (int i = 0; i < ComputeLoopIterations; i++)
        {
            acc += (i ^ (i >> 3)) & 0xFF;
        }
        sw.Stop();
        double nsPerIter = sw.Elapsed.TotalMilliseconds * 1_000_000.0 / ComputeLoopIterations;
        return (acc > 0, $"iterations={ComputeLoopIterations} ms={sw.Elapsed.TotalMilliseconds:F1} nsPerIter={nsPerIter:F2}");
    }

    // ─────────────────────────────────────────────────────────────────────
    // スレッド・GC・IO・環境
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Thread 生成、スレッドプール（Task.Run）、Parallel.For、タイマ（Task.Delay）が動くか。</summary>
    private static (bool, string) CheckThreads()
    {
        int counter = 0;
        var thread = new Thread(() => Interlocked.Increment(ref counter)) { Name = "SpikeWorker" };
        thread.Start();
        thread.Join();
        const int expectedTask = 42;
        int taskResult = Task.Run(() => expectedTask).GetAwaiter().GetResult();
        long sum = 0;
        Parallel.For(0, ParallelRange, i => Interlocked.Add(ref sum, i));
        bool delayed = Task.Delay(DelayMs).Wait(DelayTimeout);
        ThreadPool.GetMinThreads(out int minWorkers, out _);
        ThreadPool.GetMaxThreads(out int maxWorkers, out _);
        long expectedSum = (long)ParallelRange * (ParallelRange - 1) / 2;
        bool ok = counter == 1 && taskResult == expectedTask && sum == expectedSum && delayed;
        return (ok, $"thread={counter} task={taskResult} parallelSum={sum}/{expectedSum} delay={delayed} pool(min={minWorkers},max={maxWorkers})");
    }

    /// <summary>大きめの確保 → 解放 → GC.Collect でメモリが戻るか。GC の構成も記録する。</summary>
    private static (bool, string) CheckGc()
    {
        long before = GC.GetTotalMemory(forceFullCollection: false);
        int gen2Before = GC.CollectionCount(2);
        var junk = new List<byte[]>(GcAllocationCount);
        for (int i = 0; i < GcAllocationCount; i++) junk.Add(new byte[GcAllocationBytes]);
        long peak = GC.GetTotalMemory(forceFullCollection: false);
        junk.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        long after = GC.GetTotalMemory(forceFullCollection: true);
        GCMemoryInfo info = GC.GetGCMemoryInfo();
        bool ok = after < peak;
        return (ok, $"before={before / BytesPerMiB:F1}MiB peak={peak / BytesPerMiB:F1}MiB after={after / BytesPerMiB:F1}MiB " +
                    $"gen2 {gen2Before}->{GC.CollectionCount(2)} totalAvailable={info.TotalAvailableMemoryBytes / BytesPerMiB:F0}MiB " +
                    $"server={GCSettings.IsServerGC} concurrent={AppContext.GetData("System.GC.Concurrent") ?? "(default)"}");
    }

    /// <summary>SpikeLib.dll と同じフォルダへの UTF-8 ファイル書き込み・読み戻し・削除。</summary>
    private static (bool, string) CheckFileIo()
    {
        string dir = WorkDirectory();
        string file = Path.Combine(dir, "spike_io_check.tmp");
        File.WriteAllText(file, Utf8Sample, Encoding.UTF8);
        string back = File.ReadAllText(file, Encoding.UTF8);
        File.Delete(file);
        return (back == Utf8Sample, $"dir={dir} roundtrip={back == Utf8Sample}");
    }

    /// <summary>一時フォルダ・ホーム・特殊フォルダの解決結果（段階B でアプリの private dir に向け直す必要があるかの判断材料）。</summary>
    private static (bool, string) CheckEnvironmentPaths()
    {
        string temp = Path.GetTempPath();
        static string Folder(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        return (true, $"GetTempPath={temp} tempExists={Directory.Exists(temp)} HOME={Environment.GetEnvironmentVariable("HOME")} " +
                      $"CWD={Environment.CurrentDirectory} LocalAppData={Folder(Environment.SpecialFolder.LocalApplicationData)} " +
                      $"AppData={Folder(Environment.SpecialFolder.ApplicationData)} UserProfile={Folder(Environment.SpecialFolder.UserProfile)} " +
                      $"UserName={Environment.UserName}");
    }
    // ─────────────────────────────────────────────────────────────────────
    // 暗号・圧縮・JSON（ネイティブ依存の有無がランタイム実装で異なる）
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>SHA256（linux-bionic の Mono は OpenSSL、Android の CoreCLR は JNI 経由の実装を使う）。</summary>
    private static (bool, string) CheckSha256()
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("abc"));
        string hex = Convert.ToHexString(hash);
        const string expected = "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD";
        return (hex == expected, $"sha256(abc)={hex[..16]}...");
    }

    /// <summary>暗号論的乱数（libSystem.Native 経由）。</summary>
    private static (bool, string) CheckRandom()
    {
        const int length = 16;
        byte[] bytes = System.Security.Cryptography.RandomNumberGenerator.GetBytes(length);
        return (bytes.Length == length && bytes.Any(b => b != 0), $"bytes={Convert.ToHexString(bytes)}");
    }

    /// <summary>暗号でない乱数と GUID 生成（ゲームで多用する。暗号ライブラリに依存しないことの確認）。</summary>
    private static (bool, string) CheckNonCryptoRandom()
    {
        const int maxValue = 1000;
        int value = Random.Shared.Next(maxValue);
        Guid guid = Guid.NewGuid();
        return (guid != Guid.Empty && value >= 0, $"random={value} guid={guid}");
    }

    /// <summary>DeflateStream の圧縮・展開往復（libSystem.IO.Compression.Native）。</summary>
    private static (bool, string) CheckDeflate()
    {
        const int sourceLength = 64 * 1024;
        byte[] source = new byte[sourceLength];
        for (int i = 0; i < source.Length; i++) source[i] = (byte)(i % 251);
        byte[] compressed;
        using (var ms = new MemoryStream())
        {
            using (var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
            {
                ds.Write(source, 0, source.Length);
            }
            compressed = ms.ToArray();
        }
        using var input = new System.IO.Compression.DeflateStream(new MemoryStream(compressed), System.IO.Compression.CompressionMode.Decompress);
        using var output = new MemoryStream();
        input.CopyTo(output);
        bool ok = output.ToArray().AsSpan().SequenceEqual(source);
        return (ok, $"src={source.Length} compressed={compressed.Length}");
    }

    /// <summary>System.Text.Json のリフレクションベース（フィールド込み）シリアライズ往復。</summary>
    private static (bool, string) CheckJson()
    {
        var options = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };
        var src = new SampleComponent { Speed = 2.5f, Counter = 3, Label = "日本語", Offset = new SampleStruct(1, 2, 3) };
        string json = System.Text.Json.JsonSerializer.Serialize(src, options);
        SampleComponent back = System.Text.Json.JsonSerializer.Deserialize<SampleComponent>(json, options)!;
        bool ok = back.Speed == src.Speed && back.Counter == src.Counter && back.Label == src.Label && back.Offset.Z == src.Offset.Z;
        return (ok, $"json={json}");
    }
}

/// <summary>libc への P/Invoke 宣言。</summary>
internal static class NativeMethods
{
    /// <summary>bionic の getpid。"libc" は "libc.so" として探索される想定。</summary>
    [DllImport("libc", EntryPoint = "getpid")]
    public static extern int GetPid();
}
