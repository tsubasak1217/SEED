// ============================================================
//  model_component.rs — ModelComponent
//
//  モデル描画に必要なデータを保持する純粋データコンポーネント。
//  ライフサイクルロジックを持たない（GPU バッチ更新は System が担う）。
//
//  1 エンティティにつき 1 つの ModelComponent を持てる。
//  複数のモデルが必要な場合は子 Actor を作成してそれぞれに持たせる。
// ============================================================

use std::any::Any;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use serde::{Deserialize, Serialize};
use crate::engine::ecs::Component;
use crate::engine::components::Transform;
use crate::engine::core::loader::model::Model;
use crate::engine::methods::gizmo_interact::mat4x4_mul;
use crate::engine::methods::drawer::{GpuModel, InstancedModelBatch};
use super::material_override::{MaterialOverride, MaterialOverrideKind, overrides_signature};

/// `batch_key()` でオーバーライド署名を source_path に連結する際の区切り文字。
/// ファイルパスに通常出現しない制御文字（SOH）を使い、パス文字列との衝突を避ける。
const BATCH_KEY_SEPARATOR: char = '\u{1}';

/// インライン編集の「安定バッチキー」に付ける接頭辞。
/// 署名（16 進ハッシュ = `[0-9a-f]` のみ）と衝突しないよう `#` を先頭に置く。
const INLINE_STABLE_KEY_PREFIX: char = '#';

/// MC ごとに一意な「バッチインスタンス ID」を採番するプロセス内カウンタ。
///
/// 【なぜ必要か】インラインオーバーライドは per-instance の値編集用途であり、値をドラッグ
/// 編集するたびに署名（＝バッチキー）が変わると統合バッチ・GpuModel・BLAS が丸ごと再生成され、
/// VRAM が瞬間 2 倍需要 → OOM を起こしていた（本修正の対象）。そこでインラインを含む MC の
/// バッチキーを「値に依存しない安定キー（source_path ＋ この ID）」にする。値をいくら編集しても
/// キーが不変になり、既存 GpuModel の material uniform を in-place 更新するだけで済む
/// （バッチ・BLAS 再構築ゼロ）。
///
/// 揮発（非シリアライズ）で構わない。バッチキーはフレーム内グルーピングにしか使わず、
/// セッションをまたいで安定である必要はない（再ロード時は GpuModel ごと作り直すため）。
static NEXT_BATCH_INSTANCE_ID: AtomicU64 = AtomicU64::new(1);

/// 新しい MC 用のバッチインスタンス ID を採番する（プロセス内で単調増加・一意）。
pub fn next_batch_instance_id() -> u64 {
    NEXT_BATCH_INSTANCE_ID.fetch_add(1, Ordering::Relaxed)
}

// ─── 定数 ─────────────────────────────────────────────────────────────────────

/// グループ ID はこの値以上（インスタンスインデックスと衝突しない）
pub const GROUP_ID_BASE: u32 = 1_000_000;

fn default_next_group_id() -> u32 { GROUP_ID_BASE }

/// cast_shadows の既定値（true）。シャドウマップレンダリングで使用する
/// （LightComponent.cast_shadows と同一の慣例。旧 .scene には存在しない
/// フィールドのため、欠落時は #[serde(default = ...)] でこの値にフォールバックする）。
fn default_cast_shadows() -> bool { true }

// ─── 描画オフセットトランスフォーム（既定値） ────────────────────────────────
//
// 【何のための機能か】
// アクタの Transform（ワールド）はそのままに、**モデルの描画だけ**をローカルに
// ずらす・回す・拡縮するための補正値。用途は主に 2 つ:
//   - モデルの原点ズレ補正（glTF の原点が足元でなく中心にある等）
//   - 「持ち手」合わせ（釣り竿を手にアタッチした際のグリップ位置合わせ）
//
// 【適用範囲】描画のみ。物理コライダー・Transform・instance_mats には一切影響しない
// （詳細は `ModelComponent::render_matrix` のコメント参照）。

/// offset_position の既定値（＝ずらさない）。
pub const OFFSET_POSITION_DEFAULT: [f32; 3] = [0.0, 0.0, 0.0];
/// offset_rotation の既定値（YXZ オイラー角・度。＝回さない）。
pub const OFFSET_ROTATION_DEFAULT: [f32; 3] = [0.0, 0.0, 0.0];
/// offset_scale の既定値（＝拡縮しない）。
pub const OFFSET_SCALE_DEFAULT: [f32; 3] = [1.0, 1.0, 1.0];

/// 旧 `.scene`（オフセット未導入）互換のための serde 既定値（位置）。
fn default_offset_position() -> [f32; 3] { OFFSET_POSITION_DEFAULT }
/// 旧 `.scene` 互換のための serde 既定値（回転・度）。
fn default_offset_rotation() -> [f32; 3] { OFFSET_ROTATION_DEFAULT }
/// 旧 `.scene` 互換のための serde 既定値（スケール）。
fn default_offset_scale() -> [f32; 3] { OFFSET_SCALE_DEFAULT }

// ─── InstanceMeta ─────────────────────────────────────────────────────────────

