// ============================================================
//  platform_sim_ops.rs — SEED.Platform のデスクトップの模擬の操作 IPC（PLATFORM_SIM）のアプリ側ハンドラ（2026-10-01）
//
//  エディタ（MCP 経由の AI）・自動の確かめのスクリプトから届いた `PLATFORM_SIM:{verb},{args…}` を、
//  下の表で模擬だけの命令（permission.sim_set など）へ置き換えて SEED.Platform の基盤へ送り、IPC で応答を返す。
//
//  【表（動詞 → 模擬の命令）】
//    permission,<kind>,<status>          → permission.sim_set    { kind, status }  状態を変える（変われば platform.permission_changed）
//    permission_answer,<kind|all>,<ans>  → permission.sim_answer { kind, answer }  Permissions.Request の模擬の利用者の答え（状態の名前か none）
//    lifecycle,<resumed|paused>          → app.sim_lifecycle     { phase }         前面・背面の出入り（platform.resumed / paused。窓のフォーカスと同じ扱い）
//  応答: `PLATFORM_SIM_OK:{命令の返答の JSON}` / `PLATFORM_SIM_ERROR:{reason}`
//    reason … not_playing（Play 中でない）・not_simulated（模擬でない＝Android の端末など）・unknown_verb・
//             bad_arguments（引数の数が表と違う）・命令の失敗の理由（invalid_argument など。wire の ERROR_*）
//
//  【責務】受理可否の判定（Play 中か・模擬か）と、表による置き換え・応答だけ。値の検査（種類・状態の名前）は模擬の命令が持つ
//  （desktop_sim/permission_commands.rs・lifecycle_commands.rs。スクリプトの PlatformDiagnostics と同じ入口を通す）。
// ============================================================

use serde_json::{Map, Value};

use crate::engine::core::app_base::app::RuntimeMode;
use crate::engine::platform::bridge::{self, wire, PlatformBridgeStatus};

use super::App;

/// 受理したときの応答の接頭辞（この後ろに命令の返答の JSON が付く）。
pub const PLATFORM_SIM_REPLY_OK_PREFIX: &str = "PLATFORM_SIM_OK:";

/// 拒否したときの応答の接頭辞（この後ろに理由が付く）。
pub const PLATFORM_SIM_REPLY_ERROR_PREFIX: &str = "PLATFORM_SIM_ERROR:";

/// 拒否理由: Play 中でない（Edit 中はスクリプトが走っておらず、模擬の状態は Play の開始で起動時の設定へ戻る）。
pub const PLATFORM_SIM_ERROR_NOT_PLAYING: &str = "not_playing";

/// 拒否理由: 基盤が模擬でない（Android の端末には模擬だけの命令が無い）。
pub const PLATFORM_SIM_ERROR_NOT_SIMULATED: &str = "not_simulated";

/// 拒否理由: 表に無い動詞。
pub const PLATFORM_SIM_ERROR_UNKNOWN_VERB: &str = "unknown_verb";

/// 拒否理由: 引数の数が表と違う。
pub const PLATFORM_SIM_ERROR_BAD_ARGUMENTS: &str = "bad_arguments";

/// 表の 1 行（動詞 → 模擬の命令と、引数を入れる欄の名前の並び）。
struct PlatformSimVerb {
    /// IPC の動詞。
    verb: &'static str,
    /// 送るモジュール。
    module: &'static str,
    /// 送るメソッド（模擬だけの命令）。
    method: &'static str,
    /// 引数を順に入れる欄の名前（数もこれで決まる）。
    arg_keys: &'static [&'static str],
}

