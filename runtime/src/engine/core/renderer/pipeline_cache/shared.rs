// ============================================================
//  pipeline_cache/shared.rs — プロセスで共有するパイプラインキャッシュのハンドル
//
//  【役割】
//  パイプラインの多くは生成時に `cache: Option<&wgpu::PipelineCache>` を引数で受け取る
//  （Renderer → DrawContext::new → 各パイプラインの new）。一方、次のように Renderer から
//  引数で渡す経路が無い箇所がある:
//    - 実行中に遅延生成するもの（シェーディングアセット・水面シェーディングアセット・Hi-Z）
//    - DrawContext の外で作る描画器（テキスト・2D/3D プリミティブ・軸ギズモ・アイコン等）
//  そこへ引数を通すために十数個のコンストラクタの形を変える代わりに、Renderer が作った
//  キャッシュの複製（wgpu のハンドルは Arc 相当で、複製しても同じキャッシュを指す）をここに置き、
//  生成箇所が `shared()` で受け取れるようにする。
//
//  【制約】wgpu のパイプラインキャッシュは作ったデバイス専用。アプリのデバイスは Renderer の 1 つだけ
//  （別のデバイスを作るのは実 GPU の単体テストだけで、テストでは Renderer を作らない＝ここは空）。
//  Renderer が無い・GPU が非対応なら None で、各生成箇所は従来どおりキャッシュ無しで作る。
// ============================================================

use std::sync::RwLock;

/// Renderer が作ったパイプラインキャッシュの複製（未作成・非対応なら None）。
static SHARED: RwLock<Option<wgpu::PipelineCache>> = RwLock::new(None);

/// 共有するキャッシュを登録する（Renderer の生成時に 1 回。None で登録を外す）。
///
/// # 引数
/// * `cache` - Renderer が作ったキャッシュ（GPU が非対応なら None）
pub fn install(cache: Option<&wgpu::PipelineCache>) {
    // 毒されたロック（他スレッドが保持中に panic）でも値の差し替えは安全なので中身を取り出して続ける。
    let mut slot = SHARED.write().unwrap_or_else(|poisoned| poisoned.into_inner());
    *slot = cache.cloned();
}

/// 共有のキャッシュを返す（生成箇所で `cache: shared().as_ref()` として使う）。
///
/// 未登録（Renderer 生成前・単体テスト）・GPU 非対応なら None。
pub fn shared() -> Option<wgpu::PipelineCache> {
    SHARED
        .read()
        .unwrap_or_else(|poisoned| poisoned.into_inner())
        .clone()
}
