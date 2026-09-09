// ============================================================
//  AssetFixture.cs — テスト用の仮アセットツリー生成
//
//  【役割】
//  一時フォルダに「実プロジェクトの縮小版」を作る。
//  参照の 4 系統・同伴ファイル・除外対象・未参照ファイルを 1 つずつ含み、
//  AssetCollector の各判断を 1 回の収集で検証できるようにしてある。
//
//  【なぜファイルを実際に作るか】
//  収集の肝は「参照文字列が実在するか」の判定なので、
//  ファイルシステムを模擬（モック）すると検証したい部分がそのまま抜け落ちる。
// ============================================================

using System;
using System.IO;
using System.Text;

namespace SEEDEditor.Tests.PackagingCollector;

/// <summary>
/// 一時フォルダに仮のアセットツリーを作り、破棄時に消す使い捨てフィクスチャ。
/// </summary>
public sealed class AssetFixture : IDisposable
{
    /// <summary>アセットルートの絶対パス。</summary>
    public string Root { get; }

    /// <summary>仮アセットツリーを作る。</summary>
    public AssetFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "seed_pak_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
        Build();
    }

    // ============================================================
    //  ツリーの構築
    // ============================================================

    /// <summary>テストで使う全ファイルを書き出す。</summary>
    private void Build()
    {
        // ── アセットルート絶対パスの 4 表記 ────────────────────
        //   実プロジェクトの .scene に実際に現れる 4 形式をそのまま埋め込む。
        var slash    = Root.Replace('\\', '/');            // C:/tmp/xxx
        var back     = slash.Replace('/', '\\');           // C:\tmp\xxx
        var escBack  = back.Replace("\\", "\\\\");         // C:\\tmp\\xxx（JSON エスケープ）
        var escSlash = slash.Replace("/", "\\/");          // C:\/tmp\/xxx（JSON エスケープ）

        // ── 起点: project_settings.json ────────────────────────
        //   demo_missing は実体を作らない（登録シーンの欠落検出用）。
        WriteText("project_settings.json", """
        {
          "game_name": "FixtureGame",
          "start_scene": "assets://scenes/main.scene",
          "scenes": [
            { "name": "main",    "path": "assets://scenes/main.scene" },
            { "name": "missing", "path": "assets://scenes/missing.scene" }
          ]
        }
        """);

        // ── メインシーン: 参照の見本市 ─────────────────────────
        var scene = new StringBuilder();
        scene.AppendLine("{");
        scene.AppendLine("  \"actor\":      \"assets://actors/hero.actor\",");            // 仮想パス
        scene.AppendLine($"  \"model\":      \"{slash}/models/box.glb\",");               // 絶対 (1) スラッシュ
        scene.AppendLine($"  \"audio\":      \"{back}\\audio\\bgm.mp3\",");               // 絶対 (2) バックスラッシュ
        scene.AppendLine($"  \"logo\":       \"{escBack}\\\\ui\\\\logo.png\",");          // 絶対 (3) エスケープ済み
        scene.AppendLine($"  \"frame\":      \"{escSlash}\\/ui\\/frame.png\",");          // 絶対 (4) エスケープ済み
        scene.AppendLine("  \"gltf\":       \"assets://models/man.gltf\",");
        scene.AppendLine("  \"terrain\":    \"assets://world/chunk_0_0_0.tvox\",");
        scene.AppendLine("  \"layers\":     \"assets://terrain/layers.json\",");
        scene.AppendLine("  \"font\":       \"assets://templates/fonts/f.ttf\",");        // 除外フォルダだが参照あり
        scene.AppendLine("  \"editorOnly\": \"assets://packaging_settings.json\",");      // 参照されても同梱禁止
        scene.AppendLine("  \"broken\":     \"assets://scenes/no_such_texture.png\"");    // 欠落参照
        scene.AppendLine("}");
        WriteText("scenes/main.scene", scene.ToString());

        // ── アクタ -> スクリプト -> テクスチャ（多段の閉包） ────
        WriteText("actors/hero.actor", """
        { "script": "assets://scripts/Hero.cs" }
        """);
        WriteText("scripts/Hero.cs", """
        namespace Game;
        public class Hero
        {
            // C# の文字列リテラルに書かれた参照も拾えること
            private const string SparkTexture = "assets://fx/spark.png";
        }
        """);
        WriteBinary("fx/spark.png", 16);

        // ── コメント中の参照（終端文字が現れず日本語が続く） ────
        //   実プロジェクトの .cs コメントに実在する書き方をそのまま再現する。
        WriteText("scripts/Notes.cs", """
        namespace Game;
        /// <summary>説明。パスは <c>assets://...</c> の形式で書く。</summary>
        public class Notes
        {
            // 出題データは assets://notes/memo.png）から読む。
        }
        """);
        WriteBinary("notes/memo.png", 10);

        // ── glTF: uri（相対 / URL エンコード） ──────────────────
        WriteText("models/man.gltf", """
        {
          "asset": { "version": "2.0" },
          "buffers": [ { "uri": "man.bin", "byteLength": 8 } ],
          "images":  [ { "uri": "tex%20a.png" } ]
        }
        """);
        WriteBinary("models/man.bin", 8);
        WriteBinary("models/tex a.png", 12);
        WriteBinary("models/box.glb", 32);

        // ── 絶対パスで参照される葉 ─────────────────────────────
        WriteBinary("audio/bgm.mp3", 24);
        WriteBinary("ui/logo.png", 20);
        WriteBinary("ui/frame.png", 20);

        // ── アセットルート相対の参照（layers.json の作法） ──────
        WriteText("terrain/layers.json", """
        { "layers": [ { "base_color_texture": "textures/rock.png" } ] }
        """);
        WriteBinary("textures/rock.png", 40);

        // ── 地形の同伴ファイル（参照グラフには現れない） ────────
        WriteBinary("world/chunk_0_0_0.tvox", 64);
        WriteBinary("world/chunk_0_0_0.tscatter", 16);
        WriteBinary("world/chunk_0_0_0.tcover", 16);
        WriteText("world/terrain_meta.json", "{ \"note\": \"folder companion\" }");

        // ── 除外対象（未参照） ─────────────────────────────────
        WriteText(".backup/old.scene", "{ }");
        WriteText(".backup/Old.cs", "public class Old { }");   // 常時同梱拡張子だが除外フォルダ
        WriteBinary("junk/scratch.tmp", 8);                    // 除外拡張子
        WriteBinary("unused/unused.png", 8);                   // 除外ではないが未参照

        // ── 除外フォルダにあるが参照される ──────────────────────
        WriteBinary("templates/fonts/f.ttf", 48);

        // ── エディタ専用（参照されても同梱禁止） ────────────────
        WriteText("packaging_settings.json", "{ \"game_name\": \"FixtureGame\" }");

        // ── 追加同梱フォルダのテスト用（未参照） ────────────────
        WriteBinary("extra/extra.bin", 8);
    }

    // ============================================================
    //  ヘルパー
    // ============================================================

    /// <summary>テキストファイルを書く（親フォルダは自動作成）。</summary>
    /// <param name="relative">ルート相対パス。</param>
    /// <param name="text">中身。</param>
    public void WriteText(string relative, string text)
    {
        var abs = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        File.WriteAllText(abs, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>指定バイト数のダミーバイナリを書く。</summary>
    /// <param name="relative">ルート相対パス。</param>
    /// <param name="size">バイト数（中身は連番）。</param>
    public void WriteBinary(string relative, int size)
    {
        var abs = Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(abs)!);
        var bytes = new byte[size];
        for (int i = 0; i < size; i++) bytes[i] = (byte)(i & 0xFF);
        File.WriteAllBytes(abs, bytes);
    }

    /// <summary>一時フォルダごと削除する。</summary>
    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch { /* 掃除に失敗してもテスト結果には影響させない */ }
    }
}
