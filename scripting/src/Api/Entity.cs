namespace SEED;

/// <summary>
/// ECS エンティティ識別子（Rust ランタイムの Entity と同じ (index, generation) の組）。
///
/// スクリプトが直接組み立てることはなく、<see cref="GameObject"/> / Transform が
/// 内部でコンポーネントアクセスのキーとして使う。世代はエンティティ再利用の検出用。
/// </summary>
public readonly struct Entity
{
    /// <summary>SparseSet へのインデックス。</summary>
    public readonly uint Index;
    /// <summary>再利用検出用の世代カウンタ。</summary>
    public readonly uint Generation;

    public Entity(uint index, uint generation)
    {
        Index = index;
        Generation = generation;
    }

    /// <summary>未束縛（無効）を表すエンティティ。</summary>
    public static Entity None => new(uint.MaxValue, 0);

    /// <summary>有効なエンティティに束縛されているか（None でないか）。</summary>
    public bool IsValid => Index != uint.MaxValue;
}
