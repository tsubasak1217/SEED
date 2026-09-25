// ============================================================
//  app/build.gradle.kts — SEED ランタイムの APK / AAB（段階0〜D）
//
//  中身は「Java の薄い Activity（MainActivity）＋ cargo ndk が作った libSEED.so」と、
//  パッケージ実行のときだけ「配布物（assets/seed/assets.pak と bin/ のスクリプト DLL）」、
//  それに同梱 .NET（段階B。.so と BCL・目録 bundle.json）。
//  置き場へ置くのは SeedAndroid（editor/src/Android/。build_and_run.ps1 はそれを呼ぶだけのラッパー）:
//    .so         … app/src/main/jniLibs/<ABI>/libSEED.so（AGP の既定の置き場）
//    配布物      … --project のとき app/src/main/assets/seed/（AGP の既定の assets の置き場）
//    同梱 .NET   … runtime/android/dotnet_runtime.json から app/src/seedDotnet/ へ組み立てる
//                 （下の sourceSets で jniLibs・assets の置き場として足す。生成物・追跡しない。docs/android.md §17）
//    アイコン    … プロジェクト設定 android.icon から app/src/seedIcon/res/ へ各密度の mipmap とアダプティブアイコンを生成する
//                 （下の sourceSets で res の置き場として足す。生成物・追跡しない。docs/android.md §24）
//  プロジェクト設定から決まる値は Gradle のプロジェクトプロパティ（-Pseed.* か環境変数 ORG_GRADLE_PROJECT_seed.*）で受け取り、
//  下の変換で APK へ焼き込む（値の変換はこのファイルの 1 か所。docs/android.md §15.1・§18・§24）:
//    seed.orientation                  … screen_orientation → マニフェストの screenOrientation（変換表）
//    seed.applicationId / seed.appName … android.application_id / app_name → applicationId・android:label
//    seed.versionCode / seed.versionName … android.version_code / version_name → versionCode・versionName
//    seed.launcherIcon                 … generated なら生成したアイコン（@mipmap/ic_launcher）→ android:icon
//    seed.signing.*                    … 配布用（release）の署名。キーストアの場所・別名・パスワード（パスワードは必ず環境変数。
//                                        SeedAndroid の Signing/。このファイル・gradle.properties・コマンドラインには書かない）
//  渡されなければ既定値（com.seedengine.runtime・SEED Runtime・システムの既定のアイコン等。手で gradlew を叩いたときもこれ）。
//
//  【ビルドの種類（docs/android.md §24）】
//    debug   … assembleDebug。デバッグ用の鍵で署名・debuggable・INTERNET（src/debug/ のマニフェスト。エディタとの IPC）。開発用
//    release … assembleRelease（APK）/ bundleRelease（AAB。Google Play へ出す形）。debuggable=false・INTERNET なし・
//              アップロード鍵で署名（seed.signing.* が揃っていなければデバッグ署名・無署名にはせずビルドを止める）
// ============================================================

plugins {
    id("com.android.application")
}

/** 最低 Android バージョン（API 29 = Android 10）。Vulkan 1.1 がほぼ行き渡る世代を下限にする。 */
val seedMinSdk = 29

/**
 * ビルド時に使う SDK と、動作を合わせる対象の API（36 = Android 16）。
 * Google Play は 2026-08-31 以降の新規アプリ・更新に API 36 以上を求める（延長の申請で 2026-11-01 まで。
 * https://developer.android.com/google/play/requirements/target-sdk。要件の表は runtime/android/play_requirements.json）。
 * 36 にしたことで効く動作の変更と対処（docs/android.md §24）:
 *   ・予測型の「戻る」… 既定で有効になり KEYCODE_BACK がアプリへ届かなくなる → マニフェストの
 *     android:enableOnBackInvokedCallback="false" で従来どおり届ける（戻るキー → Escape。§14.5）
 *   ・大画面（最小幅 600dp 以上）での向き・サイズ変更の制限の無視 … ゲーム（android:appCategory="game"）は対象外
 *   ・エッジツーエッジの無効化の廃止 … 35 の時点で既に強制（安全領域は ScreenReporter で扱い済み）
 * SeedAndroid の AndroidRuntimeContract.TargetApiLevel と一致させる（要件チェックの材料）。
 */
