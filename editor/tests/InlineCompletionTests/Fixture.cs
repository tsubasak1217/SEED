using System;
using System.Linq;
using SEEDEditor.Panels.ScriptEditor.InlineCompletion.Reference;

namespace InlineCompletionTests;

/// <summary>
/// テスト用の小さなリファレンス（docs/scripting_api.md と同じ形の見出し・コード・表・注記）と、
/// 編集中のファイルの見本。実際の docs に依らず規則だけを確かめるために使う。
///
/// 形の対応: 前書き（重要注記）→ §1（基本形＋属性の表）→ §2（ライフサイクル表）→ §3 → §6.5 →
/// §7（GameObject の先頭・Transform・#### のレシピ・コンポーネント一覧）→ §7.18（ScreenStack）→
/// §7.21（L10n）→ §7.22（Bind）→ §8（メンテナ向け。以降は捨てられる）→ §9。
/// </summary>
public static class Fixture
{
    /// <summary>カーソルの位置の印（見本のファイルから取り除いてカーソルの位置にする）。</summary>
    public const string CaretMark = "/*|*/";

    /// <summary>テスト用のリファレンスの Markdown。</summary>
    public const string Markdown = """
        # SEED スクリプト API リファレンス

        このファイルは正典です（散文は落ちる）。

        > **重要（AI 向け）**: SEED は **Unity ではありません**。
        >
        > 本リファレンスの一覧は SEED. を省略しています（この注記は落ちる）。

        ## 1. スクリプトの基本形

        スクリプトは C# で書きます（散文）。

        ```csharp
        public class Mover : SEEDScript
        {
            public override void Update(ref NativeFrameContext ctx) { }
        }
        ```

        ### インスペクタ公開の属性（`SEEDEditor.Scripting` 名前空間）

        | 属性 | 付ける先 | 効果 |
        |------|----------|------|
        | `[SerializeField]` | フィールド | インスペクタに公開する |
        | `[Bindable]` | フィールド | バインド元として公開する |

        ## 2. ライフサイクル関数

        | 関数 | 呼ばれるとき |
        |---|---|
        | `OnStart()` | 生成直後 |
        | `Update(ref NativeFrameContext ctx)` | 毎フレーム |

        ## 3. Time（フレーム時間）

        ```csharp
        float dt = Time.DeltaTime;
        float elapsed = Time.ElapsedTime;
        ```

        ## 6.5 Input（キーボード・マウス）

        ```csharp
        bool jump = Input.GetKeyDown(KeyCode.Space);
        Vector2 mouse = Input.MousePosition;
        ```

        ## 7. GameObject とコンポーネント（GetComponent<T>）

        ```csharp
        var t = gameObject.GetComponent<Transform>();
        GameObject? enemy = GameObject.Find("Enemy");
        ```

        ### Transform（3D 位置・回転・スケール）

        ```csharp
        transform.Position = Vector3.Zero;
        transform.Rotation = Quaternion.Identity;
        ```

        #### レシピ: 回す

        ```csharp
        transform.Rotation *= Quaternion.Euler(0, 90, 0);
        ```

        ### 利用可能なコンポーネント一覧

        | コンポーネント | 取得 | 主な用途 |
        |---|---|---|
        | `Transform` | `GetComponent<Transform>()` | 3D の位置 |
        | `Text` | `GetComponent<Text>()` | 文字の表示 |

        ## 7.18 画面の組み立て（SEED.UI：画面のスタック・ダイアログ）

        ```csharp
        using SEED.UI;

        var stack = UiWidget.Of<ScreenStack>(GameObject.Find("RootStack"));
        ScreenHandle h = stack.Push("assets://ui/edit.actor");
        stack.Pop();

        DialogHandle? d = Dialog.Show(new DialogOptions { Title = "削除しますか？" });
        ```

        > **重要**: 画面のプレハブは次のフレームにできあがる。

        ## 7.21 Localization（多言語。SEED.Localization）

        ```csharp
        using SEED.Localization;

        string s = L10n.Get("menu.start");
        bool ok = L10n.SetLanguage("en");
        ```

        ## 7.22 Binding（観測できる値と UI 部品への結び付け。SEED.Binding）

        ```csharp
        using SEED.Binding;

        var count = new Observable<int>(0);
        Bind.Text(label, count, n => $"{n}");
        ```

        ## 8. （メンテナ向け）新しいコンポーネントをスクリプトへ公開する手順

        ```csharp
        MaintainerOnlyApi.Register();
        ```

        ## 9. 使用可能なライブラリ

        | ライブラリ | 用途 |
        |---|---|
        | `System.Linq` | 一覧の操作 |
        """;

