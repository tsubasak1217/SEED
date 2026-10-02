using System.Collections.Generic;
using SEEDEditor.Scripting;

namespace SEED.Localization;

// ============================================================
//  LocalizedBinding.cs — 多言語の文字のスクリプトの共通の土台（LocalizedText・LocalizedLabel）
//
//  【役割】インスペクタの「キー」「差し込み」と、スクリプトから渡す実行中の値（SetArg・SetCount）から引き方（LocalizedRequest）を
//  作り、当てる先（ILocalizedTarget。派生が FindTarget で決める）へ今の言語の文字を当てる。
//    - OnStart: 登録簿へ載せ、L10n.Changed を this.On で購読（スクリプトの破棄で自動で外れる）し、最初の文字を当てる
//    - 言語の切り替え・表の読み直し（L10n.Changed）: 当て直す
//    - キーが空なら何もしない（プレハブに書いた文字のまま）
//  文字を変えたら SEED.Redraw.Request()（render_policy: on_demand でも描き直す）。
// ============================================================

/// <summary>多言語の文字のスクリプトの共通の土台。</summary>
public abstract class LocalizedBinding : SEEDScript
{
    /// <summary>ログの接頭辞。</summary>
    protected const string LogPrefix = "[SEED.Localization]";

    /// <summary>言語の表のキー（"menu.start"。空なら文字を変えない）。</summary>
    [SerializeField(Label = "キー", Tooltip = "言語の表のキー（例 menu.start）。空なら文字を変えない")]
    public string Key = string.Empty;

    /// <summary>文の {名前} へ差し込む値（インスペクタで書く。値は文字列のまま）。</summary>
    [SerializeField(Label = "差し込み", Tooltip = "文の {名前} へ差し込む値（文字列）。スクリプトの SetArg が同じ名前なら SetArg が勝つ")]
    public List<LocalizedArg> Args = new();

    /// <summary>スクリプトから渡した差し込み（インスペクタの差し込みより先＝勝つ）。</summary>
    private readonly List<(string name, object? value)> _runtimeArgs = new();

    /// <summary>複数形の数（null なら複数形にしない）。</summary>
    private long? _count;

    /// <summary>登録したアクタ。</summary>
    private Entity _actor = Entity.None;

    /// <summary>今の当てる先（まだ無ければ null）。</summary>
    private ILocalizedTarget? _target;

    /// <summary>「当てる文字の欄が無い」を警告したか（同じスクリプトでは 1 度だけ）。</summary>
    private bool _warnedNoLabel;

    /// <summary>このスクリプトのアクタ。</summary>
    public GameObject Owner => new(_actor);

    /// <summary>今の当てる先（まだ無ければ null）。</summary>
    public ILocalizedTarget? Target => _target;

    /// <summary>複数形の数（null なら複数形にしない）。</summary>
    public long? Count => _count;

    /// <summary>アクタの多言語の文字のスクリプトのうち型 T の最初のもの（無ければ null。相手の OnStart の前も null）。</summary>
    /// <param name="actor">アクタ。</param>
    /// <returns>スクリプトか null。</returns>
    public static T? Of<T>(GameObject actor) where T : LocalizedBinding => LocalizedRegistry.Find<T>(actor);

    /// <summary>登録して、言語の切り替えを購読し、最初の文字を当てる。</summary>
    public sealed override void OnStart()
    {
        _actor = gameObject.Entity;
        LocalizedRegistry.Register(this, _actor);
        On(L10n.Changed, (string _) => Apply());
        OnBindingStart();
        Apply();
    }

    /// <summary>登録を外す（L10n.Changed の購読は SEEDScript が自動で外す）。</summary>
    public sealed override void OnDestroy() => LocalizedRegistry.Unregister(this, _actor);

    /// <summary>毎フレーム: 派生の更新（LocalizedLabel の当てる先の引き直し）。</summary>
    public sealed override void Update(ref NativeFrameContext ctx) => OnBindingUpdate();

    // ============================================================
    //  スクリプトから
    // ============================================================

    /// <summary>キーを変えて当て直す。</summary>
    /// <param name="key">キー（空なら文字を変えない）。</param>
    public void SetKey(string key)
    {
        Key = key ?? string.Empty;
        Apply();
    }

