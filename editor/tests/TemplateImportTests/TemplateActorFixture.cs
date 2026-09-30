// ============================================================
//  TemplateActorFixture.cs — テンプレートアクタのテスト用の仮ライブラリ
//
//  【役割】
//  一時フォルダに「カタログ付きのテンプレートライブラリの縮小版」と「空のプロジェクト」を作る。
//  1 つのフィクスチャで次の判断を 1 つずつ検証できるようにしてある:
//   - カタログの並び（sort_order）・カテゴリの木・カタログに無い .actor を出さないこと
//   - 壊れたエントリ・未来の版のカタログを飛ばして警告にすること
//   - サムネイルの既定の場所 / 明示した場所
//   - 入れ子のプレハブ（参照だけのノード / 中身を保存したノード）の展開と印の除去
//   - 依存ファイル（テクスチャ・実行時に読む部品・requires）のコピーと「既にあれば触らない」
// ============================================================

using System;
using System.IO;
using System.Text;

namespace SEEDEditor.Tests.TemplateImport;

/// <summary>
/// 仮のテンプレートライブラリ（カタログ付き）とプロジェクトを一時フォルダに作り、破棄時に消す。
/// </summary>
public sealed class TemplateActorFixture : IDisposable
{
    // ── テストデータ（アサートと突き合わせるのでここを正典にする）──

    /// <summary>2D の基本のボタン（テクスチャを参照する）。</summary>
    public const string ButtonPath = "ui/prefabs/button.actor";

    /// <summary>実行中に行の部品を読むホイール（スクリプトの欄に .actor のパス）。</summary>
    public const string WheelPath = "ui/prefabs/wheel.actor";

    /// <summary>ホイールが実行中に読む行の部品（カタログには載せない内部の部品）。</summary>
    public const string RowPath = "ui/prefabs/row.actor";

    /// <summary>入れ子のプレハブを含む板（参照だけのノードと中身を保存したノード）。</summary>
    public const string PanelPath = "ui/prefabs/panel.actor";

    /// <summary>requires で枠の部品を要求するスタック。</summary>
    public const string StackPath = "ui/prefabs/stack.actor";

    /// <summary>スタックが実行中に読む枠の部品（JSON には現れず requires で要求される）。</summary>
    public const string FramePath = "ui/prefabs/frame.actor";

    /// <summary>JSON として壊れたテンプレート（カタログには載っているが飛ばされる）。</summary>
    public const string BrokenPath = "ui/prefabs/broken.actor";

    /// <summary>ボタンが参照するテクスチャ。</summary>
    public const string ButtonTexturePath = "ui/textures/btn.png";

    /// <summary>ボタンの既定の場所のサムネイル。</summary>
    public const string ButtonThumbnailPath = "ui/thumbnails/button.png";

    /// <summary>ホイールに明示したサムネイル（カタログの thumbnail 欄）。</summary>
    public const string WheelThumbnailPath = "ui/thumbnails/custom_wheel.png";

    /// <summary>3D のテンプレート（モデルを参照する）。</summary>
    public const string ThingPath = "actors/thing.actor";

    /// <summary>3D のテンプレートが参照するモデル。</summary>
    public const string ModelPath = "models/m.glb";

    /// <summary>UI のカタログのエントリ数（壊れた 1 件・欠けた 1 件・重複 1 件を除いた有効な数）。</summary>
    public const int ValidUiEntryCount = 4;

    /// <summary>ダミーバイナリの既定サイズ（バイト）。</summary>
    private const int DummyBinarySize = 16;

    // ── 生成物 ────────────────────────────────────────────────

    /// <summary>テンプレートライブラリのルート（絶対パス）。</summary>
    public string LibraryRoot { get; }

    /// <summary>プロジェクトのアセットルート（絶対パス）。</summary>
    public string AssetsRoot { get; }

    /// <summary>一時ファイルの書き出し先（OS の一時フォルダを汚さないようフィクスチャの中に置く）。</summary>
    public string StagingRoot { get; }

