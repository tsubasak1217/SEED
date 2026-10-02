namespace SEED.Binding;

// ============================================================
//  VisibleTarget.cs — GameObject の表示（Visible）を当てる先（Bind.Visible）
//
//  Visible の set はフレームの末尾にエンジンへ反映される（同じフレームの get は書いた値。GameObject.Visible の約束どおり）。
//  アクタが破棄されたら（ActorLife）結び付けは外れる。
// ============================================================

/// <summary>GameObject の表示を当てる先。</summary>
internal sealed class VisibleTarget : IBindTarget<bool>
{
    /// <summary>当てる GameObject の見張り。</summary>
    private readonly ActorLife _life;

    /// <summary>当てる先を作る。</summary>
    /// <param name="node">当てる GameObject。</param>
    internal VisibleTarget(GameObject node)
    {
        _life = new ActorLife(node);
    }

    /// <inheritdoc />
    public bool IsAlive => _life.IsAlive;

    /// <inheritdoc />
    public bool IsReady => true;

    /// <inheritdoc />
    public void Write(bool value)
    {
        // Visible の set は同じ値でもエンジンへ遅延の命令を積むので、変わるときだけ書く
        var node = _life.Node;
        if (node.Visible != value) node.Visible = value;
    }
}
