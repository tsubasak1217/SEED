using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SEEDEditor.Panels;

namespace SEEDEditor.Controls;

/// <summary>
/// 参照フィールド 1 本が要求する内容の宣言（データドリブンの記述部）。
/// 見た目と操作は <see cref="ReferencePicker"/> が一手に引き受けるため、
/// 各インスペクタはこの仕様だけを書けばよい。
/// </summary>
public sealed class ReferenceFieldSpec
{
    /// <summary>要求する種別名（<see cref="ReferenceKindCatalog"/> のキー）。</summary>
    public string Kind { get; init; } = ReferenceKindCatalog.GameObjectKind;

    /// <summary>
    /// 確定値がスロット名を含むか。
    ///
    /// true  … 「アクタ名 + スロット名」で保存する（例: Canvas の基準カメラ参照、
    ///          スクリプトのコンポーネント参照）。同種スロットが複数あれば選択ウィンドウを出す。
    /// false … 「アクタ名」だけで保存する（例: WaterVolume の制御点参照、WaterLink の接続先）。
    ///          保存側がスロットを表現できないため、複数あってもランタイム規約どおり
    ///          先頭スロットに解決される。選択ウィンドウは出さず、存在検証だけを行う。
    /// </summary>
    public bool WantSlotName { get; init; }

    /// <summary>行の左側に出す見出し（null なら見出し列を作らない＝外側の行レイアウトに任せる）。</summary>
    public string? Caption { get; init; }

    /// <summary>参照が未設定のときにドロップゾーンへ出す文言。</summary>
    public string UnsetText { get; init; } = "（未設定）";

    /// <summary>ドロップゾーンのツールチップ先頭に足す補足説明（null なら共通文のみ）。</summary>
    public string? ExtraTooltip { get; init; }

    /// <summary>✕ ボタンのツールチップ。</summary>
    public string ClearTooltip { get; init; } = "参照を解除する";
}

/// <summary>
/// ドラッグ中の可否判定の結果。
/// </summary>
public enum ReferenceDropVerdict
{
    /// <summary>要求型に適合するので受け付ける。</summary>
    Accept,

    /// <summary>適合コンポーネントがゼロなので受け付けない。</summary>
    Reject,

    /// <summary>手元の情報だけでは判断できない（ドロップ後に IPC で検証する）。</summary>
    Unknown,
}

/// <summary>
/// 参照ドロップの解決役。IPC を持つインスペクタ側が実装する。
/// </summary>
public interface IReferenceDropResolver
{
    /// <summary>
    /// ドラッグ中の可否を判定する（IPC を伴わない即時判定）。
    /// </summary>
    ReferenceDropVerdict Evaluate(IDataObject data, ReferenceFieldSpec spec);

    /// <summary>
    /// ドロップされたデータを解決し、確定したらコールバックを呼ぶ。
    /// 適合が複数あれば選択ウィンドウを挟むため、非同期に完了することがある。
    /// </summary>
    /// <param name="data">ドロップされたドラッグデータ。</param>
    /// <param name="spec">参照フィールドの要求仕様。</param>
    /// <param name="apply">確定値（アクタ名, スロット名 or null）を適用するコールバック。</param>
    void Resolve(IDataObject data, ReferenceFieldSpec spec, Action<string, string?> apply);
}

/// <summary>
/// シーン内のアクタ／コンポーネント参照を設定する共通ピッカー行。
///
/// エディタ内の参照フィールドはすべてこのコントロールへ集約する。提供する挙動:
///   ・現在の参照先表示（"アクタ名" / "アクタ名 / スロット名"、未設定は仕様の文言）
///   ・Hierarchy のアクタ行、シーンビューのアクタ、インスペクタのコンポーネントヘッダの
///     いずれからでも D&amp;D で設定できる
///   ・ドラッグ中に適合ゼロなら枠を赤くしてドロップを拒否する
///   ・✕ ボタンで参照解除
///   ・ダブルクリックで Hierarchy の参照先アクタへジャンプ
///   ・参照先アクタが見つからないときは ⚠ を出して知らせる
/// </summary>
internal sealed class ReferencePicker
{
    // ── レイアウト定数 ────────────────────────────────────────
    private const double CaptionWidth   = 90;
    private const double ClearButtonWidth = 22;

    /// <summary>解除ボタンのアイコン一辺サイズ（px）。</summary>
    private const double ClearButtonIconSize = 10;
    private const double LabelFontSize  = 11;

