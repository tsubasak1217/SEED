// ============================================================
//  ui_draw_pass.rs — UI 1 ゾーン分の「統合描画列」の構築と描画
//
//  【役割】
//  `ui_draw_order::merge_ui_draw_runs`（純ロジック）が決めた描画順に従って、
//  スプライト・スクリプト 2D プリミティブ・2D パーティクル・テキストを
//  **1 本の描画列**として GPU へ積み（`build`）、レンダーパスへ流す（`draw`）。
//
//  【なぜ必要か】
//  種別ごとに別々のリストを順番に描くと `layer` が種別内でしか効かず、
//  「テキストは常にスプライトより手前」という誤った重なりになる。
//  ここでラン（同一種別の連続区間）単位に分割し、種別境界でだけ
//  パイプラインを切り替えることで、レイヤー指定どおりの前後関係を実現する。
//
//  【ドローコール数】
//  隣接する同種別アイテムは 1 ランへ融合される。レイヤーの交互出現が無い
//  UI では従来と同じドローコール数（種別ごとに 1 本）で済み、
//  最悪ケース（1 アイテムごとにレイヤーと種別が入れ替わる UI）でのみ
//  アイテム数と同数のドローコールになる。
// ============================================================

use crate::engine::core::font::GpuTextBatch;
use crate::engine::core::font::canvas_text::{CanvasTextItem, CanvasTextRenderer, TextDrawRange};
use crate::engine::core::renderer::batch2d::{
    draw_sprite_batches, InstanceStream, SpriteBatchList, SpriteDrawItem,
};
use crate::engine::core::renderer::pipeline::SpritePipeline;
use crate::engine::core::renderer::primitive2d::pass::PrimitiveSpaceMap;
use crate::engine::core::renderer::primitive2d::{
    Primitive2dRenderer, PrimitiveCommand, PrimitiveRange,
};
use crate::engine::core::renderer::particle_system::ParticleSystem;
use crate::engine::core::renderer::pipeline::ParticlePipelines;
use crate::engine::core::renderer::ui_draw_order::{merge_ui_draw_runs, UiDrawKind};
use crate::engine::components::CanvasDrawZone;
use crate::engine::ecs::Entity;

// ─── 2D パーティクル描画アイテム ─────────────────────────────

/// 2D キャンバス配下の ParticleEmitter 1 スロット分の描画アイテム。
///
/// パーティクル本体（プール・パラメータ・テクスチャ）は `ParticleSystem` が
/// エミッタの entity をキーに保持しているため、ここでは**描画順を決めるための情報**
/// （どのエミッタか・どのゾーンか・どのレイヤーか）だけを持つ。
/// 座標変換は `ParticleSystem::upload_2d_world_mats` で GPU の uniform へ渡す。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct Particle2dDrawItem {
    /// ParticleEmitter スロットの entity（`ParticleSystem` のキー）。
    pub emitter: Entity,
    /// 「エミッタのローカル px → キャンバスワールド」GPU 行列の供給元アクター。
    ///
    /// スプライト／テキスト／スクリプトプリミティブとまったく同じ
    /// `node_mesh_gpu_mat` を使うため、canvas_collect が計算した行列をそのまま持つ。
    pub model: [[f32; 4]; 4],
    /// 描画ゾーン（背景／前面）。スプライトと同じ規約。
    pub zone: CanvasDrawZone,
    /// 描画優先度レイヤー（大きいほど手前）。スプライトと同じレイヤー空間。
    pub layer: i32,
}

// ─── 入力セグメント ──────────────────────────────────────────

/// 「レイヤー空間を共有する 1 かたまり」の UI アイテム群。
///
/// - 2D キャンバス（背景／前面ゾーン）: 全キャンバス横断でレイヤーを比較するため
///   ゾーンごとに **1 セグメント**。
/// - 3D ワールドキャンバス: レイヤーはキャンバス内で完結するため
///   **キャンバスごとに 1 セグメント**（セグメント同士は宣言順で前後する）。
///
/// 各リストは呼び出し側でレイヤー昇順に安定ソート済みであること。
#[derive(Default)]
pub struct UiDrawSegment {
    /// スプライト（レイヤー昇順・安定ソート済み）。
    pub sprites: Vec<SpriteDrawItem>,
    /// スクリプト 2D プリミティブのコマンド（レイヤー昇順・安定ソート済み）。
    pub primitives: Vec<PrimitiveCommand>,
    /// 2D パーティクル（レイヤー昇順・安定ソート済み）。
    pub particles: Vec<Particle2dDrawItem>,
    /// テキスト（レイヤー昇順・安定ソート済み）。
    pub texts: Vec<CanvasTextItem>,
}

impl UiDrawSegment {
    /// 4 種すべて空か。
    pub fn is_empty(&self) -> bool {
        self.sprites.is_empty()
            && self.primitives.is_empty()
            && self.particles.is_empty()
            && self.texts.is_empty()
    }
}

