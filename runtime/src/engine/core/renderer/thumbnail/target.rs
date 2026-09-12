// ============================================================
//  target.rs — サムネイル撮影専用のオフスクリーンカラーターゲット
// ------------------------------------------------------------
//  役割:
//    サムネイル（図鑑・プロジェクトパネルの両方）を撮るフレームの
//    「最終カラー出力先」をスワップチェーンの代わりに引き受ける。
//
//  なぜ要るのか:
//    以前は撮影フレームも**提示テクスチャ**（スワップチェーンの現在の裏バッファ）へ
//    描き、そこから読み戻していた。描いた絵はそのまま present されるので、
//    生成のあいだエディタのビューポートに被写体が映り込む
//    （連続生成では隔離ワールド線に留まる時間ぶん、最大 2 秒間）。
//    プロジェクトパネルは「フォルダを開くだけ」で生成が走るため目に付く。
//
//    ここで用意したテクスチャへ描けば present は一切起こらず、
//    ユーザーが見ている絵は撮影の前後で 1 ピクセルも変わらない。
//
//  なぜ「描画解像度と同じ大きさ」なのか（要求サイズではない理由）:
//    撮影フレームは通常フレームとまったく同じパス列（G-Buffer → ライティング →
//    トーンマップ → 最終合成、および ID パス）を通る。これら中間バッファは
//    すべて `Renderer::render_size()` を基準に確保されており、最終段
//    （`RenderFrame::present_to_swapchain`）だけが出力先を差し替えられる。
//    そこでこのターゲットも描画解像度ちょうどで確保し、**最終段の宛先だけ**を
//    swapchain から差し替える。これにより
//      - G-Buffer / 深度 / Hi-Z / ID バッファのサイズ規則を 1 つも変えずに済む
//      - カラーと ID バッファの解像度が必ず一致する
//        （`thumbnail_ops::compose_thumbnail` はこの一致を要求する）
//    という 2 点が同時に満たせる。要求サイズ（16〜512px）への縮小は、
//    従来どおり読み戻し後に「中央正方形を切り出して縮小」で行う。
//
//  寿命:
//    `Renderer` が 1 枚だけ抱え、サイズ（＝ウィンドウ）が変わったときだけ作り直す。
//    サムネイル生成は断続的に何十回も走るため、毎回確保・解放すると
//    そのたびにフルスクリーン 1 枚ぶんのアロケーションが発生する。
//    「サイズ別に 1 枚だけ再利用」がもっとも素直で、常駐コストは
//    1920×1080 × 4byte ≒ 8MB 程度（提示テクスチャ 1 枚ぶん）に収まる。
// ============================================================

// ─── 定数（マジックナンバー禁止）─────────────────────────────

/// オフスクリーンターゲットの一辺に許す最大ピクセル数。
///
/// wgpu の既定リミット `max_texture_dimension_2d`（8192）を超える要求は
/// テクスチャ生成時にパニックするため、その手前で丸める。
/// 実際にここへ当たるのは「巨大なマルチモニタ環境でウィンドウを最大化した」
/// ような極端な場合だけで、通常運転では素通りする。
pub const MAX_TARGET_EDGE_PX: u32 = 8192;

/// オフスクリーンターゲットに必要な使い方（ビット和）。
///
/// - `RENDER_ATTACHMENT`: 最終合成パス（FXAA／プレゼントコピー）の出力先になる
/// - `COPY_SRC`         : 読み戻し（`copy_texture_to_buffer`）の元になる
///
/// スワップチェーンの `SurfaceConfiguration.usage` と同じ組み合わせであり、
/// これにより「提示テクスチャへ描いていたときと同じコマンド列」がそのまま通る。
pub const TARGET_USAGE: wgpu::TextureUsages = wgpu::TextureUsages::RENDER_ATTACHMENT
    .union(wgpu::TextureUsages::COPY_SRC);

/// デバッグラベル（RenderDoc / wgpu のエラーメッセージに出る名前）。
const TARGET_LABEL: &str = "Thumbnail Offscreen Color";

// ============================================================
//  TargetSpec — 「どんなターゲットが要るか」だけを表す値
// ============================================================

/// オフスクリーンターゲットの仕様（大きさとフォーマット）。
///
/// GPU 資源を持たない純粋な値なので、確保・再利用の判断をテストできる。
#[derive(Debug, Clone, Copy, PartialEq, Eq)]
pub struct TargetSpec {
    /// 幅 [px]。
    pub width: u32,
    /// 高さ [px]。
    pub height: u32,
    /// テクスチャフォーマット。**必ずスワップチェーンと同じ値**にすること。
    ///
    /// 最終合成パス（`PostContext::present`）のパイプラインは初期化時に
    /// サーフェスフォーマットで作られており、別フォーマットのアタッチメントへ
    /// 描こうとすると wgpu のバリデーションで落ちる。
    pub format: wgpu::TextureFormat,
}

/// 要求された大きさを、テクスチャとして確保できる値へ整える。
///
/// # 戻り値
/// - `Some((w, h))`: そのまま確保してよい大きさ（上限で丸めた場合を含む）
/// - `None`        : 幅または高さが 0。テクスチャを作れないので撮影自体を諦める
///   （ウィンドウ最小化直後など。呼び出し側はフレームを捨てて次に備える）
pub fn sanitize_size(width: u32, height: u32) -> Option<(u32, u32)> {
    if width == 0 || height == 0 {
        return None;
    }
    Some((width.min(MAX_TARGET_EDGE_PX), height.min(MAX_TARGET_EDGE_PX)))
}

