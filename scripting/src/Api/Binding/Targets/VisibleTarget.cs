namespace SEED.Binding;

// ============================================================
//  VisibleTarget.cs — GameObject の表示（Visible）を当てる先（Bind.Visible）
//
//  Visible の set はフレームの末尾にエンジンへ反映される（同じフレームの get は書いた値。GameObject.Visible の約束どおり）。
//  アクタが破棄されたら（ActorLife）結び付けは外れる。
//  書くかどうかは「最後に書いた値」と比べて決める（2026-10-03。2 回目のレビュー #32）: Instantiate した直後（構築の前）のノードは
//  読み戻しが true なので、根を隠して保存したプレハブへ Bind.Visible(true) を結ぶと書かれず、構築の後も隠れたまま残った。
//  最初の 1 回は必ず書く（同じフレームに積んだ命令は構築の後に当たり、プレハブの値より優先される）。
// ============================================================

/// <summary>GameObject の表示を当てる先。</summary>
internal sealed class VisibleTarget : IBindTarget<bool>
{
    /// <summary>当てる GameObject の見張り。</summary>
    private readonly ActorLife _life;

    /// <summary>最後に書いた値（まだ書いていなければ null。同じ値の命令を積み直さない）。</summary>
    private bool? _written;

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
        // Visible の set は同じ値でもエンジンへ遅延の命令を積むので、最後に書いた値から変わるときだけ書く
        // （読み戻しと比べない: 構築の前のノードは読み戻しが true で、隠して保存したプレハブの根へ true を書き損ねる）
        if (_written == value) return;
        _written = value;
        var node = _life.Node;   // GameObject は値型なので、写しの上で set する（中身はエンティティの番号だけ）
        node.Visible = value;
    }
}