/// インスタンスごとのメタデータ（ヒエラルキー・アニメーション）。
#[derive(Clone, Serialize, Deserialize)]
pub struct InstanceMeta {
    pub name:      String,
    pub parent:    Option<u32>,
    /// 【旧機能・シーン互換のため残置】位相シード付き群衆デモ再生（廃止済み）で
    /// 使用していた安定アニメーション位相シード。現在は参照されないが、
    /// 既存 .scene に保存済みのため serde 互換維持でフィールドのみ残す。
    #[serde(default)]
    pub anim_seed: u32,
}

impl InstanceMeta {
    pub fn new(name: impl Into<String>) -> Self {
        Self { name: name.into(), parent: None, anim_seed: 0 }
    }
}

// ─── GroupMeta ────────────────────────────────────────────────────────────────

/// グループフォルダのメタデータ（描画なし・ヒエラルキー整理用）。
#[derive(Clone, Serialize, Deserialize)]
pub struct GroupMeta {
    pub id:     u32,
    pub name:   String,
    pub parent: Option<u32>,
}

// ─── ModelComponentData ───────────────────────────────────────────────────────

/// シリアライズ用データ（JSON 保存・Undo スナップショット）。
#[derive(Clone, Serialize, Deserialize)]
pub struct ModelComponentData {
    pub model_path:    String,
    pub instances:     Vec<[[f32; 4]; 4]>,
    #[serde(default)]
    pub meta:          Vec<InstanceMeta>,
    #[serde(default)]
    pub groups:        Vec<GroupMeta>,
    #[serde(default = "default_next_group_id")]
    pub next_group_id: u32,
    /// 影を落とすか（シャドウマップレンダリングで使用）。既定 true。
    #[serde(default = "default_cast_shadows")]
    pub cast_shadows: bool,
    /// マテリアルスロットごとのオーバーライド（Phase R7）。
    /// 旧 .scene にはフィールドが存在しないため、欠落時は空 Vec（=オーバーライド無し）にフォールバックする。
    #[serde(default)]
    pub material_overrides: Vec<MaterialOverride>,
    /// セマンティックタグ（0..15。0 = タグ無し）。
    /// 旧 .scene にはフィールドが無いため欠落時は 0（＝タグ無し・従来と完全に同じ描画）。
    #[serde(default)]
    pub render_tag: u8,
    /// LOD（距離による簡略メッシュ切替）を適用しないか。既定 false（＝従来どおり適用）。
    /// 旧 `.scene` にはフィールドが無いため欠落時は false へフォールバックする。
    #[serde(default)]
    pub disable_lod: bool,
    /// 描画オフセット: 位置（アクタのローカル空間・既定 [0,0,0]）。
    /// 旧 .scene には無いため欠落時は既定へフォールバックし、従来と完全に同じ描画になる。
    #[serde(default = "default_offset_position")]
    pub offset_position: [f32; 3],
    /// 描画オフセット: 回転（YXZ オイラー角・度・既定 [0,0,0]）。
    #[serde(default = "default_offset_rotation")]
    pub offset_rotation: [f32; 3],
    /// 描画オフセット: スケール（既定 [1,1,1]）。
    #[serde(default = "default_offset_scale")]
    pub offset_scale: [f32; 3],
}

// ─── ModelAnimDrive ─────────────────────────────────────────────────────────

/// Animator が駆動する glTF 内蔵アニメの再生状態（揮発・非シリアライズ）。
///
/// `AnimatorComponent` の kind=Model クリップ再生中、`animation_ops::update_animations`
/// が毎フレームこの値を書き込む。`Some` のときレンダラのスキニングは `time` を
/// 権威時刻として使う。`None` のとき（Animator 無し／非再生）モデルは静止する
/// （animations[0] の t=0 で凍結。旧仕様のグローバルクロックによるデモ再生は廃止済み）。
///
/// 【複数アニメ】GPU スキニング（`SkinComputeSystem`）はモデル内の全アニメを
/// パッキング済みなので、`anim_idx` には任意の index を指定できる
/// （同一モデルのインスタンスごとに別アニメを再生できる）。
///
/// 【クロスフェード】クリップ切替中は `fade_from`（フェード元アニメの index と時刻）と
/// `weight`（0→1 で現在クリップへ遷移）を併せて持つ。`fade_from == None` かつ
/// `weight == 1.0` がフェード無しの通常再生で、従来と同一のポーズになる。
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ModelAnimDrive {
    /// 再生対象アニメの Model::animations インデックス（現在クリップ＝フェード先）
    pub anim_idx: usize,
    /// 権威再生時刻（秒。ループ/クランプ後の 0..=duration 正規化済み）
    pub time:     f32,
    /// 再生中フラグ（false = 一時停止・停止でこの時刻を保持）
    pub playing:  bool,
    /// フェード元アニメの (index, 正規化済み時刻)。None = フェードしていない。
    pub fade_from: Option<(usize, f32)>,
    /// ブレンド率（0 = フェード元のみ / 1 = 現在クリップのみ）。
    /// `fade_from` が None のときは常に 1.0。
    pub weight:   f32,
}

impl ModelAnimDrive {
    /// フェード無し（単一クリップ）の駆動状態を作る。
    pub fn single(anim_idx: usize, time: f32, playing: bool) -> Self {
        Self { anim_idx, time, playing, fade_from: None, weight: 1.0 }
    }
}

