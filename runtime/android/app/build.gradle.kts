// ============================================================
//  app/build.gradle.kts — SEED ランタイムの APK（段階0: 1 枚絵を出すスパイク）
//
//  中身は「Java の薄い Activity（MainActivity）＋ cargo ndk が作った libSEED.so」だけ。
//  .so は build_and_run.ps1 が app/src/main/jniLibs/<ABI>/libSEED.so へ置く（AGP の既定の置き場）。
// ============================================================

plugins {
    id("com.android.application")
}

/** 最低 Android バージョン（API 29 = Android 10）。Vulkan 1.1 がほぼ行き渡る世代を下限にする。 */
val seedMinSdk = 29

/** ビルド時に使う SDK と、動作を合わせる対象の API（35 = Android 15）。 */
val seedTargetSdk = 35

/** Java のソース／バイトコードの版（AGP 9 の既定に合わせる）。 */
val seedJavaVersion = JavaVersion.VERSION_17

/** 同梱する ABI の既定値。実機（arm64-v8a）とエミュレータ（x86_64）。 */
val defaultSeedAbis = listOf("arm64-v8a", "x86_64")

/**
 * 実際に APK へ詰める ABI。build_and_run.ps1 は -Abi で選んだものだけを -Pseed.abis=a,b で渡す
 * （jniLibs に残っている別 ABI の古い .so を詰めない・APK を小さくするため）。未指定なら既定値。
 * jniLibs に無い ABI は単に入らないだけなので、片方だけビルドした場合もそのまま詰められる。
 */
val seedAbis = providers.gradleProperty("seed.abis").orNull
    ?.split(',')
    ?.map { it.trim() }
    ?.filter { it.isNotEmpty() }
    ?.takeIf { it.isNotEmpty() }
    ?: defaultSeedAbis

/**
 * GameActivity の版。android-activity 0.6.1（winit 0.30 が使う Rust 側の glue）が同梱する
 * C 側の GameActivity 4.4.0 と一致させる必要がある（ずれると JNI の呼び出し規約が合わず起動しない）。
 */
val gamesActivityVersion = "4.4.0"

android {
    // Java の名前空間（R クラス等）。applicationId と同じにしておく。
    namespace = "com.seedengine.runtime"
    compileSdk = seedTargetSdk

    // NDK の場所（APK へ詰める前に .so のデバッグシンボルを削るのに使う）。
    // マシン固有のパスをリポジトリへ書かないため、build_and_run.ps1 が環境変数 ANDROID_NDK_HOME から
    // -Pseed.ndkPath=... で渡す。未指定なら AGP の既定 NDK を使う（無ければ削らずに詰めるだけ）。
    providers.gradleProperty("seed.ndkPath").orNull?.let { ndkPath = it }

    defaultConfig {
        // 段階0 の仮の ID。段階C でプロジェクト設定（ゲームごとの ID）からデータドリブンに生成する。
        applicationId = "com.seedengine.runtime"
        minSdk = seedMinSdk
        targetSdk = seedTargetSdk
        versionCode = 1
        versionName = "0.0.1-stage0"
        ndk {
            abiFilters += seedAbis
        }
    }

    compileOptions {
        sourceCompatibility = seedJavaVersion
        targetCompatibility = seedJavaVersion
    }

    buildTypes {
        getByName("release") {
            // 段階0 は配布しない（署名・AAB・難読化は段階D）。
            isMinifyEnabled = false
        }
    }
}

dependencies {
    // GameActivity は AppCompatActivity 派生で、androidx.core の WindowInsets 系も使う
    // （games-activity の POM は依存を宣言していないため、ここで明示する）。
    implementation("androidx.appcompat:appcompat:1.7.1")
    implementation("androidx.core:core:1.13.1")
    implementation("androidx.games:games-activity:$gamesActivityVersion")
}
