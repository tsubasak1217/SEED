using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace SEEDEditor;

/// <summary>
/// プロジェクトブラウザから新規アイテム（3D/2D Actor、スクリプト等）を作成するダイアログ。
/// 選択された種類に応じてテンプレート内容のファイルを対象フォルダに生成し、<see cref="ItemCreated"/> で通知する。
/// </summary>
public partial class CreateItemWindow : Window
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attr, ref int value, int size);
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    private readonly string _targetPath;

    /// <summary>アイテムが作成されたときに発火する（作成したファイルのフルパス）。</summary>
    public event Action<string>? ItemCreated;

    public CreateItemWindow(string targetPath)
    {
        InitializeComponent();
        _targetPath = targetPath;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var helper = new WindowInteropHelper(this);
        int dark = 1;
        DwmSetWindowAttribute(helper.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    // ── 共通ヘルパー ──────────────────────────────────────────────

    /// <summary>
    /// 作成先フォルダ内で未使用のファイルパスを返す。
    /// 既存と衝突する場合は "BaseName(2).ext", "BaseName(3).ext" … と連番で回避する。
    /// </summary>
    /// <param name="baseName">拡張子を除いた基本ファイル名。</param>
    /// <param name="ext">ドット付きの拡張子（例 ".scene"）。</param>
    private string MakeUniquePath(string baseName, string ext)
    {
        var path = Path.Combine(_targetPath, baseName + ext);
        int n = 1;
        while (File.Exists(path) || Directory.Exists(path))
        {
            n++;
            path = Path.Combine(_targetPath, $"{baseName}({n}){ext}");
        }
        return path;
    }

    /// <summary>
    /// テンプレート内容のファイルを作成し、<see cref="ItemCreated"/> を発火してウィンドウを閉じる。
    ///
    /// 書き込みは BOM なし UTF-8 で行う（`new UTF8Encoding(false)`）。
    /// ランタイム側は JSON / WGSL をそのままパースするため、先頭に BOM があると
    /// パースに失敗する可能性があるためこの指定は必須である。
    /// </summary>
    /// <param name="baseName">拡張子を除いた基本ファイル名。</param>
    /// <param name="ext">ドット付きの拡張子。</param>
    /// <param name="contentFactory">
    /// 確定したファイル名（拡張子なし）を受け取り、書き込む内容を返す関数。
    /// アセット内部の "name" フィールドなどをファイル名に合わせるために使う。
    /// </param>
    private void CreateFileAndClose(string baseName, string ext, Func<string, string> contentFactory)
    {
        var path = MakeUniquePath(baseName, ext);
        var stem = Path.GetFileNameWithoutExtension(path);
        try
        {
            File.WriteAllText(path, contentFactory(stem), new UTF8Encoding(false));
        }
        catch
        {
            // 書き込み失敗（権限・パス長など）時は何も作らずウィンドウだけ閉じる
            Close();
            return;
        }

        ItemCreated?.Invoke(path);
        Close();
    }

    /// <summary>3D Actor ファイルを作成する（デフォルト Transform 使用）。</summary>
    private void OnCreateActor3D(object sender, RoutedEventArgs e)
    {
        const string BaseName = "NewActor";
        const string Ext      = ".actor";

        var path = Path.Combine(_targetPath, BaseName + Ext);
        int n = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(_targetPath, $"{BaseName}({n}){Ext}");
            n++;
        }

        var actorName = Path.GetFileNameWithoutExtension(path);
        // actor_kind は省略すると Actor3D（デフォルト）として扱われる
        var json = $"{{\n  \"name\": \"{actorName}\",\n  \"components\": [],\n  \"children\": []\n}}";
        File.WriteAllText(path, json, Encoding.UTF8);

        ItemCreated?.Invoke(path);
        Close();
    }

    /// <summary>2D Actor ファイルを作成する（CanvasTransform 使用）。</summary>
    private void OnCreateActor2D(object sender, RoutedEventArgs e)
    {
        const string BaseName = "NewActor2D";
        const string Ext      = ".actor";

        var path = Path.Combine(_targetPath, BaseName + Ext);
        int n = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(_targetPath, $"{BaseName}({n}){Ext}");
            n++;
        }

        var actorName = Path.GetFileNameWithoutExtension(path);
        // actor_kind を "Actor2D" に設定することで CanvasTransform が割り当てられる
        var json = $"{{\n  \"name\": \"{actorName}\",\n  \"actor_kind\": \"Actor2D\",\n  \"components\": [],\n  \"children\": []\n}}";
        File.WriteAllText(path, json, Encoding.UTF8);

        ItemCreated?.Invoke(path);
        Close();
    }

    private void OnCreateScript(object sender, RoutedEventArgs e)
    {
        const string BaseName = "NewScript";
        const string Ext      = ".cs";

        var path = Path.Combine(_targetPath, BaseName + Ext);
        int n = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(_targetPath, $"{BaseName}({n}){Ext}");
            n++;
        }

        var className = Path.GetFileNameWithoutExtension(path);
        // ランタイム（SEEDScripting.dll）の SEEDScript を継承するテンプレート。
        // SEEDScript が提供する基本ライフサイクル関数を一通り雛形として生成し、
        // 不要なものは削除、必要なものへ処理を書き足すだけで使えるようにする。
        // 各関数は 1 フレーム内で「BeginFrame → EarlyUpdate → Update →
        // ConstantUpdate → LateUpdate → Render → EndFrame」の順に呼ばれる。
        var template  = $$"""
            using SEEDEditor.Scripting;   // SEEDScript・[SerializeField]・NativeFrameContext（衝突しない基盤のみ）

            /// <summary>{{className}} スクリプト。</summary>
            public class {{className}} : SEEDScript
            {
                // インスペクタに公開するフィールドは [SerializeField] を付ける
                // [SerializeField(Label = "速度")]
                // private float speed = 1.0f;

                // ゲーム向けエンジン API（Mathf/Vector3/Time/Random/Debug/GameObject など）は
                // SEED 名前空間にあります。System と型名が衝突する（例: Random ↔ System.Random）ため、
                // エンジン側からは using を付けていません。「SEED.」で修飾して呼び出してください。
                //   例) num += SEED.Random.Range(0, 10);
                //       transform.Position += SEED.Vector3.Right * SEED.Time.DeltaTime;
                // ※ どうしても無修飾で書きたい場合は自分で「using SEED;」を足せます（衝突解決は自己責任）。
                //
                // 使える API 例:
                //   transform.Position / .Rotation / .Scale        … 自分の GameObject の Transform（get/set）
                //   gameObject.GetComponent<SEED.Camera>()         … 他コンポーネント取得（T?。未アタッチは null）
                //       例) if (gameObject.GetComponent<SEED.InputMap>() is { } input) { input.GetAction("Jump"); }
                //   ctx.DeltaTime                                  … 前フレームからの経過秒
                //   SEED.Mathf.Lerp / SEED.Vector3 / SEED.Random / SEED.Debug.Log … 数学・乱数・ログ
                // 詳細は docs/scripting_api.md を参照。

                /// <summary>フレーム開始時に呼ばれる。入力取得や状態リセット向け。</summary>
                public override void BeginFrame(ref NativeFrameContext ctx)
                {
                    // ctx.DeltaTime : 前フレームからの経過秒
                    // ctx.AnimTime  : ゲーム内累計時間
                }

                /// <summary>Update より前の更新。他スクリプトへ渡す事前計算向け。</summary>
                public override void EarlyUpdate(ref NativeFrameContext ctx)
                {
                }

                /// <summary>毎フレーム呼ばれる主更新処理。ゲームロジックの中心。</summary>
                public override void Update(ref NativeFrameContext ctx)
                {
                }

                /// <summary>固定タイムステップの更新。物理など時間刻みを一定にしたい処理向け。</summary>
                public override void ConstantUpdate(ref NativeFrameContext ctx)
                {
                }

                /// <summary>Update 後の更新。追従カメラなど他更新の結果を使う処理向け。</summary>
                public override void LateUpdate(ref NativeFrameContext ctx)
                {
                }

                /// <summary>描画フェーズで呼ばれる。描画に関わる処理向け。</summary>
                public override void Render(ref NativeFrameContext ctx)
                {
                }

                /// <summary>フレーム終了時に呼ばれる。後片付けや状態確定向け。</summary>
                public override void EndFrame(ref NativeFrameContext ctx)
                {
                }
            }
            """;
        File.WriteAllText(path, template, Encoding.UTF8);

        ItemCreated?.Invoke(path);
        Close();
    }

    // ── Scene / Material / Shader / PostFX ────────────────────────

    /// <summary>
    /// 空のシーンファイル (.scene) を作成する。
    ///
    /// ランタイムの `SceneData`（runtime/src/engine/core/app_base/scene.rs）で
    /// `#[serde(default)]` が付いていない必須キーは `name` と `actors` の 2 つだけであり、
    /// `debug_camera` / `shading_asset` / `settings` はすべて省略可能。
    /// したがってこの 2 キーだけがロード可能な最小構成になる。
    /// </summary>
    private void OnCreateScene(object sender, RoutedEventArgs e)
    {
        const string BaseName = "NewScene";
        const string Ext      = ".scene";

        CreateFileAndClose(BaseName, Ext, stem => $$"""
            {
              "name": "{{stem}}",
              "actors": []
            }
            """);
    }

    /// <summary>
    /// 既定 PBR のマテリアルファイル (.mat) を作成する。
    /// 内容は Rust 側のマテリアルスキーマ（loader/model.rs）に合わせた最新形で、
    /// シェーディングモデル ID（`shading_model`）を含む。
    /// </summary>
    private void OnCreateMaterial(object sender, RoutedEventArgs e)
    {
        const string BaseName = "NewMaterial";
        const string Ext      = ".mat";

        CreateFileAndClose(BaseName, Ext, stem => $$"""
            {
              "name": "{{stem}}",
              "base_color": [1.0, 1.0, 1.0, 1.0],
              "metallic": 1.0,
              "roughness": 1.0,
              "emissive": [0.0, 0.0, 0.0],
              "alpha_mode": "opaque",
              "alpha_cutoff": 0.5,
              "cull_face": "back",
              "ior": 1.0,
              "transmission": 0.0,
              "diffuse_transmission": 0.0,
              "mr_tex_ignore": false,
              "shading_model": 0,
              "textures": { "albedo": "", "normal": "", "metallic_roughness": "", "occlusion": "", "emissive": "" }
            }
            """);
    }

    /// <summary>
    /// シェーディングアセット (.wgsl) の雛形を作成する。
    ///
    /// 契約の正典は docs/shading_asset.md と
    /// runtime/src/engine/core/renderer/shaders/shading_contract.wgsl。
    /// 雛形は `shade_default` がエンジン標準 PBR（`shade_model_0`）をそのまま返すだけの
    /// 「素通し」実装にしてあり、差した瞬間に見た目が変わらないところから書き始められる。
    /// </summary>
    private void OnCreateShader(object sender, RoutedEventArgs e)
    {
        const string BaseName = "NewShading";
        const string Ext      = ".wgsl";

        // 先頭行の `// @shading_contract 1` はローダが読む必須の宣言（省略すると警告付きで v1 扱い）。
        CreateFileAndClose(BaseName, Ext, _ => """
            // @shading_contract 1
            //
            // シェーディングアセット: ライト 1 灯ぶんの「光応答式（BRDF）」を差し替える WGSL。
            // 正典: docs/shading_asset.md / renderer/shaders/shading_contract.wgsl
            //
            // ── 書き方 ────────────────────────────────────────────────
            //  * 定義できる関数（すべて任意）
            //      fn shade_default(sf: ShadingSurface, li: LightSample) -> vec3<f32>
            //          … シェーディングモデル ID 0 の全表面（地形・草・未設定マテリアル）に効く。
            //            「作品全体の画作り」を決めるのはこれ。基本はこれだけ書けばよい。
            //      fn shade_model_1 / shade_model_2 / shade_model_3（同じシグネチャ）
            //          … マテリアルの shading_model を 1..3 にした面だけを追加で上書きする。
            //  * 返す値は「そのライト 1 灯ぶんの直接光の寄与（リニア HDR 放射輝度）」。
            //  * li.color には color × intensity × 距離減衰 × 円錐 × 影 が織り込み済み。
            //    アセット側で減衰・影を再計算してはならない（二重適用になる）。
            //  * アンビエント・エミッシブ・幾何ゲートはエンジンが外側で処理するので足さない。
            //  * バインディング（uniform / texture）は宣言できない。使えるのは
            //    ShadingSurface / LightSample と契約の標準ライブラリ（saturate 等）だけ。
            //  * Edit モードなら保存するだけでホットリロードされる。
            //
            // ── 使える主な入力 ───────────────────────────────────────
            //  sf.normal / sf.geo_normal / sf.vertex_normal … 各種法線（ワールド・正規化済み）
            //  sf.view_dir      … 表面→カメラ方向 V
            //  sf.base_color    … アルベド（リニア）
            //  sf.roughness / sf.metallic / sf.occlusion    … 物性
            //  sf.frag_coord    … ピクセル座標（ディザ・ハッチングの種）
            //  li.direction     … 表面→光源方向 L
            //  li.color         … 実効放射輝度（影・減衰込み）
            //  li.color_unshadowed / li.distance / li.kind  … 影抜き放射輝度・距離・ライト種別

            /// 既定のシェーディング。まずはエンジン標準 PBR をそのまま返す「素通し」実装。
            /// ここを書き換えると画面全体の見た目が変わる。
            ///
            /// 例）トゥーン調にする場合は、標準 PBR を捨てて自前の式に置き換える:
            ///   let ndl = saturate(dot(sf.normal, li.direction));
            ///   let step_ndl = select(0.3, 1.0, ndl > 0.5);
            ///   return sf.base_color * li.color * step_ndl;
            fn shade_default(sf: ShadingSurface, li: LightSample) -> vec3<f32> {
                return shade_model_0(sf, li);
            }
            """);
    }

    /// <summary>
    /// ポストエフェクトファイル (.postfx) を作成する。
    /// 内容は Rust 側の PostFxAsset スキーマ（every_frame / effects[blur, vignette, tint]）と一致させる。
    /// </summary>
    private void OnCreatePostFX(object sender, RoutedEventArgs e)
    {
        const string BaseName = "NewPostFX";
        const string Ext      = ".postfx";

        CreateFileAndClose(BaseName, Ext, _ => """
            {
              "every_frame": false,
              "effects": [
                { "type": "blur", "radius": 4 },
                { "type": "vignette", "strength": 0.3, "mask": "" },
                { "type": "tint", "color": [1.0, 0.95, 0.9, 1.0] }
              ]
            }
            """);
    }
}
