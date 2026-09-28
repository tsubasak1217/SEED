// ============================================================
//  UiGalleryThemeBar.cs — ギャラリーの上のテーマの帯（templates/ui/scenes/ui_gallery.scene の ThemeBar。W2-9。docs/ui_theme.md §9）
//
//  帯のノードに付ける。見本のテーマ（templates/ui/themes/ → プロジェクトの assets/ui/themes/）を切り替え、明暗の選び方を変える。
//  テーマのパスは assets:// の完全なパスで書く（パッケージの収録〈docs/packaging.md §2〉がスクリプトの文字列から拾い、APK に入る）:
//    - ThemeDefault / ThemeForest / ThemeSunrise / ThemeRound … 組み込みの既定・森（暗）・朝焼け（明）・森・丸（森を基にした一部だけの上書き）
//    - BrightnessMode（セグメント）… テーマのまま・端末に従う・明るい方を強制・暗い方を強制（UiTheme.SetBrightnessMode）
//    - AnimateToggle … オンなら色を補間しながら切り替える（テーマの motion.theme 秒）
//  切り替えは UiTheme が表示中の全部品（と ThemeStyle を付けた飾り）へその場で当て直す。知らせ（UiTheme.Changed）をログへ出す。
//  始めに StartTheme（空 = 組み込みの既定のテーマ）と選び方「テーマのまま」を当てる（アプリの起動のスクリプトと同じ形）。
//  デバッグの命令（SCRIPT_DEBUG:theme,<名前>）: default・forest・sunrise・round・<assets:// の .json>（切り替え）・
//  mode,<theme|system|light|dark>・sysmode,<dark|light|unknown|system>（PC の模擬の端末の明暗を差し替える＝OS の設定を変えずに
//  「端末に従う」を試す）・animate,<on|off>・info（今のテーマ・明暗・版・いくつかのトークンの値・部品の数をログへ）。
// ============================================================
using System;
using System.Collections.Generic;
using SEED;
using SEED.Platform;
using SEED.UI;
using SEEDEditor.Scripting;

public class UiGalleryThemeBar : SEEDScript
{
    /// <summary>
    /// 始めに当てるテーマ（assets:// のパス。空 = 組み込みの既定のテーマ）。アプリの起動のスクリプトと同じく OnStart で当てる
    /// （UiTheme は SEEDScripting の静的な状態なので、エディタに埋め込んだ Play を止めて始め直すと前の Play のテーマが残っているため）。
    /// </summary>
    [SerializeField(Label = "始めのテーマ")]
    public string StartTheme = "";

    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[UI] theme:";
    /// <summary>デバッグの命令の名前。</summary>
    private const string CommandName = "theme";
    /// <summary>組み込みの既定のテーマを表す名前（ファイルなし）。</summary>
    private const string BuiltInName = "default";
    /// <summary>テーマのファイルの拡張子。</summary>
    private const string JsonExtension = ".json";

    /// <summary>ボタンの名前 → デバッグの命令の名前・テーマのファイル（assets:// の完全なパス。空 = 組み込み）。</summary>
    private static readonly (string Button, string Name, string File)[] Themes =
    {
        ("ThemeDefault", BuiltInName, ""),
        ("ThemeForest", "forest", "assets://ui/themes/forest.json"),
        ("ThemeSunrise", "sunrise", "assets://ui/themes/sunrise.json"),
        ("ThemeRound", "round", "assets://ui/themes/forest_round.json"),
    };

    /// <summary>セグメントの項目の順の明暗の選び方。</summary>
    private static readonly UiBrightnessMode[] Modes =
        { UiBrightnessMode.Theme, UiBrightnessMode.System, UiBrightnessMode.Light, UiBrightnessMode.Dark };

    /// <summary>info で値を出すトークン（色・角丸・書体の太さ）。</summary>
    private static readonly string[] InfoTokens =
        { UiTokens.ColorPrimary, UiTokens.ColorSurface, UiTokens.ColorOnSurface, UiTokens.RadiusButton, UiTokens.FontWeight, UiTokens.TextLabel };

    /// <summary>つないだ部品（二重につながない）。</summary>
    private readonly HashSet<UiWidget> _bound = new();
    /// <summary>最後につなぎ直した登録簿の版。</summary>
    private int _registryVersion = -1;
    /// <summary>色を補間しながら切り替えるか。</summary>
    private bool _animate;
    /// <summary>デバッグの命令の受け口。</summary>
    private Action<string>? _cmd;
    /// <summary>テーマの切り替えの受け口。</summary>
    private Action<UiThemeChange>? _changed;

    public override void OnStart()
    {
        _cmd = OnCommand;
        SEED.Debug.OnCommand(CommandName, _cmd);
        _changed = OnThemeChanged;
        UiTheme.Changed += _changed;
        // 始めの状態（テーマのまま・始めのテーマ）へ揃える。帯のセグメント（テーマ）・トグル（オフ）の初めの見た目と合わせる
        UiTheme.SetBrightnessMode(UiBrightnessMode.Theme);
        ApplyTheme("start", StartTheme);
    }