val seedTargetSdk = 36

/** Java のソース／バイトコードの版（AGP 9 の既定に合わせる）。 */
val seedJavaVersion = JavaVersion.VERSION_17

/** 同梱する ABI の既定値。実機（arm64-v8a）とエミュレータ（x86_64）。 */
val defaultSeedAbis = listOf("arm64-v8a", "x86_64")

/**
 * 実際に APK へ詰める ABI。SeedAndroid は今回の ABI（--abi か端末から判定）だけを -Pseed.abis=a,b で渡す
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
 * SeedAndroid がプロジェクト設定から読んで -Pseed.orientation=<値> で渡す。未指定なら既定値。
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

// ── アプリの識別情報（プロジェクト設定 project_settings.json の "android" 節。docs/android.md §18）──────────
// SeedAndroid（editor/src/Android/Gradle/GradleInvocation.cs）が値を検査・既定値の補完をしてから渡す。
// ここは受け取った値をそのまま APK へ反映するだけ（無ければ下の既定値）。

/**
 * アプリ ID の既定値（プロジェクトから決められないとき・手で gradlew を叩いたとき）。
 * SeedAndroid の AndroidRuntimeContract.DefaultApplicationId と同じ。Java のクラスの名前空間（namespace）は
 * アプリ ID を変えてもこのまま（JNI の関数名が名前空間に結び付いているため）。
 */
val defaultApplicationId = "com.seedengine.runtime"

/** ランチャーに出る名前の既定値（res/values/strings.xml の app_name = "SEED Runtime" を参照する）。 */
val defaultAppLabel = "@string/app_name"

/** 整数の版の既定値。 */
val defaultVersionCode = 1

/** 版の文字列の既定値（プロジェクトの無い開発用の APK）。プロジェクトがあれば SeedAndroid が "1.0" 等を渡す。 */
val defaultVersionName = "0.0.1-dev"

/** Gradle のプロジェクトプロパティ seed.<name> の値（前後の空白を落とし、空なら null）。 */
fun seedProperty(name: String): String? =
    providers.gradleProperty("seed.$name").orNull?.trim()?.takeIf { it.isNotEmpty() }

/** このビルドのアプリ ID（-Pseed.applicationId）。 */
val seedApplicationId = seedProperty("applicationId") ?: defaultApplicationId

/** このビルドのランチャーの名前（-Pseed.appName。マニフェストの android:label へ ${seedAppLabel} として入る）。 */
val seedAppLabel = seedProperty("appName") ?: defaultAppLabel

/** このビルドの整数の版（-Pseed.versionCode。整数でなければ警告して既定値）。 */
val seedVersionCode = seedProperty("versionCode")?.let { text ->
    text.toIntOrNull() ?: run {
        logger.warn("SEED: seed.versionCode=\"$text\" は整数ではありません。$defaultVersionCode として扱います。")
        null
    }
} ?: defaultVersionCode

/** このビルドの版の文字列（-Pseed.versionName）。 */
val seedVersionName = seedProperty("versionName") ?: defaultVersionName

// ── ランチャーのアイコン（プロジェクト設定 android.icon / icon_background。docs/android.md §24）──────────
// SeedAndroid（editor/src/Android/Icons/）が PNG から各密度の mipmap（ic_launcher.png）とアダプティブアイコン
// （mipmap-anydpi-v26/ic_launcher.xml ＋ 前景 ic_launcher_foreground.png ＋ 背景色 @color/ic_launcher_background）を
// src/seedIcon/res/ へ生成し、-Pseed.launcherIcon=generated を渡す。渡されなければ（アイコン未設定・手で gradlew を叩いた）
// 従来どおりシステムの既定のアイコン。

/** seed.launcherIcon がこの値なら、生成したアイコンを使う。 */
val generatedLauncherIconMarker = "generated"

/** 生成したアイコンのリソース（src/seedIcon/res/mipmap-*）。 */
val generatedLauncherIcon = "@mipmap/ic_launcher"

/** アイコンを生成していないときのアイコン（android:icon を書かないときと同じシステムの既定のアイコン）。 */
val defaultLauncherIcon = "@android:drawable/sym_def_app_icon"

