// ============================================================
//  UiTextInputDemo.cs — 文字入力の見本（W2-6b。templates/ui/scenes/ui_text_input.scene。docs/ui_text_input.md §11）
//
//  シーンの根に付ける。ページ（縦のスクロール）の枠へ部品のプレハブを作ってつなぐ:
//    - 文字の欄（text_field.actor）: 例の文「例：田中太郎」・最大 20 文字。打つたびに下の行へ本文を写す
//    - 数値の欄（number_input.actor・幅 112・数字は中央）＋スライダ（slider.actor。2026-10-03 から 4 刻みの点）: 起床猶予 1〜5 分。欄の文字が数字として読めれば
//      範囲へ収めてスライダへ（欄は打っている間は書き換えない）、フォーカスが外れた・完了したら欄を収めた値の文字へ直す。
//      スライダを動かしたら欄にフォーカスが無いときだけ欄を書き換える（Wake or Pay の数値のサブ画面の決まり）
//    - 計算の起床確認: 数字の欄＋「答える」（完了でも答える）。12 + 7 = 19 で正解
//    - 文字入力の起床確認: 手本の文と入力欄（貼り付け・コピー禁止）。打つたびに全文一致を見て、一致したら解除（フォーカスを外す）
//    - ダイアログの中の 1 行の入力: 「名前を変える」で Dialog（DialogOptions.Input）を開き、変更なら名前を置き換える（前後の空白を落とす）
//    - スクロールの下の欄: キーボードを避ける確かめ用（ページの末尾の余白とスクロールの送り）
//  ダイアログの面（modal_host.actor）も作る。
//  デバッグの命令（SCRIPT_DEBUG:textinput,<命令>）: state（全部の欄の本文・フォーカス・変換中をログへ）・focus,<name|grace|calc|typing|bottom>・
//  unfocus・dialog（名前の変更のダイアログを開く）・scroll,<位置>（ページを位置へすぐ移す）・slider,<値>（スライダを動かす）・
//  error,<欄>,<0|1>（エラーの見た目）。
// ============================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using SEED;
using SEED.UI;
using SEEDEditor.Scripting;

public class UiTextInputDemo : SEEDScript
{
    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[UI] textinput:";
    /// <summary>デバッグの命令の名前。</summary>
    private const string CommandName = "textinput";

    // ── プレハブ ──
    private const string TextFieldPrefab = "assets://ui/prefabs/text_field.actor";
    private const string NumberInputPrefab = "assets://ui/prefabs/number_input.actor";
    private const string SliderPrefab = "assets://ui/prefabs/slider.actor";
    private const string ButtonPrefab = "assets://ui/prefabs/button.actor";
    private const string ModalHostPrefab = "assets://ui/prefabs/modal_host.actor";

    // ── シーンの枠の名前 ──
    private const string PageName = "Page";
    private const string NameSlotName = "NameSlot";
    private const string NameEchoName = "NameEcho";
    private const string GraceSlotName = "GraceSlot";
    private const string GraceSliderSlotName = "GraceSliderSlot";
    private const string CalcRowName = "CalcRow";
    private const string CalcResultName = "CalcResult";
    private const string TypingSampleName = "TypingSample";
    private const string TypingSlotName = "TypingSlot";
    private const string TypingResultName = "TypingResult";
    private const string DialogRowName = "DialogRow";
    private const string DialogResultName = "DialogResult";
    private const string BottomSlotName = "BottomSlot";

    // ── 見本の値 ──
    /// <summary>名前の欄の最大の長さ（書記素）。</summary>
    private const int NameMaxLength = 20;
    /// <summary>名前の例の文。</summary>
    private const string NamePlaceholder = "例：田中太郎";
    /// <summary>起床猶予の範囲（分）と初めの値。</summary>
    private const int GraceMin = 1, GraceMax = 5, GraceInitial = 1;
    /// <summary>計算の問題と答え。</summary>
    private const int CalcAnswer = 19;
    /// <summary>計算の欄の最大の桁。</summary>
    private const int CalcMaxLength = 3;
    /// <summary>初めの名前。</summary>
    private const string InitialName = "つばさ";

