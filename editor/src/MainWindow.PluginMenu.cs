// ============================================================
//  MainWindow.PluginMenu.cs — プラグインが宣言したメニューの動的生成
//
//  担当:
//   - PLUGIN_LIST（plugin.json の editor_menus）からメニューバー項目を生成する
//   - 項目クリック時の確認ダイアログと PLUGIN_ACTION 送信
//   - 実行結果（PLUGIN_ACTION_OK / _ERROR）のログ・トースト表示
//   - ランタイム未接続時の無効化（グレーアウト）
//
//  設計:
//   ここにはプラグイン固有の知識を一切置かない。何のメニューが増えるかは
//   plugin.json だけで決まる（データドリブン）。エディタ側は
//   「宣言された通りに並べて、押されたら id を送り返す」だけを行う。
//
//  配置ルール:
//   - 同名のトップレベルメニューが既に存在すれば（静的な「表示」等も含む）、
//     新規作成せずそのメニューの末尾へ項目を足す。
//   - 存在しなければ、静的メニューすべての右隣（メニューバーの末尾）へ追加する。
//   - 複数プラグインが同じメニュー名を宣言した場合は 1 つへマージする。
//   - 再受信のたびに前回生成分だけを取り除いて作り直すので重複しない。
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using SEEDEditor.Runtime;

namespace SEEDEditor;

public partial class MainWindow
{
    // ============================================================
    //  定数
    // ============================================================

    /// <summary>ランタイム未接続時にメニュー項目へ出すツールチップ。</summary>
    private const string PluginMenuDisabledTooltip =
        "ランタイムが起動していないため実行できません。";

    /// <summary>確認ダイアログのタイトル。</summary>
    private const string PluginMenuConfirmTitle = "確認";

    // ============================================================
    //  状態
    // ============================================================

    /// <summary>
    /// 今回生成したプラグイン由来のトップレベルメニュー。
    /// 次回の作り直しでメニューバーから取り除くために保持する。
    /// </summary>
    private readonly List<MenuItem> _pluginTopLevelMenus = new();

    /// <summary>
    /// 静的メニューへ相乗りさせた項目（区切り線を含む）。
    /// キーは相乗り先の静的メニュー、値はそこへ追加した要素。
    /// 次回の作り直しでこれだけを取り除き、静的項目には触れない。
    /// </summary>
    private readonly Dictionary<MenuItem, List<object>> _pluginBorrowedItems = new();

    /// <summary>
    /// 生成済みのクリック可能な項目。ランタイム接続状態に応じて
    /// まとめて IsEnabled を切り替えるために保持する。
    /// </summary>
    private readonly List<MenuItem> _pluginActionItems = new();

    // ============================================================
    //  初期化
    // ============================================================

    /// <summary>
    /// プラグインメニュー機構の購読を開始する（MainWindow の初期化から 1 回だけ呼ぶ）。
    /// </summary>
    private void InitPluginMenus()
    {
        if (_runtimeManager is null) return;

        // プラグイン一覧を受け取るたびにメニューを作り直す。
        // IPC はワーカースレッドから来るため、UI 操作は必ずディスパッチャ経由で行う。
        _runtimeManager.PluginListParsed += plugins =>
            Dispatcher.BeginInvoke(() => RebuildPluginMenus(plugins));

        // 実行結果の表示。
        _runtimeManager.PluginActionSucceeded += (plugin, id) =>
            Dispatcher.BeginInvoke(() => OnPluginActionSucceeded(plugin, id));
        _runtimeManager.PluginActionFailed += (plugin, id, reason) =>
            Dispatcher.BeginInvoke(() => OnPluginActionFailed(plugin, id, reason));

        // ランタイムの起動・停止に追従して有効/無効を切り替える。
        _runtimeManager.StateChanged += _ =>
            Dispatcher.BeginInvoke(RefreshPluginMenuEnabled);
    }

    // ============================================================
    //  メニュー生成
    // ============================================================

