// ============================================================
//  camera_cursor.rs — カメラ操作中のカーソル可視状態/ 閉じ込めの単一管理
//
//  【なぜ独立させるか】
//  シーンビューのカメラ操作は「中ボタン（パン）」「右ボタン（回転）」「中＋右
//  （オービット）」の 3 通りがあり、押下順・解放順の組み合わせは 4 通りある。
//  以前は押下側と解放側の双方で「自分がカーソルを隠す担当か」を
//  `!other_button_held` で個別判定していたため、担当の決定（押下時）と
//  復帰の実行（解放時）が別条件になり、次の順序で復帰が落ちていた:
//
//      右押下 → 中押下 → 右解放 → 中解放
//
//  右押下時に right が担当となりカーソルを隠すが、中押下時は「右が押されている」
//  ため中は担当にならず grab 座標を保存しない。右解放時は「中が押されている」ため
//  再表示せず、最後の中解放時は grab 座標が None なので復帰処理ブロックごと
//  スキップされ、カーソルが隠れたまま残る。
//
//  【方針】
//  可視状態は「押下中のカメラ操作ボタンが 1 つでもあるか」だけで決まる派生値に
//  し、担当という概念そのものを無くす。ボタン状態を更新したあと必ず 1 回
//  `reconcile()` を通し、直前に OS へ適用した状態と違うときだけ呼び分ける。
//  これにより enter / exit は定義上つねに対称になり、全ボタンを離せば
//  （どの順序でも、どのボタンから始まっても）必ずカーソルが戻る。
// ============================================================

/// カメラ操作中のカーソル可視状態を保持する状態機械。
///
/// OS 呼び出し（`Window::set_cursor_visible`）は副作用なので、この構造体自身は
/// 「今 OS へ適用済みの状態」だけを持ち、呼び出しの要否を返すことに徹する。
/// これにより winit / Win32 に触れずに全遷移を単体テストできる。
#[derive(Debug, Default, Clone, Copy, PartialEq, Eq)]
pub(super) struct CameraCursorGrab {
    /// 現在 OS へ「非表示」を適用済みか。
    hidden: bool,
}

impl CameraCursorGrab {
    /// 望ましい可視状態を求め、OS へ反映が必要なときだけ `Some(visible)` を返す。
    ///
    /// - `enabled`: カメラ操作でカーソルを隠してよい文脈か（Edit / Pause のみ true）。
    ///   Play モードではカーソル管理をエディタ側（ClipCursor）に任せるため false。
    ///   false のときは押下状態に関わらず「表示」へ収束するので、
    ///   隠したままモードが切り替わっても取り残されない。
    /// - `mmb` / `rmb`: 中 / 右ボタンの**更新後**の押下状態。
    ///
    /// 返り値 `Some(true)` = 表示にする / `Some(false)` = 非表示にする / `None` = 変化なし。
    pub(super) fn reconcile(&mut self, enabled: bool, mmb: bool, rmb: bool) -> Option<bool> {
        let want_hidden = enabled && (mmb || rmb);
        if want_hidden == self.hidden {
            return None;
        }
        self.hidden = want_hidden;
        Some(!want_hidden)
    }

    /// 押下状態に関係なく強制的に表示へ戻す（フォーカス喪失・モード遷移など）。
    ///
    /// ウィンドウがフォーカスを失うとボタンの解放イベントが届かないことがあるため、
    /// 「隠したまま操作不能」を防ぐ最後の砦として使う。
    pub(super) fn force_show(&mut self) -> Option<bool> {
        if !self.hidden {
            return None;
        }
        self.hidden = false;
        Some(true)
    }

    /// 現在 OS へ非表示を適用済みか（テスト・診断用）。
    #[cfg(test)]
    pub(super) fn is_hidden(&self) -> bool {
        self.hidden
    }
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用ヘルパ: 一連のボタン操作を流し、最終的な可視状態を返す。
    ///
    /// `ops` は `(mmb, rmb)` の遷移列。`reconcile` の返り値を「OS へ適用した状態」
    /// として畳み込むため、実際のウィンドウと同じ観測結果になる。
    fn run(ops: &[(bool, bool)]) -> (CameraCursorGrab, bool) {
        let mut grab = CameraCursorGrab::default();
        // OS 側の初期状態は「表示」。
        let mut os_visible = true;
        for &(mmb, rmb) in ops {
            if let Some(v) = grab.reconcile(true, mmb, rmb) {
                os_visible = v;
            }
        }
        (grab, os_visible)
    }

