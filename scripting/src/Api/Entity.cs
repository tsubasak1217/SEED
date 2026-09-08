namespace SEED;

/// <summary>
/// ECS エンティティ識別子（Rust ランタイムの Entity と同じ (index, generation) の組）。
///
/// スクリプトが直接組み立てることはなく、<see cref="GameObject"/> / Transform が
/// 内部でコンポーネントアクセスのキーとして使う。世代はエンティティ再利用の検出用。
///
/// 【未束縛（default）の扱い】
/// C# の <c>default(Entity)</c> は全ビット 0、すなわち <c>Index == 0</c> になる。
/// ランタイムの世代カウンタは 0 から始まるため <c>(index=0, generation=0)</c> は
/// **実在しうる正当なエンティティ**であり、インデックスの値だけでは
/// 「一度も束縛されていないハンドル」と区別できない。
/// そこで「コンストラクタを通ったか」を <see cref="_bound"/> で明示的に持ち、
/// <c>default</c> のハンドルは必ず <see cref="IsValid"/> == false になるようにしている。
/// （この印が無いと、未生成のフィールドが「エンティティ 0 番」を指す有効ハンドルとして
///  振る舞い、生成済みかどうかの判定がすべて反転する。）
/// </summary>
public readonly struct Entity
{
    /// <summary>無効（未束縛）を表すインデックス値。</summary>
    private const uint InvalidIndex = uint.MaxValue;

    /// <summary>
    /// コンストラクタを通って値が入っている（＝ default ではない）ことを表す印。
    /// bool の既定値が false であることを利用して <c>default(Entity)</c> を無効と判定する。
    /// </summary>
    private readonly bool _bound;

    /// <summary>SparseSet へのインデックス。</summary>
    public readonly uint Index;
    /// <summary>再利用検出用の世代カウンタ。</summary>
    public readonly uint Generation;

    /// <summary>ランタイムから受け取った (index, generation) で束縛する。</summary>
    /// <param name="index">SparseSet インデックス。</param>
    /// <param name="generation">世代カウンタ。</param>
    public Entity(uint index, uint generation)
    {
        _bound = true;
        Index = index;
        Generation = generation;
    }

    /// <summary>未束縛（無効）を表すエンティティ。</summary>
    public static Entity None => new(InvalidIndex, 0);

    /// <summary>
    /// 有効なエンティティに束縛されているか。
    /// 未束縛（<c>default</c>）と <see cref="None"/> のどちらでも false になる。
    /// </summary>
    public bool IsValid => _bound && Index != InvalidIndex;
}