    /// <summary>
    /// 受信したプラグイン一覧からメニューバーを作り直す。
    /// 前回生成分を取り除いてから作るため、何度呼んでも重複しない。
    /// </summary>
    /// <param name="plugins">ランタイムにロードされているプラグインの一覧。</param>
    private void RebuildPluginMenus(IReadOnlyList<PluginInfo> plugins)
    {
        if (MainMenuBar is null) return;

        ClearPluginMenus();

        // ── 1. メニュー名ごとに項目をマージする ───────────────────
        // 宣言順を保ちたいので、順序付きのリストにバケットを並べていく。
        var merged = new List<PluginMenuBucket>();

        foreach (var plugin in plugins)
        {
            foreach (var menu in plugin.Menus)
            {
                var bucket = merged.FirstOrDefault(
                    m => string.Equals(m.MenuName, menu.MenuName, StringComparison.Ordinal));

                if (bucket is null)
                {
                    bucket = new PluginMenuBucket(menu.MenuName);
                    merged.Add(bucket);
                }

                foreach (var item in menu.Items)
                    bucket.Items.Add(new PluginMenuAction(plugin.Name, item));
            }
        }

        // ── 2. メニュー名ごとに WPF の要素を作る ──────────────────
        foreach (var bucket in merged)
        {
            // 同名の既存メニュー（静的メニューを含む）があればそこへ相乗りする
            var host = FindTopLevelMenu(bucket.MenuName);
            bool isNewMenu = host is null;

            if (host is null)
            {
                host = new MenuItem { Header = bucket.MenuName };
                // 静的メニューの右隣＝メニューバーの末尾へ追加する
                MainMenuBar.Items.Add(host);
                _pluginTopLevelMenus.Add(host);
            }

            foreach (var action in bucket.Items)
            {
                object element = action.Item.IsSeparator
                    ? new Separator()
                    : CreateActionMenuItem(action);

                host.Items.Add(element);

                // 既存メニューへ相乗りした分は、次回の作り直しで個別に取り除く必要がある
                if (!isNewMenu)
                {
                    if (!_pluginBorrowedItems.TryGetValue(host, out var list))
                    {
                        list = new List<object>();
                        _pluginBorrowedItems[host] = list;
                    }
                    list.Add(element);
                }
            }
        }

        // 生成直後の有効/無効をランタイムの接続状態へ合わせる
        RefreshPluginMenuEnabled();

        EditorLog.Write(
            $"[PluginMenu] メニューを再構築しました: メニュー {merged.Count} 個 / 実行項目 {_pluginActionItems.Count} 個");
    }

    /// <summary>クリックで PLUGIN_ACTION を送る 1 項目を生成する。</summary>
    /// <param name="action">送信先プラグイン名と項目定義。</param>
    private MenuItem CreateActionMenuItem(PluginMenuAction action)
    {
        var menuItem = new MenuItem
        {
            Header = action.Item.Label,
            // Tag に「どのプラグインのどの id か」を持たせ、クリック時はここだけを見る。
            // 文字列ではなくモデルを入れることで、確認文言の取り違えを防ぐ。
            Tag = action,
            // どのプラグインが提供する項目かをツールチップで示す（同名項目の出所が分かる）
            ToolTip = BuildActionTooltip(action),
        };

        menuItem.Click += OnPluginMenuItemClick;

        _pluginActionItems.Add(menuItem);
        return menuItem;
    }

    /// <summary>接続中に表示するツールチップ文言（提供元プラグインとアクション id）。</summary>
    private static string BuildActionTooltip(PluginMenuAction action)
        => $"{action.PluginName} / {action.Item.Id}";

    /// <summary>メニューバーから指定ヘッダのトップレベルメニューを探す（完全一致）。</summary>
    /// <returns>見つからなければ null。</returns>
    private MenuItem? FindTopLevelMenu(string header)
        => MainMenuBar?.Items.OfType<MenuItem>()
            .FirstOrDefault(mi => mi.Header as string == header);

    /// <summary>前回生成したプラグイン由来のメニュー・項目をすべて取り除く。</summary>
    private void ClearPluginMenus()
    {
        // 相乗りした項目は 1 つずつ取り除く（静的項目を消さないため）
        foreach (var pair in _pluginBorrowedItems)
        {
            foreach (var element in pair.Value)
                pair.Key.Items.Remove(element);
        }
        _pluginBorrowedItems.Clear();

        // プラグイン専用に作ったトップレベルメニューは丸ごと取り除く
        foreach (var menu in _pluginTopLevelMenus)
            MainMenuBar?.Items.Remove(menu);
        _pluginTopLevelMenus.Clear();

        // クリックハンドラを外してから参照を捨てる（イベントの取りこぼし防止）
        foreach (var item in _pluginActionItems)
            item.Click -= OnPluginMenuItemClick;
        _pluginActionItems.Clear();
    }

