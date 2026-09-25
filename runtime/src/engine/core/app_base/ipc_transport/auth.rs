// ============================================================
//  auth.rs — TCP の通信路の接続トークンの照合（段階D-1 の追加。純粋な処理）
//
//  【なぜ要るのか】
//  TCP の待ち受けは 127.0.0.1 だけなので端末の外からは届かないが、同じ端末で INTERNET 権限を持つ他のアプリは
//  127.0.0.1:<ポート> へつなげる。そのまま命令（一時停止・入力の注入・セーブデータ・STOP 等）を受け付けると、
//  他のアプリから自分のアプリを操作できてしまう。
//
//  【決まり】
//    - エディタ／SeedAndroid は起動のたびに使い捨てのトークンを作り、起動オプション（am start の extra seed.ipc_token）で渡す
//      （engine::platform::launch_options。PC の検証用は --ipc-token=）。
//    - つないだ側は最初の 1 行で `HELLO:<トークン>` を送る。ランタイムは一致したときだけ挨拶（READY:0）を返して命令を受け付ける。
//    - 一致しない・最初の行が HELLO でない・時間内に来ない → `IPC_DENIED:<理由>` を 1 行書いて接続を閉じる
//      （その接続の行は 1 行も App へ渡さない）。理由は照合に使った値を含めない（token / hello）。
//    - 名前付きパイプ（PC のエディタ）は従来どおりトークン無し（このファイルは使わない）。
//  比較は長さと中身の両方を、先頭から最後まで同じ手間で比べる（どこで違ったかで時間が変わらないように）。
// ============================================================

use std::time::Duration;

/// 照合の行の接頭辞（`HELLO:<トークン>`。C# の RuntimeIpcCommands.HelloPrefix と一致させる）。
pub const HELLO_PREFIX: &str = "HELLO:";

/// 断った行の接頭辞（`IPC_DENIED:<理由>`。C# の RuntimeIpcCommands.DeniedPrefix と一致させる）。
pub const DENIED_PREFIX: &str = "IPC_DENIED:";

/// 断った理由: トークンが一致しない。
pub const DENIED_REASON_TOKEN: &str = "token";

/// 断った理由: 最初の行が HELLO でない・時間内に来ない・読めない。
pub const DENIED_REASON_HELLO: &str = "hello";

/// 最初の行（HELLO）を待つ時間の既定（秒）。つないだ側は接続の直後に送るので、ふつうは数ミリ秒で届く。
/// 黙ったままの接続で 1 本だけの受け付けを塞がれ続けないよう短めにする。
pub const DEFAULT_HELLO_TIMEOUT_SECS: u64 = 5;

/// 最初の行として読む最大のバイト数（改行が来ないまま大量に送られてもメモリを使い続けないように）。
pub const MAX_HELLO_LINE_BYTES: u64 = 256;

/// TCP の通信路の照合の設定。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct TcpAuth {
    /// 期待するトークン（起動オプションの ipc_token）。
    pub token: String,
    /// 最初の行を待つ時間。
    pub hello_timeout: Duration,
}

impl TcpAuth {
    /// 既定の待ち時間で作る。
    ///
    /// # 引数
    /// * `token` - 期待するトークン
    pub fn new(token: String) -> Self {
        Self {
            token,
            hello_timeout: Duration::from_secs(DEFAULT_HELLO_TIMEOUT_SECS),
        }
    }
}

/// 最初の行を断った理由。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub enum HelloRejection {
    /// HELLO の行でない（命令をいきなり送った・空・時間切れ・読めない）。
    NotHello,
    /// トークンが一致しない。
    WrongToken,
}

impl HelloRejection {
    /// 断った行に書く理由（照合に使った値は含めない）。
    pub fn reason(self) -> &'static str {
        match self {
            Self::NotHello => DENIED_REASON_HELLO,
            Self::WrongToken => DENIED_REASON_TOKEN,
        }
    }
}

/// 最初の行を照合する【純関数】。
///
/// # 引数
/// * `line`     - 受け取った最初の行（前後の空白・改行は落として比べる）
/// * `expected` - 期待するトークン
///
/// # 戻り値
/// 一致すれば Ok、しなければ断る理由。
pub fn check_hello(line: &str, expected: &str) -> Result<(), HelloRejection> {
    let Some(token) = line.trim().strip_prefix(HELLO_PREFIX) else {
        return Err(HelloRejection::NotHello);
    };
    if constant_time_eq(token.as_bytes(), expected.as_bytes()) {
        Ok(())
    } else {
        Err(HelloRejection::WrongToken)
    }
}

/// 断った行を作る【純関数】（`IPC_DENIED:<理由>`）。
pub fn denied_line(rejection: HelloRejection) -> String {
    format!("{DENIED_PREFIX}{}", rejection.reason())
}

/// バイト列が等しいか（長さが違っても、長い方の最後まで同じ手間で比べる）【純関数】。
fn constant_time_eq(actual: &[u8], expected: &[u8]) -> bool {
    let length = actual.len().max(expected.len());
    // 長さの違いも差分として数える
    let mut difference = actual.len() ^ expected.len();
    for index in 0..length {
        let a = actual.get(index).copied().unwrap_or(0);
        let b = expected.get(index).copied().unwrap_or(0);
        difference |= usize::from(a ^ b);
    }
    difference == 0
}

#[cfg(test)]
mod tests {
    use super::*;

    const TOKEN: &str = "0123456789abcdef0123456789abcdef";

    /// 一致する HELLO だけを受け付ける（前後の空白・\r は許す）。
    #[test]
    fn accepts_only_matching_hello() {
        assert_eq!(check_hello(&format!("HELLO:{TOKEN}"), TOKEN), Ok(()));
        assert_eq!(check_hello(&format!("  HELLO:{TOKEN}\r\n"), TOKEN), Ok(()));
        assert_eq!(check_hello(&format!("HELLO:{TOKEN}x"), TOKEN), Err(HelloRejection::WrongToken), "長い");
        assert_eq!(check_hello("HELLO:0123456789abcdef", TOKEN), Err(HelloRejection::WrongToken), "短い");
        assert_eq!(check_hello("HELLO:", TOKEN), Err(HelloRejection::WrongToken), "空のトークン");
        assert_eq!(check_hello(&format!("HELLO:{}", TOKEN.to_uppercase()), TOKEN), Err(HelloRejection::WrongToken), "大小文字も区別");
    }

    /// HELLO でない最初の行（いきなりの命令・空）は断る。
    #[test]
    fn rejects_commands_before_hello() {
        assert_eq!(check_hello("PAUSE", TOKEN), Err(HelloRejection::NotHello));
        assert_eq!(check_hello("", TOKEN), Err(HelloRejection::NotHello));
        assert_eq!(check_hello(&format!("hello:{TOKEN}"), TOKEN), Err(HelloRejection::NotHello), "接頭辞は大文字");
    }

    /// 断った行は理由だけで、トークンを含めない。
    #[test]
    fn denied_line_carries_only_the_reason() {
        assert_eq!(denied_line(HelloRejection::WrongToken), "IPC_DENIED:token");
        assert_eq!(denied_line(HelloRejection::NotHello), "IPC_DENIED:hello");
    }

    /// 比較は長さの違いも中身の違いも見分ける。
    #[test]
    fn constant_time_eq_compares_length_and_content() {
        assert!(constant_time_eq(b"abc", b"abc"));
        assert!(!constant_time_eq(b"abc", b"abd"));
        assert!(!constant_time_eq(b"abc", b"abcd"));
        assert!(!constant_time_eq(b"", b"a"));
        assert!(constant_time_eq(b"", b""));
    }
}
