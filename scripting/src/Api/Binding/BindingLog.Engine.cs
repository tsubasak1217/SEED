namespace SEED.Binding;

// ============================================================
//  BindingLog.Engine.cs — 結び付けの警告・エラーをエンジンのログ（SEED.Debug）へ出す（実行中だけの部分）
//
//  Model/BindingLog.cs の部分メソッドの実装。テスト（editor/tests/BindingTests）はこのファイルを取り込まないので、
//  そちらでは標準エラー（または BindingLog.Listener）へ出る。
// ============================================================

/// <summary>結び付けの警告・エラーの出し先（エンジンのログへ出す部分）。</summary>
internal static partial class BindingLog
{
    /// <summary>エンジンのログへ出す（警告は LogWarning、エラーは LogError）。</summary>
    /// <param name="line">接頭辞つきの 1 行。</param>
    /// <param name="isError">エラーなら true。</param>
    /// <param name="delivered">出したので true にする。</param>
    static partial void WriteToEngine(string line, bool isError, ref bool delivered)
    {
        if (isError) SEED.Debug.LogError(line);
        else SEED.Debug.LogWarning(line);
        delivered = true;
    }
}
