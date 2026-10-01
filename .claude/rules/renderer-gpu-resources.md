---
paths:
  - "runtime/src/engine/core/renderer/**"
  - "runtime/src/engine/methods/drawer/**"
  - "runtime/src/engine/core/font/**"
---

# 描画の GPU 資源（runtime の renderer / drawer / font を触るときの規約）

- **GPU の資源（テクスチャ・バッファ）を新しく作るコードは、`wgpu::Device::create_texture` / `create_buffer` を直接呼ばず、`*_tracked`（`runtime/src/engine/core/renderer/gpu_mem/track.rs`）を使う。** ラベル・分類（`gpu_mem/category.rs` の表）・バイト数が計測に乗り、`SEED_GPU_MEM_LOG=1` と IPC `GPU_MEM_REPORT` の内訳に出る。分類の表に無いラベルは「その他」になるので、新しい種類は表に 1 行足す。
- **3D だけの資源（G-Buffer・影・GI・クラスタ・bindless・ピッキングの ID・スキニング）は `render.profile`（`runtime/config/render_profiles.json`・`render_profile/`）の旗を見て作る。** `ui` 構成では作らない。固定の大きさで前もって確保する資源は、要るときに作る（遅延確保）か上限を設定で持つ。
- **シェーダーでスレッドごとの大きな `var<private>` 配列を持たない**（パイプラインを作るだけで数百 MiB を予約する。`docs/rendering_profiles.md` §14）。配列はストレージバッファか `var<workgroup>` へ。
- 計測の手順と内訳の正典は `docs/rendering_profiles.md`。既定の `full` の振る舞いを変える変更は、WarashibeFishing の回帰（`SEED_FIXED_FRAME_DT` で時刻を揃える）を取る。