    /// <summary>作った部品のノード（名前 → ノード）。</summary>
    private readonly Dictionary<string, GameObject> _nodes = new();
    /// <summary>つないだ部品。</summary>
    private TextField? _name, _grace, _calc, _typing, _bottom;
    private Slider? _slider;
    private Button? _answer, _rename;
    /// <summary>最後に見た登録簿の版（部品の OnStart の後につなぐ）。</summary>
    private int _registryVersion = -1;
    /// <summary>今の名前。</summary>
    private string _currentName = InitialName;
    /// <summary>デバッグの命令の受け口。</summary>
    private Action<string>? _cmd;

    public override void OnStart()
    {
        _cmd = OnCommand;
        SEED.Debug.OnCommand(CommandName, _cmd);
        Make("name", TextFieldPrefab, NameSlotName);
        Make("grace", NumberInputPrefab, GraceSlotName);
        Make("slider", SliderPrefab, GraceSliderSlotName);
        Make("calc", NumberInputPrefab, CalcRowName);
        Make("answer", ButtonPrefab, CalcRowName);
        Make("typing", TextFieldPrefab, TypingSlotName);
        Make("rename", ButtonPrefab, DialogRowName);
        Make("bottom", TextFieldPrefab, BottomSlotName);
        // ダイアログの面（いちばん手前に並ぶよう最後に作る）
        GameObject.Instantiate(ModalHostPrefab, gameObject);
        Report("started");
    }

    public override void OnDestroy()
    {
        if (_cmd is not null) SEED.Debug.OffCommand(CommandName, _cmd);
    }

    public override void Update(ref NativeFrameContext ctx)
    {
        if (_registryVersion == UiRegistry.Version) return;
        _registryVersion = UiRegistry.Version;
        Bind();
    }

    /// <summary>プレハブを枠の下に作る。</summary>
    private void Make(string key, string prefab, string slotName)
    {
        var slot = gameObject.FindChild(slotName);
        if (!slot.IsValid)
        {
            Report($"枠が見つかりません: {slotName}");
            return;
        }
        var node = GameObject.Instantiate(prefab, slot);
        if (node.IsValid) _nodes[key] = node;
    }

    /// <summary>作った部品のスクリプト（まだ OnStart の前なら null）。</summary>
    private T? Widget<T>(string key) where T : UiWidget
        => _nodes.TryGetValue(key, out var node) ? UiWidget.Of<T>(node) : null;

