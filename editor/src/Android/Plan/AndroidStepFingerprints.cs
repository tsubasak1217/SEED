// ============================================================
//  AndroidStepFingerprints.cs — 各ビルド工程の「今の入力の指紋・今の出力の同一性」を集める（ファイルを読む側）
//
//  計画（AndroidBuildPlan.Create。純粋な処理）の材料を作る。工程を終えた後も同じ関数で作り直して記録する
//  （Gradle の入力は上流の工程の出力を含むので、上流を作り直した後に計算し直す必要がある）。
//
//  WPF に依存しない（コンソールツール・単体テストからリンクされる）。
// ============================================================

using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SEEDEditor.Android.Dotnet;
using SEEDEditor.Android.Gradle;
using SEEDEditor.Android.Icons;
using SEEDEditor.Android.Project;
using SEEDEditor.Android.Toolchain;

namespace SEEDEditor.Android.Plan;

/// <summary>各ビルド工程の指紋の計算。</summary>
public static class AndroidStepFingerprints
{
    /// <summary>pak の指紋の材料で、収録の起点に足すシーン 1 つを表す名前（段階C-4）。</summary>
    private const string ExtraSceneMaterialKey = "extra_scene";

    /// <summary>
    /// libSEED.so（1 つの ABI）の指紋。入力はエンジンのソースとビルドのパラメータ、出力は jniLibs の .so。
    /// </summary>
    /// <param name="engine">エンジン側の置き場。</param>
    /// <param name="abi">ABI。</param>
    /// <param name="release">--release か。</param>
    /// <param name="ndkPath">NDK の場所（分からなければ null）。</param>
    /// <returns>指紋。</returns>
    public static AndroidStepFingerprint Native(AndroidEnginePaths engine, AndroidAbi abi, bool release, string? ndkPath)
    {
        var builder = new AndroidFingerprintBuilder()
            .AddValue("abi", abi.Name)
            .AddValue("profile", release ? "release" : "debug")
            .AddValue("api_level", AndroidRuntimeContract.MinApiLevel.ToString(CultureInfo.InvariantCulture))
            .AddValue("ndk", ndkPath);
        AddRepositoryTrees(builder, engine, AndroidBuildInputs.NativeSources);
        return new AndroidStepFingerprint(builder.Build(), AndroidOutputIdentity.OfFile(engine.NativeLibraryPath(abi)));
    }

    /// <summary>
    /// APK に入れる配布物（pak とスクリプト）の指紋。入力はプロジェクトのアセットと SeedPak の作り方、出力は置き場のフォルダ。
    /// プロジェクトを APK に入れない（開発用・プロジェクト無し）ときは「置き場を空にする」ことが工程の中身になる。
    /// </summary>
    /// <param name="engine">エンジン側の置き場。</param>
    /// <param name="project">プロジェクト（無ければ null）。</param>
    /// <param name="extraScenes">
    /// pak の収録の起点に足すシーン（未登録の起動シーン。段階C-4。Project/AndroidPakSceneSeeds）。足すシーンが変われば pak の中身が
    /// 変わるので材料に入れる。空なら材料に足さない（足さないときの指紋は段階C-3 までと同じ式）。
    /// </param>
    /// <returns>指紋。</returns>
    public static AndroidStepFingerprint PackageContent(
        AndroidEnginePaths engine, AndroidProjectInfo? project, IReadOnlyList<string>? extraScenes = null)
    {
        var builder = new AndroidFingerprintBuilder();
        if (project is { Mode: AndroidProjectMode.Packaged })
        {
            builder.AddValue("mode", "packaged")
                .AddValue("project", project.SourceArgument)
                .AddValue("assets_root", project.Folder.AssetsRoot)
                // プロジェクトのアセットは生成物の名前（build 等）でも中身なので除外しない
                .AddTree("assets", project.Folder.AssetsRoot);
            AddRepositoryTrees(builder, engine, AndroidBuildInputs.PackageToolSources);
            if (extraScenes is { Count: > 0 })
            {
                // 1 つずつ別の材料にする（区切り文字を含むパスでも、別の組み合わせと同じ材料にならないように）
                foreach (var scene in extraScenes) builder.AddValue(ExtraSceneMaterialKey, scene);
            }
        }
        else
        {
            builder.AddValue("mode", "empty");
        }
        return new AndroidStepFingerprint(builder.Build(), AndroidOutputIdentity.OfDirectory(engine.ApkPackageDir));
    }

