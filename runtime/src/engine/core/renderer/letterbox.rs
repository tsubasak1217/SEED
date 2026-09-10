// ============================================================
//  letterbox.rs — 内部解像度 ⇔ ウィンドウ実サイズ のレターボックス写像
//
//  【役割】
//  「固定内部解像度（project_settings.json の window_width × window_height）で
//    全て描画し、最終段でウィンドウへアスペクト維持で拡大縮小する」という
//    描画解像度モード（RenderResolutionMode::Fixed）のための **座標計算だけ** を担う。
//
//  【なぜ独立モジュールなのか】
//  この写像は 2 か所で使われる:
//    (1) 描画側 … 最終プレゼントパスの set_viewport 矩形（余白はクリア色＝黒帯）
//    (2) 入力側 … CursorMoved のウィンドウ座標 → 内部解像度座標への変換
//  片方だけ式が変わると「見た目とクリック位置がズレる」という再現の難しい不具合になる。
//  そこで wgpu にも winit にも依存しない純関数としてここへ 1 本化し、
//  ユニットテストで固定してある（本ファイル末尾の tests）。
//
//  【座標系】
//  ウィンドウ座標・内部解像度座標ともに「クライアント領域の左上原点・Y 下向き・
//  物理ピクセル」。winit の WindowEvent::CursorMoved と wgpu の set_viewport が
//  どちらもこの規約なので、変換なしで両者へ渡せる。
// ============================================================

/// 幅・高さのゼロ除算ガードに使う最小ピクセル数。
///
/// 最小化中のウィンドウ（0x0）や、設定ミスで 0 が入った内部解像度を渡されても
/// パニック・NaN を出さずに「極小の矩形」として処理を続けるための下限値。
const MIN_DIMENSION_PX: u32 = 1;

/// 矩形の中心を求めるための係数（幅・高さの半分）。
const HALF: f32 = 0.5;

/// `set_viewport` に渡せる最小の幅・高さ（px）。
/// これ未満に縮退した矩形はビューポート指定自体を省く（wgpu は幅・高さ 0 を受け付けない）。
const MIN_VIEWPORT_EXTENT_PX: f32 = 1.0;

/// レターボックス矩形（ウィンドウ実ピクセル座標系・左上原点）。
///
/// 「内部解像度の映像が、実際にウィンドウのどこへ・どの大きさで映るか」を表す。
/// `x` / `y` が 0 より大きい辺の外側が黒帯（ピラーボックス／レターボックス）になる。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct LetterboxRect {
    /// 映像左上の X（ウィンドウ座標・px）。左右の帯幅と等しい。
    pub x: f32,
    /// 映像左上の Y（ウィンドウ座標・px）。上下の帯高と等しい。
    pub y: f32,
    /// 映像の幅（px）。
    pub w: f32,
    /// 映像の高さ（px）。
    pub h: f32,
}

/// 内部解像度をウィンドウへアスペクト維持で最大内接させた矩形を返す。
///
/// - 拡大率は `min(window_w / internal_w, window_h / internal_h)`（＝はみ出さない側に合わせる）
/// - 余った方向は左右（または上下）へ均等に振り分ける＝センタリング
/// - 引数が 0 でも `MIN_DIMENSION_PX` へ切り上げるのでゼロ除算しない
///
/// 戻り値はウィンドウ実ピクセル座標系なので、そのまま `RenderPass::set_viewport` に渡せる。
pub fn letterbox_rect(
    window_w: u32,
    window_h: u32,
    internal_w: u32,
    internal_h: u32,
) -> LetterboxRect {
    // 0 除算・0 サイズテクスチャを避けるため、全辺を最小値でクランプしてから f32 化する。
    let ww = window_w.max(MIN_DIMENSION_PX) as f32;
    let wh = window_h.max(MIN_DIMENSION_PX) as f32;
    let iw = internal_w.max(MIN_DIMENSION_PX) as f32;
    let ih = internal_h.max(MIN_DIMENSION_PX) as f32;

    // アスペクト維持の拡大率。小さい方を採るとウィンドウからはみ出さずに最大内接する。
    let scale = (ww / iw).min(wh / ih);
    let w = iw * scale;
    let h = ih * scale;
    // 余白は上下（左右）へ均等配分＝センタリング。
    LetterboxRect {
        x: (ww - w) * HALF,
        y: (wh - h) * HALF,
        w,
        h,
    }
}

