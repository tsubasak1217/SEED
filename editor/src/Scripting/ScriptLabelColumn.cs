using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace SEEDEditor.Scripting;

/// <summary>
/// スクリプトフィールド行の「ラベル列の幅」を、1 つのスクリプトセクション内で
/// まとめて決めるための調停役。
///
/// 【解決したい問題】
/// ラベル列を固定幅にすると、長いラベルは常に「…」で切れたままになり、
/// インスペクタを広げても入力欄だけが伸びてラベルは読めるようにならない。
///
/// 【方針：ラベル優先で幅を配る】
/// 1. 行の生成時にラベルの「折り返さない自然幅」を測っておく（<see cref="Register"/>）。
/// 2. セクション内の全ラベルの自然幅の最大値をラベル列の希望幅とする
///    （＝どの行も同じ幅になるので、入力欄の左端が揃う）。
/// 3. 希望幅は「上限 px」と「行幅に対する割合」の小さい方でクランプする。
///    これにより、パネルが狭いときでも入力欄の幅が必ず残る（ラベルは「…」で切れる）。
/// 4. パネルを広げると上限が上がるので、余った幅はまずラベルへ、
///    ラベルが全部収まった時点から先は入力欄（Star 列）へ回る。
///
/// 【使い方】
/// セクションのルート要素に <see cref="SetGroup"/> でグループを付ける。
/// 添付プロパティは継承する（Inherits）ので、配下のどの深さで作られた行からでも
/// <see cref="AttachRow"/> がグループを見つけて自動登録できる
/// （配列要素の追加など、後から作られる行にも効く）。
/// </summary>
internal sealed class ScriptLabelColumnGroup
{
    // ── レイアウト定数 ───────────────────────────────────────

    /// <summary>ラベル列の最小幅（px）。ラベルが短くても入力欄の左端が寄りすぎないようにする。</summary>
    private const double MinLabelWidth = 60;

    /// <summary>ラベル列の最大幅（px）。これ以上は広げない（入力欄が遠くなりすぎるため）。</summary>
    public const double MaxLabelWidth = 260;

    /// <summary>ラベル列が占めてよい行幅の割合の上限（0〜1）。狭いパネルでの入力欄を守る。</summary>
    private const double MaxLabelWidthRatio = 0.55;

    /// <summary>ラベルと入力欄の間に必ず空ける余白（px）。自然幅へ加算する。</summary>
    private const double LabelRightGap = 8;

    /// <summary>幅の再計算をこの差以下では行わない閾値（px）。無用なレイアウト再実行を防ぐ。</summary>
    private const double WidthEpsilon = 0.5;

    // ── 添付プロパティ（セクション配下へグループを配る）─────

    /// <summary>
    /// セクションのルートに設定するグループ。子孫へ継承され、行はこれを辿って自分を登録する。
    /// </summary>
    public static readonly DependencyProperty GroupProperty = DependencyProperty.RegisterAttached(
        "Group",
        typeof(ScriptLabelColumnGroup),
        typeof(ScriptLabelColumnGroup),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits));

    /// <summary>グループを設定する（セクションのルート要素に対して呼ぶ）。</summary>
    public static void SetGroup(DependencyObject target, ScriptLabelColumnGroup? value)
        => target.SetValue(GroupProperty, value);

    /// <summary>継承されたグループを取得する（無ければ null）。</summary>
    public static ScriptLabelColumnGroup? GetGroup(DependencyObject target)
        => (ScriptLabelColumnGroup?)target.GetValue(GroupProperty);

    // ── 登録済みの行 ─────────────────────────────────────────

    /// <summary>登録済みの行 1 件（ラベル列の定義と、そのラベルの自然幅）。</summary>
    private readonly record struct Entry(ColumnDefinition Column, double NaturalWidth);

    private readonly Dictionary<FrameworkElement, Entry> _entries = new();

    /// <summary>幅の上限を決める基準になる要素（セクションのルート）。</summary>
    private FrameworkElement? _scope;

    /// <summary>再計算がディスパッチャへ予約済みか（連続登録での多重計算を防ぐ）。</summary>
    private bool _recomputeScheduled;

    /// <summary>
    /// グループを基準要素へ結び付ける。以後、その要素の幅が変わるたびに上限を計算し直す。
    /// </summary>
    public void AttachScope(FrameworkElement scope)
    {
        _scope = scope;
        SetGroup(scope, this);
        scope.SizeChanged += (_, _) => ScheduleRecompute();
    }

    /// <summary>
    /// 行をグループへ参加させる。読み込み時に継承グループを探して登録し、
    /// 取り外し時に登録解除する（インスペクタは編集のたびに UI を作り直すため）。
    /// </summary>
    /// <param name="row">行のルート（Grid）。</param>
    /// <param name="column">ラベル列の定義。</param>
    /// <param name="naturalWidth">ラベルを折り返さずに表示するのに必要な幅（px）。</param>
    public static void AttachRow(FrameworkElement row, ColumnDefinition column, double naturalWidth)
    {
        row.Loaded   += (_, _) => GetGroup(row)?.Register(row, column, naturalWidth);
        row.Unloaded += (_, _) => GetGroup(row)?.Unregister(row);
    }

    /// <summary>行を登録する（既に登録済みなら何もしない）。</summary>
    private void Register(FrameworkElement row, ColumnDefinition column, double naturalWidth)
    {
        if (_entries.ContainsKey(row)) return;
        _entries[row] = new Entry(column, naturalWidth + LabelRightGap);
        ScheduleRecompute();
    }

    /// <summary>行の登録を解除する。</summary>
    private void Unregister(FrameworkElement row)
    {
        if (_entries.Remove(row)) ScheduleRecompute();
    }

    /// <summary>再計算をレイアウト後に 1 回だけ実行するよう予約する。</summary>
    private void ScheduleRecompute()
    {
        if (_recomputeScheduled) return;
        _recomputeScheduled = true;

        var dispatcher = _scope?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _recomputeScheduled = false;
            Recompute();
        }));
    }

    /// <summary>
    /// ラベル列の幅を決めて全行へ反映する。
    /// 幅 = clamp(全ラベルの自然幅の最大値, 最小幅, min(最大幅, 行幅 × 割合上限))。
    /// </summary>
    private void Recompute()
    {
        if (_entries.Count == 0) return;

        // セクション幅から「ラベルに割いてよい上限」を求める
        // （まだレイアウト前で幅が取れないときは px 上限だけを使う）。
        var available = _scope?.ActualWidth ?? 0;
        var limit     = available > 0
            ? Math.Min(MaxLabelWidth, available * MaxLabelWidthRatio)
            : MaxLabelWidth;
        limit = Math.Max(MinLabelWidth, limit);

        // 全ラベルが収まる幅を希望値とし、上限でクランプする
        double natural = 0;
        foreach (var e in _entries.Values) natural = Math.Max(natural, e.NaturalWidth);
        var width = Math.Clamp(natural, MinLabelWidth, limit);

        foreach (var e in _entries.Values)
        {
            if (Math.Abs(e.Column.Width.Value - width) <= WidthEpsilon
                && e.Column.Width.GridUnitType == GridUnitType.Pixel) continue;
            e.Column.Width = new GridLength(width);
        }
    }
}