    /// <summary>差し込みの値を 1 つ置く（同じ名前は置き換え。数・日付は今の言語の文化で書く）して当て直す。</summary>
    /// <param name="name">名前。</param>
    /// <param name="value">値。</param>
    public void SetArg(string name, object? value)
    {
        int index = _runtimeArgs.FindIndex(arg => arg.name == name);
        if (index >= 0) _runtimeArgs[index] = (name, value);
        else _runtimeArgs.Add((name, value));
        Apply();
    }

    /// <summary>スクリプトから渡した差し込みを外して当て直す（インスペクタの差し込みは残る）。</summary>
    public void ClearArgs()
    {
        _runtimeArgs.Clear();
        Apply();
    }

    /// <summary>複数形の数を置いて当て直す（キーを複数形のまとまりとして引く。数は {n}）。</summary>
    /// <param name="count">数。</param>
    public void SetCount(long count)
    {
        _count = count;
        Apply();
    }

    /// <summary>複数形の数を外して当て直す（普通のキーとして引く）。</summary>
    public void ClearCount()
    {
        _count = null;
        Apply();
    }

    /// <summary>今のキー・差し込み・数の引き方（スクリプトの差し込み → インスペクタの差し込みの順。名前の空の組は飛ばす）。</summary>
    /// <returns>引き方。</returns>
    public LocalizedRequest BuildRequest()
    {
        var inspectorArgs = Args ?? new List<LocalizedArg>();
        var args = new List<(string name, object? value)>(_runtimeArgs.Count + inspectorArgs.Count);
        args.AddRange(_runtimeArgs);
        foreach (var arg in inspectorArgs)
        {
            if (string.IsNullOrEmpty(arg.Name)) continue;
            args.Add((arg.Name, arg.Value ?? string.Empty));
        }
        return new LocalizedRequest(Key ?? string.Empty, args.ToArray(), _count);
    }

    /// <summary>
    /// 今の言語の文字を当てる（キーが空なら何もしない。当てる先が無ければ探し、それでも無ければ派生の判断に任せる）。
    /// </summary>
    public void Apply()
    {
        if (string.IsNullOrEmpty(Key)) return;
        if (_target is not { IsAlive: true }) _target = FindTarget(Owner);
        if (_target is null)
        {
            OnTargetMissing();
            return;
        }
        if (!_target.Apply(BuildRequest()))
        {
            WarnNoLabelOnce();
            return;
        }
        Redraw.Request();
    }

    // ============================================================
    //  派生の口
    // ============================================================

    /// <summary>アクタの当てる先を探す（無ければ null）。</summary>
    /// <param name="actor">アクタ。</param>
    /// <returns>当てる先か null。</returns>
    protected abstract ILocalizedTarget? FindTarget(GameObject actor);

    /// <summary>当てる先が見つからなかった（派生が警告の時期を決める）。</summary>
    protected virtual void OnTargetMissing() { }

    /// <summary>OnStart の最初の当てる前に 1 回。</summary>
    protected virtual void OnBindingStart() { }

    /// <summary>毎フレーム。</summary>
    protected virtual void OnBindingUpdate() { }

    /// <summary>
    /// 当てる先を探し直す。種類が替わったら（部品が後から登録簿に載った）その先へ当て直し、
    /// <paramref name="reapplySameKind"/> なら同じ種類でも当て直す（選択のグループの項目が増えたとき）。
    /// </summary>
    /// <param name="reapplySameKind">同じ種類でも当て直すか。</param>
    protected void Retarget(bool reapplySameKind)
    {
        if (string.IsNullOrEmpty(Key)) return;
        var found = FindTarget(Owner);
        if (found is null) return;
        bool kindChanged = _target is null || !_target.IsAlive || _target.KindName != found.KindName;
        if (!kindChanged && !reapplySameKind) return;
        _target = found;
        Apply();
    }

    /// <summary>「当てる文字の欄が無い」を 1 度だけ警告する（Toggle・Checkbox に子 Label が無いなど）。</summary>
    private void WarnNoLabelOnce()
    {
        if (_warnedNoLabel) return;
        _warnedNoLabel = true;
        string kind = _target?.KindName ?? string.Empty;
        Debug.LogWarning($"{LogPrefix} {GetType().Name}: {kind} に文字を当てる欄がありません（キー {Key}・アクタ {Owner.Name}。" +
                         $"Toggle・Checkbox は子「{LocalizedTargetTable.LabelChild}」の Text へ当てます）");
    }
}