// ─── 構築済みラン ────────────────────────────────────────────

/// GPU 資源まで解決済みの 1 ラン。
enum UiZoneRun {
    /// スプライト（テクスチャ境界で融合したバッチ列）。
    Sprite(SpriteBatchList),
    /// スクリプト 2D プリミティブ（インデックス区間）。
    Primitive(PrimitiveRange),
    /// 2D パーティクル（このランに含まれるエミッタ entity 列）。
    ///
    /// パーティクルはエミッタごとに専用のバインドグループとプールを持つため
    /// バッチ融合できない（1 エミッタ = 1 ドローコール）。
    Particle(Vec<Entity>),
    /// テキスト（インデックス区間）。
    Text(TextDrawRange),
}

/// 1 ゾーン分の統合描画列。
///
/// `build` で GPU へ積み、`draw` でレンダーパスへ流す。
/// テキストの頂点バッファはゾーンにつき 1 本だけ作り、ランは区間で参照する。
#[derive(Default)]
pub struct UiZoneDraw {
    /// 描画順に並んだラン列。
    runs: Vec<UiZoneRun>,
    /// このゾーンのテキスト頂点バッチ（テキストが 1 文字も無ければ None）。
    text_gpu: Option<GpuTextBatch>,
}

/// `UiZoneDraw::build` に渡す構築パラメータ。
///
/// 引数が多くなるため 1 構造体へ束ねる（呼び出し側の可読性を優先）。
pub struct UiZoneBuildParams<'a> {
    /// プリミティブの座標空間マップ（キャンバスアクター entity → 行列）。
    pub prim_spaces: &'a PrimitiveSpaceMap,
    /// スクリーンスペース（`space = None`）プリミティブ用のモデル行列。
    pub prim_screen_model: &'a [[f32; 4]; 4],
    /// プリミティブを NDC 化するビュー射影行列（行優先）。
    pub prim_view_proj: &'a [[f32; 4]; 4],
    /// プリミティブを深度テスト付きで描くか（3D ワールドキャンバスのみ true）。
    pub prim_depth_tested: bool,
    /// テキストを NDC 化するビュー射影行列（行優先）。
    pub text_view_proj: &'a [[f32; 4]; 4],
}

impl UiZoneDraw {
    /// セグメント列から統合描画列を構築する。
    ///
    /// セグメントは渡された順に前後する（先のセグメントほど奥）。
    /// 各セグメント内は `(layer 昇順, 種別 昇順)` でマージされる。
    ///
    /// 呼び出し順の制約:
    /// - `sprite_stream` は事前に `begin()` 済みで、全 `build` 後に `upload()` すること。
    /// - `primitive2d` も同様（`begin()` → 各 `build` → `upload()`）。
    pub fn build(
        mut segments: Vec<UiDrawSegment>,
        sprite_stream: &mut InstanceStream,
        primitive2d: Option<&mut Primitive2dRenderer>,
        canvas_text: Option<&mut CanvasTextRenderer>,
        device: &wgpu::Device,
        queue: &wgpu::Queue,
        params: &UiZoneBuildParams<'_>,
    ) -> Self {
        // ── 1) 描画順（セグメント番号 + ラン）を決める ──────────────
        // レイヤー値だけを取り出して純ロジックへ渡す。
        let mut ordered: Vec<(usize, crate::engine::core::renderer::ui_draw_order::UiDrawRun)> =
            Vec::new();
        for (si, seg) in segments.iter().enumerate() {
            let sprite_layers: Vec<i32> = seg.sprites.iter().map(|it| it.layer).collect();
            let prim_layers: Vec<i32> = seg.primitives.iter().map(|c| c.layer).collect();
            let part_layers: Vec<i32> = seg.particles.iter().map(|it| it.layer).collect();
            let text_layers: Vec<i32> = seg.texts.iter().map(|it| it.layer).collect();
            for run in merge_ui_draw_runs(
                &sprite_layers,
                &prim_layers,
                &part_layers,
                &text_layers,
            ) {
                ordered.push((si, run));
            }
        }
        if ordered.is_empty() {
            return Self::default();
        }

        // ── 2) テキストを 1 本のバッチへ焼く（ランごとの区間付き）────
        // グループはラン順に並べるので、後段でラン列と 1:1 に取り出せる。
        let mut text_ranges: Vec<TextDrawRange> = Vec::new();
        let mut text_gpu: Option<GpuTextBatch> = None;
        if let Some(ct) = canvas_text {
            let groups: Vec<&[CanvasTextItem]> = ordered
                .iter()
                .filter(|(_, r)| r.kind == UiDrawKind::Text)
                .map(|(si, r)| &segments[*si].texts[r.start..r.end])
                .collect();
            if !groups.is_empty() {
                if let Some((gpu, ranges)) =
                    ct.build_grouped(device, queue, &groups, params.text_view_proj)
                {
                    text_gpu = Some(gpu);
                    text_ranges = ranges;
                }
            }
        }

        // ── 3) スプライトをセグメントごとに取り出す（ラン順に消費する）──
        let mut sprite_iters: Vec<std::vec::IntoIter<SpriteDrawItem>> = segments
            .iter_mut()
            .map(|seg| std::mem::take(&mut seg.sprites).into_iter())
            .collect();

        // ── 4) ラン順に GPU へ積む ─────────────────────────────────
        let mut primitive2d = primitive2d;
        let mut text_cursor = 0usize;
        let mut runs: Vec<UiZoneRun> = Vec::with_capacity(ordered.len());
        for (si, run) in &ordered {
            match run.kind {
                UiDrawKind::Sprite => {
                    // このランのぶんだけイテレータから取り出して push する。
                    // → テクスチャ融合はラン内で閉じる（ランを跨いだ融合は起きない）。
                    let list = sprite_stream.push(sprite_iters[*si].by_ref().take(run.len()));
                    runs.push(UiZoneRun::Sprite(list));
                }
                UiDrawKind::Primitive => {
                    if let Some(p) = primitive2d.as_deref_mut() {
                        let range = p.push(
                            &segments[*si].primitives[run.start..run.end],
                            params.prim_spaces,
                            params.prim_screen_model,
                            params.prim_view_proj,
                            params.prim_depth_tested,
                        );
                        runs.push(UiZoneRun::Primitive(range));
                    }
                }
                UiDrawKind::Particle => {
                    // パーティクルは GPU 資源をエミッタ側（ParticleSystem）が持つため、
                    // ここでは描画順に並んだ entity 列を控えるだけでよい。
                    runs.push(UiZoneRun::Particle(
                        segments[*si].particles[run.start..run.end]
                            .iter()
                            .map(|it| it.emitter)
                            .collect(),
                    ));
                }
                UiDrawKind::Text => {
                    // テキストランはグループと同順で並んでいる。
                    // バッチ構築に失敗した場合（フォント未初期化等）は区間が無いので飛ばす。
                    if let Some(range) = text_ranges.get(text_cursor).copied() {
                        runs.push(UiZoneRun::Text(range));
                    }
                    text_cursor += 1;
                }
            }
        }

        Self { runs, text_gpu }
    }