// ─── ModelComponent ───────────────────────────────────────────────────────────

/// Actor にアタッチするモデルコンポーネント。
/// GPU リソース (GpuModel, InstancedModelBatch) を含む純粋データ構造。
pub struct ModelComponent {
    pub source_path:     String,
    /// CPU モデルデータ。Arc 共有でモデルキャッシュを実現する（同一パスの GPU リソース再生成コスト削減）。
    pub model:           Option<Arc<Model>>,
    pub gpu_model:       Option<GpuModel>,
    pub instanced_batch: Option<InstancedModelBatch>,
    pub instance_mats:   Vec<[[f32; 4]; 4]>,
    pub instance_meta:   Vec<InstanceMeta>,
    pub group_meta:      Vec<GroupMeta>,
    pub next_group_id:   u32,
    /// Animator 駆動のアニメ再生状態（揮発。None = デモ再生 / Animator 非駆動）
    pub anim_drive:      Option<ModelAnimDrive>,
    /// 影を落とすか（シャドウマップレンダリングで使用）。既定 true。
    pub cast_shadows:    bool,
    /// マテリアルスロットごとのオーバーライド（Phase R7）。
    /// GpuModel 構築時にこの内容が `apply_overrides` で焼き込まれる。
    /// `batch_key()` の署名計算にも使われる（インスタンスバッチのマージキー）。
    pub material_overrides: Vec<MaterialOverride>,
    /// このアクタ（モデル）のセマンティックタグ（0..15。0 = タグ無し）。
    ///
    /// 「このアクタは敵」「インタラクト可能」といった**意味**を描画側へ伝えるための値で、
    /// G-Buffer RT3.a へ 4bit で焼かれ、将来の合成（第 3 層）が 1 ピクセル単位で引ける。
    /// ID バッファ（per-actor の厳密なマスク）より粗いが、読み戻しもテクスチャ追加も不要で
    /// 「敵だけ縁取る」「インタラクト可能物だけ光らせる」といった用途はこれで足りる。
    ///
    /// 配管経路: 本フィールド → 統合バッチの `render_tags` → `ModelUniform` の
    /// インスタンス拡張スロット（normal_matrix 4 列目）→ VertexOutput（flat）→ RT3.a。
    /// タグはアクタ（MC）単位で、その MC の全インスタンスに同じ値が複製される。
    ///
    /// 有効ビット幅は `renderer::surface_id::RENDER_TAG_BITS`。範囲外の値は
    /// GPU へ渡す直前にマスクされる（隣のビットを侵食しない）。
    pub render_tag:      u8,
    /// このモデルへ距離 LOD を適用しないか（既定 false ＝従来どおり適用）。
    ///
    /// true のとき、この MC の全インスタンスはカメラ距離に関係なく **常に LOD0
    /// （フル解像度）** で描かれる。近景で常に最高品質を保ちたいヒーローアセットや、
    /// LOD 生成で形が崩れるモデルの救済用。
    ///
    /// 配管経路: 本フィールド → 統合バッチの `disable_lods` →
    /// `InstancedModelBatch::set_disable_lod_flags` → LOD 振り分け。
    /// LOD の振り分け結果は通常描画・シャドウマップ・ID パス・アウトライン・
    /// 半透明のすべてが同じ `lod_visible_counts` を共有するため、この 1 フラグが
    /// 全ラスタ経路へ一貫して効く。RT（BLAS）はもともと LOD0 のインデックス
    /// バッファのみで構築されるため、この設定にかかわらず常に LOD0 である。
    pub disable_lod:     bool,
    /// 描画オフセット: 位置（アクタのローカル空間。既定 [0,0,0] ＝ずらさない）。
    ///
    /// アクタの `Transform` は動かさず、**このモデルの描画だけ**をローカルにずらす。
    /// 適用は `render_matrix()` 1 箇所に集約されており、通常描画・スキン・LOD・
    /// シャドウ・RT(BLAS/TLAS)・ID ピッキング・アウトラインの全経路が同じ行列を通る。
    pub offset_position: [f32; 3],
    /// 描画オフセット: 回転（YXZ オイラー角・度。既定 [0,0,0]）。
    /// 回転規約は `Transform`（正典）と同一。
    pub offset_rotation: [f32; 3],
    /// 描画オフセット: スケール（既定 [1,1,1]）。
    pub offset_scale: [f32; 3],
    /// この MC を一意に識別する揮発 ID（非シリアライズ）。
    /// インラインオーバーライドを持つ MC の `batch_key()` に使い、値編集でキーが変わらない
    /// 「安定バッチキー」を実現する（詳細は `next_batch_instance_id` のコメント参照）。
    pub batch_instance_id: u64,
}