    /// 右のみ: 押して離せば必ず戻る。
    #[test]
    fn rmb_only_restores_cursor() {
        let (g, visible) = run(&[(false, true), (false, false)]);
        assert!(visible, "右ボタンを離したらカーソルは表示に戻るべき");
        assert!(!g.is_hidden());
    }

    /// 中のみ: 押して離せば必ず戻る。
    #[test]
    fn mmb_only_restores_cursor() {
        let (g, visible) = run(&[(true, false), (false, false)]);
        assert!(visible, "中ボタンを離したらカーソルは表示に戻るべき");
        assert!(!g.is_hidden());
    }

    /// 本バグの再現順序: 右 → 中 → 右解放 → 中解放。
    /// 旧実装では最後の中解放で復帰処理がスキップされ、隠れたまま残っていた。
    #[test]
    fn rmb_then_mmb_released_rmb_first_restores_cursor() {
        let (g, visible) = run(&[
            (false, true),  // 右押下
            (true, true),   // 中を追加（オービット成立）
            (true, false),  // 右解放（中はまだ押されている）
            (false, false), // 中解放（全離し）
        ]);
        assert!(visible, "中＋右のあと全ボタンを離したらカーソルは必ず戻る");
        assert!(!g.is_hidden());
    }

    /// 逆順（中 → 右 → 中解放 → 右解放）でも同じく戻る。
    #[test]
    fn mmb_then_rmb_released_mmb_first_restores_cursor() {
        let (g, visible) = run(&[
            (true, false),
            (true, true),
            (false, true),
            (false, false),
        ]);
        assert!(visible);
        assert!(!g.is_hidden());
    }

    /// 押下順 × 解放順の全 4 通りで、全離し後は必ず表示に戻る（総当たり）。
    #[test]
    fn every_press_release_order_restores_cursor() {
        // (押下1, 押下2, 解放1) の並び。最終状態は必ず (false,false)。
        let orders: [[(bool, bool); 4]; 4] = [
            // 中→右, 中解放先
            [(true, false), (true, true), (false, true), (false, false)],
            // 中→右, 右解放先
            [(true, false), (true, true), (true, false), (false, false)],
            // 右→中, 右解放先
            [(false, true), (true, true), (true, false), (false, false)],
            // 右→中, 中解放先
            [(false, true), (true, true), (false, true), (false, false)],
        ];
        for (i, ops) in orders.iter().enumerate() {
            let (g, visible) = run(ops);
            assert!(visible, "順序 #{i} で全離し後にカーソルが戻らなかった");
            assert!(!g.is_hidden(), "順序 #{i} で内部状態が非表示のまま残った");
        }
    }

    /// 片方を離してもう片方が残っている間は隠れたまま（途中で点滅しない）。
    #[test]
    fn cursor_stays_hidden_while_one_button_remains() {
        let mut g = CameraCursorGrab::default();
        assert_eq!(g.reconcile(true, false, true), Some(false)); // 右押下で非表示
        assert_eq!(g.reconcile(true, true, true), None);         // 中追加でも変化なし
        assert_eq!(g.reconcile(true, true, false), None);        // 右解放、中が残る＝変化なし
        assert!(g.is_hidden());
        assert_eq!(g.reconcile(true, false, false), Some(true)); // 全離しで復帰
    }

    /// 同じ状態で何度呼んでも OS 呼び出しは発生しない（冪等）。
    #[test]
    fn reconcile_is_idempotent() {
        let mut g = CameraCursorGrab::default();
        assert_eq!(g.reconcile(true, true, false), Some(false));
        assert_eq!(g.reconcile(true, true, false), None);
        assert_eq!(g.reconcile(true, true, false), None);
    }

    /// enabled=false（Play モードなど）では押下中でも表示へ収束する。
    /// 隠したまま Edit → Play へ遷移してもカーソルが取り残されない。
    #[test]
    fn disabling_restores_cursor_even_while_buttons_are_held() {
        let mut g = CameraCursorGrab::default();
        assert_eq!(g.reconcile(true, true, true), Some(false));
        assert_eq!(g.reconcile(false, true, true), Some(true));
        assert!(!g.is_hidden());
    }

    /// フォーカス喪失時の強制復帰。ボタン解放イベントが届かなくても戻せる。
    #[test]
    fn force_show_recovers_from_lost_release_event() {
        let mut g = CameraCursorGrab::default();
        assert_eq!(g.reconcile(true, true, true), Some(false));
        assert_eq!(g.force_show(), Some(true));
        // すでに表示なら何も起きない。
        assert_eq!(g.force_show(), None);
    }
}
