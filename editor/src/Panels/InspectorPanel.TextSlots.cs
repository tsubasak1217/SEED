using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace SEEDEditor.Panels;

/// <summary>
/// InspectorPanel の「Text コンポーネント 差し込みスロット欄」実装。
///
/// 本文（内容）に書かれたプレースホルダ記法（<c>{image}</c> / <c>{color}〜{/color}</c> /
/// <c>{string}</c> / <c>{num}</c> / <c>{num.3}</c> / <c>{image h=1.4}</c> / 番号指定）に対応する
/// 「値の入力欄」を、本文欄の直下に並べる。
///
/// 【設計上の原則 ─ 記法の解析はランタイムだけが行う】
/// エディタは本文の文字列を一切解析しない。どのスロットが何番で・どの種類で・
/// 小数桁や高さ倍率がいくつかは、ランタイムが <c>ACTOR_COMPONENTS</c> の
/// <c>"text_slots"</c> 配列として送ってくる情報が**唯一の正典**である。
/// ここでは届いた配列を <c>index</c> の順にそのまま行へ落とすだけで、
/// 記法の規則を C# 側にミラーしない（二重実装は必ず食い違うため）。
///
/// 【本文を編集したとき】
/// 本文欄の確定でランタイムがスロットを再マッピングし、
/// <c>ACTOR_COMPONENTS</c> を再送する。インスペクタはそれを受けて丸ごと再構築されるので、
/// スロット欄の並び替え・増減はこの経路だけで完結する
/// （エディタ側で旧スロットと新スロットを対応付ける処理は持たない）。
///
/// 【送信】
/// 値の変更はすべて Text セクション共通の <c>SET_TEXT_FIELD:{actor},{slot},{key},{value}</c>
/// に載せる。キーは <c>slot.{添字}.{フィールド}</c>（<c>path</c> / <c>rgba</c> /
/// <c>bind</c> / <c>text</c> / <c>num</c>）で、Undo はキー単位で既存経路が拾う。
/// </summary>
public partial class InspectorPanel
{
    // ============================================================
    //  ワイヤ表現の定数（ランタイムの綴りと 1 対 1）
    // ============================================================

    /// <summary>スロット配列の JSON キー（ACTOR_COMPONENTS の TextComponent 要素）。</summary>
    internal const string TextSlotsJsonKey = "text_slots";

    /// <summary>スロットが空のときに使う JSON（キーが無い旧シーンでも落ちないように）。</summary>
    internal const string TextSlotsEmptyJson = "[]";

    /// <summary>種類「画像」（<c>{image}</c>）。</summary>
    private const string TextSlotKindImage = "image";

    /// <summary>種類「色」（<c>{color}〜{/color}</c>）。</summary>
    private const string TextSlotKindColor = "color";

    /// <summary>種類「文字列」（<c>{string}</c>）。</summary>
    private const string TextSlotKindString = "string";

    /// <summary>種類「数値」（<c>{num}</c> / <c>{num.3}</c>）。</summary>
    private const string TextSlotKindNum = "num";

    /// <summary>小数桁「指定なし」を表す値（ランタイムの TEXT_SLOT_NO_FORMAT と対）。</summary>
    private const int TextSlotNoFormat = -1;

    /// <summary>高さ倍率「指定なし」を表す値（ランタイムの TEXT_SLOT_NO_HEIGHT と対）。</summary>
    private const float TextSlotNoHeight = 0f;

    /// <summary>フィールドキーの接頭辞（<c>slot.0.num</c> の <c>slot.</c>）。</summary>
    private const string TextSlotKeyPrefix = "slot.";

    /// <summary>画像パスのフィールド名（<c>slot.{i}.path</c>）。</summary>
    private const string TextSlotFieldPath = "path";

    /// <summary>色のフィールド名（<c>slot.{i}.rgba</c>）。</summary>
    private const string TextSlotFieldRgba = "rgba";

    /// <summary>バインドのフィールド名（<c>slot.{i}.bind</c>）。</summary>
    private const string TextSlotFieldBind = "bind";

