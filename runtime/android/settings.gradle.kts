// ============================================================
//  settings.gradle.kts — SEED ランタイム Android プロジェクト（段階0）
//
//  エンジン本体（Rust）は cargo ndk で libSEED.so にしてから app/src/main/jniLibs/ へ置き、
//  この Gradle プロジェクトは「その .so と Java の薄い Activity を APK に詰める」だけを担う。
//  手順は SeedAndroid（editor/tools/SeedAndroid。runtime/android/build_and_run.ps1 はそのラッパー）、全体像は docs/android.md。
//
//  ツールチェーンの組み合わせ（AGP 本体の版チェック定数と実際のビルドで確認済み）:
//    AGP 9.1.0（build.gradle.kts）… Gradle 9.3.1 以上が必須
//    Gradle 9.3.1（gradle/wrapper）… 実行には Java 17 以上（Android Studio 同梱の JBR 25 で動作確認）
//    JDK … Android Studio 同梱の JBR（JAVA_HOME）
// ============================================================

pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    // 依存の取得元はここだけに集約する（モジュール側で勝手に増やさない）。
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "SEEDRuntimeAndroid"
include(":app")