    /// <summary>
    /// 同梱 .NET の指紋。入力は設定ファイルの中身・ABI・組み立て方の版、出力は置き場のフォルダ。
    /// </summary>
    /// <param name="engine">エンジン側の置き場。</param>
    /// <param name="abis">今回の ABI。</param>
    /// <returns>指紋。</returns>
    public static AndroidStepFingerprint DotnetBundle(AndroidEnginePaths engine, IReadOnlyList<AndroidAbi> abis)
    {
        var builder = new AndroidFingerprintBuilder()
            .AddValue("abis", AndroidAbis.Describe(abis))
            .AddValue("manifest_format", DotnetRuntimeBundle.ManifestFormatVersion.ToString(CultureInfo.InvariantCulture))
            .AddValue("assembler_revision", DotnetRuntimeBundle.AssemblerRevision.ToString(CultureInfo.InvariantCulture))
            .AddFileContent("dotnet_runtime.json", engine.DotnetSettingsPath);
        return new AndroidStepFingerprint(builder.Build(), AndroidOutputIdentity.OfDirectory(engine.DotnetStagingDir));
    }

    /// <summary>
    /// APK / AAB（Gradle）の指紋。入力は上流の出力（.so・pak の置き場・同梱 .NET の置き場）・Gradle のソース・渡すプロパティ
    /// （パスワードは伏せ字のまま。値そのものは材料にしない）、配布用はキーストアのファイル、アイコンを作るならその元の PNG と作り方の版と
    /// 置き場。出力はビルドの種類と形式ごとの配布物（段階D）。
    /// </summary>
    /// <param name="engine">エンジン側の置き場。</param>
    /// <param name="parameters">Gradle へ渡す値。</param>
    /// <param name="launcherIcon">アイコンの元（作らないなら null）。</param>
    /// <returns>指紋。</returns>
    public static AndroidStepFingerprint Gradle(AndroidEnginePaths engine, GradleBuildParameters parameters, LauncherIconSource? launcherIcon = null)
    {
        var builder = new AndroidFingerprintBuilder();
        foreach (var property in GradleInvocation.Build(parameters).Properties)
        {
            builder.AddValue("property:" + property.Name, property.Value);
        }
        foreach (var abi in parameters.Abis)
        {
            builder.AddValue("native:" + abi.Name, AndroidOutputIdentity.OfFile(engine.NativeLibraryPath(abi)));
        }
        builder.AddValue("package", AndroidOutputIdentity.OfDirectory(engine.ApkPackageDir))
            .AddValue("dotnet", AndroidOutputIdentity.OfDirectory(engine.DotnetStagingDir));
        if (parameters.Signing is { } signing)
        {
            // 鍵を差し替えたら作り直す（中身は読まず、大きさと更新時刻。パスワードは材料にしない）
            builder.AddValue("keystore", AndroidOutputIdentity.OfFile(signing.KeystorePath));
        }
        if (launcherIcon is not null)
        {
            builder.AddValue("icon_source", AndroidOutputIdentity.OfFile(launcherIcon.IconPath))
                .AddValue("icon_background", launcherIcon.Background.ToAndroidHex())
                .AddValue("icon_revision", LauncherIconStager.GeneratorRevision.ToString(CultureInfo.InvariantCulture))
                .AddValue("icon_staging", AndroidOutputIdentity.OfDirectory(engine.LauncherIconStagingDir));
        }
        AddRepositoryTrees(builder, engine, AndroidBuildInputs.GradleSources);
        return new AndroidStepFingerprint(builder.Build(), AndroidOutputIdentity.OfFile(engine.ArtifactPath(parameters.Variant, parameters.Format)));
    }

    /// <summary>リポジトリのルートからの相対パスの表を材料に足す（生成物のフォルダは辿らない）。</summary>
    private static void AddRepositoryTrees(AndroidFingerprintBuilder builder, AndroidEnginePaths engine, IEnumerable<string> relativePaths)
    {
        foreach (var relative in relativePaths)
        {
            var path = Path.Combine(engine.RepositoryRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            builder.AddTree(relative, path, AndroidBuildInputs.ExcludedDirectoryNames);
        }
    }
}