impl ModelComponent {
    /// モデルが未設定の空コンポーネントを作成する。
    pub fn empty() -> Self {
        Self {
            source_path:     String::new(),
            model:           None,
            gpu_model:       None,
            instanced_batch: None,
            instance_mats:   Vec::new(),
            instance_meta:   Vec::new(),
            group_meta:      Vec::new(),
            next_group_id:   GROUP_ID_BASE,
            anim_drive:      None,
            cast_shadows:    true,
            material_overrides: Vec::new(),
            // タグ無し（既定）。0 は「未設定」を表す予約値。
            render_tag:      crate::engine::core::renderer::surface_id::RENDER_TAG_NONE,
            // LOD は既定で適用する（従来と 1 ビットも変わらない描画）。
            disable_lod:     false,
            // 描画オフセットは既定＝恒等（従来と 1 ビットも変わらない描画）。
            offset_position: OFFSET_POSITION_DEFAULT,
            offset_rotation: OFFSET_ROTATION_DEFAULT,
            offset_scale:    OFFSET_SCALE_DEFAULT,
            batch_instance_id: next_batch_instance_id(),
        }
    }

    pub fn is_loaded(&self) -> bool { self.model.is_some() }

    /// instanced_batch に「次回更新が必要」フラグを立てる。
    pub fn mark_batch_dirty(&mut self) {
        if let Some(b) = &mut self.instanced_batch { b.mark_dirty(); }
    }

    /// インスタンスバッチのマージキーを返す（frame_renderer 側の shared_model_batches が使用）。
    ///
    /// マテリアルオーバーライドが無ければ `source_path` とビット単位で完全一致する
    /// （＝旧シーン・オーバーライド未使用モデルの描画経路・性能を一切変えない）。
    /// オーバーライドがある場合のみ、オーバーライドの内容から決まる署名を
    /// 区切り文字（SOH）で連結し、異なるオーバーライドを持つ ModelComponent 同士が
    /// 誤って同一バッチにマージされないようにする（per-アクタ整合、方式(a)の最軽量形）。
    /// オーバーライドの中に 1 件でも Inline があるか（＝安定キーを使うべきか）。
    fn has_inline_override(&self) -> bool {
        self.material_overrides.iter()
            .any(|o| matches!(o.kind, MaterialOverrideKind::Inline { .. }))
    }

    pub fn batch_key(&self) -> String {
        // ① オーバーライド無し → source_path とビット一致（旧シーン・性能を一切変えない）。
        if self.material_overrides.is_empty() {
            return self.source_path.clone();
        }
        // ② Inline を含む → 値に依存しない「安定キー」（source_path ＋ 一意 ID）。
        //    インラインは per-instance の値編集用途なので、値をいくら編集してもキーが変わらず、
        //    統合バッチ・GpuModel・BLAS の再生成が一切起きない（in-place uniform 更新で反映）。
        //    副作用: 同一インライン値を持つ別 MC 同士はインスタンシングされなくなる（=別バッチ）。
        //    per-instance 編集用途では実害がなく、OOM 回避の利益が上回るため許容する。
        if self.has_inline_override() {
            return format!(
                "{}{BATCH_KEY_SEPARATOR}{INLINE_STABLE_KEY_PREFIX}{}",
                self.source_path, self.batch_instance_id,
            );
        }
        // ③ MatAsset のみ → 従来どおり署名キー。同じ .mat を共有する複数 MC の
        //    インスタンシング（1 バッチ統合）を保つ。
        let sig = overrides_signature(&self.material_overrides);
        format!("{}{BATCH_KEY_SEPARATOR}{}", self.source_path, sig)
    }

    pub fn rendering_refs(&self) -> Option<(&GpuModel, &InstancedModelBatch)> {
        match (&self.gpu_model, &self.instanced_batch) {
            (Some(gpu), Some(batch)) => Some((gpu, batch)),
            _ => None,
        }
    }

    // ─── インスタンス階層ヘルパー ──────────────────────────────

    /// 指定インスタンスの直接の子インスタンスインデックス一覧を返す。
    pub fn children_of(&self, idx: u32) -> Vec<u32> {
        self.instance_meta.iter().enumerate()
            .filter(|(_, m)| m.parent == Some(idx))
            .map(|(i, _)| i as u32)
            .collect()
    }

    /// 指定インスタンスの全子孫インデックスを BFS で収集する。
    pub fn all_descendants(&self, root: u32) -> Vec<u32> {
        let mut result = Vec::new();
        let mut queue  = std::collections::VecDeque::new();
        queue.extend(self.children_of(root));
        while let Some(idx) = queue.pop_front() {
            result.push(idx);
            queue.extend(self.children_of(idx));
        }
        result
    }

    /// 選択セットのうち「他の選択インスタンスの子孫でないもの」を返す（ルート選択）。
    pub fn filter_selection_roots(&self, selected: &[u32]) -> Vec<u32> {
        let set: std::collections::HashSet<u32> = selected.iter().copied().collect();
        selected.iter().copied().filter(|&idx| {
            let mut cur = self.instance_meta.get(idx as usize).and_then(|m| m.parent);
            while let Some(p) = cur {
                if set.contains(&p) { return false; }
                cur = self.instance_meta.get(p as usize).and_then(|m| m.parent);
            }
            true
        }).collect()
    }

    /// roots の全子孫のうち roots 自身に含まれないものを (index, start_mat) で収集する。
    pub fn collect_non_root_descendants(&self, roots: &[u32]) -> Vec<(u32, [[f32; 4]; 4])> {
        let roots_set: std::collections::HashSet<u32> = roots.iter().copied().collect();
        let mut result = Vec::new();
        for &root in roots {
            self.collect_desc_inner(root, &roots_set, &mut result);
        }
        result
    }