    public override void OnDestroy()
    {
        if (_cmd is not null) SEED.Debug.OffCommand(CommandName, _cmd);
        if (_changed is not null) UiTheme.Changed -= _changed;
    }

    public override void Update(ref NativeFrameContext ctx)
    {
        if (_registryVersion == UiRegistry.Version) return;
        _registryVersion = UiRegistry.Version;
        BindAll();
    }

    /// <summary>帯の部品をつなぐ（部品の OnStart の順は決まっていないので、登録簿が変わるたびに見つかったものだけ）。</summary>
    private void BindAll()
    {
        foreach (var (button, name, file) in Themes)
        {
            if (UiWidget.Of<Button>(gameObject.FindChild(button)) is { } b && _bound.Add(b))
            {
                string themeName = name, themeFile = file;
                b.Clicked += _ => ApplyTheme(themeName, themeFile);
            }
        }
        if (UiWidget.Of<SegmentedControl>(gameObject.FindChild("BrightnessMode")) is { } seg && _bound.Add(seg))
            seg.SelectionChanged += g => { if (g.SelectedIndices.Count > 0) SetMode(Modes[g.SelectedIndices[0]]); };
        if (UiWidget.Of<Toggle>(gameObject.FindChild("AnimateToggle")) is { } t && _bound.Add(t))
            t.Changed += (_, on) => { _animate = on; Report($"animate {(on ? "on" : "off")}"); };
    }

    /// <summary>テーマを当てる（ファイルが空なら組み込みの既定のテーマ）。</summary>
    private void ApplyTheme(string name, string file)
    {
        var theme = file.Length == 0 ? UiTheme.BuiltIn : UiTheme.Load(file);
        if (theme is null)
        {
            Report($"failed {name}");
            return;
        }
        UiTheme.Apply(theme, _animate);
    }

    /// <summary>明暗の選び方を変える。</summary>
    private void SetMode(UiBrightnessMode mode)
    {
        UiTheme.SetBrightnessMode(mode, _animate);
        Report($"mode {mode} → {UiTheme.Brightness}（端末 {(UiTheme.SystemBrightness?.ToString() ?? "不明")}）");
    }

    /// <summary>テーマが切り替わった（UiTheme.Changed）。</summary>
    private void OnThemeChanged(UiThemeChange change)
        => Report($"changed {change.Theme.Name} {change.Brightness} animated={change.Animated} version={UiTheme.Version} widgets={UiRegistry.Count}");

    /// <summary>デバッグの命令（SCRIPT_DEBUG:theme,…）。</summary>
    private void OnCommand(string arg)
    {
        var p = arg.Split(',');
        string At(int i) => p.Length > i ? p[i].Trim() : "";
        switch (At(0))
        {
            case "mode":
                if (Array.FindIndex(Modes, m => m.ToString().Equals(At(1), StringComparison.OrdinalIgnoreCase)) is int mi and >= 0)
                    SetMode(Modes[mi]);
                break;
            case "sysmode":
                SystemUiMode? sys = At(1) switch
                {
                    "dark" => SystemUiMode.Dark,
                    "light" => SystemUiMode.Light,
                    "unknown" => SystemUiMode.Unknown,
                    _ => null,
                };
                Report($"sysmode {At(1)} accepted={PlatformDiagnostics.SimulateUiMode(sys)} ui_mode={App.UiMode}");
                break;
            case "animate":
                _animate = At(1) == "on";
                Report($"animate {(_animate ? "on" : "off")}");
                break;
            case "info":
                LogInfo();
                break;
            case var name when name.EndsWith(JsonExtension, StringComparison.Ordinal):
                ApplyTheme(name, name);
                break;
            case var name:
                foreach (var (_, themeName, file) in Themes)
                    if (themeName == name) ApplyTheme(themeName, file);
                break;
        }
    }

    /// <summary>今のテーマの様子をログへ（検査が場面ごとに読む）。</summary>
    private void LogInfo()
    {
        var t = UiTheme.Current;
        var parts = new List<string>();
        foreach (var token in InfoTokens) parts.Add(t.Describe(token));
        Report($"info {UiTheme.Definition.Name} brightness={UiTheme.Brightness} mode={UiTheme.BrightnessMode} system={(UiTheme.SystemBrightness?.ToString() ?? "-")} "
               + $"version={UiTheme.Version} transitioning={UiTheme.IsTransitioning} widgets={UiRegistry.Count} refresh={UiWidget.RefreshCount} "
               + $"chain={string.Join(">", UiTheme.Definition.ChainOrigins)} | {string.Join(" ; ", parts)}");
    }

    /// <summary>知らせをログと画面の Log へ出す。</summary>
    private static void Report(string message)
    {
        SEED.Debug.Log($"{LogPrefix} {message}");
        if (GameObject.Find("Log").GetComponent<Text>() is { } log) log.Content = message;
    }
}
