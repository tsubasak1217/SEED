// ============================================================
//  PluginMenuModel.cs — プラグインが宣言するエディタメニューのモデル
//
//  担当:
//   - PLUGIN_LIST IPC で受信した JSON を型付きモデルへ落とし込む
//   - メニュー UI（MainWindow.PluginMenu.cs）が参照する読み取り専用データを提供
//
//  設計:
//   メニュー構成は plugin.json（データ）だけで決まり、エディタ側にプラグイン固有の
//   コードは一切書かない。ここは「JSON → モデル」の変換だけを担当し、
//   UI 生成やクリック時の送信は MainWindow 側の責務とする（単一責任）。
//
//  受信する JSON の形:
//   [
//     { "name": "GameTools", "version": "0.1.0", "description": "...",
//       "editor_menus": [
//         { "menu": "Game",
//           "items": [ { "id": "delete_save", "label": "...", "confirm": "...",
//                        "separator": false } ] }
//       ] }
//   ]
// ============================================================

using System;
using System.Collections.Generic;
using System.Text.Json;

namespace SEEDEditor.Runtime;

// ============================================================
//  PluginMenuItemInfo
// ============================================================

/// <summary>プラグインが宣言したメニュー項目 1 件。</summary>
/// <param name="Id">アクション識別子（PLUGIN_ACTION で送る値）。区切り線では空。</param>
/// <param name="Label">メニューに表示する文言。</param>
/// <param name="Confirm">実行前に表示する確認ダイアログ本文。空なら確認しない。</param>
/// <param name="IsSeparator">true なら区切り線として描画する。</param>
public sealed record PluginMenuItemInfo(
    string Id,
    string Label,
    string Confirm,
    bool   IsSeparator)
{
    /// <summary>クリックして実行できる項目か（区切り線でなく id を持つ）。</summary>
    public bool IsActionable => !IsSeparator && Id.Length > 0;

    /// <summary>実行前に確認ダイアログが必要か。</summary>
    public bool NeedsConfirm => Confirm.Length > 0;
}

// ============================================================
//  PluginMenuInfo
// ============================================================

/// <summary>プラグインが宣言したトップレベルメニュー 1 つ分。</summary>
/// <param name="MenuName">トップレベルメニュー名（同名は複数プラグイン間でマージされる）。</param>
/// <param name="Items">このメニューへ追加する項目。</param>
public sealed record PluginMenuInfo(
    string MenuName,
    IReadOnlyList<PluginMenuItemInfo> Items);

// ============================================================
//  PluginInfo
// ============================================================

/// <summary>ランタイムにロードされているプラグイン 1 つ分の情報。</summary>
/// <param name="Name">プラグイン識別名（PLUGIN_ACTION の送信先）。</param>
/// <param name="Version">バージョン文字列。</param>
/// <param name="Description">説明文。</param>
/// <param name="Menus">このプラグインが宣言したエディタメニュー。</param>
public sealed record PluginInfo(
    string Name,
    string Version,
    string Description,
    IReadOnlyList<PluginMenuInfo> Menus);

// ============================================================
//  PluginListParser
// ============================================================

/// <summary>PLUGIN_LIST の JSON を <see cref="PluginInfo"/> のリストへ変換する。</summary>
public static class PluginListParser
{
    // ── JSON のプロパティ名（マジックストリングの一元管理） ──
    private const string PropName        = "name";
    private const string PropVersion     = "version";
    private const string PropDescription = "description";
    private const string PropEditorMenus = "editor_menus";
    private const string PropMenu        = "menu";
    private const string PropItems       = "items";
    private const string PropId          = "id";
    private const string PropLabel       = "label";
    private const string PropConfirm     = "confirm";
    private const string PropSeparator   = "separator";

    /// <summary>
    /// PLUGIN_LIST の JSON 本文をパースする。
    /// 壊れた JSON・想定外の型が来ても例外を投げず、読めた分だけを返す
    /// （プラグイン 1 つの記述ミスでエディタのメニューが全部消えないようにするため）。
    /// </summary>
    public static IReadOnlyList<PluginInfo> Parse(string json)
    {
        var result = new List<PluginInfo>();
        if (string.IsNullOrWhiteSpace(json)) return result;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return result;

            foreach (var pluginElem in doc.RootElement.EnumerateArray())
            {
                var name = ReadString(pluginElem, PropName);
                // 名前のないプラグインはアクション送信先を特定できないので捨てる
                if (name.Length == 0) continue;

                result.Add(new PluginInfo(
                    Name:        name,
                    Version:     ReadString(pluginElem, PropVersion),
                    Description: ReadString(pluginElem, PropDescription),
                    Menus:       ParseMenus(pluginElem)));
            }
        }
        catch (JsonException e)
        {
            EditorLog.Write($"[PluginListParser] PLUGIN_LIST のパースに失敗しました: {e.Message}");
        }

        return result;
    }

    /// <summary>1 プラグイン分の editor_menus 配列を読む。</summary>
    private static IReadOnlyList<PluginMenuInfo> ParseMenus(JsonElement pluginElem)
    {
        var menus = new List<PluginMenuInfo>();

        if (!pluginElem.TryGetProperty(PropEditorMenus, out var menusElem)
            || menusElem.ValueKind != JsonValueKind.Array)
        {
            // editor_menus を持たない（旧形式の）プラグインはメニューなしとして扱う
            return menus;
        }

        foreach (var menuElem in menusElem.EnumerateArray())
        {
            var menuName = ReadString(menuElem, PropMenu);
            // メニュー名がなければ配置先が決まらないので捨てる
            if (menuName.Length == 0) continue;

            var items = new List<PluginMenuItemInfo>();
            if (menuElem.TryGetProperty(PropItems, out var itemsElem)
                && itemsElem.ValueKind == JsonValueKind.Array)
            {
                foreach (var itemElem in itemsElem.EnumerateArray())
                {
                    var isSeparator = ReadBool(itemElem, PropSeparator);
                    var id          = ReadString(itemElem, PropId);
                    var label       = ReadString(itemElem, PropLabel);

                    // label 省略時は id を表示に流用する（plugin.json を短く書けるように）
                    if (label.Length == 0) label = id;

                    // 区切り線でもなく id もない項目は何もできないので捨てる
                    if (!isSeparator && id.Length == 0) continue;

                    items.Add(new PluginMenuItemInfo(
                        Id:          id,
                        Label:       label,
                        Confirm:     ReadString(itemElem, PropConfirm),
                        IsSeparator: isSeparator));
                }
            }

            // 項目が 1 つもないメニューは作らない（空メニューを並べない）
            if (items.Count == 0) continue;

            menus.Add(new PluginMenuInfo(menuName, items));
        }

        return menus;
    }

    /// <summary>文字列プロパティを読む（無い/型違いなら空文字列）。</summary>
    private static string ReadString(JsonElement elem, string prop)
        => elem.ValueKind == JsonValueKind.Object
           && elem.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>真偽プロパティを読む（無い/型違いなら false）。</summary>
    private static bool ReadBool(JsonElement elem, string prop)
        => elem.ValueKind == JsonValueKind.Object
           && elem.TryGetProperty(prop, out var v)
           && v.ValueKind == JsonValueKind.True;
}