    fn collect_desc_inner(
        &self,
        idx:       u32,
        roots_set: &std::collections::HashSet<u32>,
        result:    &mut Vec<(u32, [[f32; 4]; 4])>,
    ) {
        for child in self.children_of(idx) {
            if !roots_set.contains(&child) {
                if let Some(&mat) = self.instance_mats.get(child as usize) {
                    result.push((child, mat));
                }
                self.collect_desc_inner(child, roots_set, result);
            }
        }
    }

    // ─── 描画オフセット（表示だけをローカルに補正する） ────────────

    /// 描画オフセットが既定（恒等）かどうか。
    ///
    /// 既定なら `render_matrix()` は入力行列をそのまま返す（浮動小数の丸めすら発生しない）ため、
    /// オフセット未使用のシーンは従来と**ビット単位で同一**の行列で描画される。
    #[inline]
    pub fn has_offset(&self) -> bool {
        self.offset_position != OFFSET_POSITION_DEFAULT
            || self.offset_rotation != OFFSET_ROTATION_DEFAULT
            || self.offset_scale != OFFSET_SCALE_DEFAULT
    }

    /// 描画オフセットの TRS 行列（行優先）を返す。
    ///
    /// 回転規約（YXZ オイラー角・度）を `Transform`（正典）へ委譲することで、
    /// アクタ Transform とオフセットで回転の解釈がズレないようにしている。
    #[inline]
    pub fn offset_matrix(&self) -> [[f32; 4]; 4] {
        Transform {
            position: self.offset_position,
            rotation: self.offset_rotation,
            scale:    self.offset_scale,
        }
        .to_mat4()
    }

    /// アクタのワールド行列（＝`instance_mats` の 1 要素）へ描画オフセットを適用し、
    /// **GPU へ渡す最終インスタンス行列**を返す【適用の唯一の集約点】。
    ///
    ///   instance = actor_world * offset_trs
    ///
    /// 右から掛けるので、オフセットは「アクタのローカル空間での補正」になる
    /// （アクタが回っていればオフセットも一緒に回る。原点ズレ補正・持ち手合わせの直感どおり）。
    ///
    /// 【なぜここ 1 箇所なのか】描画は通常モデル・スキン・LOD・シャドウ・RT(BLAS/TLAS)・
    /// ID ピッキング・アウトラインまで、すべて `frame_renderer` の統合バッチ
    /// （`shared_model_batches`）へ積まれた行列を共有する。したがって統合バッチへ
    /// 積む瞬間にこの関数を通せば、全経路へ一貫して効く。
    ///
    /// 【物理には効かない（仕様）】オフセットは `instance_mats` にも `Transform` にも
    /// 書き戻さない。したがってコライダー・レイキャスト・JointAttach・アニメーションは
    /// 一切影響を受けない（見た目だけの補正）。また `.scene` に保存されるのは
    /// オフセット値そのものだけで、ワールド空間で保存される `instance_mats`
    /// （プレハブの再基準化が前提とする値）へは焼き込まれないため、
    /// 保存 → ロードで二重適用されることもない。
    #[inline]
    pub fn render_matrix(&self, actor_world: [[f32; 4]; 4]) -> [[f32; 4]; 4] {
        if !self.has_offset() {
            return actor_world;
        }
        mat4x4_mul(actor_world, self.offset_matrix())
    }

    // ─── シリアライズ ─────────────────────────────────────────

    /// シリアライズ用データに変換する。
    pub fn to_data(&self) -> ModelComponentData {
        ModelComponentData {
            model_path:    self.source_path.clone(),
            instances:     self.instance_mats.clone(),
            meta:          self.instance_meta.clone(),
            groups:        self.group_meta.clone(),
            next_group_id: self.next_group_id,
            cast_shadows:  self.cast_shadows,
            material_overrides: self.material_overrides.clone(),
            render_tag:    self.render_tag,
            disable_lod:   self.disable_lod,
            offset_position: self.offset_position,
            offset_rotation: self.offset_rotation,
            offset_scale:    self.offset_scale,
        }
    }
}

// ECS コンポーネントとして登録
impl Component for ModelComponent {}

// ============================================================
//  テスト（安定バッチキー）
// ============================================================

#[cfg(test)]
mod batch_key_tests {
    use super::*;
    use crate::engine::components::material_override::{MaterialOverride, MaterialOverrideKind};

    /// source_path だけ設定した空 MC を作る（GPU リソースなし）。
    fn mc_with(path: &str, ovr: Vec<MaterialOverride>) -> ModelComponent {
        let mut mc = ModelComponent::empty();
        mc.source_path = path.to_string();
        mc.material_overrides = ovr;
        mc
    }

    fn inline_base_color(c: [f32; 4]) -> MaterialOverride {
        MaterialOverride {
            slot: 0,
            kind: MaterialOverrideKind::Inline {
                base_color: Some(c),
                metallic: None, roughness: None, emissive: None,
                alpha_mode: None, alpha_cutoff: None, ior: None, transmission: None, diffuse_transmission: None, mr_tex_ignore: None, cull_face: None,
                shading_model: None,
            },
        }
    }

