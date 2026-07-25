using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;

namespace SEEDEditor;

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
}
