// ============================================================
//  bindless_lazy.rs — bindless の資源を「最初に要るとき」に作る置き場
//
//  【なぜ遅延か】bindless の資源（テクスチャ配列〈最大 4096 枠〉・UV / 索引 / 法線のメガバッファ 224 MiB・
//  インスタンス表）は、レイトレーシングのヒットシェーディングの土台（フェーズ B1〜B3）。読むのは
//  RT の色付き影・反射・半透明の屈折・水面反射と TLAS の組み立て、書くのは 3D モデルの登録
//  （DrawContext::upload_model* の finalize_bindless）だけで、どれも RT の対応 GPU でしか走らない
//  （RT の無い GPU では、この置き場そのものを作らない＝bindless::bindless_resources_wanted。機能の要求は従来どおり）。
//  起動時に作ると、3D のモデルを読まず RT のパスも走らない作品（full のまま 2D/UI だけを描く等）でも
//  224 MiB を抱えるので、DrawContext は「作れる」ことだけを持ち、使う所が初めて触ったときに作る
//  （docs/rendering_profiles.md）。
//
//  【振る舞いは従来と同じ】使う所はすべて `get_or_create` を通す（作っていなければここで作る）。
//  作った直後の中身（空の登録表・空のメガバッファ・ダミーのテクスチャ）は、起動時に作って誰も
//  触っていない状態と同じなので、作る時刻が変わるだけで、登録の番号・オフセット・描画は変わらない。
// ============================================================

use std::cell::{OnceCell, RefCell};
use std::sync::Arc;

use super::bindless::{
    BindlessResources, BINDLESS_INDEX_BUFFER_BYTES, BINDLESS_NORMAL_BUFFER_BYTES, BINDLESS_UV_BUFFER_BYTES,
};

/// MiB へ直す割り算の数（ログ用）。
const BYTES_PER_MIB: u64 = 1024 * 1024;

/// bindless の資源の置き場（bindless に対応した GPU でだけ DrawContext が持つ）。
pub struct LazyBindless {
    /// 資源を作るデバイス。
    device: Arc<wgpu::Device>,
    /// ダミーのテクスチャを書くキュー。
    queue: Arc<wgpu::Queue>,
    /// テクスチャ配列の容量（renderer/mod.rs がアダプタの上限で絞った値）。
    capacity: u32,
    /// 作った資源（最初の `get_or_create` で入る。以後は同じものを返す）。
    resources: OnceCell<RefCell<BindlessResources>>,
}

impl LazyBindless {
    /// まだ何も作らない置き場を用意する。
    pub fn new(device: &Arc<wgpu::Device>, queue: &Arc<wgpu::Queue>, capacity: u32) -> Self {
        Self {
            device: Arc::clone(device),
            queue: Arc::clone(queue),
            capacity,
            resources: OnceCell::new(),
        }
    }

    /// 資源を返す。まだ作っていなければここで作る（最初の 1 回だけ GPU の資源を確保し、どこで要ったかを 1 行ログに出す）。
    #[track_caller]
    pub fn get_or_create(&self) -> &RefCell<BindlessResources> {
        let site = std::panic::Location::caller();
        self.resources.get_or_init(|| {
            let mega_mib =
                (BINDLESS_UV_BUFFER_BYTES + BINDLESS_INDEX_BUFFER_BYTES + BINDLESS_NORMAL_BUFFER_BYTES) / BYTES_PER_MIB;
            eprintln!(
                "[SEED BINDLESS] 資源を作りました（最初に要った所 {}:{}・テクスチャ配列 {} 枠・メガバッファ {mega_mib} MiB）",
                site.file(),
                site.line(),
                self.capacity,
            );
            RefCell::new(BindlessResources::new(&self.device, &self.queue, self.capacity))
        })
    }

    /// もう作ったか（計測・テスト用）。
    pub fn is_created(&self) -> bool {
        self.resources.get().is_some()
    }
}
