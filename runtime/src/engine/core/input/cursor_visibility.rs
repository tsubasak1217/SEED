// ============================================================
//  cursor_visibility.rs — OS のマウスカーソル表示カウンタを直接制御する
//
//  【なぜ winit だけでは足りないのか】
//  winit の `Window::set_cursor_visible(false)` は内部フラグ `CursorFlags::HIDDEN` を
//  立てたうえで `ShowCursor` を **1 回だけ** 呼ぶ実装になっている。ところが winit は
//  「カーソルがウィンドウのクライアント領域内にあるか（`CursorFlags::IN_WINDOW`）」を
//  見ており、WM_MOUSELEAVE 等で領域外と判断した瞬間に `ShowCursor(TRUE)` を呼んで
//  **カーソルを表示へ戻してしまう**。
//  また winit のフラグ管理は「前回 OS へ適用した状態」を静的変数で覚えているため、
//  外部（このモジュール）や他プロセスが表示カウンタを触ると実態とズレたまま
//  復帰しなくなる。
//
//  【このモジュールの方針】
//  Win32 の `ShowCursor` は「表示カウンタ」を増減する API で、カウンタが 0 未満の間
//  だけカーソルが隠れる（MSDN）。そこで状態を覚えず、**毎回カウンタを目標の符号まで
//  追い込む**ことで冪等にする:
//    - 隠す : 戻り値が負になるまで `ShowCursor(FALSE)`
//    - 戻す : 戻り値が 0 以上になるまで `ShowCursor(TRUE)`
//  winit が途中で 1 回動かしていても、他の誰かが動かしていても、必ず目標状態へ収束する。
//
//  【スレッド】
//  `ShowCursor` / `SetCursor` は呼び出しスレッドの入力キューに紐づく。必ず
//  ウィンドウを所有するスレッド（＝ winit のイベントループスレッド）から呼ぶこと。
//  本エンジンではフレーム処理がイベントループスレッド上で走るので条件を満たす。
// ============================================================

/// 表示カウンタを追い込むループの最大試行回数。
///
/// `ShowCursor` は 1 回の呼び出しで必ず 1 だけ動くので通常 1〜2 回で収束する。
/// 万一 OS 側が値を返さない異常時に無限ループへ落ちないための安全弁であり、
/// 「何回まで許すか」を数値リテラルで散らかさないよう定数にしている。
const MAX_COUNTER_STEPS: u32 = 32;

/// カーソルを確実に隠す（表示カウンタを負まで押し下げる）。
///
/// 毎フレーム呼んでよい。すでに隠れていれば `ShowCursor` は 1 回も呼ばれない
/// （最初の呼び出しで負の値が返るため、すぐループを抜ける）。
pub fn force_hidden() {
    #[cfg(target_os = "windows")]
    unsafe {
        use windows_sys::Win32::UI::WindowsAndMessaging::{SetCursor, ShowCursor};

        // 自スレッドの現在のカーソル形状を「無し」にする。
        // WM_SETCURSOR のたびに既定のカーソルへ戻されるため単独では不十分だが、
        // ShowCursor が効かない環境（リモートデスクトップ等）の保険になる。
        SetCursor(core::ptr::null_mut());

        // 表示カウンタを「ちょうど -1」へ収束させる。
        //
        // ShowCursor(FALSE) は 1 減らした後の値を返す。すでに隠れていた（-1 以下だった）場合は
        // 戻り値が -2 以下になるので、その分を ShowCursor(TRUE) で 1 つ戻して元の値に保つ。
        // こうしないと毎フレーム呼ぶたびにカウンタが際限なく下がり、解除（force_shown）が
        // MAX_COUNTER_STEPS 回では 0 以上へ戻せなくなる（＝カーソルが消えたまま）。
        const SHOW_FALSE: i32 = 0;   // BOOL の FALSE。windows-sys では BOOL = i32。
        const SHOW_TRUE:  i32 = 1;   // BOOL の TRUE。
        /// 「隠れている」を表すカウンタの目標値（これより小さくは下げない）。
        const HIDDEN_TARGET: i32 = -1;
        let mut steps = 0;
        while steps < MAX_COUNTER_STEPS {
            let after = ShowCursor(SHOW_FALSE);
            if after < HIDDEN_TARGET {
                // すでに隠れていた: 余計に下げた 1 段を戻して終わり
                ShowCursor(SHOW_TRUE);
                break;
            }
            if after == HIDDEN_TARGET {
                break;   // いま隠れた
            }
            steps += 1;  // まだ 0 以上（表示中）: もう 1 段下げる
        }
    }
}

/// カーソルを確実に表示へ戻す（表示カウンタを 0 以上へ押し上げる）。
///
/// ロック解除・Play 停止・ポーズメニュー表示など「カーソルを返す」全経路から呼ぶ。
/// すでに表示されていれば `ShowCursor` は 1 回も呼ばれない。
pub fn force_shown() {
    #[cfg(target_os = "windows")]
    unsafe {
        use windows_sys::Win32::Foundation::POINT;
        use windows_sys::Win32::UI::WindowsAndMessaging::{
            GetCursorPos, SetCursorPos, ShowCursor,
        };

        let mut steps = 0;
        while steps < MAX_COUNTER_STEPS {
            // BOOL の TRUE。
            const SHOW_TRUE: i32 = 1;
            if ShowCursor(SHOW_TRUE) >= 0 {
                break;
            }
            steps += 1;
        }

        // 【再描画の促し】
        // `ShowCursor(TRUE)` は表示カウンタを戻すだけで、カーソルの絵は
        // 「次に実際にカーソルが動いたとき」まで描き直されない（実測: 解除直後に
        // `GetCursorInfo` を読むと CURSOR_SHOWING が 0 のまま）。ポーズメニューを
        // 開いた直後のようにプレイヤーがまだマウスを動かしていない瞬間、カーソルが
        // 見えないままになってしまう。
        //
        // 同一座標への `SetCursorPos` は OS に「動いていない」と判断されて再描画が
        // 起きないため、1px だけずらして戻す往復を行う。最終座標は元のままなので
        // 操作感には影響しない。
        const REDRAW_NUDGE_PX: i32 = 1;
        let mut pt = POINT { x: 0, y: 0 };
        if GetCursorPos(&mut pt) != 0 {
            SetCursorPos(pt.x + REDRAW_NUDGE_PX, pt.y);
            SetCursorPos(pt.x, pt.y);
        }
    }
}
