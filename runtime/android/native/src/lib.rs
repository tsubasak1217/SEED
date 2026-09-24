// ============================================================
//  lib.rs — SEED Android エントリ（libSEED.so）のクレートルート
//
//  【構成】
//    entry         … android_main（GameActivity から呼ばれる入口）
//    logcat/       … log・標準出力／標準エラー・panic を logcat へ流す
//    app_dirs      … アプリ専用フォルダ（files・cache）をエンジンのセーブ・キャッシュの書き込み先に設定する
//    launch        … 起動モード（APK 内 pak／開発用の置き場）を決めてエンジンの起動引数を組み立てる
//    apk_package/  … APK の assets/seed/ を配布物として読む読み口（AAssetManager。APK 内 pak 用）
//    jni_exports   … Java（MainActivity）から呼ばれるネイティブ関数（onDestroy 前のセーブ書き出し）
//    device_info   … 起動時に端末情報（SDK・ABI・機種）をログへ残す
//    heartbeat     … 描画ループの生存確認（提示フレーム数を一定間隔でログへ）
//    debug_hooks   … 検証用フック（システムプロパティで意図的 panic・複数指の合成タッチ列）
//    debug_save_test … 検証用フック（セーブの書き出しタイミングの確認。debug.seed.save_test）
//    sysprop       … Android システムプロパティの読み取り
//
//  全体像・ビルド手順は docs/android.md を参照。
// ============================================================

// 出力名を libSEED.so にするためクレート名を大文字の SEED にしている（Gradle・エディタとの契約）。
#![allow(non_snake_case)]
// Android 以外では中身を持たない。ワークスペース全体を Windows で check しても空の DLL になるだけ。
#![cfg(target_os = "android")]

mod apk_package;
mod app_dirs;
mod debug_hooks;
mod debug_save_test;
mod device_info;
mod entry;
mod heartbeat;
mod jni_exports;
mod launch;
mod logcat;
mod sysprop;