    /// <summary>
    /// 切れ端の分割を確かめる長い節（空行で区切ったコードの段落 3 つ・長い表・注記）。
    /// </summary>
    public static string LongSectionMarkdown()
    {
        var code = string.Join("\n\n", Enumerable.Range(0, 3).Select(p =>
            string.Join("\n", Enumerable.Range(0, 12).Select(i => $"var widget{p}_{i} = UiWidget.Of<LongWidget{p}>(GameObject.Find(\"Node{p}_{i}\"));"))));
        var rows = string.Join("\n", Enumerable.Range(0, 30).Select(i => $"| `token.long_{i}` | {i} | 長い表の行 {i} の説明 |"));
        return "# 題\n\n## 5. 長い節（分割の確認）\n\n```csharp\n" + code + "\n```\n\n"
             + "| トークン | 既定 | 意味 |\n|---|---|---|\n" + rows + "\n\n> **重要**: 長い節の終わりの注記。\n\n"
             + "## 6. 次の節\n\n```csharp\nvar next = 1;\n```\n";
    }

    /// <summary>ScreenStack を使う画面のスクリプト（Localization は使わない）。</summary>
    public const string ScreenStackFile = """
        using SEEDEditor.Scripting;
        using SEED.UI;

        public class AlarmListScreen : UiScreen
        {
            public void OpenEdit()
            {
                var stack = SEED.UI.UiWidget.Of<ScreenStack>(SEED.GameObject.Find("RootStack"));
                stack.Push("assets://alarm/edit.actor");
                /*|*/
            }
        }
        """;

    /// <summary>L10n を使うスクリプト。</summary>
    public const string LocalizationFile = """
        using SEEDEditor.Scripting;
        using SEED.Localization;

        public class TitleLabel : SEEDScript
        {
            public override void OnStart()
            {
                string title = L10n.Get("menu.start");
                /*|*/
            }
        }
        """;

    /// <summary>Bind と書きかけたスクリプト（完全一致の Bind）。</summary>
    public const string BindTypingFile = """
        using SEEDEditor.Scripting;

        public class CounterView : SEEDScript
        {
            public override void OnStart()
            {
                Bind/*|*/
            }
        }
        """;

    /// <summary>Bin まで書きかけたスクリプト（接頭辞の一致だけで Binding・Bind・Bindable に当たる）。</summary>
    public const string BinPrefixFile = """
        using SEEDEditor.Scripting;

        public class CounterView : SEEDScript
        {
            public override void OnStart()
            {
                Bin/*|*/
            }
        }
        """;

    /// <summary>手がかりの無いスクリプト（基礎の節だけが入るはず）。</summary>
    public const string PlainFile = """
        public class Empty
        {
            /*|*/
        }
        """;

    /// <summary>見本のファイルからカーソルの印を取り除き、本文とカーソルの位置にする。</summary>
    /// <param name="withCaret">印つきの本文。</param>
    /// <returns>本文とカーソルの位置。</returns>
    public static (string Text, int Caret) Split(string withCaret)
    {
        int caret = withCaret.IndexOf(CaretMark, StringComparison.Ordinal);
        if (caret < 0) throw new InvalidOperationException("見本にカーソルの印がありません");
        return (withCaret.Remove(caret, CaretMark.Length), caret);
    }

    /// <summary>テスト用の索引（切れ端に分けない大きさの上限）。</summary>
    /// <param name="maxPartChars">切れ端の上限。</param>
    /// <returns>索引。</returns>
    public static ApiReferenceIndex Index(int maxPartChars = ApiReferenceSettings.DefaultMaxPartChars) =>
        ApiReferenceIndex.FromMarkdown(Markdown, maxPartChars);

    /// <summary>名前（Label か SectionLabel）で切れ端を探す。</summary>
    /// <param name="index">索引。</param>
    /// <param name="label">名前。</param>
    /// <returns>切れ端。</returns>
    public static ApiReferencePart Part(ApiReferenceIndex index, string label) =>
        index.Parts.FirstOrDefault(p => p.Label == label || p.SectionLabel == label)
        ?? throw new InvalidOperationException($"切れ端 {label} がありません（{string.Join(" ", index.Parts.Select(p => p.Label))}）");

    /// <summary>選択がこの節（のどれかの切れ端）を含むか。</summary>
    /// <param name="selection">選択。</param>
    /// <param name="sectionLabel">節の名前。</param>
    /// <returns>含めば true。</returns>
    public static bool Includes(ApiReferenceSelection selection, string sectionLabel) =>
        selection.Parts.Any(p => p.SectionLabel == sectionLabel);
}