    // ============================================================
    //  有効/無効の切り替え
    // ============================================================

    /// <summary>
    /// ランタイムの接続状態に応じてプラグインメニュー項目の有効/無効を切り替える。
    /// 未接続時は押しても IPC が届かないため、グレーアウトして理由を示す。
    /// </summary>
    private void RefreshPluginMenuEnabled()
    {
        bool connected = _runtimeManager?.IsPipeConnected == true;

        foreach (var item in _pluginActionItems)
        {
            item.IsEnabled = connected;

            if (item.Tag is PluginMenuAction action)
            {
                item.ToolTip = connected
                    ? BuildActionTooltip(action)
                    : PluginMenuDisabledTooltip;
            }
        }
    }

    // ============================================================
    //  クリックと実行結果
    // ============================================================

    /// <summary>プラグインメニュー項目がクリックされたときの処理。</summary>
    private void OnPluginMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem menuItem) return;
        if (menuItem.Tag is not PluginMenuAction action) return;

        // 破壊的な操作（セーブ削除など）は plugin.json の confirm を出してから実行する
        if (action.Item.NeedsConfirm)
        {
            var answer = MessageBox.Show(
                this,
                action.Item.Confirm,
                PluginMenuConfirmTitle,
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning,
                MessageBoxResult.No); // 既定を「いいえ」にして誤爆を防ぐ

            if (answer != MessageBoxResult.Yes)
            {
                EditorLog.Write($"[PluginMenu] キャンセルされました: {action.PluginName}/{action.Item.Id}");
                return;
            }
        }

        EditorLog.Write($"[PluginMenu] 実行要求: {action.PluginName}/{action.Item.Id}");

        if (_runtimeManager?.SendPluginAction(action.PluginName, action.Item.Id) != true)
        {
            // 送信できなかった（未接続）。押せてしまった場合の保険。
            ShowToast(PluginMenuDisabledTooltip);
            RefreshPluginMenuEnabled();
        }
    }

    /// <summary>PLUGIN_ACTION_OK を受け取ったときの表示。</summary>
    private void OnPluginActionSucceeded(string pluginName, string actionId)
    {
        var label = FindActionLabel(pluginName, actionId) ?? actionId;
        EditorLog.Write($"[PluginMenu] 成功: {pluginName}/{actionId}");
        ShowToast($"{label} を実行しました（{pluginName}）");
    }

    /// <summary>PLUGIN_ACTION_ERROR を受け取ったときの表示。</summary>
    private void OnPluginActionFailed(string pluginName, string actionId, string reason)
    {
        var label = FindActionLabel(pluginName, actionId) ?? actionId;
        EditorLog.Write($"[PluginMenu] 失敗: {pluginName}/{actionId} : {reason}");
        ShowToast($"{label} に失敗しました: {reason}");
    }

    /// <summary>
    /// 生成済み項目から表示ラベルを引く（結果表示に id ではなく人間向けの名前を出すため）。
    /// </summary>
    /// <returns>該当項目がなければ null。</returns>
    private string? FindActionLabel(string pluginName, string actionId)
    {
        foreach (var item in _pluginActionItems)
        {
            if (item.Tag is PluginMenuAction action
                && action.PluginName == pluginName
                && action.Item.Id    == actionId)
            {
                return action.Item.Label;
            }
        }
        return null;
    }

    // ============================================================
    //  内部モデル
    // ============================================================

    /// <summary>
    /// メニュー項目の Tag に載せる実行情報。
    /// 「どのプラグインへ、どの id を送るか」と確認文言を 1 か所に束ねる。
    /// </summary>
    /// <param name="PluginName">送信先プラグイン名。</param>
    /// <param name="Item">plugin.json 由来の項目定義。</param>
    private sealed record PluginMenuAction(string PluginName, PluginMenuItemInfo Item);

    /// <summary>
    /// 同名メニューへのマージ用バケット。
    /// トップレベルメニュー 1 つ分に、複数プラグインの項目が順に積まれる。
    /// </summary>
    private sealed class PluginMenuBucket
    {
        /// <summary>トップレベルメニュー名。</summary>
        public string MenuName { get; }

        /// <summary>このメニューへ並べる項目（宣言順）。</summary>
        public List<PluginMenuAction> Items { get; } = new();

        /// <summary>メニュー名を指定して空のバケットを作る。</summary>
        public PluginMenuBucket(string menuName) => MenuName = menuName;
    }
}
