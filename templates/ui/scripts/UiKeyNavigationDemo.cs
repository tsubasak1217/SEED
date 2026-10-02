// ============================================================
//  UiKeyNavigationDemo.cs — 方向キー・パッドで UI 部品のフォーカスを移して押す見本（docs/ui_navigation.md §7.2）
//
//  画面いっぱいの Canvas（シーンの根など）に付けると、子に次を作る:
//    - ボタン 6 個（3 列 × 2 行の格子。templates/ui/prefabs/button.actor）。押すと [UI] keynav-demo: のログと Status の文字
//    - その下にスライダ 1 つ（slider.actor。左右の矢印で 1 段階ずつ動く）
//    - シーンに UiNavigator が無ければ ui_navigator.actor（方向キー・パッドの入口とフォーカスの枠）
//  できあがったら最初のボタンへフォーカスする（UiNavigation.FocusFirstIn）。矢印で移り、Enter・Space で押し、Esc で戻るの段へ。
//  子に Status（Text）があれば、今のフォーカスと押したボタンを書く（無ければログだけ）。
//  取り込み先は assets/ui/...（テンプレートライブラリの「UI 部品」）を想定したパス。
// ============================================================
using System.Collections.Generic;
using SEED;
using SEED.UI;
using SEEDEditor.Scripting;

public class UiKeyNavigationDemo : SEEDScript
{
    /// <summary>ログの接頭辞。</summary>
    private const string LogPrefix = "[UI] keynav-demo:";

    // ── プレハブ ──
    private const string ButtonPrefab = "assets://ui/prefabs/button.actor";
    private const string SliderPrefab = "assets://ui/prefabs/slider.actor";
    private const string NavigatorPrefab = "assets://ui/prefabs/ui_navigator.actor";

    // ── シーンの子の名前 ──
    /// <summary>今のフォーカスと押したボタンを書く文字（任意）。</summary>
    private const string StatusName = "Status";

    // ── 格子の寸法（dp。button.actor の既定の大きさ 120×48 と同じ）──
    /// <summary>列の数。</summary>
    private const int Columns = 3;
    /// <summary>行の数。</summary>
    private const int Rows = 2;
    /// <summary>ボタンの幅。</summary>
    private const float ButtonWidth = 120f;
    /// <summary>ボタンの高さ。</summary>
    private const float ButtonHeight = 48f;
    /// <summary>ボタンの間。</summary>
    private const float Gap = 16f;
    /// <summary>格子の左上（親の左上から）。</summary>
    private const float OriginX = 24f, OriginY = 96f;
    /// <summary>格子とスライダの間。</summary>
    private const float SliderGap = 32f;
    /// <summary>スライダの段階（0〜10 を 1 ずつ）。</summary>
    private const float SliderMax = 10f, SliderStep = 1f;

    /// <summary>シーンに UiNavigator が無ければ作るか。</summary>
    [SerializeField(Label = "UiNavigator を作る")]
    public bool CreateNavigator = true;

    /// <summary>作ったボタンのノード（格子の順）。</summary>
    private readonly List<GameObject> _buttonNodes = new();
    /// <summary>つないだボタン（二重につながない）。</summary>
    private readonly HashSet<Button> _wired = new();
    /// <summary>作ったスライダのノード。</summary>
    private GameObject _sliderNode;
    /// <summary>つないだスライダ。</summary>
    private Slider? _slider;
    /// <summary>最初のフォーカスを頼んだか。</summary>
    private bool _focused;

    /// <inheritdoc />
    public override void OnStart()
    {
        if (CreateNavigator && !UiNavigation.HasNavigator) GameObject.Instantiate(NavigatorPrefab, gameObject);
        for (int i = 0; i < Columns * Rows; i++) _buttonNodes.Add(GameObject.Instantiate(ButtonPrefab, gameObject));
        _sliderNode = GameObject.Instantiate(SliderPrefab, gameObject);
        UiNavigation.CurrentChanged += OnCurrentChanged;
    }

    /// <inheritdoc />
    public override void OnDestroy() => UiNavigation.CurrentChanged -= OnCurrentChanged;

    /// <inheritdoc />
    public override void Update(ref NativeFrameContext ctx)
    {
        // 部品は作ったフレームの末尾にできあがり、OnStart の後に登録簿から引ける: つながるまで毎フレーム試す
        WireButtons();
        WireSlider();
        if (!_focused && _wired.Count == _buttonNodes.Count && _slider is not null)
        {
            _focused = true;
            UiNavigation.FocusFirstIn(gameObject);
        }
    }

    /// <summary>できあがったボタンを格子へ置き、文字と押したときの処理をつなぐ。</summary>
    private void WireButtons()
    {
        for (int i = 0; i < _buttonNodes.Count; i++)
        {
            var node = _buttonNodes[i];
            if (UiWidget.Of<Button>(node) is not { } button || _wired.Contains(button)) continue;
            int column = i % Columns;
            int row = i / Columns;
            if (node.GetComponent<CanvasTransform>() is { } ct)
                ct.Position = new Vector2(OriginX + column * (ButtonWidth + Gap), OriginY + row * (ButtonHeight + Gap));
            int number = i + 1;
            node.Name = $"B{number}";
            button.SetText(node.Name);
            button.Clicked += _ => OnClicked(number);
            _wired.Add(button);
        }
    }

    /// <summary>できあがったスライダを格子の下へ置き、値の変化をつなぐ。</summary>
    private void WireSlider()
    {
        if (_slider is not null || UiWidget.Of<Slider>(_sliderNode) is not { } slider) return;
        if (_sliderNode.GetComponent<CanvasTransform>() is { } ct)
            ct.Position = new Vector2(OriginX, OriginY + Rows * (ButtonHeight + Gap) + SliderGap);
        slider.Max = SliderMax;
        slider.Step = SliderStep;
        slider.ValueChanged += (_, value) => Log($"slider = {value:0}");
        _slider = slider;
    }

    /// <summary>ボタンが押された（指でも Enter でも同じ）。</summary>
    private void OnClicked(int number) => Log($"clicked B{number}");

    /// <summary>今のフォーカスが変わった。</summary>
    private void OnCurrentChanged(IUiNavigable? current) => Log($"focus = {current?.NavNode.Name ?? "-"}");

    /// <summary>ログと Status の文字へ書く。</summary>
    private void Log(string message)
    {
        Debug.Log($"{LogPrefix} {message}");
        if (gameObject.FindChild(StatusName).GetComponent<Text>() is { } status) status.Content = message;
    }
}