    /// オーバーライド無しは source_path とビット一致（旧挙動不変）。
    #[test]
    fn no_override_key_equals_source_path() {
        let mc = mc_with("chess.glb", vec![]);
        assert_eq!(mc.batch_key(), "chess.glb");
    }

    /// インライン値を変更してもバッチキーは不変（安定キー）。
    #[test]
    fn inline_value_change_keeps_key_stable() {
        let mut mc = mc_with("chess.glb", vec![inline_base_color([1.0, 0.0, 0.0, 1.0])]);
        let k1 = mc.batch_key();
        // 値だけ変える（署名は変わるが、安定キーなのでバッチキーは不変であるべき）。
        mc.material_overrides = vec![inline_base_color([0.0, 1.0, 0.0, 1.0])];
        let k2 = mc.batch_key();
        assert_eq!(k1, k2, "インライン値編集でバッチキーが変わってはならない");
        // 安定キーは source_path を接頭辞に持つ。
        assert!(k1.starts_with("chess.glb"), "安定キーは source_path 起点であること");
    }

    /// エンティティ（MC）が違えばインラインキーも違う（別バッチに分離）。
    #[test]
    fn different_mc_have_different_inline_keys() {
        let a = mc_with("chess.glb", vec![inline_base_color([1.0, 0.0, 0.0, 1.0])]);
        let b = mc_with("chess.glb", vec![inline_base_color([1.0, 0.0, 0.0, 1.0])]);
        assert_ne!(a.batch_key(), b.batch_key(),
            "別 MC の同一インライン値でもキーは分かれること（per-instance 編集）");
    }

    /// MatAsset は署名キー（同一 .mat 共有 MC は同一キー＝インスタンシング維持）。
    #[test]
    fn mat_asset_uses_signature_key_shared_across_mcs() {
        let asset = |p: &str| MaterialOverride {
            slot: 0,
            kind: MaterialOverrideKind::MatAsset { path: p.to_string() },
        };
        let a = mc_with("chess.glb", vec![asset("assets://red.mat")]);
        let b = mc_with("chess.glb", vec![asset("assets://red.mat")]);
        // 同一 .mat → 同一署名キー（別 MC でも一致）。
        assert_eq!(a.batch_key(), b.batch_key(), "同一 .mat 共有 MC は同一キーであること");
        // 別 .mat → キーが変わる。
        let c = mc_with("chess.glb", vec![asset("assets://blue.mat")]);
        assert_ne!(a.batch_key(), c.batch_key(), ".mat が変われば署名キーも変わること");
    }
}

// ============================================================
//  テスト（material_overrides の serde ラウンドトリップ）
//
//  シーン保存→ロードで material_overrides の全フィールド
//  （ior / transmission / cull_face / mr_tex_ignore / shading_model 含む）が
//  往復することを保証する回帰テスト。`.scene` は ModelComponentData を
//  そのまま JSON 化する（scene.rs の SceneData 経由）ため、
//  ここで ModelComponentData の JSON 往復を検証すれば保存経路全体を代表できる。
// ============================================================

#[cfg(test)]
mod override_serde_tests {
    use super::*;
    use crate::engine::components::material_override::{MaterialOverride, MaterialOverrideKind};