/// 手持ちのターゲットを作り直す必要があるか。
///
/// 「まだ無い」「大きさが違う」「フォーマットが違う」のいずれかなら作り直す。
/// それ以外は再利用する（毎回作り直すとウィンドウサイズのテクスチャ確保が
/// サムネイル 1 枚ごとに発生してしまう）。
pub fn needs_recreate(current: Option<TargetSpec>, requested: TargetSpec) -> bool {
    current != Some(requested)
}

// ============================================================
//  ThumbnailRenderTarget — 実体（テクスチャ + ビュー）
// ============================================================

/// サムネイル撮影フレームの最終カラー出力先。
pub struct ThumbnailRenderTarget {
    /// このターゲットの仕様（再利用判定に使う）。
    spec: TargetSpec,
    /// カラーテクスチャ本体（読み戻しの元になる）。
    texture: wgpu::Texture,
    /// 全体ビュー（レンダーパスのカラーアタッチメントに使う）。
    view: wgpu::TextureView,
}

impl ThumbnailRenderTarget {
    /// 仕様どおりのテクスチャを 1 枚確保する。
    ///
    /// `spec.width` / `spec.height` は [`sanitize_size`] を通した値であること
    /// （0 や上限超過はここでは検査しない。呼び出し側の責任で弾く）。
    pub fn new(device: &wgpu::Device, spec: TargetSpec) -> Self {
        let texture = device.create_texture(&wgpu::TextureDescriptor {
            label: Some(TARGET_LABEL),
            size: wgpu::Extent3d {
                width: spec.width,
                height: spec.height,
                depth_or_array_layers: 1,
            },
            mip_level_count: 1,
            sample_count: 1,
            dimension: wgpu::TextureDimension::D2,
            format: spec.format,
            usage: TARGET_USAGE,
            view_formats: &[],
        });
        let view = texture.create_view(&wgpu::TextureViewDescriptor::default());
        Self { spec, texture, view }
    }

    /// このターゲットの仕様。
    pub fn spec(&self) -> TargetSpec {
        self.spec
    }

    /// カラーテクスチャ本体（読み戻しのコピー元）。
    pub fn texture(&self) -> &wgpu::Texture {
        &self.texture
    }

    /// カラーアタッチメント用のビュー。
    pub fn view(&self) -> &wgpu::TextureView {
        &self.view
    }
}

// ============================================================
//  単体テスト（GPU を使わない純粋ロジックのみ）
// ============================================================

#[cfg(test)]
mod tests {
    use super::*;

    /// テスト用の既定フォーマット（実機のスワップチェーンでよく出る値）。
    const FMT: wgpu::TextureFormat = wgpu::TextureFormat::Bgra8UnormSrgb;

    /// 幅・高さが 0 のときは確保を諦めること（最小化直後の 0×0 対策）。
    #[test]
    fn sanitize_rejects_zero_size() {
        assert_eq!(sanitize_size(0, 720), None);
        assert_eq!(sanitize_size(1280, 0), None);
        assert_eq!(sanitize_size(0, 0), None);
    }

    /// 通常の大きさはそのまま通ること。
    #[test]
    fn sanitize_passes_through_normal_size() {
        assert_eq!(sanitize_size(1280, 720), Some((1280, 720)));
        assert_eq!(sanitize_size(1, 1), Some((1, 1)));
    }

    /// 上限を超える要求は丸めること（テクスチャ生成のパニックを手前で防ぐ）。
    #[test]
    fn sanitize_clamps_to_max_edge() {
        let huge = MAX_TARGET_EDGE_PX * 2;
        assert_eq!(
            sanitize_size(huge, huge),
            Some((MAX_TARGET_EDGE_PX, MAX_TARGET_EDGE_PX))
        );
        // 片側だけ超える場合は、超えた側だけが丸められること
        assert_eq!(sanitize_size(huge, 720), Some((MAX_TARGET_EDGE_PX, 720)));
    }

    /// 手持ちが無ければ必ず作ること。
    #[test]
    fn recreate_when_absent() {
        let want = TargetSpec { width: 1280, height: 720, format: FMT };
        assert!(needs_recreate(None, want));
    }

    /// まったく同じ仕様なら作り直さない（連続生成でアロケーションを繰り返さない）。
    #[test]
    fn reuse_when_spec_matches() {
        let want = TargetSpec { width: 1280, height: 720, format: FMT };
        assert!(!needs_recreate(Some(want), want));
    }

    /// 大きさ・フォーマットのどれか 1 つでも違えば作り直すこと。
    #[test]
    fn recreate_when_any_field_differs() {
        let have = TargetSpec { width: 1280, height: 720, format: FMT };
        assert!(needs_recreate(
            Some(have),
            TargetSpec { width: 1920, ..have }
        ));
        assert!(needs_recreate(
            Some(have),
            TargetSpec { height: 1080, ..have }
        ));
        assert!(needs_recreate(
            Some(have),
            TargetSpec { format: wgpu::TextureFormat::Rgba8UnormSrgb, ..have }
        ));
    }

    /// 使い方フラグに「描画先」と「読み戻し元」の両方が入っていること。
    ///
    /// どちらが欠けても撮影は成立しない
    /// （RENDER_ATTACHMENT が無ければ描けず、COPY_SRC が無ければ読み戻せない）。
    #[test]
    fn usage_allows_render_and_readback() {
        assert!(TARGET_USAGE.contains(wgpu::TextureUsages::RENDER_ATTACHMENT));
        assert!(TARGET_USAGE.contains(wgpu::TextureUsages::COPY_SRC));
    }
}