    /// <summary>フィクスチャ全体の親フォルダ。</summary>
    private readonly string _baseDir;

    /// <summary>一時フォルダ一式を作る。</summary>
    public TemplateActorFixture()
    {
        _baseDir    = Path.Combine(Path.GetTempPath(), "seed_template_actor_test_" + Guid.NewGuid().ToString("N"));
        LibraryRoot = Path.Combine(_baseDir, "templates");
        AssetsRoot  = Path.Combine(_baseDir, "project_assets");
        StagingRoot = Path.Combine(_baseDir, "staging");
        Directory.CreateDirectory(AssetsRoot);

        WriteUiCatalog();
        WriteActorsCatalog();
        WriteFutureCatalog();
        WriteTemplates();
    }

    /// <summary>一時フォルダを消す（消せなくてもテストは失敗させない）。</summary>
    public void Dispose()
    {
        try { Directory.Delete(_baseDir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>ライブラリ相対パスの絶対パス。</summary>
    public string Lib(string rel) => Path.Combine(LibraryRoot, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>プロジェクト相対パスの絶対パス。</summary>
    public string Asset(string rel) => Path.Combine(AssetsRoot, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>ライブラリへテキストを書く（親フォルダは作る）。</summary>
    public void WriteLibraryText(string rel, string text)
    {
        var path = Lib(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    /// <summary>ライブラリへダミーのバイナリを書く。</summary>
    public void WriteLibraryBinary(string rel)
    {
        var path = Lib(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[DummyBinarySize]);
    }

    /// <summary>プロジェクトへテキストを書く（「既にあるファイル」を作るため）。</summary>
    public void WriteAssetText(string rel, string text)
    {
        var path = Asset(rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text, new UTF8Encoding(false));
    }

    // ============================================================
    //  カタログ
    // ============================================================

    /// <summary>UI のカタログ（コメントと末尾のカンマを含む。壊れた・欠けた・重複のエントリも含む）。</summary>
    private void WriteUiCatalog() => WriteLibraryText("ui/template_actors.json", """
        {
          // 手で書くデータなのでコメントを許す
          "format_version": 1,
          "sort_order": 10,
          "entries": [
            { "path": "prefabs/button.actor", "name": "ボタン", "description": "押すボタン",
              "category": "UI/基本", "tags": ["button", "押す"] },
            { "path": "prefabs/wheel.actor", "name": "ホイール", "description": "回して選ぶ",
              "category": "UI/入力", "tags": ["wheel"], "thumbnail": "thumbnails/custom_wheel.png" },
            { "path": "prefabs/panel.actor", "name": "板", "description": "入れ子の見本",
              "category": "UI/基本", "tags": ["panel"] },
            { "path": "prefabs/stack.actor", "name": "スタック", "description": "画面を積む",
              "category": "UI/ナビゲーション", "tags": ["stack"], "requires": ["prefabs/frame.actor"] },
            { "path": "prefabs/broken.actor", "name": "壊れた", "category": "UI/基本" },
            { "path": "prefabs/nothing.actor", "name": "無い", "category": "UI/基本" },
            { "path": "prefabs/button.actor", "name": "重複", "category": "UI/基本" },
          ],
        }
        """);

    /// <summary>3D のカタログ（UI より後ろに並ぶ sort_order）。</summary>
    private void WriteActorsCatalog() => WriteLibraryText("actors/template_actors.json", """
        {
          "format_version": 1,
          "sort_order": 20,
          "entries": [
            { "path": "thing.actor", "name": "もの", "description": "モデルの見本",
              "category": "3D/見本", "tags": ["model"] }
          ]
        }
        """);

    /// <summary>このエディタより新しい版のカタログ（読まずに警告になる）。</summary>
    private void WriteFutureCatalog() => WriteLibraryText("future/template_actors.json", """
        { "format_version": 99, "entries": [ { "path": "x.actor", "name": "未来" } ] }
        """);

    // ============================================================
    //  テンプレート
    // ============================================================

    /// <summary>テンプレート・部品・テクスチャ・モデルを書く。</summary>
    private void WriteTemplates()
    {
        // ボタン: テクスチャを参照する 2D。文字列 "ui" と "prefabs" はフォルダ名と同じ（誤ってフォルダごとコピーしないことの確認）
        WriteLibraryText(ButtonPath, """
            { "name": "Button", "actor_kind": "Actor2D",
              "components": [
                { "name": "Sprite", "component": { "type": "SpriteComponent",
                  "data": { "texture_path": "assets://ui/textures/btn.png", "width": 120.0 } } },
                { "name": "Tag", "component": { "type": "ScriptComponent",
                  "data": { "type_name": "SEED.UI.Button", "fields": { "Group": "ui", "Kind": "prefabs" } } } }
              ],
              "children": [] }
            """);

        // ホイール: スクリプトの欄で行の部品（.actor）を実行中に読む
        WriteLibraryText(WheelPath, """
            { "name": "Wheel", "actor_kind": "Actor2D",
              "components": [
                { "name": "WheelPicker", "component": { "type": "ScriptComponent",
                  "data": { "type_name": "SEED.UI.WheelPicker",
                            "fields": { "RowPrefab": "assets://ui/prefabs/row.actor" } } } }
              ],
              "children": [] }
            """);
        WriteLibraryText(RowPath, """
            { "name": "Row", "actor_kind": "Actor2D", "components": [],
              "children": [ { "name": "Label", "actor_kind": "Actor2D", "components": [], "children": [] } ] }
            """);

        // 板: 参照だけのノード（Stub）と、中身を保存したノード（Saved）を子に持つ。ルートにも印が付いている
        WriteLibraryText(PanelPath, """
            { "name": "Panel", "actor_kind": "Actor2D", "prefab_source": "assets://ui/prefabs/panel.actor",
              "components": [],
              "children": [
                { "name": "Stub", "actor_kind": "Actor2D",
                  "canvas_transform": { "position": [5.0, 6.0] },
                  "prefab_source": "assets://ui/prefabs/row.actor", "prefab_hash": "0123456789abcdef",
                  "components": [], "children": [] },
                { "name": "Saved", "actor_kind": "Actor2D",
                  "prefab_source": "assets://ui/prefabs/row.actor", "prefab_hash": "fedcba9876543210",
                  "components": [ { "name": "Kept", "component": { "type": "SpriteComponent", "data": {} } } ],
                  "children": [
                    { "name": "Deep", "actor_kind": "Actor2D", "prefab_source": "assets://ui/prefabs/row.actor",
                      "components": [ { "name": "X", "component": { "type": "SpriteComponent", "data": {} } } ],
                      "children": [] }
                  ] }
              ] }
            """);

        // スタック: JSON には現れない枠の部品を requires で要求する
        WriteLibraryText(StackPath, """
            { "name": "Stack", "actor_kind": "Actor2D",
              "components": [ { "name": "S", "component": { "type": "ScriptComponent",
                "data": { "type_name": "SEED.UI.ScreenStack", "fields": {} } } } ],
              "children": [] }
            """);
        WriteLibraryText(FramePath, """
            { "name": "Frame", "actor_kind": "Actor2D", "components": [], "children": [] }
            """);

        // 壊れたテンプレート
        WriteLibraryText(BrokenPath, "{ this is not json");

        // 3D: モデルを参照する（actor_kind が無い＝3D）
        WriteLibraryText(ThingPath, """
            { "name": "Thing",
              "components": [ { "name": "Model", "component": { "type": "ModelComponent",
                "data": { "model_path": "assets://models/m.glb" } } } ],
              "children": [] }
            """);

        WriteLibraryBinary(ButtonTexturePath);
        WriteLibraryBinary(ButtonThumbnailPath);
        WriteLibraryBinary(WheelThumbnailPath);
        WriteLibraryBinary(ModelPath);
    }
}
