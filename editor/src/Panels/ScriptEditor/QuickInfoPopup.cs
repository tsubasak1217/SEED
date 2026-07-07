using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml.Linq;
using ICSharpCode.AvalonEdit;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SEEDEditor.Panels.ScriptEditor;

/// <summary>
/// Visual Studio の QuickInfo 風のシンボル情報ホバーポップアップ。
///
/// エディタ上の識別子（メソッド・プロパティ・フィールド・型・変数・引数など）へ
/// マウスホバーすると、Roslyn の意味解析でシンボルを解決し、
///   1 行目: シグネチャ（戻り値の型・引数リスト付き。等幅フォント）
///   2 行目: シンボル種別（メソッド / プロパティ / ローカル変数 等。淡色）
///   3 行目: XML ドキュメントコメントの &lt;summary&gt;（あれば。折り返し表示）
/// をダークテーマのポップアップで表示する。
///
/// 意味解析コスト対策として SemanticModel はテキストバージョン単位でキャッシュし、
/// 編集が無い間のホバーは再コンパイルなしで応答する。
///
/// デバッガの変数ホバー（<see cref="DebugHoverPopup"/>）や IntelliSense 補完ウィンドウが
/// 優先されるべき場面では、コンストラクタで渡された抑止条件（isSuppressed）により
/// 表示を行わない。
/// </summary>
public sealed class QuickInfoPopup : IDisposable
{
    // ── 表示定数（マジックナンバー回避のため命名）───────────────
    /// <summary>ポップアップ全体の最大幅（px）。VS の QuickInfo と同程度。</summary>
    private const double MaxPopupWidth = 560;
    /// <summary>ポップアップ内側の余白（px）。</summary>
    private const double PopupPadding = 6;
    /// <summary>シグネチャ行と概要（summary）行の間隔（px）。</summary>
    private const double SummaryTopMargin = 4;
    /// <summary>
    /// ホバー解除から閉じるまでの猶予（ms）。ポップアップは MousePoint 配置で
    /// カーソルに重なるため、開いた直後に MouseHoverStopped が発火する。
    /// 即時クローズすると「開いた瞬間に消える」状態になるため、DebugHoverPopup と
    /// 同様に遅延させ、その間にマウスがポップアップへ入れば閉じない。
    /// </summary>
    private const int CloseDelayMs = 280;
    /// <summary>ポップアップをカーソルの少し下へずらす縦オフセット（px）。カーソル直下への被りを減らす。</summary>
    private const double PopupVerticalOffset = 14;

    // ── 配色（エディタのダークテーマに合わせる。DebugHoverPopup と統一）──
    private static readonly Brush Bg        = Frozen(Color.FromRgb(0x25, 0x25, 0x26));  // 背景
    private static readonly Brush Fg        = Frozen(Color.FromRgb(0xDC, 0xDC, 0xDC));  // 通常文字
    private static readonly Brush BorderC   = Frozen(Color.FromRgb(0x3F, 0x3F, 0x46));  // 枠線
    private static readonly Brush DimFg     = Frozen(Color.FromRgb(0x9A, 0x9A, 0x9A));  // 種別行（淡色）
    private static readonly Brush SigFg     = Frozen(Color.FromRgb(0x9C, 0xDC, 0xFE));  // シグネチャ（青系）
    private static readonly Brush ErrorFg   = Frozen(Color.FromRgb(0xF4, 0x8A, 0x8A));  // 診断メッセージ（赤系）
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    // ── シグネチャ表示フォーマット ───────────────────────────────
    /// <summary>
    /// 「float Mathf.Clamp(float value, float min, float max)」のような
    /// 戻り値型＋含有型＋引数名/型を含む最小修飾のシグネチャ書式。
    /// int/float などは C# キーワード表記（UseSpecialTypes）にする。
    /// </summary>
    private static readonly SymbolDisplayFormat SignatureFormat = new(
        globalNamespaceStyle:   SymbolDisplayGlobalNamespaceStyle.Omitted,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameOnly,
        genericsOptions:        SymbolDisplayGenericsOptions.IncludeTypeParameters,
        memberOptions:          SymbolDisplayMemberOptions.IncludeType
                              | SymbolDisplayMemberOptions.IncludeContainingType
                              | SymbolDisplayMemberOptions.IncludeParameters
                              | SymbolDisplayMemberOptions.IncludeRef,
        parameterOptions:       SymbolDisplayParameterOptions.IncludeType
                              | SymbolDisplayParameterOptions.IncludeName
                              | SymbolDisplayParameterOptions.IncludeParamsRefOut
                              | SymbolDisplayParameterOptions.IncludeDefaultValue,
        localOptions:           SymbolDisplayLocalOptions.IncludeType,
        propertyStyle:          SymbolDisplayPropertyStyle.NameOnly,
        miscellaneousOptions:   SymbolDisplayMiscellaneousOptions.UseSpecialTypes
                              | SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers);