/// `set_viewport` に渡せるよう、矩形を出力ターゲットの範囲内へクランプする。
///
/// 戻り値 `None` は「クランプ後に縮退した（幅または高さが 1px 未満）」の意味で、
/// 呼び出し側はそのフレームのビューポート指定を**丸ごと省く**こと
/// （`set_viewport` は幅・高さ 0 を受け付けない）。
///
/// 【なぜクランプが要るのか】
/// `letterbox_rect` の `scale = min(window_w / internal_w, …)` は f32 の除算なので、
/// `internal_w * (window_w / internal_w)` は `window_w` と厳密には一致しない。
/// 例えば window_w=1000, internal_w=3 では `(1000/3)*3` が 1000 をわずかに超え、
/// `x = (1000 - w) * 0.5` が -0.00001 のような **負値** になる。
/// `RenderPass::set_viewport` は `x >= 0 && y >= 0 && x + w <= 出力幅 && y + h <= 出力高`
/// を要求するため、この 1px 未満のはみ出しだけでバリデーションがパニックし、
/// 続けて未 present の SurfaceTexture 破棄で二次パニック → プロセス abort する。
/// （既存の `clamp_viewport_to_target` が Play ビューポートで防いでいるのと同じ事故。）
pub fn clamp_rect_to_target(
    rect: LetterboxRect,
    target_w: f32,
    target_h: f32,
) -> Option<LetterboxRect> {
    // 出力ターゲット自体が縮退している（最小化中など）なら指定不能。
    if !(target_w >= MIN_VIEWPORT_EXTENT_PX && target_h >= MIN_VIEWPORT_EXTENT_PX) {
        return None;
    }
    // 左上は 0 以上へ、右下はターゲット内へ。NaN は比較が全て false になるため
    // ここを通ると下の縮退判定で弾かれる（明示的にパニックさせない）。
    let x = rect.x.max(0.0);
    let y = rect.y.max(0.0);
    let w = rect.w.min(target_w - x);
    let h = rect.h.min(target_h - y);
    if !(w >= MIN_VIEWPORT_EXTENT_PX && h >= MIN_VIEWPORT_EXTENT_PX) {
        return None;
    }
    Some(LetterboxRect { x, y, w, h })
}

/// ウィンドウ座標を内部解像度座標へ写す。戻り値 `.1` は「枠（映像部分）の内側か」。
///
/// 【クランプしない理由】
/// 黒帯の上を指しているカーソルは「内部解像度の外」であって、
/// 縁に貼り付いた有効な座標ではない。クランプすると帯の上にカーソルがあるのに
/// 画面端の UI がホバー判定に入ってしまう。負値・内部解像度超えのまま返せば、
/// UI 側の矩形判定（0..w, 0..h の内外）が自然に外れる。
/// 「帯の上かどうか」を明示的に知りたい呼び出し側のために `.1` を返す。
pub fn window_to_internal(
    pos: [f32; 2],
    window: (u32, u32),
    internal: (u32, u32),
) -> ([f32; 2], bool) {
    let rect = letterbox_rect(window.0, window.1, internal.0, internal.1);
    // letterbox_rect は全辺を MIN_DIMENSION_PX 以上でクランプ済みなので rect.w / rect.h は必ず正。
    let iw = internal.0.max(MIN_DIMENSION_PX) as f32;
    let ih = internal.1.max(MIN_DIMENSION_PX) as f32;

    // 映像左上を原点に取り直してから、拡大率の逆数を掛けて内部解像度へ戻す。
    let ix = (pos[0] - rect.x) / rect.w * iw;
    let iy = (pos[1] - rect.y) / rect.h * ih;

    // 枠内判定は「ウィンドウ座標が映像矩形の内側にあるか」。境界上は内側として扱う
    //（画面端 1px の UI を掴めなくならないようにするため）。
    let inside = pos[0] >= rect.x
        && pos[0] <= rect.x + rect.w
        && pos[1] >= rect.y
        && pos[1] <= rect.y + rect.h;

    ([ix, iy], inside)
}

