// ============================================================
//  PreviewHostCatalog.cs — 差し込み先の案内の表（読み込み・スクリプトとの照合・欄の値の当てはめ）
//
//  【役割】
//  インスペクタのスクリプトの欄の下に出す「プレビュー」の案内（どの子の下へ・枠の有無・底上げ・既定の中身）は、
//  データの表 editor/config/screen_preview_hosts.json で決める（コードに書かない。docs/editor_screen_preview.md §5）。
//  ここはその表の読み込みと、
//    - FindHost    … スクリプトの型名（"SEED.UI.ScreenStack" / "assets://scripts/.../PopupPlane.cs"）から案内を引く
//    - ResolveSlot … 案内の 1 行に、シーンに保存された [SerializeField] の値（インスペクタの script_fields）を当てる
//  を持つ。ヒエラルキーの右クリックから出すプレビューの底上げ（default_layer_bias）も表に持つ。
//
//  【組み込みの表】
//  表が無い・読めない・未来の format_version のときは、警告して**組み込みの同じ表**で動く
//  （ProjectPanelVisibilityRules と同じ流儀。組み込みの表は JSON の写しで、同じであることは単体テストが確かめる）。
//  既定値（"Screens"・screen_frame.actor・10,000 など）がスクリプトの既定（ScreenStack.cs・ModalHost.cs・
//  UiLayers.cs）とずれていないことも単体テスト（editor/tests/ScreenPreviewTests）がソースと突き合わせて確かめる。
//
//  【WPF 非依存】
//  単体テストがリンクして試すので WPF 型・EditorLog を使わない（警告は呼び出し側の関数へ渡す）。
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace SEEDEditor.Preview;

/// <summary>
/// 差し込み先の案内の表。
/// </summary>
public sealed class PreviewHostCatalog
{
    // ── ファイル・版 ───────────────────────────────────────────

    /// <summary>表のファイル名（editor/config 直下）。</summary>
    public const string FileName = "screen_preview_hosts.json";

    /// <summary>このコードが理解する書式の版。</summary>
    public const int SupportedFormatVersion = 1;

    /// <summary>
    /// 組み込みの「ヒエラルキーの右クリックから出すプレビューの底上げ」。
    /// 置いた所の表示より手前に描くため、画面のスタック 1 段分（UiLayers.DefaultStackStep）だけ底上げする。
    /// </summary>
    public const int BuiltInDefaultLayerBias = BuiltInStackStep;

    // ── 組み込みの表の値（editor/config/screen_preview_hosts.json と同じ。数値は scripting の UiLayers の既定）──

    /// <summary>画面のスタックの 1 段（UiLayers.DefaultStackStep）。</summary>
    private const int BuiltInStackStep = 10_000;

    /// <summary>ダイアログの帯（UiLayers.DefaultDialogBand）。</summary>
    private const int BuiltInDialogBand = 3_000_000;

    /// <summary>シートの帯（UiLayers.DefaultSheetBand）。</summary>
    private const int BuiltInSheetBand = 2_000_000;

    /// <summary>覆いの帯（UiLayers.DefaultOverlayBand）。</summary>
    private const int BuiltInOverlayBand = 1_000_000;

    /// <summary>底上げなし。</summary>
    private const int NoLayerBias = 0;

    /// <summary>画面の枠のプレハブ（ScreenStack.DefaultFramePrefab）。</summary>
    private const string BuiltInFramePrefab = "assets://ui/prefabs/screen_frame.actor";

    /// <summary>枠の中の安全領域の子（ScreenStack の BodyChild）。</summary>
    private const string BuiltInFrameBody = "Body";

    /// <summary>ダイアログの面のプレハブ（ModalHost.DefaultDialogPrefab）。</summary>
    private const string BuiltInDialogPrefab = "assets://ui/prefabs/dialog.actor";

    /// <summary>シートの面のプレハブ（ModalHost.DefaultSheetPrefab）。</summary>
    private const string BuiltInSheetPrefab = "assets://ui/prefabs/bottom_sheet.actor";

    /// <summary>覆いの面のプレハブ（ModalHost.DefaultOverlayPrefab）。</summary>
    private const string BuiltInOverlayPrefab = "assets://ui/prefabs/top_sheet.actor";

    // ── 照合 ────────────────────────────────────────────────

    /// <summary>スクリプトのファイルの拡張子（型名がパスで書かれているとき外す）。</summary>
    private const string ScriptExtension = ".cs";

    /// <summary>パスの区切り（assets:// も絶対パスも）。</summary>
    private static readonly char[] PathSeparators = ['/', '\\'];