/// 動詞の表（ここへ行を足せば IPC で操作できる模擬が増える）。
const PLATFORM_SIM_VERBS: &[PlatformSimVerb] = &[
    PlatformSimVerb {
        verb: "permission",
        module: wire::permission::MODULE,
        method: wire::permission::METHOD_SIM_SET,
        arg_keys: &[wire::permission::KEY_KIND, wire::permission::KEY_STATUS],
    },
    PlatformSimVerb {
        verb: "permission_answer",
        module: wire::permission::MODULE,
        method: wire::permission::METHOD_SIM_ANSWER,
        arg_keys: &[wire::permission::KEY_KIND, wire::permission::KEY_ANSWER],
    },
    PlatformSimVerb {
        verb: "lifecycle",
        module: wire::app::MODULE,
        method: wire::app::METHOD_SIM_LIFECYCLE,
        arg_keys: &[wire::app::KEY_PHASE],
    },
];

impl App {
    /// `PLATFORM_SIM:{verb},{args…}` を 1 件処理し、必ず 1 つの応答を返す。
    ///
    /// Play 中でなければ拒否する（Edit 中に変えても Play の開始で起動時の設定へ戻るので、効いたように見えて効かない）。
    pub(super) fn handle_platform_sim(&mut self, verb: String, args: Vec<String>) {
        let reply = if self.mode != RuntimeMode::Play {
            Err(PLATFORM_SIM_ERROR_NOT_PLAYING.to_string())
        } else {
            run_platform_sim(&verb, &args)
        };
        match reply {
            Ok(json) => {
                eprintln!("{} PLATFORM_SIM:{verb} を受け付けました: {json}", bridge::LOG_PREFIX);
                self.send_platform_sim_reply(&format!("{PLATFORM_SIM_REPLY_OK_PREFIX}{json}"));
            }
            Err(reason) => {
                eprintln!("{} PLATFORM_SIM:{verb} を断りました: {reason}", bridge::LOG_PREFIX);
                self.send_platform_sim_reply(&format!("{PLATFORM_SIM_REPLY_ERROR_PREFIX}{reason}"));
            }
        }
    }

    /// 応答を IPC へ送る（通信路が無ければ何もしない）。
    fn send_platform_sim_reply(&self, line: &str) {
        if let Some(ipc) = &self.ipc {
            ipc.send(line);
        }
    }
}

/// 表で置き換えて模擬の命令を送る（Play 中の判定の後）。
///
/// # 戻り値
/// 受け付けられたら Ok(命令の返答の JSON)。断られたら Err(理由の名前)
fn run_platform_sim(verb: &str, args: &[String]) -> Result<String, String> {
    if bridge::status() != PlatformBridgeStatus::Simulated {
        return Err(PLATFORM_SIM_ERROR_NOT_SIMULATED.to_string());
    }
    let (module, method, request) = build_request(verb, args)?;
    let reply = bridge::invoke(module, method, &request)?;
    let parsed: Value = serde_json::from_str(&reply).map_err(|_| wire::ERROR_INVALID_JSON.to_string())?;
    if parsed.get(wire::KEY_OK).and_then(Value::as_bool) == Some(true) {
        Ok(reply)
    } else {
        Err(parsed.get(wire::KEY_ERROR).and_then(Value::as_str).unwrap_or(wire::ERROR_INVALID_JSON).to_string())
    }
}

/// 動詞と引数を、模擬の命令（モジュール・メソッド・引数の JSON）へ置き換える（純粋な関数。単体テストで確かめる）。
///
/// # 戻り値
/// 置き換えられたら Ok((モジュール, メソッド, 引数の JSON))。表に無い動詞・引数の数の違いは Err(理由の名前)
fn build_request(verb: &str, args: &[String]) -> Result<(&'static str, &'static str, String), String> {
    let entry = PLATFORM_SIM_VERBS
        .iter()
        .find(|entry| entry.verb == verb)
        .ok_or_else(|| PLATFORM_SIM_ERROR_UNKNOWN_VERB.to_string())?;
    if args.len() != entry.arg_keys.len() {
        return Err(PLATFORM_SIM_ERROR_BAD_ARGUMENTS.to_string());
    }
    let fields: Map<String, Value> =
        entry.arg_keys.iter().zip(args).map(|(key, value)| ((*key).to_string(), Value::from(value.as_str()))).collect();
    Ok((entry.module, entry.method, Value::Object(fields).to_string()))
}

