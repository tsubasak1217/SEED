// trim 検証用のエントリポイント（実行はしない。ILLink のルート化のためだけに存在する）。
namespace SpikeLib;

/// <summary>空のエントリポイント。</summary>
internal static class Program
{
    /// <summary>何もしない。ランタイムは Rust ホストから hostfxr 経由で起動される。</summary>
    private static void Main()
    {
    }
}
