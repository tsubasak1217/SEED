//! コマンドライン引数の解析（外部クレートを増やさないため手書き）。

use std::path::PathBuf;

/// hostfxr の初期化方式。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum InitMode {
    /// `hostfxr_initialize_for_runtime_config`（SEED 本体と同じ。フレームワーク依存の runtimeconfig が必要）
    RuntimeConfig,
    /// `hostfxr_initialize_for_dotnet_command_line`（アプリとして初期化。自己完結レイアウトを受け付ける）
    CommandLine,
}

/// 関数ポインタの取得経路。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum LoaderMode {
    /// hdt_load_assembly_and_get_function_pointer（専用 ALC に読む。SEED 現行方式）
    Assembly,
    /// hdt_get_function_pointer（Default ALC から型を解決）
    Default,
}

/// Rust の main スレッドの sigaltstack をどうするか。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum AltStackMode {
    /// Rust の既定のまま
    Keep,
    /// 無効化
    Disable,
    /// 指定バイト数で作り直す
    Size(usize),
}

/// 解析済みの引数。
#[derive(Debug, Clone)]
pub struct Options {
    /// libhostfxr.so のパス
    pub hostfxr: PathBuf,
    /// 関数ポインタを取るアセンブリ（SpikeLib.dll）
    pub assembly: PathBuf,
    /// runtimeconfig.json（省略時は assembly と同名の .runtimeconfig.json）
    pub config: Option<PathBuf>,
    /// コマンドライン初期化で渡すアプリのパス（省略時は assembly）
    pub app: Option<PathBuf>,
    /// 初期化方式
    pub mode: InitMode,
    /// 関数ポインタの取得経路
    pub loader: LoaderMode,
    /// Default 経路のとき、先に hdt_load_assembly で assembly を Default ALC へ読み込むか
    pub preload: bool,
    /// Default 経路のとき、先に hdt_load_assembly_bytes で assembly をバイト列から Default ALC へ読み込むか
    pub preload_bytes: bool,
    /// hostfxr へ明示的に渡す dotnet ルート（省略時は hostfxr の位置から推定させる）
    pub dotnet_root: Option<PathBuf>,
    /// sigaltstack の扱い
    pub altstack: AltStackMode,
    /// ハードウェア例外の変換を試すか（落ちる可能性があるので任意）
    pub probe_hardware_exceptions: bool,
    /// RunChecks を呼ぶ回数
    pub runs: u32,
}

/// RunChecks の既定の呼び出し回数（コールド 1 回＋ウォーム 1 回）。
const DEFAULT_RUNS: u32 = 2;

impl Options {
    /// 使い方の説明。
    pub const USAGE: &'static str = "usage: spike_host --hostfxr <libhostfxr.so> --assembly <SpikeLib.dll> \
[--config <x.runtimeconfig.json>] [--mode rc|cmd] [--loader assembly|default] [--preload] [--preload-bytes] [--app <app.dll>] [--dotnet-root <dir>] \
[--altstack keep|off|<bytes>] [--probe-hw] [--runs <n>]";

    /// 引数列を解析する。
    pub fn parse(mut args: impl Iterator<Item = String>) -> Result<Self, String> {
        let mut hostfxr = None;
        let mut assembly = None;
        let mut config = None;
        let mut app = None;
        let mut mode = InitMode::RuntimeConfig;
        let mut loader = LoaderMode::Assembly;
        let mut preload = false;
        let mut preload_bytes = false;
        let mut dotnet_root = None;
        let mut altstack = AltStackMode::Keep;
        let mut probe_hardware_exceptions = false;
        let mut runs = DEFAULT_RUNS;

        while let Some(arg) = args.next() {
            let mut value = |name: &str| args.next().ok_or_else(|| format!("{name} には値が必要です"));
            match arg.as_str() {
                "--hostfxr" => hostfxr = Some(PathBuf::from(value("--hostfxr")?)),
                "--assembly" => assembly = Some(PathBuf::from(value("--assembly")?)),
                "--config" => config = Some(PathBuf::from(value("--config")?)),
                "--app" => app = Some(PathBuf::from(value("--app")?)),
                "--dotnet-root" => dotnet_root = Some(PathBuf::from(value("--dotnet-root")?)),
                "--mode" => {
                    mode = match value("--mode")?.as_str() {
                        "rc" => InitMode::RuntimeConfig,
                        "cmd" => InitMode::CommandLine,
                        other => return Err(format!("不明な --mode: {other}")),
                    }
                }
                "--altstack" => {
                    altstack = match value("--altstack")?.as_str() {
                        "keep" => AltStackMode::Keep,
                        "off" => AltStackMode::Disable,
                        bytes => AltStackMode::Size(bytes.parse().map_err(|_| format!("不正な --altstack: {bytes}"))?),
                    }
                }
                "--loader" => {
                    loader = match value("--loader")?.as_str() {
                        "assembly" => LoaderMode::Assembly,
                        "default" => LoaderMode::Default,
                        other => return Err(format!("不明な --loader: {other}")),
                    }
                }
                "--preload" => preload = true,
                "--preload-bytes" => preload_bytes = true,
                "--probe-hw" => probe_hardware_exceptions = true,
                "--runs" => runs = value("--runs")?.parse().map_err(|_| "不正な --runs".to_owned())?,
                other => return Err(format!("不明な引数: {other}")),
            }
        }

        Ok(Self {
            hostfxr: hostfxr.ok_or("--hostfxr は必須です")?,
            assembly: assembly.ok_or("--assembly は必須です")?,
            config,
            app,
            mode,
            loader,
            preload,
            preload_bytes,
            dotnet_root,
            altstack,
            probe_hardware_exceptions,
            runs,
        })
    }

    /// 実際に使う runtimeconfig.json のパス。
    pub fn config_path(&self) -> PathBuf {
        self.config.clone().unwrap_or_else(|| self.assembly.with_extension("runtimeconfig.json"))
    }

    /// コマンドライン初期化で渡すアプリのパス。
    pub fn app_path(&self) -> PathBuf {
        self.app.clone().unwrap_or_else(|| self.assembly.clone())
    }
}
