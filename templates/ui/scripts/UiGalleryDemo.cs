// ============================================================
//  UiGalleryDemo.cs — UI 部品のギャラリー（templates/ui/scenes/ui_gallery.scene）の結線（W2-4・W2-5）
//
//  ギャラリーのルートに付ける。部品（SEED.UI.*）はそれぞれのスクリプトで動くので、ここは組み合わせだけを行う:
//    - スライダ ↔ 数値欄 を同期し、進捗の棒・輪へ値を映す（同じ値は知らせないので往復しない）
//    - 「全部を無効にする」ボタンで全部品の Interactable を切り替える（押下・無効の見た目を並べて見るため）
//    - 部品のイベントを画面の Log とログ（[UI] …）へ出す（IPC の検査がログで当たりを確かめる）
//    - 時刻ホイール（W2-5）: 値の変化（time）と止まった値（time-settled）、数のホイールの中央の値（wheel・wheel-settled）
//  部品のスクリプトは OnStart の順が決まっていないので、登録簿（UiRegistry.Version）が変わるたびにつなぎ直す。
//  デバッグの命令（SCRIPT_DEBUG:ui,<名前>）: disable（全部品を無効）・enable（戻す）・theme,<assets:// のテーマ>・
//  mark <文字>（ログへ区切りを出す。検査が場面ごとにログを分けるため）・
//  time,<時刻ホイール>,<HH:mm>[,jump]（値の設定。既定は動きあり）・time24,<時刻ホイール>,<on|off>（24 時間表記）・
//  timestep,<時刻ホイール>,<分>（分の刻み）・wheel,<ホイール>,<値>[,jump]・focus,<ホイール>[,<Hour|Minute|Meridiem>]（キーボードの相手）・
//  stats（部品が見た目を作り直した回数 UiWidget.RefreshCount。ホイールを回している間に他の部品が作り直されないことを見る）。
// ============================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using SEED;
using SEED.UI;
using SEEDEditor.Scripting;

public class UiGalleryDemo : SEEDScript
{
    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[UI]";
    /// <summary>進捗の輪・棒へ映すとき、スライダの値を割る数（スライダの最大）。</summary>
    private const float SliderMax = 60f;

    /// <summary>ボタンの名前（押されたらログ）。</summary>
    private static readonly string[] ButtonNames =
        { "BtnFilled", "BtnTonal", "BtnOutlined", "BtnText", "BtnDisabled", "RoundButton", "RoundClip/HitPad" };
    /// <summary>トグルの名前。</summary>
    private static readonly string[] ToggleNames = { "ToggleOff", "ToggleOn", "ToggleDisabled" };
    /// <summary>チェックボックスの名前。</summary>
    private static readonly string[] CheckNames = { "CheckOff", "CheckOn", "CheckDisabled" };
    /// <summary>選択のグループの名前。</summary>
    private static readonly string[] GroupNames = { "Segmented", "Chips", "Radios" };
    /// <summary>時刻ホイールの名前（W2-5）。</summary>
    private static readonly string[] TimeWheelNames = { "Time24", "Time12" };
    /// <summary>数のホイールの名前（W2-5）。</summary>
    private static readonly string[] WheelNames = { "Snooze" };
    /// <summary>時刻の書式（ログ・命令）。</summary>
    private const string TimeFormat = "HH:mm";
    /// <summary>値の設定の命令で「すぐ移す」を表す語。</summary>
    private const string JumpWord = "jump";

    /// <summary>つないだ部品（二重につながない）。</summary>
    private readonly HashSet<UiWidget> _bound = new();
    /// <summary>最後につなぎ直した登録簿の版。</summary>
    private int _registryVersion = -1;
    /// <summary>全部品を無効にしているか。</summary>
    private bool _allDisabled;
    /// <summary>デバッグの命令の受け口。</summary>
    private System.Action<string>? _cmd;

    public override void OnStart()
    {
        _cmd = OnCommand;
        SEED.Debug.OnCommand("ui", _cmd);
    }

    public override void OnDestroy()
    {
        if (_cmd is not null) SEED.Debug.OffCommand("ui", _cmd);
    }

    public override void Update(ref NativeFrameContext ctx)
    {
        if (_registryVersion == UiRegistry.Version) return;
        _registryVersion = UiRegistry.Version;
        BindAll();
    }

