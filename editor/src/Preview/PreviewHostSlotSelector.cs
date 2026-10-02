// ============================================================
//  PreviewHostSlotSelector.cs — 名前で指定された「差し込み先の案内の行」を、アクタの構成から選んで欄の値を当てる
//
//  【役割】
//  インスペクタの「プレビュー」の欄（InspectorPanel.Preview.cs）は、表示中のアクタのスクリプトを
//  差し込み先の案内の表（screen_preview_hosts.json。PreviewHostCatalog）に照らして行を並べ、
//  押された行の定義へシーンの欄の値（script_fields）を当てて差し込み先を作る。
//  AI ツール（seed_preview の host）はボタンを押せないので、同じことを「行の見出し」で指定して行う:
//    1. ACTOR_COMPONENTS の components から ScriptComponent を拾う（model_path = 型名・パス、script_fields = 欄の値）
//    2. 表に載っているスクリプトの行を、指定（"見出し" か "スクリプト名/見出し"）で選ぶ
//    3. PreviewHostCatalog.ResolveSlot で欄の値を当てる（インスペクタと同じ関数）
//
//  【WPF 非依存】
//  単体テスト（editor/tests/ScreenPreviewTests）がリンクして試すので WPF 型を使わない。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace SEEDEditor.Preview;

/// <summary>
/// 差し込み先の行を選んだ結果。
/// </summary>
/// <param name="Slot">欄の値を当てた差し込み先（選べなければ null）。</param>
/// <param name="HostShortName">選んだ行を持つスクリプトの短いクラス名（選べなければ null）。</param>
/// <param name="Error">選べなかった理由（AI 向けの文。選べたら null）。</param>
/// <param name="Available">このアクタで選べる行（"スクリプト名/見出し"。表の順）。</param>
public sealed record PreviewHostSlotSelection(
    ResolvedPreviewSlot? Slot,
    string? HostShortName,
    string? Error,
    IReadOnlyList<string> Available);

/// <summary>
/// 指定された差し込み先の行を、アクタの構成（ACTOR_COMPONENTS）から選ぶ。状態を持たない。
/// </summary>
public static class PreviewHostSlotSelector
{
    /// <summary>指定の「スクリプト名」と「見出し」の区切り（例 "ModalHost/ダイアログ"）。</summary>
    public const char ScriptSeparator = '/';

    /// <summary>ACTOR_COMPONENTS の構成の配列の欄。</summary>
    private const string JsonComponents = "components";

    /// <summary>構成 1 つの種類の欄。</summary>
    private const string JsonType = "type";

    /// <summary>ScriptComponent の型名・パスの欄。</summary>
    private const string JsonModelPath = "model_path";

    /// <summary>ScriptComponent の保存された欄の値（[SerializeField]）の欄。</summary>
    private const string JsonScriptFields = "script_fields";

    /// <summary>スクリプトの構成の種類名（ランタイムの ACTOR_COMPONENTS の type）。</summary>
    private const string ScriptComponentType = "ScriptComponent";

