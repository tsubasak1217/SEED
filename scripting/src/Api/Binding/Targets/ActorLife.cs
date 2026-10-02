namespace SEED.Binding;

// ============================================================
//  ActorLife.cs — GameObject（アクタ）がまだあるかの見張り（結び付けの当てる先の IsAlive に使う）
//
//  GameObject.IsValid は「ハンドルが束縛されているか」だけで、破棄されたかは分からない。そこで疑似コンポーネント
//  "GameObject"（アクタのルートか。GameObject.HasComponent）で確かめる。
//  ただし Instantiate したばかりのアクタは、構築がフレームの末尾なので、そのフレームの間は "GameObject" を持たない。
//  なので「一度でもアクタとして見えた後に見えなくなった」ときだけ破棄とみなす（作ったばかりの間は生きている扱い）。
// ============================================================

/// <summary>GameObject（アクタ）がまだあるかの見張り。</summary>
internal sealed class ActorLife
{
    /// <summary>アクタのルートかを問う疑似コンポーネントの名前（Rust 側 has_component の "GameObject" と一致）。</summary>
    private const string ActorPseudoComponent = "GameObject";

    /// <summary>見張る GameObject。</summary>
    private readonly GameObject _node;

    /// <summary>一度でもアクタとして見えたか。</summary>
    private bool _seen;

    /// <summary>見張りを作る。</summary>
    /// <param name="node">見張る GameObject。</param>
    internal ActorLife(GameObject node)
    {
        _node = node;
    }

    /// <summary>見張る GameObject。</summary>
    internal GameObject Node => _node;

    /// <summary>まだあるか（ハンドルが無効・一度見えた後に消えたら false。作ったばかりで構築の前は true）。</summary>
    internal bool IsAlive
    {
        get
        {
            if (!_node.IsValid) return false;
            bool exists = _node.HasComponent(ActorPseudoComponent);
            if (exists)
            {
                _seen = true;
                return true;
            }
            // まだ一度も見えていない（Instantiate したフレーム）なら生きている扱い。見えた後に消えたら破棄
            return !_seen;
        }
    }
}
