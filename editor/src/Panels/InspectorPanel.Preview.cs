// ============================================================
//  InspectorPanel.Preview.cs — インスペクタの画面プレビュー（帯・読み取り専用・差し込み先の案内）
//
//  【役割】（InspectorPanel の部分クラス。正典は docs/editor_screen_preview.md §1・§3・§5）
//  1. プレビューの帯: 選んだアクタが画面プレビュー（保存されない表示用のアクタ）の中なら、アコーディオンの最上部
//     （プレハブ参照バーより上）に「プレビュー（保存されません）」の帯を出す。中身のファイル名（フルパスはツールチップ）と
//     ［プレハブを開く］（既存の ActorFileOpenRequested で絶対パスを渡す）・［プレビューを消す］を持つ。
//     ACTOR_COMPONENTS の "editor_preview" の読み取りは WPF 非依存の Preview/InspectorPreviewInfo。
//  2. 読み取り専用: プレビューの中のアクタは帯のボタン以外を押せなくする（当てるのは InspectorPanel.ReadOnly.cs の
//     ApplyEditability。閲覧専用と両立させるため 1 か所にまとめてある）。直すのは元のプレハブ（保存すると作り直される）。
//  3. 差し込み先の案内: ScreenStack・ModalHost・PopupPlane など、表（editor/config/screen_preview_hosts.json。
//     Preview/PreviewHostCatalog）に載ったスクリプトの「スクリプトを編集」の後に「プレビュー」の欄を足す。
//     行ごとに［{既定のプレハブ} をプレビュー］と［選ぶ...］。押すと MainWindow へ差し込み先を知らせる
//     （世界線・親の安定キーは MainWindow がヒエラルキーから埋める。送るのは MainWindow.ScreenPreview.cs）。
// ============================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SEEDEditor.Controls;
using SEEDEditor.Preview;
using SEEDEditor.Theme;

namespace SEEDEditor.Panels;

public partial class InspectorPanel
{
    // ── 文言 ────────────────────────────────────────────────

    /// <summary>帯の見出し。</summary>
    private const string PreviewBannerTitle = "プレビュー（保存されません）";

    /// <summary>帯のツールチップ（プレビューとは何か・どう直すか）。</summary>
    private const string PreviewBannerToolTip =
        "編集画面での確かめ用のプレビューです。シーンには保存されず、Play を始めると外れます（止めると戻ります）。" +
        "直すときはプレハブを開いて保存してください（保存するとプレビューにも反映されます）";

    /// <summary>プレビューの中の値の欄が押せない理由（ツールチップ）。</summary>
    private const string PreviewReadOnlyReason =
        "プレビューは読み取り専用です（シーンに保存されないため、ここで変えても残りません）。" +
        "直すときは上の［プレハブを開く］で元のプレハブを開いて保存してください";

    /// <summary>［プレハブを開く］の見出し。</summary>
    private const string PreviewOpenPrefabButtonText = "プレハブを開く";

    /// <summary>［プレハブを開く］の説明の書式（{0} = 中身のプレハブ）。</summary>
    private const string PreviewOpenPrefabToolTipFormat = "元のプレハブをアクタータブで開きます: {0}";

    /// <summary>［プレビューを消す］の見出し。</summary>
    private const string PreviewClearButtonText = "プレビューを消す";

    /// <summary>［プレビューを消す］の説明。</summary>
    private const string PreviewClearToolTip = "このプレビューを（中の入れ子ごと）消します。シーンは変わりません";

    /// <summary>帯のファイル名のツールチップの書式（{0} = 中身、{1} = 枠の行）。</summary>
    private const string PreviewSourceToolTipFormat = "中身: {0}{1}";

    /// <summary>帯のファイル名のツールチップの枠の行の書式（{0} = 枠）。</summary>
    private const string PreviewFrameLineFormat = "\n枠: {0}";

    /// <summary>差し込み先の案内の欄の見出し。</summary>
    private const string PreviewHostSectionTitle = "プレビュー";

    /// <summary>差し込み先の案内の欄の頭の説明の書式（{0} = 案内の見出し）。</summary>
    private const string PreviewHostSectionNoteFormat = "{0}: 編集画面での確かめ用に、保存されないプレビューを差し込みます";

    /// <summary>既定のプレハブのボタンの書式（{0} = ファイル名）。</summary>
    private const string PreviewHostDefaultButtonFormat = "{0} をプレビュー";

    /// <summary>［選ぶ...］の見出し。</summary>
    private const string PreviewHostPickButtonText = "選ぶ...";