    /// <summary>
    /// 指定の行を選び、欄の値を当てる。
    /// </summary>
    /// <param name="catalog">差し込み先の案内の表。</param>
    /// <param name="actorComponentsJson">親のアクタの ACTOR_COMPONENTS（GET_ACTOR_COMPONENTS の応答）。</param>
    /// <param name="slotSpec">行の指定（"見出し" か "スクリプト名/見出し"。例 "画面"・"ModalHost/ダイアログ"）。</param>
    /// <returns>選んだ結果。</returns>
    public static PreviewHostSlotSelection Select(PreviewHostCatalog catalog, string actorComponentsJson, string slotSpec)
    {
        var scripts   = ReadScripts(actorComponentsJson);
        var available = new List<string>();

        // 表に載っているスクリプトを、構成の順（＝インスペクタの並び）に見ていく
        foreach (var (modelPath, _) in scripts)
        {
            if (catalog.FindHost(modelPath) is not { } host) continue;
            foreach (var slot in host.Slots)
                available.Add(host.ShortName + ScriptSeparator + slot.Label);
        }

        if (string.IsNullOrWhiteSpace(slotSpec))
            return new(null, null, "'host' が空です（差し込み先の行の見出し。例 \"画面\"・\"ModalHost/ダイアログ\"）。", available);

        // 指定の読み方は 2 通り: 全体を見出しとして読む → 区切りがあれば「スクリプト名/見出し」として読む
        // （見出しそのものに区切りの文字が入っていても、先に全体で照合するので選べる）
        foreach (var (scriptName, label) in SpecCandidates(slotSpec))
        {
            foreach (var (modelPath, fields) in scripts)
            {
                if (catalog.FindHost(modelPath) is not { } host) continue;
                if (scriptName.Length > 0 && !string.Equals(host.ShortName, scriptName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // 見出しは表の文字そのまま（日本語）。英字の大小だけは見逃す
                var slot = host.Slots.FirstOrDefault(s => string.Equals(s.Label, label, StringComparison.OrdinalIgnoreCase));
                if (slot is null) continue;
                return new(PreviewHostCatalog.ResolveSlot(slot, fields), host.ShortName, null, available);
            }
        }

        var hint = available.Count == 0
            ? "このアクタには差し込み先の案内の表（editor/config/screen_preview_hosts.json）に載っているスクリプトがありません"
            : $"選べる行: {string.Join(" / ", available)}";
        return new(null, null, $"差し込み先の行 '{slotSpec}' が見つかりません（{hint}）。", available);
    }

    /// <summary>
    /// script_fields の値を「欄の名前 → 値の文字列」にする（文字列以外の値は JSON の表記のまま）。
    /// </summary>
    /// <param name="scriptFields">script_fields の値（オブジェクトでなければ空）。</param>
    /// <returns>欄の値。</returns>
    public static IReadOnlyDictionary<string, string> ReadScriptFieldValues(JsonElement scriptFields)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (scriptFields.ValueKind != JsonValueKind.Object) return values;
        foreach (var prop in scriptFields.EnumerateObject())
        {
            values[prop.Name] = prop.Value.ValueKind switch
            {
                JsonValueKind.String => prop.Value.GetString() ?? "",
                JsonValueKind.Null   => "",
                _                    => prop.Value.GetRawText(),
            };
        }
        return values;
    }

    // ============================================================
    //  小道具
    // ============================================================

    /// <summary>
    /// ACTOR_COMPONENTS からスクリプトの構成を (型名・パス, 欄の値) の並びで拾う。読めなければ空。
    /// </summary>
    /// <param name="json">ACTOR_COMPONENTS の JSON。</param>
    /// <returns>スクリプトの並び（構成の順）。</returns>
    private static List<(string ModelPath, IReadOnlyDictionary<string, string> Fields)> ReadScripts(string json)
    {
        var result = new List<(string, IReadOnlyDictionary<string, string>)>();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty(JsonComponents, out var comps) || comps.ValueKind != JsonValueKind.Array)
                return result;

            foreach (var comp in comps.EnumerateArray())
            {
                if (!comp.TryGetProperty(JsonType, out var type) || type.GetString() != ScriptComponentType) continue;
                var modelPath = comp.TryGetProperty(JsonModelPath, out var mp) && mp.ValueKind == JsonValueKind.String
                    ? mp.GetString() ?? ""
                    : "";
                var fields = comp.TryGetProperty(JsonScriptFields, out var sf)
                    ? ReadScriptFieldValues(sf)
                    : new Dictionary<string, string>();
                result.Add((modelPath, fields));
            }
        }
        catch (JsonException)
        {
            // 壊れた応答は「スクリプト無し」と同じ扱い（呼び出し側が選べる行の一覧で気づける）
        }
        return result;
    }

    /// <summary>
    /// 指定の読み方の候補を、試す順に並べる（全体を見出しとして → 最初の区切りで「スクリプト名/見出し」として）。
    /// </summary>
    /// <param name="spec">指定。</param>
    /// <returns>(スクリプト名〈空なら問わない〉, 見出し) の並び（前後の空白は落とす）。</returns>
    private static IEnumerable<(string ScriptName, string Label)> SpecCandidates(string spec)
    {
        var text = spec.Trim();
        yield return ("", text);

        var separator = text.IndexOf(ScriptSeparator);
        if (separator > 0)
            yield return (text[..separator].Trim(), text[(separator + 1)..].Trim());
    }
}
