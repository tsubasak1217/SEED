// ============================================================
//  lib.rs — SEED エンジン本体（ライブラリ・クレート seed_engine）
//
//  【なぜライブラリにしたのか】
//  Android 対応（docs/android.md）で、エンジン本体を 2 つの成果物から使う必要が生じた。
//    - Windows : 同じパッケージの src/main.rs（bin）→ SEED.exe
//    - Android : runtime/android/native（cdylib）→ libSEED.so
//  エンジンのコードはすべて engine モジュール以下にあり、このファイルはその入口だけを持つ。
//  起動引数の解釈や Windows 固有の起動前処理（タイマ分解能・起動ログ）は各成果物側
//  （main.rs / android/native）の責務で、ここには置かない。
// ============================================================

pub mod engine;