    // ── 依存（コンストラクタで注入）─────────────────────────────
    private readonly TextEditor      _editor;
    /// <summary>現在のファイルに対応する Roslyn Document を返すデリゲート（ワークスペース未構築などは null）。</summary>
    private readonly Func<Document?> _getDocument;
    /// <summary>表示を抑止すべきとき true（デバッグホバー有効時・補完ウィンドウ表示中など）。</summary>
    private readonly Func<bool>      _isSuppressed;
    /// <summary>指定オフセットの診断（赤波線）メッセージを返すデリゲート（無ければ null）。</summary>
    private readonly Func<int, string?> _getDiagnostic;

    // ── UI 要素 ─────────────────────────────────────────────────
    private readonly Popup     _popup;
    private readonly TextBlock _diagText;        // 診断メッセージ（赤波線のエラー内容。あれば最上段）
    private readonly TextBlock _signatureText;   // シグネチャ（等幅）
    private readonly TextBlock _kindText;        // シンボル種別（淡色）
    private readonly TextBlock _summaryText;     // XML doc の summary（折り返し）

    /// <summary>ホバー解除後の遅延クローズ用タイマー（ポップアップへ入ったらキャンセル）。</summary>
    private readonly System.Windows.Threading.DispatcherTimer _closeTimer;

    /// <summary>ポップアップが表示中か（診断ツールチップとの二重表示防止に使う）。</summary>
    public bool IsOpen => _popup.IsOpen;

    /// <summary>
    /// ポップアップが開いた直後に発火する。QuickInfo は非同期解析の完了後に開くため、
    /// 先に表示された旧診断ツールチップをこのタイミングで閉じてもらう（二重表示防止）。
    /// </summary>
    public event Action? Opened;

    // ── SemanticModel キャッシュ ─────────────────────────────────
    /// <summary>エディタのテキスト変更ごとに増えるバージョン番号（キャッシュ無効化キー）。</summary>
    private int            _textVersion;
    /// <summary>キャッシュ済み SemanticModel（_cachedVersion のテキストに対応）。</summary>
    private SemanticModel? _cachedModel;
    /// <summary>_cachedModel を計算した時点のテキストバージョン。</summary>
    private int            _cachedVersion = -1;

    /// <summary>ホバー処理の世代番号。非同期解析の完了時に古い結果を破棄するために使う。</summary>
    private int _hoverGeneration;

    private bool _disposed;

