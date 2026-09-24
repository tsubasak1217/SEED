// ============================================================
//  lib.rs — SEED Android エントリ（libSEED.so）のクレートルート
//
//  【構成】
//    entry         … android_main（GameActivity から呼ばれる入口）
//    logcat/       … log・標準出力／標準エラー・panic を logcat へ流す
//    launch        … アプリ専用データフォルダからエンジンの起動引数を組み立てる
//    device_info   … 起動時に端末情報（SDK・ABI・機種）をログへ残す
//    heartbeat     … 描画ループの生存確認（提示フレーム数を一定間隔でログへ）
//    debug_hooks   … 検証用フック（システムプロパティで意図的に panic させる）
//    sysprop       … Android システムプロパティの読み取り
//
//  全体像・ビルド手順は docs/android.md を参照。
// ============================================================

// 出力名を libSEED.so にするためクレート名を大文字の SEED にしている（Gradle・エディタとの契約）。
#![allow(non_snake_case)]
// Android 以外では中身を持たない。ワークスペース全体を Windows で check しても空の DLL になるだけ。
#![cfg(target_os = "android")]

mod debug_hooks;
mod device_info;
mod entry;
mod heartbeat;
mod launch;
mod logcat;
mod sysprop;
