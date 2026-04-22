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

    private void OnCreateActor(object sender, RoutedEventArgs e)
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
        var json = $"{{\n  \"name\": \"{actorName}\",\n  \"components\": [],\n  \"children\": []\n}}";
        File.WriteAllText(path, json, Encoding.UTF8);

        ItemCreated?.Invoke(path);
        Close();
    }
}
