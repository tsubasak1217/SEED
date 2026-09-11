// ============================================================
//  TemplateLibraryFixture.cs — テスト用の仮テンプレートライブラリ生成
//
//  【役割】
//  一時フォルダに「テンプレートライブラリの縮小版」と「空のプロジェクトアセット」を作る。
//  参照の連鎖（scene → actor → png）・フォルダエントリ・参照切れ・
//  未知カテゴリ・隠し要素を 1 つずつ含み、1 回の走査で各判断を検証できるようにしてある。
//
//  【なぜファイルを実際に作るか】
//  検証したいのは「参照先が実在するか」「コピー先に同名ファイルがあるか」という
//  ファイルシステムの判断そのものなので、モックにすると検証対象が消える。
// ============================================================

using System;
using System.IO;
using System.Text;

namespace SEEDEditor.Tests.TemplateImport;

/// <summary>
/// 一時フォルダに仮のテンプレートライブラリとコピー先を作り、破棄時に消すフィクスチャ。
/// </summary>
public sealed class TemplateLibraryFixture : IDisposable
{
    // ── テストデータの内容（アサートと突き合わせるのでここを正典にする）──

    /// <summary>参照の連鎖の起点になるシーンの相対パス。</summary>
    public const string SceneEntry = "scenes/demo.scene";

    /// <summary>参照を持たないシーンの相対パス。</summary>
    public const string EmptySceneEntry = "scenes/empty.scene";

    /// <summary>シーンから参照されるアクタの相対パス。</summary>
    public const string ActorPath = "actors/hero.actor";

    /// <summary>シーンから直接参照されるテクスチャの相対パス。</summary>
    public const string RockTexturePath = "textures/rock.png";

    /// <summary>アクタ経由で参照されるテクスチャの相対パス（2 段目の閉包）。</summary>
    public const string HeroTexturePath = "textures/hero.png";

    /// <summary>シーンが参照するが実体の無いテクスチャ（欠落検出用）。</summary>
    public const string MissingTexturePath = "textures/missing.png";

    /// <summary>フォルダエントリの相対パス。</summary>
    public const string FontFolderEntry = "fonts/Digital";

    /// <summary>フォルダエントリに含まれるファイル（配下が丸ごと入ることの確認用）。</summary>
    public const string FontFilePath = "fonts/Digital/digital.ttf";

    /// <summary>フォルダエントリの入れ子（階層を保ってコピーされることの確認用）。</summary>
    public const string FontNestedFilePath = "fonts/Digital/meta/info.json";

    /// <summary>表示名の対応表に無いカテゴリ（フォルダ名がそのまま表示されること）。</summary>
    public const string UnknownCategoryFolder = "mystery";

    /// <summary>どのテンプレートからも参照されないファイル（閉包に入らないことの確認用）。</summary>
    public const string UnreferencedPath = "textures/unused.png";

    /// <summary>フォルダエントリ配下のファイル数。</summary>
    public const int FontFolderFileCount = 2;

    /// <summary>ダミーバイナリの既定サイズ（バイト）。</summary>
    private const int DummyBinarySize = 16;

    // ── 生成物 ────────────────────────────────────────────────

    /// <summary>テンプレートライブラリのルート（絶対パス）。</summary>
    public string LibraryRoot { get; }

    /// <summary>コピー先となるプロジェクトアセットルート（絶対パス）。</summary>
    public string AssetsRoot { get; }

    /// <summary>一時フォルダ一式を作る。</summary>
    public TemplateLibraryFixture()
    {
        var baseDir = Path.Combine(
            Path.GetTempPath(), "seed_template_test_" + Guid.NewGuid().ToString("N"));
        LibraryRoot = Path.Combine(baseDir, "templates");
        AssetsRoot  = Path.Combine(baseDir, "project_assets");
        Directory.CreateDirectory(LibraryRoot);
        Directory.CreateDirectory(AssetsRoot);
        BuildLibrary();
    }

    // ============================================================
    //  ライブラリの構築
    // ============================================================