// ============================================================
//  テスト
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// 浮動小数比較の許容誤差（px）。本モジュールの計算は 2 の冪スケールでは厳密だが、
    /// 3:2 などの割り切れない比率でも安全に比較できるようにする。
    const EPS: f32 = 1e-3;

    fn approx(a: f32, b: f32) -> bool {
        (a - b).abs() <= EPS
    }

    fn assert_rect(got: LetterboxRect, want: (f32, f32, f32, f32)) {
        assert!(
            approx(got.x, want.0)
                && approx(got.y, want.1)
                && approx(got.w, want.2)
                && approx(got.h, want.3),
            "rect mismatch: got={got:?} want={want:?}"
        );
    }

    /// ウィンドウと内部解像度が同一なら、矩形は全面・写像は恒等になること。
    /// （＝ fixed モードでもウィンドウが設定解像度そのままなら何も起きない）
    #[test]
    fn same_size_is_identity() {
        assert_rect(letterbox_rect(1280, 720, 1280, 720), (0.0, 0.0, 1280.0, 720.0));

        let (p, inside) = window_to_internal([100.0, 200.0], (1280, 720), (1280, 720));
        assert!(approx(p[0], 100.0) && approx(p[1], 200.0), "恒等写像であること: {p:?}");
        assert!(inside);
    }

    /// 同アスペクト（16:9 → 16:9）の拡大では帯が出ず、座標は一様に 1/scale される。
    #[test]
    fn same_aspect_scales_without_bars() {
        assert_rect(letterbox_rect(1920, 1080, 1280, 720), (0.0, 0.0, 1920.0, 1080.0));

        // 画面中央 → 内部解像度の中央
        let (p, inside) = window_to_internal([960.0, 540.0], (1920, 1080), (1280, 720));
        assert!(approx(p[0], 640.0) && approx(p[1], 360.0), "scale=1.5 の逆写像: {p:?}");
        assert!(inside);
    }

    /// ウルトラワイド（21:9 相当）では左右に帯が出る（ピラーボックス）。
    #[test]
    fn wider_window_gives_pillarbox() {
        // scale = min(2560/1280, 1080/720) = min(2.0, 1.5) = 1.5 → 1920x1080 を中央へ
        assert_rect(letterbox_rect(2560, 1080, 1280, 720), (320.0, 0.0, 1920.0, 1080.0));

        // 映像の左上隅は内部解像度の (0,0)
        let (p0, inside0) = window_to_internal([320.0, 0.0], (2560, 1080), (1280, 720));
        assert!(approx(p0[0], 0.0) && approx(p0[1], 0.0), "映像左上が原点: {p0:?}");
        assert!(inside0);

        // 左帯の上（x=100 < 320）は枠外。座標はクランプせず負値のまま返る。
        let (p1, inside1) = window_to_internal([100.0, 540.0], (2560, 1080), (1280, 720));
        assert!(!inside1, "左帯の上は inside=false であること");
        assert!(p1[0] < 0.0, "クランプせず負値を返すこと: {p1:?}");
    }

    /// 4:3 ウィンドウに 16:9 を収めると上下に帯が出る（レターボックス）。
    #[test]
    fn taller_window_gives_letterbox() {
        // scale = min(800/1280, 600/720) = min(0.625, 0.8333) = 0.625 → 800x450 を中央へ
        assert_rect(letterbox_rect(800, 600, 1280, 720), (0.0, 75.0, 800.0, 450.0));

        // 上帯の上（y=10 < 75）は枠外
        let (_, inside) = window_to_internal([400.0, 10.0], (800, 600), (1280, 720));
        assert!(!inside, "上帯の上は inside=false であること");
    }

    /// ウィンドウ幅・高さが 0（最小化中）でもパニックせず、NaN を出さないこと。
    #[test]
    fn zero_window_does_not_panic() {
        let r = letterbox_rect(0, 0, 1280, 720);
        assert!(r.w.is_finite() && r.h.is_finite(), "NaN/Inf を出さないこと: {r:?}");
        assert!(r.w > 0.0 && r.h > 0.0, "縮退しても正の大きさを保つこと: {r:?}");

        let (p, _) = window_to_internal([0.0, 0.0], (0, 0), (1280, 720));
        assert!(p[0].is_finite() && p[1].is_finite(), "NaN/Inf を出さないこと: {p:?}");

        // 内部解像度側が 0 でも同様（設定ミス耐性）。
        let r2 = letterbox_rect(1920, 1080, 0, 0);
        assert!(r2.w.is_finite() && r2.h.is_finite(), "NaN/Inf を出さないこと: {r2:?}");
    }

    /// クランプ: f32 誤差で 1px 未満はみ出した矩形が set_viewport の要件内へ収まること。
    #[test]
    fn clamp_fixes_subpixel_overflow() {
        // 意図的に負の x と、ターゲットをわずかに超える w を作る。
        let r = LetterboxRect { x: -0.00001, y: 0.0, w: 1000.00002, h: 600.0 };
        let c = clamp_rect_to_target(r, 1000.0, 600.0).expect("縮退していないので Some");
        assert!(c.x >= 0.0 && c.y >= 0.0, "左上が負にならないこと: {c:?}");
        assert!(
            c.x + c.w <= 1000.0 + EPS && c.y + c.h <= 600.0 + EPS,
            "右下がターゲットを超えないこと: {c:?}"
        );
    }

    /// クランプ: 通常の矩形は素通し（値が変わらない）。
    #[test]
    fn clamp_keeps_valid_rect() {
        let r = letterbox_rect(2560, 1080, 1280, 720);
        let c = clamp_rect_to_target(r, 2560.0, 1080.0).expect("有効な矩形");
        assert_rect(c, (r.x, r.y, r.w, r.h));
    }

    /// クランプ: 縮退（極小ウィンドウ・最小化中）は None を返し、呼び出し側がスキップできる。
    #[test]
    fn clamp_degenerate_returns_none() {
        // ターゲット自体が 0
        assert!(clamp_rect_to_target(letterbox_rect(1, 1, 1280, 720), 0.0, 0.0).is_none());
        // 幅 1px のウィンドウへ 1280x720 を収めると高さが 0.5625px へ縮退する
        // （scale = min(1/1280, 1000/720) = 1/1280 → h = 720/1280）。
        let r = letterbox_rect(1, 1000, 1280, 720);
        assert!(r.h < MIN_VIEWPORT_EXTENT_PX, "前提: 高さが 1px 未満へ縮退していること: {r:?}");
        assert!(
            clamp_rect_to_target(r, 1.0, 1000.0).is_none(),
            "縮退した矩形は None であること: {r:?}"
        );
        // 矩形が完全にターゲット外
        let out = LetterboxRect { x: 2000.0, y: 0.0, w: 100.0, h: 100.0 };
        assert!(clamp_rect_to_target(out, 1000.0, 600.0).is_none());
    }

    /// 逆変換の整合: 映像矩形の中心（ウィンドウ座標）は内部解像度の中心へ写る。
    /// 帯のある構成でも成り立つことを 2 パターンで確認する。
    #[test]
    fn center_maps_to_center() {
        for (win, internal) in [((2560u32, 1080u32), (1280u32, 720u32)), ((800, 600), (1280, 720))]
        {
            let r = letterbox_rect(win.0, win.1, internal.0, internal.1);
            let win_center = [r.x + r.w * HALF, r.y + r.h * HALF];
            let (p, inside) = window_to_internal(win_center, win, internal);
            assert!(
                approx(p[0], internal.0 as f32 * HALF) && approx(p[1], internal.1 as f32 * HALF),
                "映像中心 → 内部中心 (win={win:?} internal={internal:?}): {p:?}"
            );
            assert!(inside);
        }
    }
}
