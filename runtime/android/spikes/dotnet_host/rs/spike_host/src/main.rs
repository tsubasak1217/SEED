//! spike_host
//!
//! Android（linux-bionic）上で hostfxr → .NET ランタイムを起動し、
//! SpikeLib の `[UnmanagedCallersOnly]` 関数を呼び出して結果と所要時間を表示する検証用ホスト。
//!
//! SEED 本体（runtime/src/engine/core/scripting/mod.rs の ScriptingHost::load）と同じ
//! `initialize_for_runtime_config` → `get_delegate_loader_for_assembly` →
//! `get_function_with_unmanaged_callers_only` の流れを、hostfxr のパス直指定で再現する。
//!
//! 出力は 1 行 1 レコードのタブ区切り:
//!   TIME   <ラベル>  <ミリ秒>
//!   RESULT <内容>
//!   CHECK  <名前> <OK|NG> ...   （C# 側 RunChecks の出力もそのまま流す）

mod options;

use netcorehost::{
    hostfxr::{AssemblyDelegateLoader, DelegateLoader, Hostfxr, HostfxrContext},
    pdcstr,
    pdcstring::{PdCStr, PdCString},
};
use options::{AltStackMode, InitMode, LoaderMode, Options};
use std::{
    error::Error,
    io::Write,
    process::ExitCode,
    time::Instant,
};

/// 関数ポインタを取得する型（アセンブリ修飾名）。pdcstr! は const 文脈で使えないため関数にする。
fn exports_type() -> &'static PdCStr {
    pdcstr!("SpikeLib.Exports, SpikeLib")
}

/// RunChecks の結果を受け取るバッファの容量（C# 側レポートは数 KB 程度）。
const REPORT_CAPACITY: usize = 256 * 1024;

/// EchoUtf8 の書き戻しバッファ容量。
const ECHO_CAPACITY: usize = 4 * 1024;

/// UTF-8 往復確認に使う文字列（日本語・絵文字・ダイアクリティカル）。
const UTF8_SAMPLE: &str = "日本語テスト🐟 Ünïcödé";

/// Add の疎通確認に使う引数と期待値。
const ADD_ARGS_FIRST: (i32, i32) = (2, 3);
const ADD_ARGS_SECOND: (i32, i32) = (40, 2);
const ADD_ARGS_THREAD: (i32, i32) = (7, 8);

/// ProbeHardwareException の種別（C# 側 Exports.ProbeKind* と一致させる）。
const PROBE_KIND_NULL_REFERENCE: i32 = 0;
const PROBE_KIND_DIVIDE_BY_ZERO: i32 = 1;

/// ProbeHardwareException の戻り値（C# 側 Exports.ProbeResult* と一致させる）。
const PROBE_RESULT_CAUGHT: i32 = 1;

/// Add のシグネチャ。
type AddFn = fn(i32, i32) -> i32;
/// RunChecks のシグネチャ（buf, cap）→ 書き込みバイト数（不足時は負値）。
type RunChecksFn = fn(*mut u8, i32) -> i32;
/// EchoUtf8 のシグネチャ（src, srcLen, dst, dstCap）→ 書き込みバイト数。
type EchoFn = fn(*const u8, i32, *mut u8, i32) -> i32;
/// ProbeHardwareException のシグネチャ（kind）→ 結果コード。
type ProbeFn = fn(i32) -> i32;

/// 経過時間を TIME 行として出力する。
fn report_time(label: &str, started: Instant) {
    println!("TIME\t{label}\t{:.3}", started.elapsed().as_secs_f64() * 1000.0);
}

/// CHECK 行（ホスト側で判定した項目）を出力する。
fn report_check(name: &str, ok: bool, detail: &str) {
    println!("CHECK\t{name}\t{}\t-\t{detail}", if ok { "OK" } else { "NG" });
}

/// 標準出力を即座に吐き出す（後続でプロセスが落ちてもそこまでの出力を残すため）。
fn flush_stdout() {
    let _ = std::io::stdout().flush();
}

fn main() -> ExitCode {
    let process_started = Instant::now();
    let options = match Options::parse(std::env::args().skip(1)) {
        Ok(o) => o,
        Err(message) => {
            eprintln!("{message}\n\n{}", Options::USAGE);
            return ExitCode::from(2);
        }
    };
    println!("INFO\thost.options\t{options:?}");
    println!("INFO\thost.exe\t{:?}", std::env::current_exe().ok());

    match run(&options) {
        Ok(()) => {
            report_time("process.total", process_started);
            ExitCode::SUCCESS
        }
        Err(e) => {
            println!("ERROR\t{e}\t{e:?}");
            report_time("process.total", process_started);
            ExitCode::FAILURE
        }
    }
}

