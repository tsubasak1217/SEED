using SEEDEditor.Scripting;

namespace SEED.UI;

// ============================================================
//  UiWidget.cs — 部品（ボタン・トグル・スライダ…）のスクリプトの共通の土台（W2-4）
//
//  【役割】部品は ECS のアクタ（プレハブ）に付けるスクリプト。見た目はプレハブの子（Sprite・Text の色・形・位置）で、
//  スクリプトは「状態（値・無効・押下）→ 見た目」を `ApplyLook` の 1 か所で決めて当てる（値はテーマのトークン）。
//  この土台は次を受け持つ:
//    - 登録簿（UiRegistry）への登録と `Of<T>` での引き当て（組み合わせる側が相手のスクリプトを引く）
//    - 無効（`Interactable`）
//    - テーマが替わったら見た目を作り直す（W2-9: UiTheme が登録簿の全部品へその場で ReapplyTheme を呼ぶ。
//      登録の前に替わった分は OnStart で、取りこぼしは毎フレームの UiTheme.Version の見比べで拾う）
//    - 見た目を変えたら SEED.Redraw.Request()（render_policy: on_demand でも描き直す。W2-10a）
//  スクリプトは ScriptComponent の type_name に型名（例 "SEED.UI.Button"）を書いて付ける（templates/ui/prefabs/）。
// ============================================================

/// <summary>部品のスクリプトの共通の土台。</summary>
public abstract class UiWidget : SEEDScript
{
    /// <summary>押せるか（false で無効の見た目になり、操作を受けない）。</summary>
    [SerializeField(Label = "押せる")]
    public bool Interactable = true;

    /// <summary>最後に見た目を作ったテーマの版（-1 = まだ）。</summary>
    private int _themeVersion = -1;

    /// <summary>登録したアクター。</summary>
    private Entity _actor = Entity.None;

    /// <summary>親のアクターの鍵（選択の項目がグループを引くため。親が無ければ既定値）。</summary>
    internal (uint, uint) ParentKey { get; private set; }

    /// <summary>この部品のアクター（スクリプトの外から引くとき用）。</summary>
    public GameObject Owner => new(_actor);

    /// <summary>今のテーマ。</summary>
    protected static UiThemeData Theme => UiTheme.Current;

    /// <summary>アクターの部品のうち型 T の最初のもの（無ければ null。相手の OnStart の前は null のことがある）。</summary>
    public static T? Of<T>(GameObject actor) where T : UiWidget => UiRegistry.Find<T>(actor);

    /// <summary>操作を受けるか（押せて、部品ごとの条件〈処理中など〉も満たす）。</summary>
    public virtual bool IsEnabled => Interactable;

    /// <summary>無効を切り替える（見た目も変える）。</summary>
    public void SetInteractable(bool interactable)
    {
        if (Interactable == interactable) return;
        Interactable = interactable;
        Refresh();
    }

    /// <summary>登録して、部品の初期化と最初の見た目を作る。</summary>
    public sealed override void OnStart()
    {
        _actor = gameObject.Entity;
        var parent = gameObject.Parent;
        ParentKey = parent.IsValid ? (parent.Entity.Index, parent.Entity.Generation) : default;
        UiRegistry.Register(this, _actor);
        OnWidgetStart();
        _themeVersion = UiTheme.Version;
        Refresh();
    }

    /// <summary>登録を外す。</summary>
    public sealed override void OnDestroy()
    {
        UiRegistry.Unregister(this, _actor);
        OnWidgetDestroy();
    }

    /// <summary>毎フレーム: テーマの替わりを見て、部品ごとの更新を呼ぶ。</summary>
    public sealed override void Update(ref NativeFrameContext ctx)
    {
        if (_themeVersion != UiTheme.Version)
        {
            _themeVersion = UiTheme.Version;
            Refresh();
        }
        OnWidgetUpdate(ctx.DeltaTime);
    }

    /// <summary>
    /// 全部品が見た目を作り直した回数（計測用。W2-5: 時刻ホイールを回している間に他の部品が作り直されない〈UC-4〉ことを
    /// ギャラリーの `ui,stats` で確かめる）。
    /// </summary>
    public static long RefreshCount { get; private set; }

    /// <summary>テーマが替わった: 今のテーマで見た目を当て直す（UiTheme から。毎フレームの見比べで 2 度作らないよう版を揃える）。</summary>
    internal void ReapplyTheme()
    {
        _themeVersion = UiTheme.Version;
        Refresh();
    }

    /// <summary>見た目を作り直して描き直しを頼む。</summary>
    protected void Refresh()
    {
        RefreshCount++;
        ApplyLook();
        Redraw.Request();
    }

    /// <summary>部品の初期化（子の参照を引く等。OnStart から 1 回）。</summary>
    protected virtual void OnWidgetStart() { }

    /// <summary>部品の後片付け。</summary>
    protected virtual void OnWidgetDestroy() { }

    /// <summary>部品の毎フレームの更新（動き・長押しの連続など）。</summary>
    /// <param name="dt">前のフレームからの秒。</param>
    protected virtual void OnWidgetUpdate(float dt) { }

    /// <summary>状態 → 見た目（プレハブの子の色・形・位置）を当てる（1 か所で決める）。</summary>
    protected abstract void ApplyLook();

    // ── 見た目を当てる小道具（子が無ければ何もしない）──

    /// <summary>自分か子（名前・パス。空文字なら自分）の Sprite。</summary>
    protected Sprite? SpriteOf(string childPath = "")
        => (childPath.Length == 0 ? gameObject : gameObject.FindChild(childPath)).GetComponent<Sprite>();

    /// <summary>子の Text。</summary>
    protected Text? TextOf(string childPath)
        => gameObject.FindChild(childPath).GetComponent<Text>();

    /// <summary>子の CanvasTransform。</summary>
    protected CanvasTransform? TransformOf(string childPath)
        => gameObject.FindChild(childPath).GetComponent<CanvasTransform>();
}