/** このビルドのランチャーのアイコン（マニフェストの android:icon へ ${seedAppIcon} として入る）。 */
val seedAppIcon = if (seedProperty("launcherIcon") == generatedLauncherIconMarker) generatedLauncherIcon else defaultLauncherIcon

// ── 配布用（release）の署名（docs/android.md §24）─────────────────────────────
// SeedAndroid（editor/src/Android/Signing/・Gradle/GradleInvocation.cs）がプロジェクトの packaging_settings.json の
// android.signing（キーストアの場所・別名）と、パスワード（エディタの保護保存か環境変数 SEED_ANDROID_KEYSTORE_PASSWORD /
// SEED_ANDROID_KEY_PASSWORD）を、環境変数 ORG_GRADLE_PROJECT_seed.signing.*（-P と同じプロジェクトプロパティ）で渡す。
// パスワードはコマンドラインにもログにも出さない。揃っていなければ release のビルドを止める（下の preReleaseBuild）。

/** キーストアのファイル（絶対パス）。 */
val seedSigningStoreFile = seedProperty("signing.storeFile")

/** キーの別名。 */
val seedSigningKeyAlias = seedProperty("signing.keyAlias")

/** キーストアのパスワード（前後の空白もパスワードの一部なので trim しない）。 */
val seedSigningStorePassword = providers.gradleProperty("seed.signing.storePassword").orNull?.takeIf { it.isNotEmpty() }

/** キーのパスワード（PKCS12 のキーストアはキーストアのパスワードと同じ。SeedAndroid が同じ値を入れて渡す）。 */
val seedSigningKeyPassword = providers.gradleProperty("seed.signing.keyPassword").orNull?.takeIf { it.isNotEmpty() }

/** release の署名の材料が揃っているか（揃っていなければ release のビルドを止める）。 */
val seedReleaseSigningReady = seedSigningStoreFile != null && seedSigningKeyAlias != null &&
    seedSigningStorePassword != null && seedSigningKeyPassword != null

/** 署名の材料が無いまま release を作ろうとしたときの説明。 */
val missingReleaseSigningMessage =
    "SEED: 配布用（release）の署名の材料がありません（seed.signing.storeFile / keyAlias / storePassword / keyPassword）。" +
        "デバッグ署名・無署名の release は作りません。SeedAndroid の build --variant release --keystore <キーストア> --key-alias <別名> と" +
        "環境変数 SEED_ANDROID_KEYSTORE_PASSWORD、またはエディタのパッケージ化ウィンドウの「署名」で指定してください（docs/android.md §24）。"

