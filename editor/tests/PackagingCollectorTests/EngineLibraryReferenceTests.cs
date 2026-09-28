// ============================================================
//  EngineLibraryReferenceTests.cs — エンジンの C# ライブラリ（SEEDScripting）の定数の assets:// パスを収録の起点に足す
//  （2026-09-28。docs/backlog.md「SEED.UI の部品が既定で読むプレハブがパッケージに入らない」）の単体テスト
//
//  【検証範囲】
//   - runtime/src の隣の scripting/src の .cs に定数で書かれた assets:// パスは、プロジェクトにあれば収録される
//     （ScreenStack の既定の枠のプレハブ。シーンにもプロジェクトのスクリプトにも現れない）
//   - コメント行（// と ///）の中の例のパスは起点にしない
//   - プロジェクトに無いパスは欠落として報告しない（Rust の内蔵参照と同じ扱い）
//   - runtime/src を渡さない（エンジンのソースが無い）ときは従来どおり足さない
// ============================================================

using System;
using System.IO;
using System.Linq;
using SEEDEditor.Packaging.Collect;
using SpriteRigTests;

namespace SEEDEditor.Tests.PackagingCollector;

/// <summary>エンジンの C# ライブラリの定数の参照のテスト。</summary>
public static class EngineLibraryReferenceTests
{
    /// <summary>ライブラリの定数が指すプレハブ（どのシーンからも参照しない）。</summary>
    private const string DefaultFramePrefab = "ui/prefabs/screen_frame.actor";

    /// <summary>ライブラリのコメントにだけ例として書かれたファイル（入れてはいけない）。</summary>
    private const string CommentOnlyTheme = "ui/themes/base.json";

    /// <summary>ライブラリの定数が指すが、プロジェクトには無いプレハブ。</summary>
    private const string AbsentPrefab = "ui/prefabs/toast.actor";

    /// <summary>テストを登録する。</summary>
    /// <param name="h">テストランナー。</param>
    public static void Register(TestHarness h)
    {
        h.Add("エンジンの C# ライブラリ: 定数の既定のプレハブはプロジェクトにあれば収録（コメントの例・無いファイルは足さない）", IncludesLibraryConstants);
        h.Add("エンジンの C# ライブラリ: runtime/src を渡さなければ従来どおり足さない・場所は runtime/src の ../../scripting/src", NeedsRuntimeSource);
    }

    /// <summary>仮のリポジトリ（runtime/src と scripting/src）とプロジェクトを作る。</summary>
    private sealed class RepoFixture : IDisposable
    {
        /// <summary>リポジトリのルート。</summary>
        public string Repo { get; }

        /// <summary>runtime/src。</summary>
        public string RuntimeSource => Path.Combine(Repo, "runtime", "src");

        /// <summary>プロジェクトのアセットルート。</summary>
        public string Assets => Path.Combine(Repo, "project", "assets");

        public RepoFixture()
        {
            Repo = Path.Combine(Path.GetTempPath(), "seed_engine_lib_ref_" + Guid.NewGuid().ToString("N"));
            Write("runtime/src/lib.rs", "// エンジン本体（このテストでは assets:// を書かない）\n");
            Write("scripting/src/Api/UI/Navigation/ScreenStack.cs", string.Join("\n", new[]
            {
                "namespace SEED.UI;",
                "/// 例: <c>\"assets://" + CommentOnlyTheme + "\"</c> の書き方（文書コメントの例は起点にしない）",
                "public sealed class ScreenStack",
                "{",
                "    // \"assets://" + CommentOnlyTheme + "\" もコメントの例",
                "    public const string DefaultFramePrefab = \"assets://" + DefaultFramePrefab + "\";",
                "    public const string DefaultToastPrefab = \"assets://" + AbsentPrefab + "\";",
                "}",
            }));
            Write("project/assets/project_settings.json",
                "{ \"start_scene\": \"assets://scenes/nav.scene\", \"scenes\": [ { \"name\": \"nav\", \"path\": \"assets://scenes/nav.scene\" } ] }");
            // シーンは ScreenStack を使うが、枠のプレハブの欄は空（既定の値を使う）
            Write("project/assets/scenes/nav.scene", "{ \"script\": \"ScreenStack\", \"FramePrefab\": \"\" }");
            Write("project/assets/" + DefaultFramePrefab, "{ \"name\": \"screen_frame\" }");
            Write("project/assets/" + CommentOnlyTheme, "{ \"name\": \"base\" }");
        }

        /// <summary>リポジトリの中にファイルを書く。</summary>
        private void Write(string relative, string text)
        {
            var path = Path.Combine(Repo, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }

        public void Dispose()
        {
            try { Directory.Delete(Repo, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>定数の既定のプレハブが入り、コメントの例・無いファイルは入らない。</summary>
    private static void IncludesLibraryConstants()
    {
        using var fx = new RepoFixture();
        var result = new AssetCollector(fx.Assets, new AssetPackagingSettings(), fx.RuntimeSource).Collect();
        var included = result.Included.Select(a => a.RelPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check.True(included.Contains(DefaultFramePrefab), $"ライブラリの定数のプレハブが入る: {string.Join(", ", included)}");
        Check.True(!included.Contains(CommentOnlyTheme), "コメントの例のパスは入らない");
        Check.True(!result.MissingReferences.Any(m => m.ReferencePath.Contains("toast", StringComparison.OrdinalIgnoreCase)),
            "プロジェクトに無い既定のプレハブは欠落として報告しない（内蔵参照と同じ）");
    }

    /// <summary>runtime/src が無ければ足さない。</summary>
    private static void NeedsRuntimeSource()
    {
        using var fx = new RepoFixture();
        var result = new AssetCollector(fx.Assets, new AssetPackagingSettings()).Collect();
        Check.True(!result.Included.Any(a => string.Equals(a.RelPath, DefaultFramePrefab, StringComparison.OrdinalIgnoreCase)),
            "エンジンのソースを渡さなければ従来どおり（参照グラフに現れないプレハブは入らない）");
        Check.Equal(Path.GetFullPath(Path.Combine(fx.Repo, "scripting", "src")),
            AssetCollector.ScriptingLibrarySourceRoot(fx.RuntimeSource), "runtime/src の ../../scripting/src");
        Check.True(AssetCollector.ScriptingLibrarySourceRoot(null) is null, "runtime/src が無ければ null");
    }
}
