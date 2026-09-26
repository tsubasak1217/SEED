// W1-0 スパイク（使い捨て）。SEED の runtime/android と同じツールチェーン（AGP 9.1.0 / Gradle 9.3.1 / JBR）。
pluginManagement {
    repositories {
        google()
        mavenCentral()
        gradlePluginPortal()
    }
}

dependencyResolutionManagement {
    repositoriesMode.set(RepositoriesMode.FAIL_ON_PROJECT_REPOS)
    repositories {
        google()
        mavenCentral()
    }
}

rootProject.name = "PlatformSpike"
include(":app")
