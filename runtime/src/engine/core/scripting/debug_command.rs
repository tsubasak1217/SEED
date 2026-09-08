// ============================================================
//  scripting/debug_command.rs — デバッグコマンド（SCRIPT_DEBUG IPC）の待ち行列
//
//  エディタ／MCP から送られてくる `SCRIPT_DEBUG:<name>,<arg>` を溜めておき、
//  ゲームスレッドの C# 側（SEED.Debug.OnCommand）が 1 件ずつ取り出して配る。
//
//  【なぜ待ち行列を挟むのか】
//  IPC の受信は専用スレッド（ipc::read_loop）で走るので、そこから直接
//  C# のハンドラを呼ぶと「ゲームスレッド以外から ECS を触る」ことになり壊れる。
//  そこで「IPC スレッドが積む → ゲームスレッドが取り出す」だけの箱を 1 つ置き、
//  取り出しのタイミング（フレーム先頭の BeginFrame）を C# 側に固定させる。
//
//  【この箱が持たない責務】
//  - コマンド名の意味づけ（何をするか）… ゲーム側の C# スクリプトが決める
//  - Play 中かどうかの判定 …………………… 積む側（ipc_handler）が判定する
// ============================================================

use std::collections::VecDeque;
use std::sync::Mutex;

/// 1 件のデバッグコマンド（名前と引数の組）。
///
/// `name` は「どのハンドラへ配るか」の鍵、`arg` はその引数（空文字可）。
/// どちらも IPC の 1 行から切り出したものなので改行は含まない。
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct DebugCommand {
    /// ハンドラを引く名前（例 `catch_test`）。
    pub name: String,
    /// ハンドラへ渡す引数（例 魚のアクタ名。無指定なら空文字）。
    pub arg: String,
}

/// 溜められるコマンドの上限。
///
/// 誰も取り出さない（＝ゲームが Play していない／スクリプトが登録していない）
/// まま送り続けられても、メモリが際限なく伸びないようにするための蓋。
/// 溢れたときは<b>古いものから捨てる</b>（最新の指示のほうが意味があるため）。
const MAX_PENDING_COMMANDS: usize = 64;

/// name と arg を 1 本の文字列へ詰めるときの区切り。
///
/// IPC は 1 行 1 コマンドなので、name にも arg にも改行は入り得ない。
/// そのため改行を区切りに使えば、エスケープ無しで確実に元へ戻せる。
pub const FIELD_SEPARATOR: char = '\n';

/// 待ち行列の本体（プロセスに 1 つ）。
///
/// IPC スレッドが `push`、ゲームスレッドが `peek_front` / `pop_front` を呼ぶので
/// 排他が要る。中身は文字列 2 本だけなのでロック時間はごく短い。
static PENDING: Mutex<VecDeque<DebugCommand>> = Mutex::new(VecDeque::new());

/// デバッグコマンドを 1 件積む【IPC 受信側の唯一の入口】。
///
/// 上限（<see cref="MAX_PENDING_COMMANDS"/>）を超えたら古いものから落とす。
pub fn push(name: String, arg: String) {
    let Ok(mut queue) = PENDING.lock() else { return };
    while queue.len() >= MAX_PENDING_COMMANDS {
        queue.pop_front();
    }
    queue.push_back(DebugCommand { name, arg });
}

/// 先頭のコマンドを「name\narg」の形に組み立てて返す（取り出しはしない）。
///
/// 呼び出し側が用意した領域に収まるかを、取り出す前に確かめられるようにするための覗き見。
/// 空なら `None`。
pub fn peek_front_encoded() -> Option<String> {
    let queue = PENDING.lock().ok()?;
    let head = queue.front()?;
    Some(format!("{}{}{}", head.name, FIELD_SEPARATOR, head.arg))
}

/// 先頭のコマンドを捨てる（`peek_front_encoded` で受け取り切ったあとに呼ぶ）。
pub fn pop_front() {
    if let Ok(mut queue) = PENDING.lock() {
        queue.pop_front();
    }
}

/// 溜まっているものを全部捨てる（Play の開始／停止で持ち越さないため）。
pub fn clear() {
    if let Ok(mut queue) = PENDING.lock() {
        queue.clear();
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    /// 積んだ順に、区切り付きで取り出せる。
    #[test]
    fn pushes_and_peeks_in_order() {
        clear();
        push("catch_test".into(), "トビウオ".into());
        push("warp".into(), String::new());

        assert_eq!(peek_front_encoded().as_deref(), Some("catch_test\nトビウオ"));
        pop_front();
        assert_eq!(peek_front_encoded().as_deref(), Some("warp\n"));
        pop_front();
        assert_eq!(peek_front_encoded(), None);
        clear();
    }

    /// 上限を超えたら古いものから落ちる（新しいものは必ず残る）。
    #[test]
    fn drops_oldest_when_full() {
        clear();
        for i in 0..(MAX_PENDING_COMMANDS + 3) {
            push(format!("cmd{i}"), String::new());
        }
        // 先頭は「捨てられた 3 件」の次＝cmd3
        assert_eq!(peek_front_encoded().as_deref(), Some("cmd3\n"));
        clear();
    }
}