    /// <param name="editor">対象のエディタ。</param>
    /// <param name="getDocument">最新テキストが反映された Roslyn Document を返すデリゲート。</param>
    /// <param name="isSuppressed">表示を抑止すべきとき true を返すデリゲート
    /// （デバッグセッション中のホバー評価や補完ウィンドウ表示中は QuickInfo を出さない）。</param>
    /// <param name="getDiagnostic">指定オフセットの診断（赤波線）メッセージを返すデリゲート。
    /// エラー箇所ではシンボル情報と診断内容を 1 つのポップアップに統合して表示する。</param>
    public QuickInfoPopup(
        TextEditor editor,
        Func<Document?> getDocument,
        Func<bool> isSuppressed,
        Func<int, string?>? getDiagnostic = null)
    {
        _editor        = editor;
        _getDocument   = getDocument;
        _isSuppressed  = isSuppressed;
        _getDiagnostic = getDiagnostic ?? (_ => null);

        // ── ポップアップの中身を構築する ──
        _diagText = new TextBlock
        {
            Foreground   = ErrorFg,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, SummaryTopMargin),
        };
        _signatureText = new TextBlock
        {
            Foreground   = SigFg,
            FontFamily   = Mono,
            TextWrapping = TextWrapping.Wrap,   // 長いシグネチャは折り返す
        };
        _kindText = new TextBlock
        {
            Foreground = DimFg,
        };
        _summaryText = new TextBlock
        {
            Foreground   = Fg,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, SummaryTopMargin, 0, 0),
        };

        var stack = new StackPanel();
        stack.Children.Add(_diagText);
        stack.Children.Add(_signatureText);
        stack.Children.Add(_kindText);
        stack.Children.Add(_summaryText);

        var border = new Border
        {
            Background      = Bg,
            BorderBrush     = BorderC,
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(PopupPadding),
            MaxWidth        = MaxPopupWidth,
            Child           = stack,
        };
        // マウスがポップアップへ入ったら遅延クローズをキャンセル（内容を選択・熟読できるように）、
        // 出たら閉じる（DebugHoverPopup と同じ操作感）
        border.MouseEnter += (_, _) => _closeTimer.Stop();
        border.MouseLeave += (_, _) => Hide();

        _popup = new Popup
        {
            AllowsTransparency = true,
            StaysOpen          = true,
            Placement          = PlacementMode.MousePoint,   // DebugHoverPopup と同じ配置方式
            VerticalOffset     = PopupVerticalOffset,        // カーソル直下への被りを減らす
            PlacementTarget    = editor,
            Child              = border,
        };

        // ホバー解除後の遅延クローズタイマー。
        // MousePoint 配置のポップアップはカーソルに重なるため、表示直後に
        // MouseHoverStopped が発火する。即時クローズだと「開いた瞬間に消える」ため、
        // 猶予を設けてその間にポップアップへ入れば閉じないようにする。
        _closeTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(CloseDelayMs),
        };
        _closeTimer.Tick += (_, _) => { _closeTimer.Stop(); Hide(); };

        // ── イベント購読 ──
        // ホバー開始/終了（AvalonEdit の TextView が発行するホバーイベント）
        editor.TextArea.TextView.MouseHover        += OnMouseHover;
        editor.TextArea.TextView.MouseHoverStopped += OnMouseHoverStopped;
        // テキスト変更: キャッシュを無効化し、開いていれば閉じる
        editor.TextChanged += OnTextChanged;
        // スクロール・フォーカス喪失: 表示位置がずれる/操作が移るので閉じる
        editor.TextArea.TextView.ScrollOffsetChanged += OnScrollChanged;
        editor.LostKeyboardFocus += OnLostFocus;
    }

    /// <summary>イベント購読を解除し、ポップアップを閉じる（タブを閉じるとき）。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Hide();
        _editor.TextArea.TextView.MouseHover          -= OnMouseHover;
        _editor.TextArea.TextView.MouseHoverStopped   -= OnMouseHoverStopped;
        _editor.TextChanged                           -= OnTextChanged;
        _editor.TextArea.TextView.ScrollOffsetChanged -= OnScrollChanged;
        _editor.LostKeyboardFocus                     -= OnLostFocus;
        _cachedModel = null;
    }

    // ── イベントハンドラ ─────────────────────────────────────────

    /// <summary>テキスト変更: SemanticModel キャッシュを無効化し、ポップアップを閉じる。</summary>
    private void OnTextChanged(object? sender, EventArgs e)
    {
        _textVersion++;
        Hide();
    }

    /// <summary>スクロールされたら閉じる（ホバー位置と表示位置がずれるため）。</summary>
    private void OnScrollChanged(object? sender, EventArgs e) => Hide();

    /// <summary>エディタがフォーカスを失ったら閉じる。</summary>
    private void OnLostFocus(object? sender, KeyboardFocusChangedEventArgs e) => Hide();

    /// <summary>
    /// ホバーが外れた: 少し待ってから閉じる（その間にマウスがポップアップへ
    /// 入れば閉じない）。表示直後の疑似的なホバー解除で即消えするのを防ぐ。
    /// </summary>
    private void OnMouseHoverStopped(object? sender, MouseEventArgs e)
    {
        if (_popup.IsOpen) _closeTimer.Start();
    }

    /// <summary>
    /// ホバー開始: マウス位置のシンボルを Roslyn で解決し、QuickInfo を表示する。
    /// 抑止条件（デバッグホバー有効・補完ウィンドウ表示中）では何もしない。
    /// </summary>
    private async void OnMouseHover(object? sender, MouseEventArgs e)
    {
        if (_disposed || _isSuppressed()) return;

        // マウス位置 → ドキュメントオフセット（文字上でなければ何もしない）
        var tvp = _editor.GetPositionFromPoint(e.GetPosition(_editor));
        if (tvp is null) { Hide(); return; }
        int offset = _editor.Document.GetOffset(tvp.Value.Line, tvp.Value.Column);

        // 非同期解析の競合対策: 世代番号を進め、完了時に古い結果を捨てる
        int generation = ++_hoverGeneration;

        QuickInfoContent? info;
        try
        {
            info = await BuildQuickInfoAsync(offset);
        }
        catch
        {
            // 解析途中の不整合などは表示しないだけ（次のホバーで再試行される）
            return;
        }

        // 待機中にテキスト変更・別ホバー・破棄が起きていたら結果を破棄する
        if (_disposed || generation != _hoverGeneration) return;

        // 診断（赤波線）があればシンボル情報と統合して 1 つのポップアップで表示する。
        // シンボル未解決でも診断だけあれば表示する（未定義識別子のエラー等）。
        string? diag = _getDiagnostic(offset);
        if (info is null && diag is null) { Hide(); return; }

        // ── 表示内容を反映して開く ──
        _diagText.Text          = diag ?? string.Empty;
        _diagText.Visibility    = string.IsNullOrEmpty(diag)
            ? Visibility.Collapsed : Visibility.Visible;
        _signatureText.Text     = info?.Signature ?? string.Empty;
        _signatureText.Visibility = info is null ? Visibility.Collapsed : Visibility.Visible;
        _kindText.Text          = info?.Kind ?? string.Empty;
        _kindText.Visibility    = info is null ? Visibility.Collapsed : Visibility.Visible;
        _summaryText.Text       = info?.Summary ?? string.Empty;
        _summaryText.Visibility = string.IsNullOrEmpty(info?.Summary)
            ? Visibility.Collapsed : Visibility.Visible;

        _closeTimer.Stop();     // 表示し直すときは進行中の遅延クローズをキャンセルする
        _popup.IsOpen = true;
        Opened?.Invoke();
    }

    /// <summary>ポップアップを閉じる。</summary>
    private void Hide()
    {
        _closeTimer.Stop();
        _popup.IsOpen = false;
    }

    // ── シンボル解決・表示内容の構築 ─────────────────────────────

    /// <summary>QuickInfo の表示内容（シグネチャ・種別・概要）。</summary>
    private sealed record QuickInfoContent(string Signature, string Kind, string? Summary);

    /// <summary>
    /// 指定オフセットのシンボルを解決し、表示内容を組み立てる。
    /// シンボルが無い・コメント/文字列内・解析不能の場合は null。
    /// </summary>
    private async Task<QuickInfoContent?> BuildQuickInfoAsync(int offset)
    {
        var model = await GetSemanticModelAsync();
        if (model is null) return null;

        var root = await model.SyntaxTree.GetRootAsync();
        if (offset < 0 || offset >= root.FullSpan.End) return null;

        // オフセット位置のトークンを取得。トリビア（コメント・空白）上や
        // 識別子以外（文字列リテラル・数値・記号など）は対象外にする。
        var token = root.FindToken(offset);
        if (!token.Span.Contains(offset)) return null;                       // コメント・空白上
        if (!token.IsKind(SyntaxKind.IdentifierToken)) return null;         // 識別子のみ対象
        if (token.Parent is null) return null;

        // 参照ならバインド結果、宣言なら宣言シンボルで解決する
        var symbolInfo = model.GetSymbolInfo(token.Parent);
        ISymbol? symbol = symbolInfo.Symbol
            ?? symbolInfo.CandidateSymbols.FirstOrDefault()   // オーバーロード未確定などの候補
            ?? model.GetDeclaredSymbol(token.Parent);          // 宣言箇所（メソッド定義行など）
        if (symbol is null) return null;

        // ラベルやエイリアスなど情報価値の薄いものは表示しない
        if (symbol.Kind is SymbolKind.Label or SymbolKind.Alias or SymbolKind.Preprocessing)
            return null;

        string signature = FormatSignature(symbol);
        string kind      = KindLabel(symbol);
        string? summary  = ExtractSummary(symbol);
        return new QuickInfoContent(signature, kind, summary);
    }

    /// <summary>
    /// 現在テキストの SemanticModel を返す。テキストバージョンが変わっていなければ
    /// キャッシュを返し、ホバーのたびの再コンパイルを避ける。
    /// </summary>
    private async Task<SemanticModel?> GetSemanticModelAsync()
    {
        int version = _textVersion;
        if (_cachedModel is not null && _cachedVersion == version)
            return _cachedModel;

        // ワークスペースの Document はエディタの TextChanged で常に最新化されている
        // （ScriptEditorPanel が UpsertText 済み）ため、そこから意味解析を取得する
        var document = _getDocument();
        if (document is null) return null;

        var model = await document.GetSemanticModelAsync();
        // 解析中にさらに編集されていたらキャッシュしない（次回計算し直す）
        if (model is not null && _textVersion == version)
        {
            _cachedModel   = model;
            _cachedVersion = version;
        }
        return model;
    }

    /// <summary>シンボルのシグネチャ文字列（1 行目）を作る。</summary>
    private static string FormatSignature(ISymbol symbol)
    {
        // 名前空間はフル表記のほうが分かりやすい（例: SEED.Scripting）
        if (symbol is INamespaceSymbol ns)
            return ns.ToDisplayString();

        // プロパティは「型 含有型.名前 { get; set; }」風に型を先頭へ付ける
        if (symbol is IPropertySymbol prop)
        {
            var accessors = (prop.GetMethod, prop.SetMethod) switch
            {
                (not null, not null) => "{ get; set; }",
                (not null, null)     => "{ get; }",
                (null, not null)     => "{ set; }",
                _                    => string.Empty,
            };
            return $"{prop.Type.ToDisplayString(SignatureFormat)} {prop.ToDisplayString(SignatureFormat)} {accessors}".TrimEnd();
        }

        // フィールドは「型 含有型.名前」（const は値の情報を SymbolDisplay に任せる）
        if (symbol is IFieldSymbol field)
            return $"{field.Type.ToDisplayString(SignatureFormat)} {field.ToDisplayString(SignatureFormat)}";

        // イベントも型を先頭に付ける
        if (symbol is IEventSymbol ev)
            return $"{ev.Type.ToDisplayString(SignatureFormat)} {ev.ToDisplayString(SignatureFormat)}";

        // メソッドは SignatureFormat（IncludeType）で「戻り値型 含有型.名前(引数...)」になる。
        // ローカル・引数も localOptions / parameterOptions により型付きで表示される。
        return symbol.ToDisplayString(SignatureFormat);
    }

    /// <summary>シンボル種別の日本語ラベル（2 行目）を返す。</summary>
    private static string KindLabel(ISymbol symbol) => symbol switch
    {
        IMethodSymbol m => m.MethodKind switch
        {
            MethodKind.Constructor       => "コンストラクタ",
            MethodKind.LocalFunction     => "ローカル関数",
            MethodKind.ReducedExtension  => "拡張メソッド",
            _                            => "メソッド",
        },
        IPropertySymbol                  => "プロパティ",
        IFieldSymbol f => f switch
        {
            { IsConst: true }                            => "定数",
            { ContainingType.TypeKind: TypeKind.Enum }   => "列挙メンバー",
            _                                            => "フィールド",
        },
        IEventSymbol                     => "イベント",
        ILocalSymbol                     => "ローカル変数",
        IParameterSymbol                 => "引数",
        ITypeParameterSymbol             => "型引数",
        INamespaceSymbol                 => "名前空間",
        INamedTypeSymbol t => t.TypeKind switch
        {
            TypeKind.Class     => "クラス",
            TypeKind.Struct    => "構造体",
            TypeKind.Interface => "インターフェイス",
            TypeKind.Enum      => "列挙型",
            TypeKind.Delegate  => "デリゲート",
            _                  => "型",
        },
        _                                => symbol.Kind.ToString(),
    };

    // ── XML ドキュメントコメント（summary）の抽出 ─────────────────

    /// <summary>
    /// シンボルの XML ドキュメントコメントから &lt;summary&gt; の本文テキストを取り出す。
    /// &lt;see cref="X"/&gt; は X の短い名前へ、&lt;paramref name="x"/&gt; は x へ展開し、
    /// 連続する空白・改行は 1 個のスペースへ畳む。summary が無ければ null。
    /// </summary>
    private static string? ExtractSummary(ISymbol symbol)
    {
        string? xml;
        try { xml = symbol.GetDocumentationCommentXml(); }
        catch { return null; }
        if (string.IsNullOrWhiteSpace(xml)) return null;

        try
        {
            // GetDocumentationCommentXml は <member> 要素（または断片）を返す。
            // 断片でも安全にパースできるようルート要素で包んでから探す。
            var rootElement = XElement.Parse($"<root>{xml}</root>");
            var summary = rootElement.Descendants("summary").FirstOrDefault();
            if (summary is null) return null;

            var sb = new StringBuilder();
            FlattenDocNode(summary, sb);

            // 連続空白（改行・インデント含む）を 1 スペースへ畳む
            var collapsed = string.Join(' ',
                sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            return collapsed.Length == 0 ? null : collapsed;
        }
        catch
        {
            return null;   // 壊れた XML コメントは概要なし扱い
        }
    }

    /// <summary>
    /// summary 要素の子ノードを再帰的にテキスト化する。
    /// see/seealso の cref・paramref/typeparamref の name はその識別子名へ展開する。
    /// </summary>
    private static void FlattenDocNode(XElement element, StringBuilder sb)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    sb.Append(text.Value);
                    break;
                case XElement child:
                    switch (child.Name.LocalName)
                    {
                        case "see":
                        case "seealso":
                            // cref="M:SEED.Mathf.Clamp(...)" → "Clamp" のように短縮する
                            var cref = (string?)child.Attribute("cref")
                                    ?? (string?)child.Attribute("langword");
                            if (cref is not null) sb.Append(ShortCrefName(cref));
                            else FlattenDocNode(child, sb);   // <see>本文</see> 形式
                            break;
                        case "paramref":
                        case "typeparamref":
                            sb.Append((string?)child.Attribute("name") ?? string.Empty);
                            break;
                        default:
                            // <c> <code> <para> などは中身のテキストだけ拾う
                            FlattenDocNode(child, sb);
                            break;
                    }
                    break;
            }
        }
    }

    /// <summary>
    /// cref 文字列（例: "M:SEED.Mathf.Clamp(System.Single)"）から短い表示名（"Clamp"）を作る。
    /// </summary>
    private static string ShortCrefName(string cref)
    {
        // 先頭の "M:" "T:" などの種別プレフィックスを除去する
        const int PrefixLength = 2;   // 「種別 1 文字 + ':'」
        var s = (cref.Length > PrefixLength && cref[1] == ':') ? cref[PrefixLength..] : cref;
        // 引数リストを除去する
        int paren = s.IndexOf('(');
        if (paren >= 0) s = s[..paren];
        // 最後のセグメント（メンバ名/型名）だけ残す
        int dot = s.LastIndexOf('.');
        return dot >= 0 ? s[(dot + 1)..] : s;
    }

    /// <summary>Freeze 済みブラシを作る（描画スレッド安全・省メモリ）。</summary>
    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}
