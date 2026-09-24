// ============================================================
//  app/build.gradle.kts — SEED ランタイムの APK（段階0 / 段階A）
//
//  中身は「Java の薄い Activity（MainActivity）＋ cargo ndk が作った libSEED.so」と、
//  パッケージ実行のときだけ「配布物（assets/seed/assets.pak）」。
//  .so は build_and_run.ps1 が app/src/main/jniLibs/<ABI>/libSEED.so へ置く（AGP の既定の置き場）。
//  配布物は build_and_run.ps1 -ProjectDir が app/src/main/assets/seed/ へ置く（AGP の既定の assets の置き場）。
//  画面の向きはプロジェクト設定の screen_orientation を build_and_run.ps1 が -Pseed.orientation=<値> で渡し、
//  下の変換表でマニフェストの screenOrientation へ差し込む（manifestPlaceholders）。
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

/**
 * 画面の向きの変換表（唯一の置き場）: プロジェクト設定の screen_orientation の値 → マニフェストの screenOrientation。
 *   both      … fullSensor      … 縦横 4 方向に追従（端末の回転ロックは無視してセンサーに従う）
 *   portrait  … sensorPortrait  … 縦だけ（逆さの縦へ回るかは端末の設定次第。エミュレータでは回らなかった）
 *   landscape … sensorLandscape … 横だけ（左右どちら向きの横もセンサーに従う）
 * 設定の値はエディタの ProjectSettingsData.ScreenOrientation（project_settings.json の screen_orientation）と同じ。
 */
val screenOrientationTable = mapOf(
    "both" to "fullSensor",
    "portrait" to "sensorPortrait",
    "landscape" to "sensorLandscape",
)

/** screen_orientation が無い・空のときの値（エディタの既定値と同じ）。 */
val defaultScreenOrientationSetting = "both"

/**
 * このビルドの screen_orientation（前後の空白を落として小文字にそろえる）。
 * build_and_run.ps1 がプロジェクト設定から読んで -Pseed.orientation=<値> で渡す。未指定なら既定値。
 */
val seedOrientationSetting = providers.gradleProperty("seed.orientation").orNull
    ?.trim()
    ?.lowercase()
    ?.takeIf { it.isNotEmpty() }
    ?: defaultScreenOrientationSetting

/**
 * マニフェストへ差し込む screenOrientation。表に無い値は、設定ミスでビルドを止めるより従来動作へ倒すほうが安全なので、
 * 警告を出して既定値（both = fullSensor）にする（ランタイムの設定値の読み方と同じ方針）。
 */
val seedScreenOrientation = screenOrientationTable[seedOrientationSetting] ?: run {
    logger.warn(
        "SEED: screen_orientation=\"$seedOrientationSetting\" は不明な値です（使える値: " +
            "${screenOrientationTable.keys.joinToString()}）。\"$defaultScreenOrientationSetting\" として扱います。"
    )
    screenOrientationTable.getValue(defaultScreenOrientationSetting)
}

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
        // AndroidManifest.xml の ${seedScreenOrientation} を置き換える（画面の向きの固定。上の変換表）。
        manifestPlaceholders["seedScreenOrientation"] = seedScreenOrientation
        logger.lifecycle("SEED: screen_orientation=$seedOrientationSetting → screenOrientation=$seedScreenOrientation")
    }

    compileOptions {
        sourceCompatibility = seedJavaVersion
        targetCompatibility = seedJavaVersion
    }

    androidResources {
        // 配布物の pak は圧縮せずに APK へ入れる。ネイティブ側は AAsset を Read + Seek しながらエントリを
        // 読むが、圧縮されていると後ろ向きの Seek のたびに先頭から展開し直すことになり遅い（非圧縮なら
        // APK 内の位置へ直接 Seek できる。ファイル記述子とオフセットでも読める）。
        // 非圧縮で入ったかは起動ログの「APK 内の pak で起動します … 非圧縮（APK 内の位置 N）」で分かる。
        noCompress += "pak"
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