    /// <summary>［選ぶ...］の説明。</summary>
    private const string PreviewHostPickToolTip = "プロジェクトのプレハブから選んで差し込みます";

    /// <summary>既定のプレハブも「選ぶ...」も無い行の案内。</summary>
    private const string PreviewHostNoPrefabText = "（中身のプレハブが設定されていません）";

    /// <summary>行のツールチップの差し込み先の行の書式（{0} = 差し込み先の見出し）。</summary>
    private const string PreviewHostTargetLineFormat = "\n{0}";

    /// <summary>アクタ名が無いときの見出しの書式（{0} = DFS 番号）。</summary>
    private const string PreviewUnnamedActorFormat = "Actor #{0}";

    /// <summary>ボタンの文字で「_」をそのまま出すための置き換え（ボタンはアクセスキーの印として食べる）。</summary>
    private const string PreviewAccessKeyMark = "_";

    /// <summary>「_」を文字として出すときの書き方。</summary>
    private const string PreviewEscapedAccessKeyMark = "__";

    // ── 寸法 ────────────────────────────────────────────────

    /// <summary>帯のアイコンの一辺（px）。</summary>
    private const double PreviewBannerIconSize = 14;

    /// <summary>帯の見出しの文字の大きさ。</summary>
    private const double PreviewBannerTitleFontSize = 12;

    /// <summary>帯のファイル名・ボタンの文字の大きさ。</summary>
    private const double PreviewBannerSmallFontSize = 11;

    /// <summary>帯の左端のアクセントの太さ（px）。</summary>
    private const double PreviewBannerAccentWidth = 3;

    /// <summary>帯の角の丸み（px）。</summary>
    private const double PreviewBannerCornerRadius = 2;

    /// <summary>帯の内側の余白。</summary>
    private static readonly Thickness PreviewBannerPadding = new(6, 4, 6, 4);

    /// <summary>帯の下の間隔（すぐ下のプレハブ参照バー・基本情報との間）。</summary>
    private static readonly Thickness PreviewBannerMargin = new(0, 0, 0, 4);

    /// <summary>アイコンと見出しの間隔。</summary>
    private static readonly Thickness PreviewBannerIconMargin = new(0, 0, 6, 0);

    /// <summary>ファイル名の行の上の間隔。</summary>
    private static readonly Thickness PreviewBannerSourceMargin = new(0, 2, 0, 0);

    /// <summary>ボタンの行の上の間隔。</summary>
    private static readonly Thickness PreviewBannerButtonsMargin = new(0, 4, 0, 0);

    /// <summary>ボタン同士の間隔（右側）。</summary>
    private static readonly Thickness PreviewButtonMargin = new(0, 0, 6, 2);

    /// <summary>小さなボタンの内側の余白。</summary>
    private static readonly Thickness PreviewButtonPadding = new(6, 1, 6, 1);

    /// <summary>案内の行の間隔。</summary>
    private static readonly Thickness PreviewHostRowMargin = new(0, 2, 0, 2);

    /// <summary>案内の行の見出しの右の間隔。</summary>
    private static readonly Thickness PreviewHostLabelMargin = new(0, 0, 8, 0);

    /// <summary>案内の欄の頭の説明の下の間隔。</summary>
    private static readonly Thickness PreviewHostNoteMargin = new(0, 0, 0, 4);

    /// <summary>案内の行の見出しの最小幅（行をそろえる）。</summary>
    private const double PreviewHostLabelMinWidth = 64;

    /// <summary>プレビューの帯の中へ出す案内の欄の上の間隔（帯のボタンの行との間）。</summary>
    private static readonly Thickness PreviewHostInBannerMargin = new(0, 6, 0, 0);

    // ── 色（実体は SeedColorTable。ボタンの色は共通書式のまま）──────────

    /// <summary>帯の地。</summary>
    private static readonly SolidColorBrush PreviewBannerBgBrush = MakeFrozenBrush(SeedThemeColors.PreviewBannerBg);

    /// <summary>帯の左端・アイコン・見出しの色。</summary>
    private static readonly SolidColorBrush PreviewBannerAccentBrush = MakeFrozenBrush(SeedThemeColors.PreviewBannerAccent);

    /// <summary>帯の本文（ファイル名）の色。</summary>
    private static readonly SolidColorBrush PreviewBannerTextBrush = MakeFrozenBrush(SeedThemeColors.PreviewBannerText);