    /// <summary>名前空間の区切り。</summary>
    private const char NamespaceSeparator = '.';

    // ── 状態 ────────────────────────────────────────────────

    /// <summary>ヒエラルキーの右クリックから出すプレビューの底上げ。</summary>
    public int DefaultLayerBias { get; }

    /// <summary>案内を出すスクリプトの一覧（表の順）。</summary>
    public IReadOnlyList<PreviewHost> Hosts { get; }

    /// <summary>読み込みで起きた問題（空なら正常）。</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>実際に読んだファイル（組み込みの表なら null）。</summary>
    public string? SourcePath { get; }

    /// <summary>表を組み立てる（生成は <see cref="BuiltIn"/> / <see cref="Load"/> / <see cref="LoadFile"/> から）。</summary>
    private PreviewHostCatalog(int defaultLayerBias, IReadOnlyList<PreviewHost> hosts,
                               IReadOnlyList<string> warnings, string? sourcePath)
    {
        DefaultLayerBias = defaultLayerBias;
        Hosts            = hosts;
        Warnings         = warnings;
        SourcePath       = sourcePath;
    }

    // ============================================================
    //  生成
    // ============================================================

    /// <summary>
    /// 組み込みの表を作る（JSON を読まない）。
    /// </summary>
    /// <param name="warnings">組み込みの表へ落ちた理由（無ければ空）。</param>
    /// <returns>組み込みの表。</returns>
    public static PreviewHostCatalog BuiltIn(IReadOnlyList<string>? warnings = null)
    {
        var file  = BuiltInFile();
        var hosts = ConvertHosts(file.Hosts!, new List<string>());
        return new PreviewHostCatalog(file.DefaultLayerBias ?? BuiltInDefaultLayerBias, hosts,
                                      warnings ?? Array.Empty<string>(), sourcePath: null);
    }

    /// <summary>
    /// 構成フォルダ（editor/config。EditorPaths.ConfigDir）の下の表を読む。
    /// </summary>
    /// <param name="configDir">構成フォルダの絶対パス（null なら組み込みの表）。</param>
    /// <param name="warn">問題を知らせる先（ログ）。省略可。</param>
    /// <returns>読んだ表（読めなければ組み込みの表）。</returns>
    public static PreviewHostCatalog Load(string? configDir, Action<string>? warn = null)
    {
        if (string.IsNullOrWhiteSpace(configDir))
            return Fallback("構成フォルダ（editor/config）が見つからないため、差し込み先の案内は組み込みの表を使います", warn);
        return LoadFile(Path.Combine(configDir, FileName), warn);
    }

    /// <summary>
    /// ファイルを指定して表を読む（単体テストの入口でもある）。例外は投げない。
    /// </summary>
    /// <param name="filePath">screen_preview_hosts.json の絶対パス。</param>
    /// <param name="warn">問題を知らせる先（ログ）。省略可。</param>
    /// <returns>読んだ表（無い・読めない・未来の版なら組み込みの表）。</returns>
    public static PreviewHostCatalog LoadFile(string filePath, Action<string>? warn = null)
    {
        // ① ファイルの有無
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return Fallback($"差し込み先の案内の表が見つからないため組み込みの表を使います: {filePath}", warn);

        // ② JSON（コメント・末尾カンマを許す）
        PreviewHostCatalogFile? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<PreviewHostCatalogFile>(File.ReadAllText(filePath), JsonOptions);
        }
        catch (Exception ex)
        {
            return Fallback($"差し込み先の案内の表を読めないため組み込みの表を使います: {filePath} — {ex.Message}", warn);
        }
        if (parsed is null)
            return Fallback($"差し込み先の案内の表が空のため組み込みの表を使います: {filePath}", warn);

        // ③ 書式の版（未来の版は欄の意味が変わっているかもしれないので、半端に読まず組み込みへ）
        if (parsed.FormatVersion != SupportedFormatVersion)
        {
            return Fallback(
                $"差し込み先の案内の表の format_version={parsed.FormatVersion} は扱えません（対応 {SupportedFormatVersion}）。" +
                $"組み込みの表を使います: {filePath}", warn);
        }
        if (parsed.Hosts is null)
            return Fallback($"差し込み先の案内の表に hosts がないため組み込みの表を使います: {filePath}", warn);