android {
    // Java の名前空間（R クラス等）。applicationId と同じにしておく。
    namespace = "com.seedengine.runtime"
    compileSdk = seedTargetSdk

    // NDK の場所（APK へ詰める前に .so のデバッグシンボルを削るのに使う）。
    // マシン固有のパスをリポジトリへ書かないため、SeedAndroid が見つけた NDK（ANDROID_NDK_HOME か SDK の ndk/）を
    // -Pseed.ndkPath=... で渡す。未指定なら AGP の既定 NDK を使う（無ければ削らずに詰めるだけ）。
    providers.gradleProperty("seed.ndkPath").orNull?.let { ndkPath = it }

    defaultConfig {
        // アプリの識別情報（プロジェクト設定の "android" 節。上の「アプリの識別情報」）。
        applicationId = seedApplicationId
        minSdk = seedMinSdk
        targetSdk = seedTargetSdk
        versionCode = seedVersionCode
        versionName = seedVersionName
        ndk {
            abiFilters += seedAbis
        }
        // AndroidManifest.xml の ${seedScreenOrientation} を置き換える（画面の向きの固定。上の変換表）。
        manifestPlaceholders["seedScreenOrientation"] = seedScreenOrientation
        // AndroidManifest.xml の ${seedAppLabel} を置き換える（ランチャーに出る名前）。
        manifestPlaceholders["seedAppLabel"] = seedAppLabel
        // AndroidManifest.xml の ${seedAppIcon} を置き換える（ランチャーのアイコン。上の「ランチャーのアイコン」）。
        manifestPlaceholders["seedAppIcon"] = seedAppIcon
        logger.lifecycle("SEED: screen_orientation=$seedOrientationSetting → screenOrientation=$seedScreenOrientation")
        logger.lifecycle("SEED: applicationId=$seedApplicationId label=$seedAppLabel versionCode=$seedVersionCode versionName=$seedVersionName")
        logger.lifecycle("SEED: targetSdk=$seedTargetSdk icon=$seedAppIcon releaseSigning=${if (seedReleaseSigningReady) "ready" else "none"}")
    }

    signingConfigs {
        // 配布用（release）の署名。材料が揃っているときだけ中身を入れる（パスワードは環境変数で受け取った値。ログに出さない）。
        create("release") {
            if (seedReleaseSigningReady) {
                storeFile = file(seedSigningStoreFile!!)
                storePassword = seedSigningStorePassword
                keyAlias = seedSigningKeyAlias
                keyPassword = seedSigningKeyPassword
            }
        }
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

    packaging {
        jniLibs {
            // .so をインストール時に端末のファイルとして展開させる（nativeLibraryDir。AndroidManifest の extractNativeLibs=true）。
            // 同梱 .NET の hostfxr / hostpolicy / CoreCLR は dotnet-root 形式のフォルダに .so が並ぶことを前提にするため、
            // ランタイムが files/dotnet/ の dotnet-root から nativeLibraryDir の .so へシンボリックリンクを張る（または複製する）。
            // APK から直接読み込む既定の形（useLegacyPackaging = false）では .so がファイルとして存在しない（docs/android.md §17.4）。
            useLegacyPackaging = true
        }
    }

    sourceSets {
        getByName("main") {
            // SeedAndroid が組み立てる同梱 .NET（生成物）。lib/<ABI>/ の .so と assets/seed/dotnet/<ABI>/ の BCL・目録。
            // 無ければ何も足されない（.NET の無い APK。端末はスクリプト無しで起動する）。
            jniLibs.srcDir("src/seedDotnet/jniLibs")
            assets.srcDir("src/seedDotnet/assets")
            // SeedAndroid が生成するランチャーのアイコン（生成物。mipmap-*・values の背景色）。無ければ何も足されない。
            res.srcDir("src/seedIcon/res")
        }
    }

    buildTypes {
        getByName("release") {
            // 配布用（段階D。docs/android.md §24）。debuggable にしない（Google Play は debuggable の APK / AAB を受け付けない）。
            // INTERNET 権限は src/debug/ のマニフェストにだけあるので release には入らない。
            isDebuggable = false
            // R8（難読化・縮小）は使わない。Java は薄い Activity だけで、JNI から名前で呼ばれるクラス・メソッド
            // （MainActivity・ScreenReporter・暗号ライブラリの net.dot.android.crypto.* 等）を消されると起動しなくなるため。
            isMinifyEnabled = false
            // 署名は材料が揃っているときだけ。揃っていなければ下の preReleaseBuild でビルドを止める（デバッグ署名にしない）。
            signingConfig = if (seedReleaseSigningReady) signingConfigs.getByName("release") else null
        }
    }
}

// 署名の材料が無いまま release（assembleRelease / bundleRelease）を作ろうとしたら、最初の工程で止める
// （無署名・デバッグ署名の配布物を作らない。SeedAndroid は Gradle を呼ぶ前に同じ検査をしている）。
tasks.matching { it.name == "preReleaseBuild" }.configureEach {
    doFirst {
        if (!seedReleaseSigningReady) throw GradleException(missingReleaseSigningMessage)
    }
}

dependencies {
    // GameActivity は AppCompatActivity 派生で、androidx.core の WindowInsets 系も使う
    // （games-activity の POM は依存を宣言していないため、ここで明示する）。
    implementation("androidx.appcompat:appcompat:1.7.1")
    implementation("androidx.core:core:1.13.1")
    implementation("androidx.games:games-activity:$gamesActivityVersion")
    // 同梱 .NET（CoreCLR）の Java 側。暗号ライブラリが JNI_OnLoad で探すクラス（net.dot.android.crypto.*）の .jar を
    // SeedAndroid が src/seedDotnet/libs/ へ置く（dotnet_runtime.json の java_libraries。無ければ何も入らない）。
    implementation(fileTree(mapOf("dir" to "src/seedDotnet/libs", "include" to listOf("*.jar"))))
}
