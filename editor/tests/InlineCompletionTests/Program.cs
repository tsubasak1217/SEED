using SpriteRigTests;

namespace InlineCompletionTests;

/// <summary>
/// AI インライン補完へ注入する API リファレンスの「節の選び方」の単体テスト
/// （docs/editor_inline_completion.md）。
///
/// 検証の柱:
///   1. 圧縮（見出し・コード・表・重要注記を残し、メンテナ向け以降を捨てる）… <see cref="CompactorTests"/>
///   2. 節と切れ端への分割・語の索引・接頭辞の検索 … <see cref="IndexTests"/>
///   3. 編集中のファイルの文脈の語（using・SEED. 修飾・近く・書きかけ）… <see cref="ContextTests"/>
///   4. 点付けと予算内の選択（常に入れる節・予算・順序・接頭辞）… <see cref="SelectorTests"/>
///   5. 選択結果の使い回し … <see cref="CacheTests"/>
///   6. 設定ファイル（同梱 JSON・壊れた JSON・範囲外）… <see cref="SettingsTests"/>
///   7. 実際の docs/scripting_api.md での文字数と選ばれる節 … <see cref="RealDocsTests"/>
///   8. 提供元 → システムプロンプト → 節の選択のつなぎ目（偽物の提供元・注入のログ・環境設定の予算）… <see cref="PromptTests"/>
/// 外部の AI サービスは呼ばない（このテストが触るのは文字列とファイルだけ）。
/// </summary>
public static class Program
{
    /// <summary>エントリポイント。全テストを実行し、失敗があれば終了コード 1 を返す。</summary>
    public static int Main()
    {
        var harness = new TestHarness();
        CompactorTests.Register(harness);
        IndexTests.Register(harness);
        ContextTests.Register(harness);
        SelectorTests.Register(harness);
        CacheTests.Register(harness);
        SettingsTests.Register(harness);
        RealDocsTests.Register(harness);
        PromptTests.Register(harness);
        return harness.Run();
    }
}