        // ④ 1 件ずつの書き損じは、その件だけ飛ばす（全体は捨てない）
        var warnings = new List<string>();
        var hosts    = ConvertHosts(parsed.Hosts, warnings);
        foreach (var w in warnings) warn?.Invoke(w);
        return new PreviewHostCatalog(parsed.DefaultLayerBias ?? BuiltInDefaultLayerBias, hosts, warnings,
                                      Path.GetFullPath(filePath));
    }

    /// <summary>警告して組み込みの表を返す。</summary>
    /// <param name="message">組み込みへ落ちた理由。</param>
    /// <param name="warn">知らせる先。</param>
    /// <returns>組み込みの表。</returns>
    private static PreviewHostCatalog Fallback(string message, Action<string>? warn)
    {
        warn?.Invoke(message);
        return BuiltIn([message]);
    }

    // ============================================================
    //  照合
    // ============================================================

    /// <summary>
    /// スクリプトの型名から案内を引く。
    /// </summary>
    /// <param name="scriptTypeName">
    /// ScriptComponent の型名（"SEED.UI.ScreenStack" のような完全名、
    /// または "assets://scripts/App/Screens/Common/PopupPlane.cs" のようなファイルのパス）。
    /// </param>
    /// <returns>案内（表に無ければ null）。</returns>
    public PreviewHost? FindHost(string? scriptTypeName)
    {
        var shortName = ShortClassName(scriptTypeName);
        if (shortName.Length == 0) return null;
        return Hosts.FirstOrDefault(h => string.Equals(h.ShortName, shortName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// 型名・ファイルのパスから短いクラス名を取り出す
    /// （パスなら最後の区切りの後ろ・拡張子なし、完全名なら名前空間の最後の後ろ）。
    /// </summary>
    /// <param name="scriptTypeName">型名、またはファイルのパス。</param>
    /// <returns>短いクラス名（取り出せなければ空文字）。</returns>
    public static string ShortClassName(string? scriptTypeName)
    {
        if (string.IsNullOrWhiteSpace(scriptTypeName)) return "";
        var name = scriptTypeName.Trim();

        // パス（assets:// ・絶対・相対）: 最後の区切りの後ろだけを見る
        var lastSeparator = name.LastIndexOfAny(PathSeparators);
        if (lastSeparator >= 0) name = name[(lastSeparator + 1)..];

        // ファイル名なら拡張子を外す
        if (name.EndsWith(ScriptExtension, StringComparison.OrdinalIgnoreCase))
            name = name[..^ScriptExtension.Length];

        // 名前空間つきの完全名なら最後の区切りの後ろ
        var lastDot = name.LastIndexOf(NamespaceSeparator);
        if (lastDot >= 0) name = name[(lastDot + 1)..];
        return name;
    }

    // ============================================================
    //  欄の値の当てはめ
    // ============================================================

    /// <summary>
    /// 差し込み先の定義に、シーンに保存された欄の値を当てて実際に送る形を決める。
    /// <list type="bullet">
    ///   <item>差し込む子・中身・枠: 欄の値（空白でない）→ 無ければ既定（中身・枠は既定も無ければ null）</item>
    ///   <item>枠の中の差し込み先: 「安全領域の中に入れるか」が false なら枠の直下（空）。欄の値（true/false。大小文字無視）→ 既定 → true</item>
    ///   <item>底上げ: 欄があれば「欄の値が正ならそれ、0・無し・読めなければ既定」。欄が無ければ固定値</item>
    /// </list>
    /// 枠なしのときは枠の中の差し込み先を空にする（ランタイムは使わない）。
    /// </summary>
    /// <param name="slot">差し込み先の定義。</param>
    /// <param name="fieldValues">
    /// [SerializeField] の名前 → 値の文字列（インスペクタの script_fields。保存されていない欄は入っていない）。null は空と同じ。
    /// </param>
    /// <returns>実際に送る形。</returns>
    public static ResolvedPreviewSlot ResolveSlot(PreviewHostSlot slot, IReadOnlyDictionary<string, string>? fieldValues)
    {
        // 欄の値（無い・空白だけなら null）
        string? Field(string? fieldName)
        {
            if (fieldName is null || fieldValues is null) return null;
            return fieldValues.TryGetValue(fieldName, out var value) && !string.IsNullOrWhiteSpace(value)
                ? value.Trim()
                : null;
        }

        var under    = Field(slot.UnderField)  ?? slot.UnderDefault;
        var prefab   = Field(slot.PrefabField) ?? slot.PrefabDefault;
        var frame    = Field(slot.FrameField)  ?? slot.FrameDefault;
        var safeArea = ParseBool(Field(slot.SafeAreaField)) ?? slot.SafeAreaDefault ?? true;

        // 安全領域の外（false）は枠の直下。枠なしなら枠の中の差し込み先は使わない
        var frameBody = frame is null || !safeArea ? "" : slot.FrameBody;

        return new ResolvedPreviewSlot(
            slot.Label, slot.Description, under, prefab, frame, frameBody,
            ResolveLayerBias(slot, Field(slot.LayerBiasField)), slot.Pick);
    }

    /// <summary>
    /// 底上げを決める（欄があれば「欄の値が正ならそれ、0・無し・読めなければ既定」。欄が無ければ固定値）。
    /// </summary>
    /// <param name="slot">差し込み先の定義。</param>
    /// <param name="fieldValue">欄の値（無ければ null）。</param>
    /// <returns>底上げ。</returns>
    private static int ResolveLayerBias(PreviewHostSlot slot, string? fieldValue)
    {
        if (slot.LayerBiasField is not null)
        {
            if (fieldValue is not null
                && int.TryParse(fieldValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                && value > 0)
                return value;
            return slot.LayerBiasDefault ?? slot.LayerBias ?? NoLayerBias;
        }
        return slot.LayerBias ?? slot.LayerBiasDefault ?? NoLayerBias;
    }

    /// <summary>"true" / "false"（大小文字無視）を読む。読めなければ null。</summary>
    /// <param name="text">欄の値。</param>
    /// <returns>読めた値、または null。</returns>
    private static bool? ParseBool(string? text) =>
        text is not null && bool.TryParse(text, out var value) ? value : null;

    // ============================================================
    //  読み込んだ形 → 変わらない値
    // ============================================================

    /// <summary>
    /// 読み込んだ形を案内の一覧にする（書き損じた件は警告して飛ばす）。
    /// </summary>
    /// <param name="files">読み込んだ形。</param>
    /// <param name="warnings">警告の積み先。</param>
    /// <returns>案内の一覧。</returns>
    private static IReadOnlyList<PreviewHost> ConvertHosts(IEnumerable<PreviewHostFile?> files, List<string> warnings)
    {
        var hosts = new List<PreviewHost>();
        foreach (var file in files)
        {
            var script    = Clean(file?.Script);
            var shortName = ShortClassName(script);
            if (file is null || script is null || shortName.Length == 0)
            {
                warnings.Add("差し込み先の案内の表に script の無い項目があるので飛ばしました");
                continue;
            }
            if (hosts.Any(h => string.Equals(h.ShortName, shortName, StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add($"差し込み先の案内の表で {shortName} が重なっているので、2 つ目以降を飛ばしました");
                continue;
            }

            var slots = new List<PreviewHostSlot>();
            foreach (var slot in file.Slots ?? new List<PreviewSlotFile>())
            {
                if (slot is null)
                {
                    warnings.Add($"差し込み先の案内の表の {shortName} に空の slot があるので飛ばしました");
                    continue;
                }
                slots.Add(ConvertSlot(slot));
            }
            hosts.Add(new PreviewHost(script, shortName, file.Label?.Trim() ?? "", slots));
        }
        return hosts;
    }

    /// <summary>読み込んだ 1 行を、前後の空白と空文字を整えた定義にする。</summary>
    /// <param name="file">読み込んだ 1 行。</param>
    /// <returns>定義。</returns>
    private static PreviewHostSlot ConvertSlot(PreviewSlotFile file) => new()
    {
        Label            = file.Label?.Trim() ?? "",
        Description      = file.Description?.Trim() ?? "",
        UnderField       = Clean(file.UnderField),
        UnderDefault     = file.UnderDefault?.Trim() ?? "",
        PrefabField      = Clean(file.PrefabField),
        PrefabDefault    = Clean(file.PrefabDefault),
        FrameField       = Clean(file.FrameField),
        FrameDefault     = Clean(file.FrameDefault),
        FrameBody        = file.FrameBody?.Trim() ?? "",
        SafeAreaField    = Clean(file.SafeAreaField),
        SafeAreaDefault  = file.SafeAreaDefault,
        LayerBias        = file.LayerBias,
        LayerBiasField   = Clean(file.LayerBiasField),
        LayerBiasDefault = file.LayerBiasDefault,
        Pick             = file.Pick ?? false,
    };

    /// <summary>前後の空白を落とし、空なら null にする。</summary>
    /// <param name="text">元の文字列。</param>
    /// <returns>整えた文字列、または null。</returns>
    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>JSON の読み方（コメント・末尾カンマを許す。欄名の大小文字は無視）。</summary>
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling         = JsonCommentHandling.Skip,
        AllowTrailingCommas         = true,
    };

    // ============================================================
    //  組み込みの表（editor/config/screen_preview_hosts.json の写し。2026-10-02 の登録）
    // ============================================================

    /// <summary>
    /// 組み込みの表。JSON と同じ形で書き、同じ変換（ConvertHosts）を通す。
    /// 中身を変えるときは JSON と両方を直す（同じであることは単体テストが確かめる）。
    /// </summary>
    /// <returns>JSON を読んだのと同じ形。</returns>
    private static PreviewHostCatalogFile BuiltInFile() => new()
    {
        FormatVersion    = SupportedFormatVersion,
        DefaultLayerBias = BuiltInDefaultLayerBias,
        Hosts =
        [
            // ── 画面のスタック（SEED.UI.ScreenStack）──
            new PreviewHostFile
            {
                Script = "ScreenStack",
                Label  = "画面のスタック",
                Slots  =
                [
                    new PreviewSlotFile
                    {
                        Label            = "根の画面",
                        Description      = "根の画面（RootPrefab）を、実行時と同じく枠（FramePrefab）の中に入れてプレビューします。" +
                                           "RootSafeArea が false なら枠の直下（安全領域の外）に入れます。",
                        UnderField       = "ScreensChild",
                        UnderDefault     = "Screens",
                        PrefabField      = "RootPrefab",
                        FrameField       = "FramePrefab",
                        FrameDefault     = BuiltInFramePrefab,
                        FrameBody        = BuiltInFrameBody,
                        SafeAreaField    = "RootSafeArea",
                        SafeAreaDefault  = true,
                        LayerBiasField   = "LayerStep",
                        LayerBiasDefault = BuiltInStackStep,
                        Pick             = false,
                    },
                    new PreviewSlotFile
                    {
                        Label            = "画面",
                        Description      = "選んだプレハブを、Push したときと同じく枠（FramePrefab）の Body（安全領域の中）に入れてプレビューします。",
                        UnderField       = "ScreensChild",
                        UnderDefault     = "Screens",
                        FrameField       = "FramePrefab",
                        FrameDefault     = BuiltInFramePrefab,
                        FrameBody        = BuiltInFrameBody,
                        LayerBiasField   = "LayerStep",
                        LayerBiasDefault = BuiltInStackStep,
                        Pick             = true,
                    },
                ],
            },

            // ── 重ねる面の受け皿（SEED.UI.ModalHost）──
            new PreviewHostFile
            {
                Script = "ModalHost",
                Label  = "重ねる面の受け皿",
                Slots  =
                [
                    new PreviewSlotFile
                    {
                        Label         = "ダイアログ",
                        Description   = "ダイアログの帯（DialogsChild）の下へ、ダイアログの面（DialogPrefab）をプレビューします。",
                        UnderField    = "DialogsChild",
                        UnderDefault  = "Dialogs",
                        PrefabField   = "DialogPrefab",
                        PrefabDefault = BuiltInDialogPrefab,
                        LayerBias     = BuiltInDialogBand,
                        Pick          = true,
                    },
                    new PreviewSlotFile
                    {
                        Label         = "シート",
                        Description   = "シートの帯（SheetsChild）の下へ、下からのシートの面（SheetPrefab）をプレビューします。",
                        UnderField    = "SheetsChild",
                        UnderDefault  = "Sheets",
                        PrefabField   = "SheetPrefab",
                        PrefabDefault = BuiltInSheetPrefab,
                        LayerBias     = BuiltInSheetBand,
                        Pick          = true,
                    },
                    new PreviewSlotFile
                    {
                        Label         = "覆い",
                        Description   = "覆いの帯（OverlaysChild）の下へ、上からの覆いの面（OverlayPrefab）をプレビューします。",
                        UnderField    = "OverlaysChild",
                        UnderDefault  = "Overlays",
                        PrefabField   = "OverlayPrefab",
                        PrefabDefault = BuiltInOverlayPrefab,
                        LayerBias     = BuiltInOverlayBand,
                        Pick          = true,
                    },
                ],
            },

            // ── 中央のポップアップ（Wake or Pay の assets://scripts/App/Screens/Common/PopupPlane.cs）──
            new PreviewHostFile
            {
                Script = "PopupPlane",
                Label  = "中央のポップアップ（Wake or Pay）",
                Slots  =
                [
                    new PreviewSlotFile
                    {
                        Label        = "中身",
                        Description  = "ポップアップの札の中身の置き場（Card/Content）の下へ、選んだ中身のプレハブをプレビューします。",
                        UnderDefault = "Card/Content",
                        LayerBias    = NoLayerBias,
                        Pick         = true,
                    },
                ],
            },
        ],
    };
}
