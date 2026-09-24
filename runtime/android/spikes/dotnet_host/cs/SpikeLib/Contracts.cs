// =============================================================================
// Contracts.cs
// SEED の IScriptComponent 相当の契約と、リフレクション検証用のサンプル型。
// =============================================================================
namespace SpikeLib;

/// <summary>
/// プラグイン（= ユーザースクリプト相当）が実装する契約。
/// collectible ALC に読み込んだ型をこのインターフェースへキャストできるか
/// （＝型の同一性が ALC を跨いで保たれるか）の確認に使う。
/// </summary>
public interface IPluginComponent
{
    /// <summary>コンポーネント名。</summary>
    string Name { get; }

    /// <summary>1 ステップ進めて結果を返す。</summary>
    int Tick(int input);
}

/// <summary>リフレクション（CreateInstance / GetFields の get/set）検証用のコンポーネント。</summary>
public sealed class SampleComponent : IPluginComponent
{
    /// <summary>float フィールド（SerializeField 相当）。</summary>
    public float Speed = 1.5f;

    /// <summary>int フィールド。</summary>
    public int Counter;

    /// <summary>string フィールド。</summary>
    public string Label = "init";

    /// <summary>値型の struct フィールド（Vector3 相当）。</summary>
    public SampleStruct Offset;

    /// <inheritdoc />
    public string Name => nameof(SampleComponent);

    /// <inheritdoc />
    public int Tick(int input) => input + Counter;
}

/// <summary>値型の struct（MakeGenericType で List&lt;値型&gt; を作る検証に使う）。</summary>
public struct SampleStruct
{
    /// <summary>X 成分。</summary>
    public float X;

    /// <summary>Y 成分。</summary>
    public float Y;

    /// <summary>Z 成分。</summary>
    public float Z;

    /// <summary>各成分を指定して作る。</summary>
    public SampleStruct(float x, float y, float z)
    {
        X = x;
        Y = y;
        Z = z;
    }

    /// <inheritdoc />
    public override readonly string ToString() => $"({X},{Y},{Z})";
}