    // ── 配色 ─────────────────────────────────────────────────
    private static readonly Brush BrushBorderNormal = new SolidColorBrush(Color.FromRgb(0x55, 0x77, 0x99));
    private static readonly Brush BrushBorderAccept = new SolidColorBrush(Color.FromRgb(0x55, 0xCC, 0x77));
    private static readonly Brush BrushBorderReject = new SolidColorBrush(Color.FromRgb(0xCC, 0x55, 0x55));
    private static readonly Brush BrushZoneBg       = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
    private static readonly Brush BrushValue        = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly Brush BrushUnset        = new SolidColorBrush(Color.FromRgb(0x77, 0x77, 0x77));
    private static readonly Brush BrushWarn         = new SolidColorBrush(Color.FromRgb(0xEE, 0xAA, 0x44));
    private static readonly Brush BrushCaption      = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
    private static readonly Brush BrushClear        = new SolidColorBrush(Color.FromRgb(0xAA, 0x55, 0x55));
    private static readonly Brush BrushClearBg      = new SolidColorBrush(Color.FromRgb(0x22, 0x22, 0x22));
    private static readonly Brush BrushClearBorder  = new SolidColorBrush(Color.FromRgb(0x44, 0x22, 0x22));

    /// <summary>参照先が見つからないときにラベル頭へ付ける印。</summary>
    private const string MissingMarker = "⚠ ";

    private readonly ReferenceFieldSpec        _spec;
    private readonly Action<string?, string?>  _onCommit;
    private readonly TextBlock                 _label;
    private readonly Border                    _dropZone;
    private readonly Button                    _clearButton;

    /// <summary>現在の参照先アクタ名（未設定なら空文字列）。</summary>
    public string ActorName { get; private set; } = "";

    /// <summary>現在の参照先スロット名（スロットを持たない参照・未設定なら空文字列）。</summary>
    public string SlotName { get; private set; } = "";

    /// <summary>
    /// インスペクタへ差し込む行本体。
    /// 余白は呼び出し側の行レイアウトに合わせて Margin を設定してよい。
    /// </summary>
    public FrameworkElement Element { get; }

