// ============================================================
//  build.gradle.kts（ルート）— プラグインのバージョンだけをここで決める
//
//  AGP 9.1.0 は Gradle 9.3.1 以上を要求する（gradle/wrapper/gradle-wrapper.properties と対）。
//  AGP を上げるときは Gradle の最低版も必ず確認すること。
// ============================================================

plugins {
    id("com.android.application") version "9.1.0" apply false
}