    /// <summary>文字列フォールバックのフィールド名（<c>slot.{i}.text</c>）。</summary>
    private const string TextSlotFieldText = "text";

    /// <summary>数値フォールバックのフィールド名（<c>slot.{i}.num</c>）。</summary>
    private const string TextSlotFieldNum = "num";

    /// <summary>色スロットの成分数（RGBA）。</summary>
    private const int TextSlotColorComponents = 4;

    /// <summary>
    /// 画像パスとアイコン名を見分けるスキーム区切り。
    /// これを含めば <c>assets://</c> 等の直接指定、含まなければアイコンセットのアイコン名
    /// （判定規則はランタイムの <c>resolve_slot_image</c> と同じ）。
    /// </summary>
    private const string TextSlotSchemeSeparator = "://";

    // ============================================================
    //  見た目の定数
    // ============================================================

    /// <summary>「スロット」小見出しの文言。</summary>
    private const string TextSlotsHeaderText = "スロット";

    /// <summary>小見出しの文字サイズ（px）。</summary>
    private const double TextSlotsHeaderFontSize = 11;

    /// <summary>小見出しの上下余白。</summary>
    private static readonly Thickness TextSlotsHeaderMargin = new(0, 6, 0, 2);

    /// <summary>スロット行をまとめる領域の余白（小見出しからのぶら下がりを示す左インデント）。</summary>
    private static readonly Thickness TextSlotsBodyMargin = new(6, 0, 0, 4);

    /// <summary>行ラベル（<c>{image} #0</c>）の列幅（px）。バインド行の見出し幅と揃える。</summary>
    private const double TextSlotCaptionWidth = 90;

    /// <summary>フォールバック値入力欄の幅（px）。バインド行の右隣に置く。</summary>
    private const double TextSlotFallbackWidth = 96;

    /// <summary>行の文字サイズ（px）。</summary>
    private const double TextSlotFontSize = 11;

    /// <summary>行どうしの上下余白。</summary>
    private static readonly Thickness TextSlotRowMargin = new(0, 2, 0, 2);

    /// <summary>入力欄・切替ボタンの左余白。</summary>
    private static readonly Thickness TextSlotSideMargin = new(4, 0, 0, 0);

    /// <summary>入力欄の内側余白。</summary>
    private static readonly Thickness TextSlotInputPadding = new(3, 1, 3, 1);