    /// <summary>見つかった部品をつなぐ（まだつないでいないものだけ）。</summary>
    private void BindAll()
    {
        foreach (var name in ButtonNames)
            if (Find<Button>(name) is { } b && _bound.Add(b))
            {
                var label = name;
                b.Clicked += _ => Report($"click {label}");
                b.LongPressed += _ => Report($"long {label}");
            }
        if (Find<Button>("BtnToggleAll") is { } all && _bound.Add(all))
            all.Clicked += _ => SetAllDisabled(!_allDisabled);
        foreach (var name in ToggleNames)
            if (Find<Toggle>(name) is { } t && _bound.Add(t))
            {
                var label = name;
                t.Changed += (_, on) => Report($"toggle {label} {(on ? "on" : "off")}");
            }
        foreach (var name in CheckNames)
            if (Find<Checkbox>(name) is { } c && _bound.Add(c))
            {
                var label = name;
                c.Changed += (_, on) => Report($"check {label} {(on ? "on" : "off")}");
            }
        foreach (var name in GroupNames)
            if (Find<SelectionGroup>(name) is { } g && _bound.Add(g))
            {
                var label = name;
                g.SelectionChanged += grp => Report($"select {label} [{string.Join(",", grp.SelectedIndices)}]");
            }
        foreach (var name in TimeWheelNames)
            if (Find<TimeWheel>(name) is { } tw && _bound.Add(tw))
            {
                var label = name;
                tw.ValueChanged += (_, t) => Report($"time {label} {Hm(t)}");
                tw.ValueSettled += (_, t) => Report($"time-settled {label} {Hm(t)}");
            }
        foreach (var name in WheelNames)
            if (Find<WheelPicker>(name) is { } wp && _bound.Add(wp))
            {
                var label = name;
                wp.SelectionChanged += (w, _) => Report($"wheel {label} {w.SelectedValue}");
                wp.Settled += (w, _) => Report($"wheel-settled {label} {w.SelectedValue}");
            }
        var slider = Find<Slider>("Slider");
        var field = Find<NumberField>("NumberField");
        if (slider is not null && field is not null && _bound.Add(slider) && _bound.Add(field))
        {
            slider.ValueChanged += (_, v) => { field.SetValue(v); ShowProgress(v); Report($"slider {v}"); };
            field.ValueChanged += (_, v) => { slider.SetValue(v); ShowProgress(v); Report($"number {v}"); };
        }
    }

    /// <summary>スライダの値を進捗の棒・輪へ映す。</summary>
    private void ShowProgress(float value)
    {
        float t = value / SliderMax;
        Find<ProgressBar>("ProgressBar")?.SetValue(t);
        Find<ProgressRing>("ProgressRing")?.SetValue(t);
        if (GameObject.Find("ProgressRing").FindChild("Label").GetComponent<Text>() is { } label)
            label.Content = $"{(int)(t * 100)}%";
    }

    /// <summary>全部品を無効にする・戻す（切り替えのボタン自身は除く）。</summary>
    private void SetAllDisabled(bool disabled)
    {
        _allDisabled = disabled;
        foreach (var w in _bound)
        {
            if (w is Button b && b.Owner.Name == "BtnToggleAll") continue;
            w.SetInteractable(!disabled);
        }
        foreach (var name in new[] { "Slider", "NumberField", "ProgressBar", "ProgressRing" })
            Find<UiWidget>(name)?.SetInteractable(!disabled);
        if (Find<Button>("BtnToggleAll") is { } all) all.SetText(disabled ? "全部を戻す" : "全部を無効にする");
        Report(disabled ? "all disabled" : "all enabled");
    }