    /// <summary>案内の補足の文字の色（ダイアログの補足と同じ）。</summary>
    private static readonly SolidColorBrush PreviewHostNoteBrush = MakeFrozenBrush(SeedThemeColors.DialogDimText);

    // ── 状態 ────────────────────────────────────────────────

    /// <summary>選んでいるアクタを含むプレビュー（外なら null）。ACTOR_COMPONENTS のたびに読み直す。</summary>
    private InspectorPreviewInfo? _currentPreview;

    /// <summary>いま出しているプレビューの帯（出していなければ null）。押せるまま残す子の見分けに使う。</summary>
    private UIElement? _previewBanner;

    /// <summary>
    /// いま出しているプレビューの帯の中身（出していなければ null）。
    /// プレビューの中のアクタでは、差し込み先の案内（入れ子のプレビューを足すだけで、プレビュー自身は変えない）を
    /// 読み取り専用で押せなくなるスクリプトの欄ではなく、押せるまま残る帯の中へ出すために使う。
    /// </summary>
    private StackPanel? _previewBannerBody;

    /// <summary>差し込み先の案内の表（MainWindow が渡す。渡されるまでは組み込みの表）。</summary>
    private PreviewHostCatalog _previewHosts = PreviewHostCatalog.BuiltIn();

    // ── MainWindow へ知らせる ─────────────────────────────────────

    /// <summary>［プレビューを消す］（引数は帯のプレビューの根の DFS 番号）。</summary>
    public event Action<int>? PreviewClearRequested;

    /// <summary>
    /// 差し込み先の案内のボタン（引数は差し込み先。世界線・親の名前と安定キーは MainWindow がヒエラルキーから埋める）。
    /// </summary>
    public event Action<PreviewInsertTarget>? PreviewHostRequested;

    /// <summary>
    /// 差し込み先の案内の表を差し替える（MainWindow.InitScreenPreview から）。
    /// </summary>
    /// <param name="catalog">読み込んだ表。</param>
    public void SetPreviewHosts(PreviewHostCatalog catalog) => _previewHosts = catalog;

    /// <summary>選んでいるアクタがプレビューの中か。</summary>
    private bool IsPreviewSelected => _currentPreview is not null;

    /// <summary>アコーディオンの子がプレビューの帯か（読み取り専用でも押せるまま残す）。</summary>
    /// <param name="child">アコーディオンの子。</param>
    /// <returns>帯なら true。</returns>
    private bool IsPreviewBanner(UIElement child) => _previewBanner is not null && ReferenceEquals(child, _previewBanner);

    // ============================================================
    //  帯
    // ============================================================

    /// <summary>
    /// ACTOR_COMPONENTS の根からプレビューの情報を読み、プレビューの中なら帯をアコーディオンの先頭へ足す
    /// （BuildActorComponentList がアコーディオンを空にした直後に呼ぶ）。
    /// </summary>
    /// <param name="root">ACTOR_COMPONENTS の根。</param>
    private void BeginPreviewBanner(JsonElement root)
    {
        _currentPreview    = InspectorPreviewInfo.Read(root);
        // 帯の中身は BuildPreviewBanner が作り直すときに控える（プレビューの外なら無し）
        _previewBannerBody = null;
        _previewBanner     = _currentPreview is { } info ? BuildPreviewBanner(info) : null;
        if (_previewBanner is not null) AccordionStack.Children.Add(_previewBanner);
    }

    /// <summary>選択なしになったら、プレビューの読み取り専用を解く（ShowNoSelection から）。</summary>
    private void ResetPreviewState()
    {
        _currentPreview    = null;
        _previewBanner     = null;
        _previewBannerBody = null;
        ApplyEditability();
    }