/// hostfxr の読み込みから初期化までを行い、初期化方式ごとのコンテキストで検証本体を回す。
fn run(options: &Options) -> Result<(), Box<dyn Error>> {
    apply_altstack(options.altstack)?;

    // ── hostfxr の読み込み（dlopen）──
    let started = Instant::now();
    let hostfxr = Hostfxr::load_from_path(&options.hostfxr)?;
    report_time("hostfxr.load_from_path", started);

    // hostfxr/hostpolicy のエラーメッセージを取りこぼさないよう stderr へ流す
    hostfxr.set_error_writer(Some(Box::new(|message: &PdCStr| {
        eprintln!("[hostfxr] {}", message.to_string_lossy());
    })));

    // ── ランタイム設定の解決（この時点ではまだランタイム本体は起動しない）──
    let started = Instant::now();
    match options.mode {
        InitMode::RuntimeConfig => {
            let config = PdCString::from_os_str(options.config_path().as_os_str())?;
            let context = match &options.dotnet_root {
                Some(root) => hostfxr.initialize_for_runtime_config_with_dotnet_root(&config, PdCString::from_os_str(root.as_os_str())?)?,
                None => hostfxr.initialize_for_runtime_config(&config)?,
            };
            report_time("hostfxr.initialize_for_runtime_config", started);
            exercise(context, options)
        }
        InitMode::CommandLine => {
            let app = PdCString::from_os_str(options.app_path().as_os_str())?;
            let context = match &options.dotnet_root {
                Some(root) => hostfxr.initialize_for_dotnet_command_line_with_dotnet_root(&app, PdCString::from_os_str(root.as_os_str())?)?,
                None => hostfxr.initialize_for_dotnet_command_line(&app)?,
            };
            report_time("hostfxr.initialize_for_dotnet_command_line", started);
            exercise(context, options)
        }
    }
}

/// Rust の main スレッドが持つ sigaltstack を指定どおりに変更する（Unix のみ）。
fn apply_altstack(mode: AltStackMode) -> Result<(), Box<dyn Error>> {
    #[cfg(unix)]
    {
        use netcorehost::utils::altstack::{self, State};
        println!("INFO\thost.altstack.before\t{:?}", altstack::get()?);
        match mode {
            AltStackMode::Keep => {}
            AltStackMode::Disable => altstack::set(State::Disabled)?,
            AltStackMode::Size(size) => altstack::set(State::Enabled { size })?,
        }
        println!("INFO\thost.altstack.after\t{:?}", altstack::get()?);
    }
    #[cfg(not(unix))]
    {
        let _ = mode;
    }
    Ok(())
}

/// 関数ポインタの取得経路。
enum Loader {
    /// hdt_load_assembly_and_get_function_pointer: 指定パスのアセンブリを専用の IsolatedComponentLoadContext に読む（SEED 現行方式）
    Assembly(AssemblyDelegateLoader),
    /// hdt_get_function_pointer: Default ALC から型を解決する（TPA 上のアセンブリ、または hdt_load_assembly で読んだもの）
    Default(DelegateLoader),
}

/// Loader から [UnmanagedCallersOnly] 関数を取得する（経路ごとに型が違うのでマクロで分岐する）。
macro_rules! get_fn {
    ($loader:expr, $ty:ty, $method:expr) => {
        match &$loader {
            Loader::Assembly(l) => l.get_function_with_unmanaged_callers_only::<$ty>(exports_type(), $method),
            Loader::Default(l) => l.get_function_with_unmanaged_callers_only::<$ty>(exports_type(), $method),
        }
    };
}