    /// <summary>部品をつなぐ（部品の OnStart の後。1 回ずつ）。</summary>
    private void Bind()
    {
        if (_name is null && Widget<TextField>("name") is { } name)
        {
            _name = name;
            name.Placeholder = NamePlaceholder;
            name.MaxLength = NameMaxLength;
            name.TextChanged += (_, text) => SetLabel(NameEchoName, $"入力中: {text}（{text.Length} 文字）");
            name.Submitted += (_, action) => Report($"name submitted {action} text={name.Text}");
            name.FocusChanged += (_, focused) => Report($"name focus={focused}");
            name.SetText("");
        }
        if (_grace is null && Widget<TextField>("grace") is { } grace)
        {
            _grace = grace;
            grace.MaxLength = CalcMaxLength;
            grace.SetText(NumberText.Format(GraceInitial));
            grace.TextChanged += (_, text) => OnGraceText(text);
            grace.FocusChanged += (_, focused) => { if (!focused) NormalizeGrace(); };
            grace.Submitted += (_, _) => NormalizeGrace();
        }
        if (_slider is null && Widget<Slider>("slider") is { } slider)
        {
            _slider = slider;
            slider.Min = GraceMin;
            slider.Max = GraceMax;
            slider.Step = 1f;
            // 刻みの点（2026-10-03。Flutter の divisions。1〜5 分なら 4 刻み＝5 つの点。値の段階 Step とは別に指定する）
            slider.SetTickCount(GraceMax - GraceMin);
            slider.SetValue(GraceInitial, notify: false);
            slider.ValueChanged += (_, value) =>
            {
                bool written = _grace?.SetTextUnlessFocused(NumberText.Format((long)MathF.Round(value))) ?? false;
                Report($"slider {value} → grace {(written ? "書き換えた" : "フォーカス中なので書き換えない")}");
            };
        }
        if (_calc is null && Widget<TextField>("calc") is { } calc)
        {
            _calc = calc;
            calc.MaxLength = CalcMaxLength;
            calc.Placeholder = "?";
            // まちがえたら打ち直せるよう、完了ではフォーカスを外さない（正解のときだけ外す）
            calc.UnfocusOnDone = false;
            calc.Submitted += (_, action) => { if (action == TextInputAction.Done) Answer(); };
        }
        if (_answer is null && Widget<Button>("answer") is { } answer)
        {
            _answer = answer;
            answer.SetText("答える");
            answer.Clicked += _ => Answer();
        }
        if (_typing is null && Widget<TextField>("typing") is { } typing)
        {
            _typing = typing;
            typing.AllowPaste = false;
            typing.AllowCopy = false;
            typing.Placeholder = "上の文を入力";
            typing.TextChanged += (_, text) => CheckTyping(text);
            typing.PasteBlocked += _ => SetLabel(TypingResultName, "貼り付けはできません");
        }
        if (_rename is null && Widget<Button>("rename") is { } rename)
        {
            _rename = rename;
            rename.SetText("名前を変える");
            rename.Clicked += _ => OpenRenameDialog();
        }
        if (_bottom is null && Widget<TextField>("bottom") is { } bottom)
        {
            _bottom = bottom;
            bottom.Placeholder = "キーボードの上に見えるか";
        }
    }

    // ── 見本の決まり ──

    /// <summary>数値の欄の文字 → スライダ（読めて範囲へ収めた値。欄の文字は変えない）。</summary>
    private void OnGraceText(string text)
    {
        if (!NumberText.TryParseClamped(text, GraceMin, GraceMax, out long value)) return;
        _slider?.SetValue(value, notify: false);
    }

    /// <summary>数値の欄を収めた値の文字へ直す（フォーカスが外れた・完了）。読めなければスライダの値へ戻す。</summary>
    private void NormalizeGrace()
    {
        if (_grace is null) return;
        long fallback = (long)MathF.Round(_slider?.Value ?? GraceInitial);
        long value = NumberText.TryParseClamped(_grace.Text, GraceMin, GraceMax, out long parsed) ? parsed : fallback;
        _grace.SetText(NumberText.Format(value));
        _slider?.SetValue(value, notify: false);
        Report($"grace normalized {value}");
    }

    /// <summary>計算に答える。</summary>
    private void Answer()
    {
        if (_calc is null) return;
        bool ok = NumberText.TryParseClamped(_calc.Text, 0, int.MaxValue, out long value) && value == CalcAnswer;
        _calc.SetError(!ok);
        SetLabel(CalcResultName, ok ? "正解（解除）" : $"ちがいます（{_calc.Text}）");
        if (ok) _calc.Unfocus();
        Report($"calc answer text={_calc.Text} ok={ok}");
    }

    /// <summary>文字入力の起床確認: 全文一致で解除。</summary>
    private void CheckTyping(string text)
    {
        string sample = gameObject.FindChild(TypingSampleName).GetComponent<Text>()?.Content ?? string.Empty;
        bool match = text == sample;
        SetLabel(TypingResultName, match ? "一致しました（解除）" : $"{text.Length} / {sample.Length} 文字");
        if (match) _typing?.Unfocus();
        Report($"typing text={text} match={match}");
    }

