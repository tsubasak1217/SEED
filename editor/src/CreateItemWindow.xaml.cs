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
        // ランタイム（SEEDScripting.dll）の ScriptComponent を継承するテンプレート。
        // 必要なライフサイクルメソッドだけ override して使う。
        var template  = $$"""
            using System;
            using SEED.Scripting;

            /// <summary>{{className}} スクリプト。</summary>
            public class {{className}} : ScriptComponent
            {
                // インスペクタに公開するフィールドは [SerializeField] を付ける
                // [SerializeField(Label = "速度")]
                // private float speed = 1.0f;

                /// <summary>毎フレーム呼ばれる更新処理。</summary>
                public override void Update(ref NativeFrameContext ctx)
                {
                    // ctx.DeltaTime : 前フレームからの経過秒
                    // ctx.AnimTime  : ゲーム内累計時間
                }
            }
            """;
        File.WriteAllText(path, template, Encoding.UTF8);

        ItemCreated?.Invoke(path);
        Close();
    }
}
