// ============================================================
//  platform/bridge/desktop_sim/os_info.rs — 模擬の OS の種類と版（2026-10-01。app.os_info の源）
//
//  Android の Build.VERSION.SDK_INT（platform/local/OsInfoCommand）の代わり。
//    種類 … ホストの OS（std::env::consts::OS）を wire の名前にする（windows / macos / linux。知らない OS は unknown）
//    版   … 既定 0（PC に Android の API レベルは無い）。環境変数 SEED_PLATFORM_SIM_OS_VERSION に 0 以上の整数を書くと
//           その値を返す（例 33 で「Android 13 の端末」として版で分ける画面を PC で試す）。読めない値はログに出して 0。
//  値はプロセスの起動のときに 1 回読む（環境変数は実行中に変わらない）。
// ============================================================

use crate::engine::platform::bridge::wire::app as app_names;
use crate::engine::platform::bridge::LOG_PREFIX;

/// 模擬の OS の版を与える環境変数。
pub const OS_VERSION_ENV: &str = "SEED_PLATFORM_SIM_OS_VERSION";

/// 模擬の OS の版の既定（PC に Android の API レベルは無いので 0）。
pub const DEFAULT_SIM_OS_VERSION: i64 = 0;

/// 模擬の OS の版の上限（C# の int に収まる値。API レベルがこれを超えることは無い）。
const MAX_SIM_OS_VERSION: i64 = i32::MAX as i64;

/// std::env::consts::OS の値 → wire の名前の表（データで持つ。無い OS は unknown）。
const HOST_PLATFORM_NAMES: &[(&str, &str)] = &[
    ("windows", app_names::PLATFORM_WINDOWS),
    ("macos", app_names::PLATFORM_MACOS),
    ("linux", app_names::PLATFORM_LINUX),
    ("android", app_names::PLATFORM_ANDROID),
];

/// 模擬の OS の種類と版。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct SimOsInfo {
    /// 種類（wire の PLATFORM_*）。
    pub platform: &'static str,
    /// 版の番号（0 以上）。
    pub os_version: i64,
}

impl SimOsInfo {
    /// ホストの OS と環境変数から作る（単体テストでは環境変数を読まず既定の版にする）。
    pub fn from_host() -> Self {
        let os_version = if cfg!(test) {
            DEFAULT_SIM_OS_VERSION
        } else {
            match parse_os_version(std::env::var(OS_VERSION_ENV).ok().as_deref()) {
                Ok(version) => {
                    if version != DEFAULT_SIM_OS_VERSION {
                        eprintln!("{LOG_PREFIX} 模擬: {OS_VERSION_ENV}={version}（App.OsVersion はこの値を返します）");
                    }
                    version
                }
                Err(error) => {
                    eprintln!("{LOG_PREFIX} 模擬: {OS_VERSION_ENV} を読めないので {DEFAULT_SIM_OS_VERSION} にします: {error}");
                    DEFAULT_SIM_OS_VERSION
                }
            }
        };
        Self { platform: platform_name(std::env::consts::OS), os_version }
    }
}

/// std::env::consts::OS の値を wire の名前にする（表に無い OS は unknown）。
pub fn platform_name(os: &str) -> &'static str {
    HOST_PLATFORM_NAMES.iter().find(|(host, _)| *host == os).map_or(app_names::PLATFORM_UNKNOWN, |(_, name)| name)
}

/// 環境変数の値を版の番号として読む（無い・空なら既定。0 以上 int の上限以下の整数だけ）。
pub fn parse_os_version(text: Option<&str>) -> Result<i64, String> {
    let Some(text) = text.map(str::trim).filter(|text| !text.is_empty()) else {
        return Ok(DEFAULT_SIM_OS_VERSION);
    };
    match text.parse::<i64>() {
        Ok(version) if (0..=MAX_SIM_OS_VERSION).contains(&version) => Ok(version),
        Ok(version) => Err(format!("0〜{MAX_SIM_OS_VERSION} の整数にしてください（{version}）")),
        Err(_) => Err(format!("整数にしてください（{text:?}）")),
    }
}

// ============================================================
//  ユニットテスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 版: 無い・空は既定、整数はその値、負・大きすぎ・整数でないものは誤り。
    #[test]
    fn os_version_parsing_rules() {
        assert_eq!(parse_os_version(None), Ok(DEFAULT_SIM_OS_VERSION));
        assert_eq!(parse_os_version(Some("  ")), Ok(DEFAULT_SIM_OS_VERSION));
        assert_eq!(parse_os_version(Some(" 33 ")), Ok(33));
        assert_eq!(parse_os_version(Some("0")), Ok(0));
        for bad in ["-1", "13.0", "Android14", "99999999999"] {
            assert!(parse_os_version(Some(bad)).is_err(), "{bad}");
        }
    }

    /// 種類: 表の OS は wire の名前、知らない OS は unknown。このホストの OS も表から引ける。
    #[test]
    fn platform_names_from_host_os() {
        assert_eq!(platform_name("windows"), app_names::PLATFORM_WINDOWS);
        assert_eq!(platform_name("macos"), app_names::PLATFORM_MACOS);
        assert_eq!(platform_name("linux"), app_names::PLATFORM_LINUX);
        assert_eq!(platform_name("freebsd"), app_names::PLATFORM_UNKNOWN);
        let host = SimOsInfo::from_host();
        assert_eq!(host.os_version, DEFAULT_SIM_OS_VERSION, "単体テストでは環境変数を読まない");
        #[cfg(target_os = "windows")]
        assert_eq!(host.platform, app_names::PLATFORM_WINDOWS);
    }
}