    /// <summary>仮ライブラリの全ファイルを書き出す。</summary>
    private void BuildLibrary()
    {
        // ── シーン: 参照の起点（assets:// のルート相対参照）──────
        //   ライブラリのルートをアセットルートとみなせば解決できる書き方（実物と同じ）。
        WriteLibraryText(SceneEntry, $$"""
        {
          "name":    "demo",
          "actor":   "assets://{{ActorPath}}",
          "texture": "assets://{{RockTexturePath}}",
          "broken":  "assets://{{MissingTexturePath}}"
        }
        """);

        // 参照を持たないシーン（エントリ単独でコピーできることの確認用）
        WriteLibraryText(EmptySceneEntry, """
        { "name": "empty" }
        """);

        // ── アクタ: 2 段目の参照 ───────────────────────────────
        WriteLibraryText(ActorPath, $$"""
        { "sprite": "assets://{{HeroTexturePath}}" }
        """);

        // ── 葉（テクスチャ）────────────────────────────────────
        WriteLibraryBinary(RockTexturePath, DummyBinarySize);
        WriteLibraryBinary(HeroTexturePath, DummyBinarySize);

        // どこからも参照されないファイル（閉包に入ってはいけない）
        WriteLibraryBinary(UnreferencedPath, DummyBinarySize);

        // ── フォルダエントリ（配下すべてで 1 単位）──────────────
        WriteLibraryBinary(FontFilePath, DummyBinarySize);
        WriteLibraryText(FontNestedFilePath, """
        { "family": "Digital" }
        """);

        // ── 未知カテゴリ（表示名の対応表に無い）─────────────────
        WriteLibraryText(UnknownCategoryFolder + "/note.txt", "just a note");

        // ── 隠し要素（一覧に出てはいけない）─────────────────────
        WriteLibraryText(".backup/old.scene", "{ }");
        WriteLibraryText("scenes/.hidden.scene", "{ }");
    }

    // ============================================================
    //  ヘルパー
    // ============================================================

    /// <summary>ライブラリ側にテキストファイルを書く（親フォルダは自動作成）。</summary>
    /// <param name="relative">ライブラリルート相対パス。</param>
    /// <param name="text">中身。</param>
    public void WriteLibraryText(string relative, string text) =>
        WriteText(LibraryRoot, relative, text);

    /// <summary>ライブラリ側にダミーバイナリを書く。</summary>
    /// <param name="relative">ライブラリルート相対パス。</param>
    /// <param name="size">バイト数。</param>
    public void WriteLibraryBinary(string relative, int size) =>
        WriteBinary(LibraryRoot, relative, size);

    /// <summary>コピー先（プロジェクト）側にテキストファイルを書く。</summary>
    /// <param name="relative">アセットルート相対パス。</param>
    /// <param name="text">中身。</param>
    public void WriteAssetText(string relative, string text) =>
        WriteText(AssetsRoot, relative, text);

    /// <summary>コピー先のファイルが存在するか。</summary>
    /// <param name="relative">アセットルート相対パス。</param>
    /// <returns>存在すれば true。</returns>
    public bool AssetExists(string relative) =>
        File.Exists(Path.Combine(AssetsRoot, relative.Replace('/', Path.DirectorySeparatorChar)));

    /// <summary>コピー先のテキストを読む（存在しなければ null）。</summary>
    /// <param name="relative">アセットルート相対パス。</param>
    /// <returns>ファイルの中身。</returns>
    public string? ReadAssetText(string relative)
    {
        var abs = Path.Combine(AssetsRoot, relative.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(abs) ? File.ReadAllText(abs) : null;
    }

    /// <summary>指定ルート配下へテキストを書く。</summary>
    /// <param name="root">書き込み先のルート。</param>
    /// <param name="relative">ルート相対パス。</param>
    /// <param name="text">中身。</param>
    private static void WriteText(string root, string relative, string text)
    {
        var abs = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>指定ルート配下へダミーバイナリを書く。</summary>
    /// <param name="root">書き込み先のルート。</param>
    /// <param name="relative">ルート相対パス。</param>
    /// <param name="size">バイト数（中身は連番）。</param>
    private static void WriteBinary(string root, string relative, int size)
    {
        var abs = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        var bytes = new byte[size];
        for (int i = 0; i < size; i++) bytes[i] = (byte)(i & 0xFF);
        File.WriteAllBytes(abs, bytes);
    }

    /// <summary>一時フォルダごと削除する。</summary>
    public void Dispose()
    {
        try
        {
            var baseDir = Path.GetDirectoryName(LibraryRoot);
            if (baseDir is not null) Directory.Delete(baseDir, recursive: true);
        }
        catch { /* 掃除に失敗してもテスト結果には影響させない */ }
    }
}
