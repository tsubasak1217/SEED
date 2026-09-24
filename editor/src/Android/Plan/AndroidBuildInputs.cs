// ============================================================
//  AndroidBuildInputs.cs — 各工程の入力になるファイル・フォルダの表（リポジトリのルートからの相対）
//
//  【ここを直すとき】
//  工程が読むファイルを増やしたら（例: エンジンが include_bytes! でリポジトリの別の場所のファイルを埋め込む）、
//  ここへ足す。足し忘れると「変えたのに作り直されない」になる（--rebuild で逃げられる）。
//  多めに入れておく分には「作り直しが余計に走る」だけで安全。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System;
using System.Collections.Generic;

namespace SEEDEditor.Android.Plan;

/// <summary>各工程の入力の表。</summary>
public static class AndroidBuildInputs
{
    /// <summary>
    /// libSEED.so（cargo ndk）の入力。ワークスペースの Cargo.toml / Cargo.lock（依存の版と [profile.*]）、
    /// エンジン本体（runtime/）、Android の糊（runtime/android/native/）、パス依存の plugin_api、
    /// エンジンが include_bytes! で埋め込むエディタのアイコン（font/icon_overlay.rs）。
    /// </summary>
    public static readonly IReadOnlyList<string> NativeSources = new[]
    {
        "Cargo.toml",
        "Cargo.lock",
        "runtime/Cargo.toml",
        "runtime/build.rs",
        "runtime/.cargo/config.toml",
        "runtime/src",
        "runtime/android/native/Cargo.toml",
        "runtime/android/native/src",
        "plugin_api/Cargo.toml",
        "plugin_api/src",
        "editor/resources/icons/viewport/location.png",
    };

    /// <summary>
    /// APK に入れる pak とスクリプト（SeedPak）の作り方の入力（プロジェクトのアセットとは別に見る）。
    /// SeedPak 本体とリンクしているパッケージ化のコード、同梱するスクリプトホスト（scripting/）、
    /// 収録の起点に加えるエンジン内蔵の assets:// 参照（runtime/src）。
    /// </summary>
    public static readonly IReadOnlyList<string> PackageToolSources = new[]
    {
        "editor/tools/SeedPak",
        "editor/src/Packaging",
        "editor/src/Project",
        "scripting",
        "runtime/src",
    };

    /// <summary>
    /// APK（Gradle）の入力のうちリポジトリにあるもの（生成物の jniLibs・assets・seedDotnet は工程の出力として別に見る）。
    /// </summary>
    public static readonly IReadOnlyList<string> GradleSources = new[]
    {
        "runtime/android/build.gradle.kts",
        "runtime/android/settings.gradle.kts",
        "runtime/android/gradle.properties",
        "runtime/android/gradle",
        "runtime/android/app/build.gradle.kts",
        "runtime/android/app/src/main/AndroidManifest.xml",
        "runtime/android/app/src/main/java",
        "runtime/android/app/src/main/res",
    };

    /// <summary>入力を辿るときに飛ばすフォルダ名（ビルドの生成物・道具の作業フォルダ）。</summary>
    public static readonly IReadOnlySet<string> ExcludedDirectoryNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "target", "build", ".gradle", ".vs", ".idea", ".backup",
    };
}