    private ReferencePicker(
        ReferenceFieldSpec spec, string? actorName, string? slotName,
        Action<string?, string?> onCommit, IReferenceDropResolver? resolver)
    {
        _spec     = spec;
        _onCommit = onCommit;

        // ── 現在値ラベル ──────────────────────────────────────
        _label = new TextBlock
        {
            FontSize          = LabelFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming      = TextTrimming.CharacterEllipsis,
        };

        // ── ドロップゾーン ────────────────────────────────────
        _dropZone = new Border
        {
            BorderBrush     = BrushBorderNormal,
            BorderThickness = new Thickness(1),
            Background      = BrushZoneBg,
            CornerRadius    = new CornerRadius(3),
            Padding         = new Thickness(6, 3, 6, 3),
            AllowDrop       = resolver is not null,
            Child           = _label,
            Cursor          = Cursors.Hand,
            ToolTip         = BuildTooltip(spec),
        };

        // ── 解除（クリア）ボタン ─────────────────────────────
        _clearButton = new Button
        {
            Content         = AppIcon.Create("Icon.Close", ClearButtonIconSize),
            Width           = ClearButtonWidth,
            Foreground      = BrushClear,
            Background      = BrushClearBg,
            BorderBrush     = BrushClearBorder,
            BorderThickness = new Thickness(1),
            Margin          = new Thickness(3, 0, 0, 0),
            ToolTip         = spec.ClearTooltip,
        };
        _clearButton.Click += (_, _) => Commit(null, null);

        // ── 行レイアウト（[見出し] ドロップゾーン ✕）───────────
        var grid = new Grid();
        if (spec.Caption is not null)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var caption = new TextBlock
            {
                Text              = spec.Caption,
                Foreground        = BrushCaption,
                FontSize          = LabelFontSize,
                Width             = CaptionWidth,
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(caption, 0);
            grid.Children.Add(caption);
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var zoneColumn = spec.Caption is null ? 0 : 1;
        Grid.SetColumn(_dropZone,    zoneColumn);
        Grid.SetColumn(_clearButton, zoneColumn + 1);
        grid.Children.Add(_dropZone);
        grid.Children.Add(_clearButton);
        Element = grid;

        // ── ダブルクリックで Hierarchy の参照先へジャンプ ──────
        // 参照は張り替わるため、現在値から都度読む。
        ActorRefJump.AttachDoubleClickReveal(_dropZone, () =>
            string.IsNullOrEmpty(ActorName) ? null : ActorName);

        // ── D&D 受け入れ ─────────────────────────────────────
        if (resolver is not null)
            AttachDragDrop(resolver);

        SetReference(actorName, slotName);
    }

    /// <summary>
    /// 参照ピッカーを 1 本生成する。
    /// </summary>
    /// <param name="spec">要求仕様。</param>
    /// <param name="actorName">初期の参照先アクタ名（未設定なら null/空）。</param>
    /// <param name="slotName">初期の参照先スロット名（無ければ null/空）。</param>
    /// <param name="onCommit">
    /// 参照が確定・解除されたときの通知。解除時は (null, null) が渡る。
    /// ここで IPC を送る（＝ランタイム共通の Undo 機構に自動で載る）。
    /// </param>
    /// <param name="resolver">ドロップ解決役。null ならドロップを受け付けない（表示専用）。</param>
    public static ReferencePicker Create(
        ReferenceFieldSpec spec, string? actorName, string? slotName,
        Action<string?, string?> onCommit, IReferenceDropResolver? resolver)
        => new(spec, actorName, slotName, onCommit, resolver);

    /// <summary>
    /// 表示だけを更新する（IPC は送らない）。
    /// ランタイムからの再送で外側の値が変わったときに使う。
    /// </summary>
    public void SetReference(string? actorName, string? slotName)
    {
        ActorName = actorName ?? "";
        SlotName  = slotName  ?? "";
        RefreshLabel();
    }

    /// <summary>値を確定してコールバックへ流し、表示を更新する。</summary>
    private void Commit(string? actorName, string? slotName)
    {
        // 既に同じ値なら IPC を送らない（再送によるログ・Undo の水増しを防ぐ）
        var newActor = actorName ?? "";
        var newSlot  = slotName  ?? "";
        if (newActor == ActorName && newSlot == SlotName) return;

        ActorName = newActor;
        SlotName  = newSlot;
        RefreshLabel();
        _onCommit(string.IsNullOrEmpty(newActor) ? null : newActor,
                  string.IsNullOrEmpty(newSlot)  ? null : newSlot);
    }

    /// <summary>現在値からラベル文言・色・警告表示を作り直す。</summary>
    private void RefreshLabel()
    {
        bool hasRef = !string.IsNullOrEmpty(ActorName);
        if (!hasRef)
        {
            _label.Text        = _spec.UnsetText;
            _label.Foreground  = BrushUnset;
            _label.ToolTip     = null;
            _clearButton.IsEnabled = false;
            return;
        }

        var display = string.IsNullOrEmpty(SlotName) ? ActorName : $"{ActorName} / {SlotName}";

        // 参照先アクタがシーンに見つからない場合は警告表示にする
        // （Hierarchy 未接続＝判定不能のときは警告を出さない）。
        bool missing = ActorRefJump.ActorExistsByName is { } exists && !exists(ActorName);
        _label.Text       = missing ? MissingMarker + display : display;
        _label.Foreground = missing ? BrushWarn : BrushValue;
        _label.ToolTip    = missing
            ? $"参照先のアクタ「{ActorName}」がシーンに見つかりません。\n"
            + "アクタが削除された／別のシーンにある可能性があります。"
            : null;
        _clearButton.IsEnabled = true;
    }

    /// <summary>ドロップゾーンのツールチップ文言を組み立てる。</summary>
    private static string BuildTooltip(ReferenceFieldSpec spec)
    {
        var head = spec.ExtraTooltip is null ? "" : spec.ExtraTooltip + "\n";
        return head
             + $"要求する型: {ReferenceKindCatalog.DisplayName(spec.Kind)}\n"
             + "Hierarchy のアクタ行、またはインスペクタのコンポーネント見出しを\n"
             + "ドロップして参照を設定します（適合しないものは受け付けません）\n"
             + "ダブルクリックで Hierarchy の参照先へジャンプ";
    }

    /// <summary>ドロップゾーンへ D&amp;D のイベントを結線する。</summary>
    private void AttachDragDrop(IReferenceDropResolver resolver)
    {
        // ドラッグ中の可否表示。
        // Hierarchy 側は DoDragDrop を Move|Copy で呼ぶため、Move を返さないと
        // Effects の AND が None になりドロップ自体が成立しない。
        void OnDragOver(object sender, DragEventArgs e)
        {
            var verdict = ReferenceDragData.HasReferenceSource(e.Data)
                ? resolver.Evaluate(e.Data, _spec)
                : ReferenceDropVerdict.Reject;

            switch (verdict)
            {
                case ReferenceDropVerdict.Accept:
                    e.Effects = DragDropEffects.Move;
                    _dropZone.BorderBrush = BrushBorderAccept;
                    break;
                case ReferenceDropVerdict.Unknown:
                    // 判定材料が無いので受け付け、ドロップ後に IPC で検証する
                    e.Effects = DragDropEffects.Move;
                    _dropZone.BorderBrush = BrushBorderNormal;
                    break;
                default:
                    // 適合ゼロ: カーソルを禁止表示にし、枠も赤くして理由を示す
                    e.Effects = DragDropEffects.None;
                    _dropZone.BorderBrush = BrushBorderReject;
                    break;
            }
            e.Handled = true;
        }

        _dropZone.DragEnter += OnDragOver;
        _dropZone.DragOver  += OnDragOver;
        _dropZone.DragLeave += (_, _) => _dropZone.BorderBrush = BrushBorderNormal;
        _dropZone.Drop      += (_, e) =>
        {
            _dropZone.BorderBrush = BrushBorderNormal;
            e.Handled = true;
            if (!ReferenceDragData.HasReferenceSource(e.Data)) return;
            if (resolver.Evaluate(e.Data, _spec) == ReferenceDropVerdict.Reject) return;
            resolver.Resolve(e.Data, _spec, (actor, slot) => Commit(actor, slot));
        };
    }
}