/// ランタイムを起動して SpikeLib の各関数を呼ぶ（初期化方式に依らない共通部分）。
fn exercise<I>(context: HostfxrContext<I>, options: &Options) -> Result<(), Box<dyn Error>> {
    // ── ランタイム本体の起動（ランタイムデリゲートの初回取得時に coreclr_initialize が走る）──
    let started = Instant::now();
    let loader = match options.loader {
        LoaderMode::Assembly => {
            let l = context.get_delegate_loader_for_assembly(PdCString::from_os_str(options.assembly.as_os_str())?)?;
            report_time("runtime.start(get_delegate_loader_for_assembly)", started);
            Loader::Assembly(l)
        }
        LoaderMode::Default => {
            let l = context.get_delegate_loader()?;
            report_time("runtime.start(get_delegate_loader)", started);
            if options.preload {
                // Default ALC へ明示的に読み込む（AssemblyLoadContext.Default.LoadFromAssemblyPath 相当。
                // 内部で AssemblyDependencyResolver を使うため Android 版 CoreLib では PlatformNotSupportedException になる）
                let started = Instant::now();
                context.load_assembly_from_path(PdCString::from_os_str(options.assembly.as_os_str())?)?;
                report_time("load_assembly_from_path(Default ALC)", started);
            }
            if options.preload_bytes {
                // バイト列から Default ALC へ読み込む（AssemblyLoadContext.Default.InternalLoad 相当。リゾルバを使わない）。
                // pak や APK アセットから直接渡す経路の確認も兼ねる。
                let started = Instant::now();
                let bytes = std::fs::read(&options.assembly)?;
                context.load_assembly_from_bytes(&bytes, [])?;
                report_time("load_assembly_from_bytes(Default ALC)", started);
            }
            Loader::Default(l)
        }
    };
    flush_stdout();

    // ── 最初の関数ポインタ取得（アセンブリ読み込み＋型/メソッド解決）──
    let started = Instant::now();
    let add = get_fn!(loader, AddFn, pdcstr!("Add"))?;
    report_time("get_function(Add)", started);

    let started = Instant::now();
    let first = add(ADD_ARGS_FIRST.0, ADD_ARGS_FIRST.1);
    report_time("call.Add#1", started);
    let started = Instant::now();
    let second = add(ADD_ARGS_SECOND.0, ADD_ARGS_SECOND.1);
    report_time("call.Add#2", started);
    report_check(
        "host.Add",
        first == ADD_ARGS_FIRST.0 + ADD_ARGS_FIRST.1 && second == ADD_ARGS_SECOND.0 + ADD_ARGS_SECOND.1,
        &format!("Add{ADD_ARGS_FIRST:?}={first} Add{ADD_ARGS_SECOND:?}={second}"),
    );
    flush_stdout();

    // ── ネイティブの別スレッドから呼ぶ（ランタイム未登録スレッドの自動アタッチ）──
    let add_ptr = *add;
    let started = Instant::now();
    let from_thread = std::thread::spawn(move || add_ptr(ADD_ARGS_THREAD.0, ADD_ARGS_THREAD.1)).join();
    report_time("call.Add(from new native thread)", started);
    report_check(
        "host.Add.fromNativeThread",
        matches!(from_thread, Ok(v) if v == ADD_ARGS_THREAD.0 + ADD_ARGS_THREAD.1),
        &format!("{from_thread:?}"),
    );

    // ── UTF-8 の往復（Rust → C# → Rust）──
    let started = Instant::now();
    let echo = get_fn!(loader, EchoFn, pdcstr!("EchoUtf8"))?;
    let mut out = vec![0u8; ECHO_CAPACITY];
    let written = echo(UTF8_SAMPLE.as_ptr(), UTF8_SAMPLE.len() as i32, out.as_mut_ptr(), out.len() as i32);
    report_time("call.EchoUtf8(incl. get_function)", started);
    let expected = format!("echo({}):{UTF8_SAMPLE}", UTF8_SAMPLE.chars().count());
    let echoed = usize::try_from(written).ok().and_then(|n| std::str::from_utf8(&out[..n]).ok().map(str::to_owned));
    report_check("host.utf8.echo", echoed.as_deref() == Some(expected.as_str()), &format!("got={echoed:?}"));
    flush_stdout();

    // ── C# 側の全チェック（1 回目はコールドスタート、2 回目以降はウォーム）──
    let run_checks = get_fn!(loader, RunChecksFn, pdcstr!("RunChecks"))?;
    let mut report = vec![0u8; REPORT_CAPACITY];
    for index in 1..=options.runs {
        println!("BEGIN\tRunChecks#{index}");
        flush_stdout();
        let started = Instant::now();
        let written = run_checks(report.as_mut_ptr(), report.len() as i32);
        let elapsed_label = format!("call.RunChecks#{index}");
        match usize::try_from(written) {
            Ok(n) => print!("{}", String::from_utf8_lossy(&report[..n])),
            Err(_) => println!("ERROR\tRunChecks buffer too small (need {} bytes)", -i64::from(written)),
        }
        report_time(&elapsed_label, started);
        println!("END\tRunChecks#{index}");
        flush_stdout();
    }

    // ── ハードウェア例外（シグナル）→ マネージド例外の変換。落ちる可能性があるので最後に行う ──
    if options.probe_hardware_exceptions {
        let probe = get_fn!(loader, ProbeFn, pdcstr!("ProbeHardwareException"))?;
        for (name, kind) in [("NullReference(SIGSEGV)", PROBE_KIND_NULL_REFERENCE), ("DivideByZero", PROBE_KIND_DIVIDE_BY_ZERO)] {
            println!("BEGIN\tprobe.{name}");
            flush_stdout();
            let started = Instant::now();
            let result = probe(kind);
            report_time(&format!("call.Probe.{name}"), started);
            report_check(&format!("host.hwexception.{name}"), result == PROBE_RESULT_CAUGHT, &format!("result={result}"));
            flush_stdout();
        }
    }

    // コンテキストはここで drop（hostfxr_close）。ランタイム自体はプロセス終了まで残る。
    Ok(())
}
