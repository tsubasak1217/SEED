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
use crate::engine::core::loader::model::Model;
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
}

// ─── ModelAnimDrive ─────────────────────────────────────────────────────────

/// Animator が駆動する glTF 内蔵アニメの再生状態（揮発・非シリアライズ）。
///
/// `AnimatorComponent` の kind=Model クリップ再生中、`animation_ops::update_animations`
/// が毎フレームこの値を書き込む。`Some` のときレンダラのスキニングは `time` を
/// 権威時刻として使う。`None` のとき（Animator 無し／非再生）モデルは静止する
/// （animations[0] の t=0 で凍結。旧仕様のグローバルクロックによるデモ再生は廃止済み）。
///
/// 【現状の制約】GPU スキニング（`SkinComputeSystem`）は `Model::animations[0]`
/// のみを再生するため、駆動可能なのは `anim_idx == 0` の場合のみ。`anim_idx != 0`
/// は上流（update_animations）で警告して静止（非駆動）にフォールバックさせる想定。
#[derive(Clone, Copy)]
pub struct ModelAnimDrive {
    /// 再生対象アニメの Model::animations インデックス
    pub anim_idx: usize,
    /// 権威再生時刻（秒。ループ/クランプ後の 0..=duration 正規化済み）
    pub time:     f32,
    /// 再生中フラグ（false = 一時停止・停止でこの時刻を保持）
    pub playing:  bool,
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
                alpha_mode: None, alpha_cutoff: None, ior: None, transmission: None, cull_face: None,
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