    /// <summary>名前の変更のダイアログを開く。</summary>
    private void OpenRenameDialog()
    {
        var handle = Dialog.Show(new DialogOptions
        {
            Title = "名前の変更",
            PositiveText = "変更",
            NegativeText = "やめる",
            Input = new DialogInputOptions { Text = _currentName, Placeholder = NamePlaceholder, MaxLength = NameMaxLength },
        });
        if (handle is null) return;
        handle.Completed += result =>
        {
            if (result == DialogResult.Positive && handle.InputText is { Length: > 0 } name) _currentName = name;
            SetLabel(DialogResultName, $"名前: {_currentName}");
            Report($"rename result={result} input=\"{handle.InputText}\" name={_currentName}");
        };
    }

    // ── 小道具 ──

    /// <summary>名前のノードの Text へ文字を書く（記法は逃がす）。</summary>
    private void SetLabel(string nodeName, string text)
    {
        if (gameObject.FindChild(nodeName).GetComponent<Text>() is { } label) label.Content = TextMarkupEscape.Escape(text);
        Redraw.Request();
    }

    /// <summary>ログへ出す。</summary>
    private static void Report(string message) => SEED.Debug.Log($"{LogPrefix} {message}");

    /// <summary>名前 → 入力欄。</summary>
    private TextField? Field(string key) => key switch
    {
        "name" => _name,
        "grace" => _grace,
        "calc" => _calc,
        "typing" => _typing,
        "bottom" => _bottom,
        _ => null,
    };

    /// <summary>デバッグの命令。</summary>
    private void OnCommand(string arg)
    {
        var p = arg.Split(',');
        string At(int i) => p.Length > i ? p[i].Trim() : "";
        switch (At(0))
        {
            case "state":
                foreach (var key in new[] { "name", "grace", "calc", "typing", "bottom" })
                {
                    if (Field(key) is not { } f) continue;
                    var s = f.State;
                    Report($"state {key} text=\"{f.Text}\" focused={f.IsFocused} composing={f.IsComposing} error={f.HasError} " +
                           $"sel={s.SelectionStart}..{s.SelectionEnd} comp={s.CompositionStart}..{s.CompositionEnd}");
                }
                Report($"state slider={_slider?.Value} keyboard={TextInput.KeyboardHeight} active={TextInput.ActiveSession} " +
                       $"focus={UiFocus.Current?.GetType().Name ?? "none"} name={_currentName}");
                if (gameObject.FindChild(PageName).GetComponent<CanvasScroll>() is { } page)
                    Report($"state page pos={page.Position.y:0.0} max={page.MaxPosition.y:0.0} inset={page.EndInset.y:0.0}");
                break;
            case "rects":
                // 確かめ用: 部品の画面の矩形（画面の画素。IPC の注入でタップする位置を決める）
                foreach (var (key, node) in _nodes)
                {
                    if (node.GetComponent<CanvasTransform>() is not { } t || !t.HasLayout) continue;
                    var r = t.LayoutRect;
                    Report($"rect {key} {r.x:0.0},{r.y:0.0},{r.width:0.0},{r.height:0.0}");
                }
                break;
            case "focus":
                Field(At(1))?.Focus();
                Report($"focus {At(1)}");
                break;
            case "unfocus":
                foreach (var key in new[] { "name", "grace", "calc", "typing", "bottom" }) Field(key)?.Unfocus();
                Report("unfocus");
                break;
            case "dialog":
                OpenRenameDialog();
                break;
            case "scroll" when float.TryParse(At(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var y):
                gameObject.FindChild(PageName).GetComponent<CanvasScroll>()?.JumpTo(new Vector2(0f, y));
                Redraw.Request();
                Report($"scroll {y}");
                break;
            case "slider" when float.TryParse(At(1), NumberStyles.Float, CultureInfo.InvariantCulture, out var v):
                _slider?.SetValue(v);
                break;
            case "error" when Field(At(1)) is { } target:
                target.SetError(At(2) == "1");
                break;
            default:
                Report($"知らない命令です: {arg}");
                break;
        }
    }
}