    /// <summary>小見出しの文字色（他セクションの見出しと同色）。</summary>
    private static readonly Brush TextSlotsHeaderBrush = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));

    /// <summary>行ラベルの文字色。</summary>
    private static readonly Brush TextSlotCaptionBrush = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    /// <summary>切替ボタンの背景色（既存の「参照」ボタンと同色）。</summary>
    private static readonly Brush TextSlotButtonBackground = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));

    /// <summary>切替ボタンの文字色（既存の「参照」ボタンと同色）。</summary>
    private static readonly Brush TextSlotButtonForeground = new SolidColorBrush(Color.FromRgb(0xBB, 0xBB, 0xBB));

    /// <summary>切替ボタンの枠色（既存の「参照」ボタンと同色）。</summary>
    private static readonly Brush TextSlotButtonBorder = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44));

    /// <summary>切替ボタンの枠の太さ（px）。</summary>
    private const double TextSlotButtonBorderThickness = 1;

    /// <summary>入力欄の高さを他の行と揃えるための最小高さ（px）。</summary>
    private const double TextSlotInputMinHeight = 18;

    /// <summary>種類切替ボタン（画像パス ⇔ アイコン名）の文字サイズ（px）。</summary>
    private const double TextSlotToggleFontSize = 10;

    /// <summary>種類切替ボタンの内側余白。</summary>
    private static readonly Thickness TextSlotTogglePadding = new(6, 1, 6, 1);

    /// <summary>画像スロットが受け付ける画像ファイルの拡張子（Sprite のテクスチャ行と同一）。</summary>
    private static readonly string[] TextSlotImageExtensions =
        [".png", ".jpg", ".jpeg", ".bmp", ".tga", ".webp"];

    /// <summary>画像選択ダイアログのタイトル。</summary>
    private const string TextSlotImageDialogTitle = "差し込む画像を選択";

    /// <summary>画像選択ダイアログのフィルタ。</summary>
    private const string TextSlotImageDialogFilter =
        "画像ファイル|*.png;*.jpg;*.jpeg;*.bmp;*.tga;*.webp|すべてのファイル|*.*";

    /// <summary>「アイコン名で指定」へ切り替えるボタンの文言。</summary>
    private const string TextSlotToggleToIconText = "アイコン名";

    /// <summary>「画像ファイルで指定」へ切り替えるボタンの文言。</summary>
    private const string TextSlotToggleToFileText = "画像ファイル";

    /// <summary>種類切替ボタンのツールチップ。</summary>
    private const string TextSlotToggleTooltip =
        "画像ファイル参照と、アイコンセット（.icons）内のアイコン名指定を切り替えます";

    /// <summary>アイコン名入力欄のツールチップ。</summary>
    private const string TextSlotIconNameTooltip =
        "アイコンセット（.icons）に登録されたアイコン名。上の「アイコンセット」行で .icons を指定しておくこと";

    /// <summary>文字列フォールバック欄のツールチップ。</summary>
    private const string TextSlotTextFallbackTooltip =
        "バインドが未設定・解決できないときに差し込む文字列";

    /// <summary>数値フォールバック欄のツールチップ。</summary>
    private const string TextSlotNumFallbackTooltip =
        "バインドが未設定・解決できないときに差し込む数値";

    /// <summary>未対応の種類が届いたときに行へ出す注記。</summary>
    private const string TextSlotUnknownKindNote = "（未対応の種類）";

    /// <summary>バインド行に渡す種別の説明（ツールチップに出る）。</summary>
    private const string TextSlotBindDescription = "差し込みスロット";

    /// <summary>数値フォールバックの表示書式（末尾の余分な 0 を出さない）。</summary>
    private const string TextSlotNumFormat = "0.####";

    /// <summary>高さ倍率をラベルへ出すときの表示書式。</summary>
    private const string TextSlotHeightFormat = "0.##";

    // ============================================================
    //  ランタイムから届くスロット 1 件
    // ============================================================

    /// <summary>
    /// <c>"text_slots"</c> 配列の 1 要素。フィールド名はランタイムの JSON と 1 対 1。
    /// </summary>
    /// <param name="Index">スロットの添字（IPC キー <c>slot.{Index}.*</c> に使う）。</param>
    /// <param name="Kind">種類（image / color / string / num）。</param>
    /// <param name="Format">数値の小数桁（指定が無ければ <see cref="TextSlotNoFormat"/>）。</param>
    /// <param name="Height">画像の高さ倍率（指定が無ければ <see cref="TextSlotNoHeight"/>）。</param>
    /// <param name="Path">画像スロットの値（assets:// パス、またはアイコン名）。</param>
    /// <param name="R">色スロットの赤成分（0..1）。</param>
    /// <param name="G">色スロットの緑成分（0..1）。</param>
    /// <param name="B">色スロットの青成分（0..1）。</param>
    /// <param name="A">色スロットのアルファ（0..1）。</param>
    /// <param name="Bind">バインド文字列（"アクタ名|スロット名|変数名"。空 = 未設定）。</param>
    /// <param name="BindOk">バインドが今解決できているか（false なら ⚠ を出す）。</param>
    /// <param name="Text">文字列スロットのフォールバック値。</param>
    /// <param name="Num">数値スロットのフォールバック値。</param>
    private sealed record TextSlotRow(
        int    Index,
        string Kind,
        int    Format,
        float  Height,
        string Path,
        float  R, float G, float B, float A,
        string Bind,
        bool   BindOk,
        string Text,
        float  Num);

    /// <summary>
    /// <c>"text_slots"</c> 配列の JSON を解析する。
    /// 壊れていれば空リストを返す（インスペクタを落とさず「スロット無し」として扱う）。
    /// </summary>
    private static List<TextSlotRow> ParseTextSlots(string json)
    {
        var result = new List<TextSlotRow>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

            foreach (var e in doc.RootElement.EnumerateArray())
            {
                // 添字と種類が読めない要素は IPC キーを組み立てられないので捨てる。
                if (!e.TryGetProperty("index", out var idxEl) ||
                    idxEl.ValueKind != JsonValueKind.Number) continue;
                var index = idxEl.GetInt32();
                var kind  = e.TryGetProperty("kind", out var kEl) ? kEl.GetString() ?? "" : "";
                if (string.IsNullOrEmpty(kind)) continue;

                // 色は 4 成分の配列。欠けている・数が合わない場合は不透明な白にする
                // （ランタイム側の既定色と同じ。見えない色にして混乱させない）。
                float r = 1f, g = 1f, b = 1f, a = 1f;
                if (e.TryGetProperty("rgba", out var rgbaEl) &&
                    rgbaEl.ValueKind == JsonValueKind.Array &&
                    rgbaEl.GetArrayLength() == TextSlotColorComponents)
                {
                    r = rgbaEl[0].GetSingle();
                    g = rgbaEl[1].GetSingle();
                    b = rgbaEl[2].GetSingle();
                    a = rgbaEl[3].GetSingle();
                }

                result.Add(new TextSlotRow(
                    Index:  index,
                    Kind:   kind,
                    Format: e.TryGetProperty("format", out var fEl) ? fEl.GetInt32()  : TextSlotNoFormat,
                    Height: e.TryGetProperty("h",      out var hEl) ? hEl.GetSingle() : TextSlotNoHeight,
                    Path:   e.TryGetProperty("path",   out var pEl) ? pEl.GetString() ?? "" : "",
                    R: r, G: g, B: b, A: a,
                    Bind:   e.TryGetProperty("bind",    out var bEl)  ? bEl.GetString() ?? "" : "",
                    BindOk: !e.TryGetProperty("bind_ok", out var boEl) || ReadJsonBool(boEl, true),
                    Text:   e.TryGetProperty("text",    out var tEl)  ? tEl.GetString() ?? "" : "",
                    Num:    e.TryGetProperty("num",     out var nEl)  ? nEl.GetSingle() : 0f));
            }
        }
        catch (JsonException ex)
        {
            EditorLog.Write($"InspectorPanel: text_slots を解析できません: {ex.Message}");
        }
        return result;
    }

    // ============================================================
    //  セクションの組み立て
    // ============================================================

    /// <summary>
    /// 差し込みスロット欄（小見出し＋各スロットの行）を組み立てる。
    /// スロットが 1 件も無ければ <c>null</c>（＝何も出さない）。
    /// </summary>
    /// <param name="info">TextComponent のスロット情報（<c>TextSlotsJson</c> を読む）。</param>
    /// <param name="sendField">
    /// Text セクション共通の送信処理（<c>SET_TEXT_FIELD</c>）。
    /// キーの組み立てはこちらで行い、宛先アクタ・スロット番号の解決は呼び出し側に任せる。
    /// </param>
    private UIElement? BuildTextSlotsSection(SlotInfo info, Action<string, string> sendField)
    {
        var slots = ParseTextSlots(info.TextSlotsJson);
        if (slots.Count == 0) return null;

        var section = new StackPanel();
        section.Children.Add(new TextBlock
        {
            Text       = TextSlotsHeaderText,
            Foreground = TextSlotsHeaderBrush,
            FontSize   = TextSlotsHeaderFontSize,
            Margin     = TextSlotsHeaderMargin,
        });

        var body = new StackPanel { Margin = TextSlotsBodyMargin };
        // ランタイムが index 順で送ってくる。並べ替えも対応付けもせず、届いた順のまま積む。
        foreach (var slot in slots)
            body.Children.Add(BuildTextSlotRow(info, slot, sendField));
        section.Children.Add(body);

        return section;
    }

    /// <summary>
    /// スロット 1 件の行を、種類に応じて組み立てる。
    /// 未知の種類（ランタイムだけが先に増えた場合）は説明だけの行にして落とさない。
    /// </summary>
    private UIElement BuildTextSlotRow(SlotInfo info, TextSlotRow slot, Action<string, string> sendField)
    {
        var caption = BuildTextSlotCaption(slot);
        return slot.Kind switch
        {
            TextSlotKindImage  => BuildTextSlotImageRow(slot, caption, sendField),
            TextSlotKindColor  => BuildTextSlotColorRow(info, slot, caption, sendField),
            TextSlotKindString => BuildTextSlotBindRow(info, slot, caption, isNumeric: false, sendField),
            TextSlotKindNum    => BuildTextSlotBindRow(info, slot, caption, isNumeric: true,  sendField),
            _                  => new TextBlock
            {
                Text       = caption + TextSlotUnknownKindNote,
                Foreground = TextSlotCaptionBrush,
                FontSize   = TextSlotFontSize,
                Margin     = TextSlotRowMargin,
            },
        };
    }

    /// <summary>
    /// 行ラベルを作る（例: <c>{image h=1.4} #0</c> / <c>{num.3} #2</c>）。
    /// 本文にどう書かれているかを思い出せるよう、記法の形をそのまま見せる。
    /// </summary>
    private static string BuildTextSlotCaption(TextSlotRow slot)
    {
        var markup = slot.Kind;
        // 数値の小数桁指定（{num.3}）。指定が無ければ何も付けない。
        if (slot.Kind == TextSlotKindNum && slot.Format > TextSlotNoFormat)
            markup = slot.Kind + "." + slot.Format.ToString(CultureInfo.InvariantCulture);
        // 画像の高さ倍率（{image h=1.4}）。0 は「指定なし」なので出さない。
        else if (slot.Kind == TextSlotKindImage && slot.Height > TextSlotNoHeight)
            markup = slot.Kind + " h=" + slot.Height.ToString(TextSlotHeightFormat, CultureInfo.InvariantCulture);

        return "{" + markup + "} #" + slot.Index.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>スロット 1 件のフィールドキー（<c>slot.{添字}.{フィールド}</c>）を作る。</summary>
    private static string TextSlotKey(int index, string field)
        => TextSlotKeyPrefix + index.ToString(CultureInfo.InvariantCulture) + "." + field;

    // ============================================================
    //  種類ごとの行
    // ============================================================

    /// <summary>
    /// 画像スロットの行。
    ///
    /// 値（<c>slot.{i}.path</c>）は「画像ファイルの assets:// パス」と
    /// 「アイコンセット内のアイコン名」のどちらでもよく、区別はスキーム区切り
    /// （<c>://</c>）の有無だけで付く（判定の正典はランタイム）。
    /// そのため 1 本の値に対して入力手段を 2 つ用意し、ボタンで切り替える:
    ///   - 画像ファイル: <see cref="FileRefBuilder"/> の参照行（Project からの D&amp;D ＋ 参照ボタン）
    ///   - アイコン名  : 1 行テキスト欄
    /// </summary>
    private UIElement BuildTextSlotImageRow(TextSlotRow slot, string caption, Action<string, string> sendField)
    {
        var key = TextSlotKey(slot.Index, TextSlotFieldPath);

        // ── ファイル参照欄（ラベル列はこの行の見出しが担うので幅 0 で潰す）──
        var fileRow = FileRefBuilder.Build(
            "", slot.Path, TextSlotImageExtensions,
            () =>
            {
                var dlg = new OpenFileDialog
                {
                    Title  = TextSlotImageDialogTitle,
                    Filter = TextSlotImageDialogFilter,
                };
                return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
            },
            path =>
            {
                if (_currentActorId < 0) return;
                // 絶対パス → assets:// 仮想パス（他のファイル参照行と同じ流儀）。
                sendField(key, VirtualPath.ToVirtual(path, _assetsPath));
            },
            // × で未設定へ戻す（本文の記法はそのまま・値だけ空になる）。
            () => sendField(key, ""),
            labelWidth: 0);

        // ── アイコン名欄 ──
        var iconBox = MakeTextSlotInput(slot.Path, TextSlotIconNameTooltip);
        // 確定はフォーカスが外れた時点と Enter（1 文字ごとに IPC を撃たない）。
        iconBox.LostFocus += (_, _) => sendField(key, iconBox.Text);
        iconBox.KeyDown   += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            sendField(key, iconBox.Text);
            e.Handled = true;
        };

        // ── 表示モード（現在値から推定し、ボタンで切り替える）──
        // 空欄のときはファイル参照を既定にする（D&D で入れるのが最短のため）。
        var isIconMode = !string.IsNullOrEmpty(slot.Path) &&
                         !slot.Path.Contains(TextSlotSchemeSeparator, StringComparison.Ordinal);

        var toggle = new Button
        {
            FontSize          = TextSlotToggleFontSize,
            Padding           = TextSlotTogglePadding,
            Margin            = TextSlotSideMargin,
            Cursor            = Cursors.Hand,
            Background        = TextSlotButtonBackground,
            Foreground        = TextSlotButtonForeground,
            BorderBrush       = TextSlotButtonBorder,
            BorderThickness   = new Thickness(TextSlotButtonBorderThickness),
            VerticalAlignment = VerticalAlignment.Center,
            Template          = FileRefBuilder.BuildButtonTemplate(),
            ToolTip           = TextSlotToggleTooltip,
        };

        // 表示だけを切り替える（値は送らない。切り替え＝入力手段の選択にすぎない）。
        void ApplyMode()
        {
            fileRow.Visibility = isIconMode ? Visibility.Collapsed : Visibility.Visible;
            iconBox.Visibility = isIconMode ? Visibility.Visible   : Visibility.Collapsed;
            toggle.Content     = isIconMode ? TextSlotToggleToFileText : TextSlotToggleToIconText;
        }
        ApplyMode();
        toggle.Click += (_, _) => { isIconMode = !isIconMode; ApplyMode(); };

        // 入力手段は同じ場所に重ねて置き、Visibility だけで出し分ける。
        var content = new Grid();
        content.Children.Add(fileRow);
        content.Children.Add(iconBox);

        return BuildTextSlotGrid(caption, content, toggle);
    }

    /// <summary>
    /// 色スロットの行。既存のカラーピッカー行をそのまま使い、
    /// 確定色を <c>slot.{i}.rgba</c> へ <c>r,g,b,a</c> の形で送る。
    ///
    /// ⟲（既定値に戻す）は付けない。汎用リセット（RESET_COMPONENT_FIELD）は
    /// serde のフィールド名で引く仕組みで、<c>slot.{i}.rgba</c> のような合成キーには
    /// 対応していないため、置いても無反応なボタンになるからである。
    /// </summary>
    private UIElement BuildTextSlotColorRow(
        SlotInfo info, TextSlotRow slot, string caption, Action<string, string> sendField)
    {
        var key = TextSlotKey(slot.Index, TextSlotFieldRgba);
        return BuildColorPickerRow(
            caption, slot.R, slot.G, slot.B, slot.A,
            info.SlotIdx, TextComponentType, field: null,
            (r, g, b, a) => sendField(key, FormattableString.Invariant($"{r},{g},{b},{a}")),
            labelWidth: TextSlotCaptionWidth);
    }

    /// <summary>
    /// 文字列／数値スロットの行。
    ///
    /// 左はシェーダの <c>@ref</c> 行と**まったく同じ**参照バインド行
    /// （アクタを D&amp;D → <c>GET_BINDABLE_SOURCES</c> → 2 段選択 → <c>slot.{i}.bind</c>）。
    /// 右にフォールバック値の入力欄を添える（バインド未設定・解決失敗時に使われる値）。
    /// </summary>
    /// <param name="isNumeric">true = 数値スロット（<c>num</c>）、false = 文字列スロット（<c>text</c>）。</param>
    private UIElement BuildTextSlotBindRow(
        SlotInfo info, TextSlotRow slot, string caption, bool isNumeric, Action<string, string> sendField)
    {
        // ── バインド行（送信先は Text セクションと同じ SET_TEXT_FIELD）──
        // BindPrefix の後ろに "{paramName},{value}" が付くので、paramName に
        // スロットのフィールドキーを入れると
        // SET_TEXT_FIELD:{actor},{slot},slot.{i}.bind,{値} になる。
        var target = new ShaderParamTarget(
            ParamsJson:  TextSlotsEmptyJson,                      // 行を 1 本だけ手で作るので宣言配列は使わない
            SetPrefix:   () => "",                                // 値の直接送信は使わない（この行はバインド専用）
            ResetPrefix: () => "",                                // ⟲ を付けないので使われない
            BindPrefix:  () => $"SET_TEXT_FIELD:{_currentActorId},{info.SlotIdx},",
            Description: TextSlotBindDescription,
            RequiresActorSelection: true);

        var bindRow = BuildShaderBindingRow(
            target,
            paramName:  TextSlotKey(slot.Index, TextSlotFieldBind),
            paramLabel: caption,
            // 値型は BindableValueTypeOf を通してランタイムの綴り（str / f32）へ写す。
            paramType:  isNumeric ? ShaderParamTypeFloat : ShaderParamTypeString,
            binding:    slot.Bind,
            bindingOk:  slot.BindOk,
            canReset:   false,
            sendResetParam: _ => { });

        // ── フォールバック値の入力欄 ──
        var fallbackKey = TextSlotKey(slot.Index, isNumeric ? TextSlotFieldNum : TextSlotFieldText);
        var initial = isNumeric
            ? slot.Num.ToString(TextSlotNumFormat, CultureInfo.InvariantCulture)
            : slot.Text;
        var box = MakeTextSlotInput(
            initial, isNumeric ? TextSlotNumFallbackTooltip : TextSlotTextFallbackTooltip);
        box.Width  = TextSlotFallbackWidth;
        box.Margin = TextSlotSideMargin;

        // 確定処理: 数値は解釈できたときだけ送る（不正入力で既存値を壊さない）。
        // 文字列は本文欄と同じ規則（EscapeIpcText）でエスケープしてから送る。
        void Commit()
        {
            if (isNumeric)
            {
                if (!float.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    return;
                sendField(fallbackKey, v.ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                sendField(fallbackKey, EscapeIpcText(box.Text));
            }
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown   += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            Commit();
            e.Handled = true;
        };

        // バインド行は自前で見出し列を持つので、この行では見出しを二重に出さない。
        var grid = new Grid { Margin = TextSlotRowMargin };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(bindRow, 0); grid.Children.Add(bindRow);
        Grid.SetColumn(box,     1); grid.Children.Add(box);
        return grid;
    }

    // ============================================================
    //  行の部品
    // ============================================================

    /// <summary>
    /// 「[見出し] [入力欄] [おまけ]」の 3 列レイアウトを作る（見出し幅は全スロット共通）。
    /// </summary>
    /// <param name="caption">左端の行ラベル。</param>
    /// <param name="content">中央に伸びる入力欄。</param>
    /// <param name="trailing">右端に置く要素（不要なら null）。</param>
    private static Grid BuildTextSlotGrid(string caption, UIElement content, UIElement? trailing)
    {
        var grid = new Grid { Margin = TextSlotRowMargin };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TextSlotCaptionWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text              = caption,
            Foreground        = TextSlotCaptionBrush,
            FontSize          = TextSlotFontSize,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip           = caption,
        };
        Grid.SetColumn(label,   0); grid.Children.Add(label);
        Grid.SetColumn(content, 1); grid.Children.Add(content);
        if (trailing is not null) { Grid.SetColumn(trailing, 2); grid.Children.Add(trailing); }
        return grid;
    }

    /// <summary>
    /// スロット行の 1 行テキスト入力欄を作る（配色は他の入力欄と共通）。
    /// </summary>
    private static TextBox MakeTextSlotInput(string text, string tooltip) => new()
    {
        Text              = text,
        FontSize          = TextSlotFontSize,
        MinHeight         = TextSlotInputMinHeight,
        AcceptsReturn     = false,
        Background        = new SolidColorBrush(InspectorFieldBackground),
        Foreground        = new SolidColorBrush(InspectorFieldForeground),
        BorderBrush       = new SolidColorBrush(InspectorFieldBorder),
        BorderThickness   = new Thickness(InspectorFieldBorderThickness),
        Padding           = TextSlotInputPadding,
        VerticalAlignment = VerticalAlignment.Center,
        ToolTip           = tooltip,
    };
}