    /// <summary>デバッグの命令（SCRIPT_DEBUG:ui,…）。</summary>
    private void OnCommand(string arg)
    {
        var p = arg.Split(',');
        switch (p[0])
        {
            case "disable": SetAllDisabled(true); break;
            case "enable": SetAllDisabled(false); break;
            case var m when m.StartsWith("mark", System.StringComparison.Ordinal):
                SEED.Debug.Log($"{LogPrefix} {arg}");
                break;
            case "theme" when p.Length > 1:
                Report(UiTheme.LoadAsset(p[1]) ? $"theme {p[1]}" : $"theme failed {p[1]}");
                break;
            // ── 時刻ホイール（W2-5）──
            case "time" when p.Length > 2 && Find<TimeWheel>(p[1]) is { } tw
                                          && TimeOnly.TryParseExact(p[2], TimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t):
                tw.SetValue(t, animate: !(p.Length > 3 && p[3] == JumpWord));
                break;
            case "time24" when p.Length > 2 && Find<TimeWheel>(p[1]) is { } tw24:
                tw24.SetUse24Hour(p[2] == "on");
                Report($"time24 {p[1]} {p[2]}");
                break;
            case "timestep" when p.Length > 2 && Find<TimeWheel>(p[1]) is { } tws && int.TryParse(p[2], out var step):
                tws.SetMinuteStep(step);
                Report($"timestep {p[1]} {tws.MinuteStep}");
                break;
            case "wheel" when p.Length > 2 && Find<WheelPicker>(p[1]) is { } wp && int.TryParse(p[2], out var value):
                wp.SetValue(value, animate: !(p.Length > 3 && p[3] == JumpWord));
                break;
            case "focus" when p.Length > 1:
                FocusWheel(p[1], p.Length > 2 ? p[2] : "");
                break;
            case "stats":
                Report($"stats refresh={UiWidget.RefreshCount}");
                break;
            // 検査用: ホイールの窓の位置を出す・同じ項目のまま周ごと飛ばす（止まったときに真ん中の周へ戻ることを見る）
            case "wheelpos" when p.Length > 1:
                ReportWheelPosition(p[1], p.Length > 2 ? p[2] : "");
                break;
            case "wheelshift" when p.Length > 3 && int.TryParse(p[3], out var cycles):
                ShiftWheel(p[1], p[2], cycles);
                break;
        }
    }

    /// <summary>名前（時刻ホイールなら列の名前も）のホイール。</summary>
    private WheelPicker? WheelOf(string name, string column)
    {
        if (Find<TimeWheel>(name) is { } tw)
            return column switch
            {
                "Minute" => tw.MinuteColumn,
                "Meridiem" => tw.MeridiemColumn,
                _ => tw.HourColumn,
            };
        return Find<WheelPicker>(name);
    }

    /// <summary>ホイールの窓のスクロール。</summary>
    private static CanvasScroll? ScrollOf(WheelPicker wheel) => wheel.Owner.FindChild("Viewport").GetComponent<CanvasScroll>();

    /// <summary>ホイールの窓の位置と中央の項目をログへ出す。</summary>
    private void ReportWheelPosition(string name, string column)
    {
        if (WheelOf(name, column) is not { } wheel || ScrollOf(wheel) is not { } scroll) return;
        float y = scroll.Position.y;
        Report($"wheelpos {name} {column} pos={y:0.##} row={y / wheel.ResolvedItemExtent:0.##} item={wheel.SelectedIndex}");
    }

    /// <summary>ホイールの窓を同じ項目のまま周の数だけ飛ばす。</summary>
    private void ShiftWheel(string name, string column, int cycles)
    {
        if (WheelOf(name, column) is not { } wheel || ScrollOf(wheel) is not { } scroll) return;
        var pos = scroll.Position;
        scroll.JumpTo(new Vector2(pos.x, pos.y + cycles * wheel.Count * wheel.ResolvedItemExtent));
        Report($"wheelshift {name} {column} {cycles}");
    }

    /// <summary>キーボードの相手のホイールを決める（時刻ホイールなら列の名前で）。</summary>
    private void FocusWheel(string name, string column)
    {
        var target = WheelOf(name, column);
        target?.Focus();
        Report(target is null ? $"focus failed {name}" : $"focus {name} {column}");
    }

    /// <summary>時刻の文字（HH:mm）。</summary>
    private static string Hm(TimeOnly t) => t.ToString(TimeFormat, CultureInfo.InvariantCulture);

    /// <summary>名前（パス）の部品を引く（ギャラリーの直下から）。</summary>
    private T? Find<T>(string path) where T : UiWidget => UiWidget.Of<T>(gameObject.FindChild(path));

    /// <summary>知らせをログと画面へ出す。</summary>
    private void Report(string message)
    {
        SEED.Debug.Log($"{LogPrefix} {message}");
        if (gameObject.FindChild("Log").GetComponent<Text>() is { } log) log.Content = message;
    }
}