    /// 全フィールドを埋めたインライン + MatAsset の 2 スロット構成で
    /// JSON へシリアライズ → デシリアライズしても内容が 1 ビットも変わらないこと。
    #[test]
    fn model_component_data_material_overrides_roundtrip() {
        let original = ModelComponentData {
            model_path:    "assets://chess.glb".to_string(),
            instances:     vec![[[1.0, 0.0, 0.0, 0.0],
                                 [0.0, 1.0, 0.0, 0.0],
                                 [0.0, 0.0, 1.0, 0.0],
                                 [0.0, 0.0, 0.0, 1.0]]],
            meta:          vec![InstanceMeta::new("Instance_0")],
            groups:        vec![],
            next_group_id: GROUP_ID_BASE,
            cast_shadows:  true,
            render_tag:    3,
            // 非既定値を入れて往復漏れを検出する。
            disable_lod:   true,
            // 非既定値を入れて往復漏れを検出する。
            offset_position: [1.5, -2.0, 0.25],
            offset_rotation: [10.0, 20.0, 30.0],
            offset_scale:    [2.0, 0.5, 3.0],
            material_overrides: vec![
                // インライン: 全フィールドを非 None で埋める（往復漏れの検出のため）。
                MaterialOverride {
                    slot: 0,
                    kind: MaterialOverrideKind::Inline {
                        base_color:    Some([0.1, 0.2, 0.3, 0.4]),
                        metallic:      Some(0.55),
                        roughness:     Some(0.66),
                        emissive:      Some([0.7, 0.8, 0.9]),
                        alpha_mode:    Some("blend".to_string()),
                        alpha_cutoff:  Some(0.25),
                        ior:           Some(1.45),
                        transmission:  Some(0.9),
                        diffuse_transmission: Some(0.35),
                        mr_tex_ignore: Some(true),
                        cull_face:     Some("none".to_string()),
                        // シェーディングモデル（0..3）。既定 0 と区別できる非既定値を入れて往復を検証する。
                        shading_model: Some(2),
                    },
                },
                // MatAsset: パスが往復すること。
                MaterialOverride {
                    slot: 2,
                    kind: MaterialOverrideKind::MatAsset {
                        path: "assets://materials/red.mat".to_string(),
                    },
                },
            ],
        };

        // シリアライズ → デシリアライズ。
        let json = serde_json::to_string(&original).expect("serialize");
        let restored: ModelComponentData =
            serde_json::from_str(&json).expect("deserialize");

        // 復元後を再シリアライズして原本 JSON と完全一致するか比較する
        // （MaterialOverride[Kind] は PartialEq を持たないため、正規化 JSON 同士で全フィールドを検証する）。
        let json_again = serde_json::to_string(&restored).expect("re-serialize");
        assert_eq!(json, json_again, "material_overrides の全フィールドが往復すること");

        // 主要フィールドを個別にも検証（JSON 比較のすり抜け防止）。
        assert_eq!(restored.material_overrides.len(), 2);
        match &restored.material_overrides[0].kind {
            MaterialOverrideKind::Inline {
                base_color, metallic, roughness, emissive,
                alpha_mode, alpha_cutoff, ior, transmission, diffuse_transmission, mr_tex_ignore, cull_face,
                shading_model,
            } => {
                assert_eq!(*base_color, Some([0.1, 0.2, 0.3, 0.4]));
                assert_eq!(*metallic, Some(0.55));
                assert_eq!(*roughness, Some(0.66));
                assert_eq!(*emissive, Some([0.7, 0.8, 0.9]));
                assert_eq!(alpha_mode.as_deref(), Some("blend"));
                assert_eq!(*alpha_cutoff, Some(0.25));
                assert_eq!(*ior, Some(1.45));
                assert_eq!(*transmission, Some(0.9));
                assert_eq!(*diffuse_transmission, Some(0.35));
                assert_eq!(*mr_tex_ignore, Some(true));
                assert_eq!(cull_face.as_deref(), Some("none"));
                assert_eq!(*shading_model, Some(2), "シェーディングモデルが往復すること");
            }
            _ => panic!("slot 0 は Inline であること"),
        }
        assert_eq!(restored.material_overrides[0].slot, 0);
        match &restored.material_overrides[1].kind {
            MaterialOverrideKind::MatAsset { path } =>
                assert_eq!(path, "assets://materials/red.mat"),
            _ => panic!("slot 2 は MatAsset であること"),
        }
        assert_eq!(restored.material_overrides[1].slot, 2);
    }

    /// 旧 `.scene` 互換: material_overrides / cast_shadows キーが無い JSON でも
    /// デシリアライズが失敗せず、既定（空 Vec / cast_shadows=true）にフォールバックすること。
    /// serde(default) 付与漏れの回帰を防ぐ。
    #[test]
    fn old_scene_without_material_overrides_deserializes() {
        // material_overrides も cast_shadows も持たない旧フォーマット。
        let old_json = r#"{
            "model_path": "assets://chess.glb",
            "instances": [],
            "meta": [],
            "groups": [],
            "next_group_id": 1000000
        }"#;
        let data: ModelComponentData =
            serde_json::from_str(old_json).expect("旧 .scene が読めること（serde default 必須）");
        assert!(data.material_overrides.is_empty(), "欠落時は空 Vec へフォールバック");
        assert!(data.cast_shadows, "欠落時は cast_shadows=true へフォールバック");
        // 描画オフセット（後から追加したフィールド）も既定へフォールバックすること。
        assert_eq!(data.offset_position, OFFSET_POSITION_DEFAULT, "欠落時は位置オフセット 0");
        assert_eq!(data.offset_rotation, OFFSET_ROTATION_DEFAULT, "欠落時は回転オフセット 0");
        assert_eq!(data.offset_scale,    OFFSET_SCALE_DEFAULT,    "欠落時はスケールオフセット 1");
    }

    /// 描画オフセットの往復を個別にも検証する（JSON 比較のすり抜け防止）。
    #[test]
    fn offset_transform_roundtrip() {
        let mut mc = ModelComponent::empty();
        mc.source_path      = "a.glb".into();
        mc.offset_position  = [1.0, 2.0, 3.0];
        mc.offset_rotation  = [0.0, 90.0, 0.0];
        mc.offset_scale     = [1.0, 2.0, 1.0];
        let json = serde_json::to_string(&mc.to_data()).expect("serialize");
        let back: ModelComponentData = serde_json::from_str(&json).expect("deserialize");
        assert_eq!(back.offset_position, [1.0, 2.0, 3.0]);
        assert_eq!(back.offset_rotation, [0.0, 90.0, 0.0]);
        assert_eq!(back.offset_scale,    [1.0, 2.0, 1.0]);
    }
}

// ============================================================
//  テスト（描画オフセットの行列合成 — 純関数）
//
//  `render_matrix()` は「アクタのワールド行列 → GPU へ渡す最終インスタンス行列」
//  の唯一の集約点であり、全描画経路（通常/スキン/LOD/シャドウ/RT/ID/アウトライン）
//  がここを通る。したがってこの純関数のテストが行列合成の正しさを代表する。
// ============================================================

