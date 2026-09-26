// ============================================================
//  W1-0 スパイク: 確実に鳴る目覚ましの基盤を Java だけで確かめる使い捨てアプリ
//  （SEED の runtime/android と同じ minSdk 29 / targetSdk 36。AndroidX なし）
// ============================================================
plugins {
    id("com.android.application")
}

/** SEED の APK と同じ下限・対象（runtime/android/app/build.gradle.kts の seedMinSdk / seedTargetSdk）。 */
val spikeMinSdk = 29
val spikeTargetSdk = 36

android {
    namespace = "com.seedengine.platformspike"
    compileSdk = spikeTargetSdk

    defaultConfig {
        applicationId = "com.seedengine.platformspike"
        minSdk = spikeMinSdk
        targetSdk = spikeTargetSdk
        versionCode = 1
        versionName = "0.1-spike"
    }

    // メインプロセス ↔ :seed_platform の比較用に AIDL も試す。
    buildFeatures {
        aidl = true
    }

    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }

    // openRawResourceFd で読むため、鳴動音（WAV）は非圧縮で入れる。
    androidResources {
        noCompress += "wav"
    }

    // lint は「USE_EXACT_ALARM と SCHEDULE_EXACT_ALARM(maxSdkVersion=32) の併記」への指摘を見るために回す（止めない）。
    lint {
        abortOnError = false
        checkReleaseBuilds = false
    }
}