    /// <summary>
    /// プレビューの帯を作る（見出し → 中身のファイル名 → ［プレハブを開く］［プレビューを消す］）。
    /// </summary>
    /// <param name="info">プレビューの情報。</param>
    /// <returns>帯。</returns>
    private UIElement BuildPreviewBanner(InspectorPreviewInfo info)
    {
        var body = new StackPanel();

        // ── 見出し（アイコン + 「プレビュー（保存されません）」）──
        var titleRow = new StackPanel { Orientation = Orientation.Horizontal };
        var icon = AppIcon.Create("Icon.Node.Visible", PreviewBannerIconSize, PreviewBannerAccentBrush);
        icon.VerticalAlignment = VerticalAlignment.Center;
        icon.Margin            = PreviewBannerIconMargin;
        titleRow.Children.Add(icon);
        titleRow.Children.Add(new TextBlock
        {
            Text              = PreviewBannerTitle,
            Foreground        = PreviewBannerAccentBrush,
            FontSize          = PreviewBannerTitleFontSize,
            FontWeight        = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        body.Children.Add(titleRow);

        // ── 中身のファイル名（フルパスはツールチップ）──
        var fileName = Path.GetFileName(info.Prefab);
        body.Children.Add(new TextBlock
        {
            Text         = string.IsNullOrEmpty(fileName) ? info.Prefab : fileName,
            Foreground   = PreviewBannerTextBrush,
            FontSize     = PreviewBannerSmallFontSize,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin       = PreviewBannerSourceMargin,
            ToolTip      = string.Format(PreviewSourceToolTipFormat, info.Prefab,
                                         info.Frame is null ? "" : string.Format(PreviewFrameLineFormat, info.Frame)),
        });

        // ── ボタン（帯のボタンだけは読み取り専用でも押せる）──
        var buttons = new WrapPanel { Margin = PreviewBannerButtonsMargin };
        var open = MakePreviewButton(PreviewOpenPrefabButtonText, string.Format(PreviewOpenPrefabToolTipFormat, info.Prefab));
        open.IsEnabled = !string.IsNullOrWhiteSpace(info.Prefab);
        // 仮想パス → 絶対パスへ直し、無ければ知らせる（プレハブ参照バーと同じ道筋で ActorFileOpenRequested へ）
        open.Click += (_, _) => TryOpenPrefabSourcePath(info.Prefab);
        buttons.Children.Add(open);

        var clear = MakePreviewButton(PreviewClearButtonText, PreviewClearToolTip);
        clear.Click += (_, _) => PreviewClearRequested?.Invoke(info.RootDfs);
        buttons.Children.Add(clear);
        body.Children.Add(buttons);
        // 差し込み先の案内（入れ子のプレビュー）はこの中へ足す（AppendPreviewHostSection）
        _previewBannerBody = body;

        return new Border
        {
            Background      = PreviewBannerBgBrush,
            BorderBrush     = PreviewBannerAccentBrush,
            BorderThickness = new Thickness(PreviewBannerAccentWidth, 0, 0, 0),   // 左端だけアクセント
            CornerRadius    = new CornerRadius(PreviewBannerCornerRadius),
            Padding         = PreviewBannerPadding,
            Margin          = PreviewBannerMargin,
            ToolTip         = PreviewBannerToolTip,
            Child           = body,
        };
    }

    // ============================================================
    //  差し込み先の案内（ScreenStack・ModalHost・PopupPlane …）
    // ============================================================

    /// <summary>
    /// スクリプトが表に載っていれば、「プレビュー」の欄（行ごとに既定のプレハブ・選ぶ...）を足す。
    /// </summary>
    /// <param name="sp">スクリプトのスロットの中身（「スクリプトを編集」の後へ足す）。</param>
    /// <param name="info">スクリプトのスロット（ModelPath = 型名・パス、ScriptFieldsJson = 保存された欄の値）。</param>
    private void AppendPreviewHostSection(StackPanel sp, SlotInfo info)
    {
        if (_previewHosts.FindHost(info.ModelPath) is not { } host || host.Slots.Count == 0) return;

        // 欄の値はシーンに保存された [SerializeField]（保存されていない欄は表の既定）
        IReadOnlyDictionary<string, string> values = ParseScriptFieldValues(info.ScriptFieldsJson);

        var section = BuildSection(PreviewHostSectionTitle);
        var body    = (StackPanel)section.Child;
        body.Children.Add(new TextBlock
        {
            Text         = string.Format(PreviewHostSectionNoteFormat, host.Label),
            Foreground   = PreviewHostNoteBrush,
            FontSize     = PreviewBannerSmallFontSize,
            TextWrapping = TextWrapping.Wrap,
            Margin       = PreviewHostNoteMargin,
            ToolTip      = PreviewBannerToolTip,
        });
        foreach (var slot in host.Slots)
            body.Children.Add(BuildPreviewHostRow(PreviewHostCatalog.ResolveSlot(slot, values)));

        // プレビューの中のアクタ（例: プレビューした中央のポップアップの PopupPlane・プレビューしたシェルの中のスタック）は
        // スクリプトの欄ごと読み取り専用で押せなくなる。案内は入れ子のプレビューを足すだけでプレビュー自身は変えないので、
        // 押せるまま残る帯の中へ出す（ApplyEditability は帯を無効にしない）。
        if (IsPreviewSelected && _previewBannerBody is not null)
        {
            section.Margin = PreviewHostInBannerMargin;
            _previewBannerBody.Children.Add(section);
            return;
        }
        sp.Children.Add(section);
    }

    /// <summary>
    /// 案内の 1 行（見出し ＋［{既定のプレハブ} をプレビュー］［選ぶ...］）を作る。
    /// </summary>
    /// <param name="slot">欄の値を当てた差し込み先。</param>
    /// <returns>行。</returns>
    private UIElement BuildPreviewHostRow(ResolvedPreviewSlot slot)
    {
        var parentName = string.IsNullOrEmpty(_currentActorName)
            ? string.Format(PreviewUnnamedActorFormat, _currentActorId)
            : _currentActorName;
        var targetLabel = PreviewInsertTarget.DescribeLabel(parentName, slot.Under, slot.Frame is not null);

        var grid = new Grid { Margin = PreviewHostRowMargin };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        // ── 見出し（説明と差し込み先はツールチップ）──
        var label = new TextBlock
        {
            Text              = slot.Label,
            Foreground        = PreviewBannerTextBrush,
            FontSize          = PreviewBannerSmallFontSize,
            MinWidth          = PreviewHostLabelMinWidth,
            Margin            = PreviewHostLabelMargin,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip           = slot.Description + string.Format(PreviewHostTargetLineFormat, targetLabel),
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        // ── ボタン（既定のプレハブ・選ぶ...）──
        var buttons = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        if (slot.Prefab is { } prefab)
        {
            var button = MakePreviewButton(
                string.Format(PreviewHostDefaultButtonFormat, PreviewMenuModel.DisplayName(prefab)),
                prefab + string.Format(PreviewHostTargetLineFormat, targetLabel));
            button.Click += (_, _) => PreviewHostRequested?.Invoke(CreateHostTarget(slot, prefab, parentName, targetLabel));
            buttons.Children.Add(button);
        }
        if (slot.CanPick)
        {
            var pick = MakePreviewButton(PreviewHostPickButtonText,
                                         PreviewHostPickToolTip + string.Format(PreviewHostTargetLineFormat, targetLabel));
            pick.Click += (_, _) => PreviewHostRequested?.Invoke(CreateHostTarget(slot, prefab: null, parentName, targetLabel));
            buttons.Children.Add(pick);
        }
        if (buttons.Children.Count == 0)
        {
            buttons.Children.Add(new TextBlock
            {
                Text       = PreviewHostNoPrefabText,
                Foreground = PreviewHostNoteBrush,
                FontSize   = PreviewBannerSmallFontSize,
            });
        }
        Grid.SetColumn(buttons, 1);
        grid.Children.Add(buttons);
        return grid;
    }

    /// <summary>
    /// 案内の行から差し込み先を作る（親 = いま表示中のアクタ。世界線・安定キーは MainWindow が埋める）。
    /// </summary>
    /// <param name="slot">欄の値を当てた差し込み先。</param>
    /// <param name="prefab">中身のプレハブ（null なら窓で選ぶ）。</param>
    /// <param name="parentName">親の名前。</param>
    /// <param name="label">窓の見出し。</param>
    /// <returns>差し込み先。</returns>
    private PreviewInsertTarget CreateHostTarget(ResolvedPreviewSlot slot, string? prefab, string parentName, string label) => new()
    {
        ParentDfs  = _currentActorId,
        ParentName = parentName,
        Under      = slot.Under,
        Frame      = slot.Frame,
        FrameBody  = slot.FrameBody,
        LayerBias  = slot.LayerBias,
        Prefab     = prefab,
        Label      = label,
    };

    /// <summary>
    /// 帯・案内の小さなボタンを作る（見た目は共通書式の Seed.Button.Outlined。色は決めない）。
    /// </summary>
    /// <param name="text">見出し（「_」はそのまま出す）。</param>
    /// <param name="toolTip">ツールチップ。</param>
    /// <returns>ボタン。</returns>
    private static Button MakePreviewButton(string text, string toolTip)
    {
        var button = new Button
        {
            Content  = text.Replace(PreviewAccessKeyMark, PreviewEscapedAccessKeyMark),
            ToolTip  = toolTip,
            FontSize = PreviewBannerSmallFontSize,
            Padding  = PreviewButtonPadding,
            Margin   = PreviewButtonMargin,
        };
        SeedButtonStyle.Apply(button, SeedButtonStyle.OUTLINED);
        return button;
    }
}