    /// 描くものが 1 つも無いか。
    pub fn is_empty(&self) -> bool {
        self.runs.is_empty()
    }

    /// スプライトの (ドローコール数, インスタンス数)。[PERF] 表示用。
    pub fn sprite_stats(&self) -> (usize, usize) {
        let mut draws = 0usize;
        let mut insts = 0usize;
        for r in &self.runs {
            if let UiZoneRun::Sprite(list) = r {
                draws += list.batches.len();
                insts += list.batches.iter().map(|b| b.count as usize).sum::<usize>();
            }
        }
        (draws, insts)
    }

    /// 統合描画列をレンダーパスへ流す（ラン順＝レイヤー順）。
    ///
    /// - `camera_bg`: スプライトパイプラインの group 0（プリミティブ／テキストの
    ///   頂点は CPU で NDC 化済みのためカメラバインドグループを使わない）。
    /// - `inst_buf`: `build` で使った `InstanceStream` の GPU バッファ。
    /// - `particles`: 2D パーティクルを描くための (システム, パイプライン)。
    ///   `None` を渡すとパーティクルランは黙って飛ばされる（描画順は変わらない）。
    #[allow(clippy::too_many_arguments)]
    pub fn draw<'rp>(
        &'rp self,
        pass: &mut wgpu::RenderPass<'rp>,
        sprite_pipeline: &'rp SpritePipeline,
        camera_bg: &'rp wgpu::BindGroup,
        inst_buf: &'rp wgpu::Buffer,
        primitive2d: Option<&'rp Primitive2dRenderer>,
        canvas_text: Option<&'rp CanvasTextRenderer>,
        particles: Option<(&'rp ParticleSystem, &'rp ParticlePipelines)>,
    ) {
        for run in &self.runs {
            match run {
                UiZoneRun::Sprite(list) => {
                    draw_sprite_batches(pass, sprite_pipeline, camera_bg, inst_buf, list);
                }
                UiZoneRun::Primitive(range) => {
                    if let Some(p) = primitive2d {
                        p.draw(range, pass);
                    }
                }
                UiZoneRun::Particle(emitters) => {
                    if let Some((sys, pl)) = particles {
                        // group0（camera）はパーティクル用パイプラインでも同一 BGL。
                        // スプライトのランがセットしたものと同じ BG なので毎ラン設定してよい。
                        pass.set_bind_group(0, camera_bg, &[]);
                        for e in emitters {
                            sys.draw_one_2d(pass, pl, *e);
                        }
                    }
                }
                UiZoneRun::Text(range) => {
                    if let (Some(ct), Some(gpu)) = (canvas_text, self.text_gpu.as_ref()) {
                        ct.draw_range(gpu, range, pass);
                    }
                }
            }
        }
    }
}