// ============================================================
//  ユニットテスト（表の置き換えと、模擬への往復）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 引数の並びの文字列（テスト用）。
    fn args(values: &[&str]) -> Vec<String> {
        values.iter().map(|value| (*value).to_string()).collect()
    }

    /// 表の動詞は模擬だけの命令へ置き換わり、引数は欄の名前つきの JSON になる。
    #[test]
    fn verbs_map_to_simulator_commands() {
        let (module, method, json) = build_request("permission", &args(&["post_notifications", "denied"])).unwrap();
        assert_eq!((module, method), (wire::permission::MODULE, wire::permission::METHOD_SIM_SET));
        let value: Value = serde_json::from_str(&json).unwrap();
        assert_eq!(value[wire::permission::KEY_KIND], Value::from("post_notifications"));
        assert_eq!(value[wire::permission::KEY_STATUS], Value::from("denied"));

        let (_, method, json) = build_request("permission_answer", &args(&["all", "none"])).unwrap();
        assert_eq!(method, wire::permission::METHOD_SIM_ANSWER);
        assert_eq!(serde_json::from_str::<Value>(&json).unwrap()[wire::permission::KEY_ANSWER], Value::from("none"));

        let (module, method, _) = build_request("lifecycle", &args(&["paused"])).unwrap();
        assert_eq!((module, method), (wire::app::MODULE, wire::app::METHOD_SIM_LIFECYCLE));
    }

    /// 表に無い動詞・引数の数の違いは理由つきで断る。
    #[test]
    fn unknown_verbs_and_bad_arguments_are_rejected() {
        assert_eq!(build_request("camera", &args(&["on"])).unwrap_err(), PLATFORM_SIM_ERROR_UNKNOWN_VERB);
        assert_eq!(build_request("permission", &args(&["post_notifications"])).unwrap_err(), PLATFORM_SIM_ERROR_BAD_ARGUMENTS);
        assert_eq!(build_request("lifecycle", &args(&[])).unwrap_err(), PLATFORM_SIM_ERROR_BAD_ARGUMENTS);
    }

    /// 表の動詞はすべて模擬の命令の表にある（名前の約束に合い、送ると unknown_method にならない）。
    #[cfg(not(target_os = "android"))]
    #[test]
    fn every_verb_reaches_the_simulator() {
        for entry in PLATFORM_SIM_VERBS {
            assert!(wire::is_valid_name(entry.module) && wire::is_valid_name(entry.method), "{}", entry.verb);
            // 空の引数で送ると、命令が在れば invalid_argument（在らなければ unknown_method）で断られる
            // （プロセスで共有する模擬の状態を変えないよう、この試験だけの模擬へ送る）
            let sim = crate::engine::platform::bridge::DesktopSimBridge::new();
            let text = crate::engine::platform::bridge::PlatformBridge::invoke(&sim, entry.module, entry.method, "{}").unwrap();
            let value: Value = serde_json::from_str(&text).unwrap();
            assert_ne!(value[wire::KEY_ERROR], Value::from(wire::ERROR_UNKNOWN_METHOD), "{}", entry.verb);
        }
    }

    /// 模擬への往復: 状態を変えると OK の応答に命令の返答が入り、値の誤りは命令の理由で断られる。
    #[cfg(not(target_os = "android"))]
    #[test]
    fn run_platform_sim_round_trips_through_simulator() {
        let reply = run_platform_sim("permission_answer", &args(&["exact_alarm", "granted"])).unwrap();
        assert_eq!(serde_json::from_str::<Value>(&reply).unwrap()[wire::KEY_OK], Value::Bool(true));
        assert_eq!(
            run_platform_sim("permission", &args(&["post_notifications", "maybe"])).unwrap_err(),
            wire::alarm::ERROR_INVALID_ARGUMENT
        );
        assert_eq!(run_platform_sim("lifecycle", &args(&["sleep"])).unwrap_err(), wire::alarm::ERROR_INVALID_ARGUMENT);
    }
}