#[cfg(test)]
mod offset_matrix_tests {
    use super::*;

    /// 平行移動だけのアクタワールド行列を作る。
    fn translate(x: f32, y: f32, z: f32) -> [[f32; 4]; 4] {
        [[1.0, 0.0, 0.0, x],
         [0.0, 1.0, 0.0, y],
         [0.0, 0.0, 1.0, z],
         [0.0, 0.0, 0.0, 1.0]]
    }

    fn approx(a: [[f32; 4]; 4], b: [[f32; 4]; 4], eps: f32) {
        for i in 0..4 {
            for j in 0..4 {
                assert!((a[i][j] - b[i][j]).abs() < eps,
                    "行列 [{i}][{j}] 不一致: {} vs {}", a[i][j], b[i][j]);
            }
        }
    }

    /// オフセット既定（恒等）なら入力行列がそのまま返る＝従来の描画と完全一致。
    #[test]
    fn identity_offset_returns_input_unchanged() {
        let mc = ModelComponent::empty();
        let world = translate(3.0, 4.0, 5.0);
        assert!(!mc.has_offset(), "既定はオフセット無しと判定されること");
        assert_eq!(mc.render_matrix(world), world, "恒等オフセットは入力をビット一致で返すこと");
    }

    /// 位置オフセット: 回転無しのアクタなら平行移動が単純加算される。
    #[test]
    fn offset_position_translates() {
        let mut mc = ModelComponent::empty();
        mc.offset_position = [1.0, 2.0, 3.0];
        let m = mc.render_matrix(translate(10.0, 20.0, 30.0));
        assert!((m[0][3] - 11.0).abs() < 1e-5);
        assert!((m[1][3] - 22.0).abs() < 1e-5);
        assert!((m[2][3] - 33.0).abs() < 1e-5);
    }

    /// 位置オフセットは**アクタのローカル空間**で効く（アクタが回れば一緒に回る）。
    /// Y 90 度回転したアクタに +X オフセットを与えると、ワールドでは -Z 方向へ動く
    /// （YXZ 規約の rotation_basis: 右列 = (cosY, 0, -sinY) → Y=90° で (0,0,-1)）。
    #[test]
    fn offset_position_is_in_actor_local_space() {
        let mut mc = ModelComponent::empty();
        mc.offset_position = [1.0, 0.0, 0.0];
        let actor = Transform { position: [0.0; 3], rotation: [0.0, 90.0, 0.0], scale: [1.0; 3] }
            .to_mat4();
        let m = mc.render_matrix(actor);
        assert!(m[0][3].abs() < 1e-5,            "X 成分は 0 になること（実際: {}）", m[0][3]);
        assert!((m[2][3] + 1.0).abs() < 1e-5,    "Z 成分は -1 になること（実際: {}）", m[2][3]);
    }

    /// スケールオフセットは行列の基底列の長さを倍にする（回転無しなら対角成分そのもの）。
    #[test]
    fn offset_scale_scales_basis() {
        let mut mc = ModelComponent::empty();
        mc.offset_scale = [2.0, 3.0, 4.0];
        let m = mc.render_matrix(translate(0.0, 0.0, 0.0));
        assert!((m[0][0] - 2.0).abs() < 1e-5);
        assert!((m[1][1] - 3.0).abs() < 1e-5);
        assert!((m[2][2] - 4.0).abs() < 1e-5);
        // 平行移動は変わらない（原点のまま）。
        assert!(m[0][3].abs() < 1e-5 && m[1][3].abs() < 1e-5 && m[2][3].abs() < 1e-5);
    }

    /// 回転オフセットは Transform（正典）の TRS と一致する
    /// ＝ 単位アクタ行列に対して render_matrix は offset_matrix そのものになる。
    #[test]
    fn offset_rotation_matches_transform_convention() {
        let mut mc = ModelComponent::empty();
        mc.offset_rotation = [15.0, 30.0, 45.0];
        let expected = Transform {
            position: OFFSET_POSITION_DEFAULT,
            rotation: [15.0, 30.0, 45.0],
            scale:    OFFSET_SCALE_DEFAULT,
        }.to_mat4();
        approx(mc.render_matrix(translate(0.0, 0.0, 0.0)), expected, 1e-5);
    }

    /// 合成順序が actor_world * offset であること
    /// （逆順 offset * actor_world との差が出るケースで検証する）。
    #[test]
    fn composition_order_is_actor_then_offset() {
        let mut mc = ModelComponent::empty();
        mc.offset_position = [1.0, 0.0, 0.0];
        mc.offset_scale    = [2.0, 2.0, 2.0];
        let actor = Transform { position: [5.0, 0.0, 0.0], rotation: [0.0; 3], scale: [3.0, 3.0, 3.0] }
            .to_mat4();
        let m = mc.render_matrix(actor);
        // actor_world * offset: 平行移動 = 5 + 3*1 = 8、スケール = 3*2 = 6。
        assert!((m[0][3] - 8.0).abs() < 1e-5, "平行移動はアクタスケールを受けること（実際: {}）", m[0][3]);
        assert!((m[0][0] - 6.0).abs() < 1e-5, "スケールは乗算されること（実際: {}）", m[0][0]);
    }
}
