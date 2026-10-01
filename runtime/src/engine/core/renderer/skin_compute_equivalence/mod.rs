// ============================================================
//  skin_compute_equivalence/ — GPU スキニングの compute の作り直し（2026-10-02）の同値テスト（テスト専用・実 GPU）
//
//  旧い skin_compute.wgsl（1 スレッド = 1 インスタンス。ノードの TRS・ワールド行列をスレッドごとの private 配列に
//  持ち、パイプラインを作るだけでドライバがローカルメモリを約 450 MiB 予約していた）の写しと、新しい
//  skin_compute.wgsl（1 ワークグループ = 1 インスタンス。ノードの行列をワークグループの共有メモリに置く）を、
//  同じモデル・同じ再生指定で走らせ、ジョイント行列が**浮動小数のビット単位で**一致することを確かめる
//  （docs/rendering_profiles.md §5・§14.2）。
//
//    legacy_skin_compute.wgsl … 旧い作りの写し（コミット 65ac203a の時点。直さないこと）
//    harness.rs               … 実 GPU の用意・旧い作りのパイプラインと 1 スレッド = 1 インスタンスの dispatch・
//                               両方を走らせて読み戻し、ビット単位で比べる共通部分
//    random_model.rs          … 決定的な乱数で作るモデル（ノードの木・TRS・ジョイント・補間 3 種のアニメ・
//                               同じノードと属性への重複チャンネル）と再生指定（ブレンド・端の時刻）
//    equivalence.rs           … 突き合わせのテスト（乱数のモデル・ノード数が上限を超えるモデル・実際の glb）
//
//  実行: cargo test skin_compute_equivalence -- --ignored --nocapture
//        実際の glb も比べるときは、環境変数 SEED_SKIN_EQUIV_MODELS にフォルダを渡す（下の階層の .glb / .gltf を全部読む）。
// ============================================================

mod equivalence;
mod harness;
mod random_model;
