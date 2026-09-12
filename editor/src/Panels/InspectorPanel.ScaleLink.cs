using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SEEDEditor.Controls;
using SEEDEditor.Panels.Inspector;

namespace SEEDEditor.Panels;

/// <summary>
/// Transform / CanvasTransform のスケール連動トグル（<see cref="InspectorPanel"/> の部分クラス）。
///
/// <para>
/// スケール行の右端に鎖アイコンのトグルを置く。ON の間は、どのチャンネルを
/// 編集（入力・ドラッグ）しても他のチャンネルが同じ比率で変わる
/// （例: (2,4,1) の X を 2→3 にしたら (3,6,1.5)）。
/// </para>
///
/// <para><b>どこに割り込むか</b>:
/// 連動は <see cref="InspectorPanel.CommitTransform"/> の入口 1 か所だけで行う。
/// 入力確定（Enter / フォーカス喪失）・数値ドラッグ・軸ラベルドラッグの
/// すべてが最終的に CommitTransform を通るため、ここで他チャンネルの
/// テキストを書き換えてから送信すれば、
///   ・IPC は従来どおり 1 通（SET_ACTOR_TRANSFORM / SET_CANVAS_TRANSFORM）
///   ・ランタイム側の Undo も 1 操作
/// になる。フィールドごとに別経路を足すと送信が二重になるため、そうしない。</para>
///
/// <para><b>基準値（baseline）を持つ理由</b>:
/// 比率は「編集前の値」からしか求まらない。ドラッグ中は 1 ピクセル動くごとに
/// CommitTransform が呼ばれるので、その都度「直前の値」を基準にすると
/// 比率の掛け算が積み重なり、表示桁への丸めが効いて値がじわじわずれる。
/// そこでドラッグ（＝マウスキャプチャ中）の間は最初に捕まえた基準値を固定し、
/// 毎回そこから絶対値で計算し直す。</para>
///
/// <para><b>状態の寿命</b>:
/// トグルの ON/OFF はアクターごとではなくエディタのセッション内で 1 つだけ持つ
/// （Transform 用と CanvasTransform 用で別々）。永続化しない。</para>
/// </summary>
public partial class InspectorPanel
{
    // ── 連動対象の種別 ─────────────────────────────────────────────────

    /// <summary>
    /// いまインスペクタに出ているスケール行の種別。
    /// チャンネル数（3 or 2）とトグル状態の置き場を決める唯一の判断材料。
    /// </summary>
    private enum ScaleLinkKind
    {
        /// <summary>スケール行が無い（フォルダノード・未選択など）。</summary>
        None,

        /// <summary>3D Transform のスケール（X/Y/Z）。</summary>
        Transform3D,

        /// <summary>2D CanvasTransform のスケール（X/Y）。</summary>
        CanvasTransform2D,
    }

    // ── レイアウト上の位置 ─────────────────────────────────────────────

    /// <summary>
    /// トランスフォームのグリッドにおけるスケール行の行番号（位置=0 / 回転=1 / スケール=2）。
    /// 3D Transform・2D CanvasTransform のどちらも同じ並びなので 1 か所で持つ。
    /// </summary>
    private const int ScaleLinkRowIndex = 2;

    // ── 見た目の定数（マジックナンバー禁止）────────────────────────────

    /// <summary>鎖アイコンの一辺サイズ（px）。スケール行の高さ（24px）に収まる値。</summary>
    private const double ScaleLinkIconSize = 13.0;

    /// <summary>トグルの当たり判定を広げる内側余白（px）。</summary>
    private static readonly Thickness ScaleLinkTogglePadding = new(3, 0, 1, 0);

    /// <summary>連動 ON のときのアイコン不透明度。</summary>
    private const double ScaleLinkOnOpacity = 1.0;

    /// <summary>連動 OFF のときのアイコン不透明度（主張しすぎない）。</summary>
    private const double ScaleLinkOffOpacity = 0.35;

    /// <summary>連動 ON のときのアイコン色（有効であることを一目で分かるように着色する）。</summary>
    private static readonly Color ScaleLinkOnColor = Color.FromRgb(0x61, 0xAF, 0xEF);

    // ── アイコンキー（editor/gen_icons.py の CATALOG と対応）───────────

    /// <summary>連動 ON のアイコン（つながった鎖）。</summary>
    private const string ScaleLinkOnIconKey = "Icon.Link";

    /// <summary>連動 OFF のアイコン（切れた鎖）。</summary>
    private const string ScaleLinkOffIconKey = "Icon.LinkOff";

    // ── 表示文言 ───────────────────────────────────────────────────────

    /// <summary>連動 ON のツールチップ。</summary>
    private const string ScaleLinkOnToolTip =
        "スケール連動: ON\nどれか 1 つを編集すると、他のチャンネルも同じ比率で変わります。\n"
        + "クリックで解除。（エディタを閉じるまでの一時設定）";

    /// <summary>連動 OFF のツールチップ。</summary>
    private const string ScaleLinkOffToolTip =
        "スケール連動: OFF\nクリックすると、どれか 1 つを編集したときに他のチャンネルも"
        + "同じ比率で変わるようになります。\n（エディタを閉じるまでの一時設定）";

    // ── セッション状態（永続化しない）──────────────────────────────────

    /// <summary>3D Transform のスケール連動が ON か。</summary>
    private bool _scaleLinkEnabled3D;

    /// <summary>2D CanvasTransform のスケール連動が ON か。</summary>
    private bool _scaleLinkEnabled2D;

    /// <summary>いま表示中のスケール行の種別。</summary>
    private ScaleLinkKind _scaleLinkKind = ScaleLinkKind.None;

    /// <summary>
    /// 編集前のスケール値（チャンネル順）。比率計算の分母になる。
    /// スケール行を組み立てた時点と、連動を適用し終えた時点で取り直す。
    /// </summary>
    private float[]? _scaleLinkBaseline;

    /// <summary>
    /// ドラッグ中に固定する基準値と編集チャンネル。
    /// ドラッグ（マウスキャプチャ）が続く間だけ有効で、終わったら捨てる。
    /// </summary>
    private (float[] Baseline, int Channel)? _scaleLinkGesture;

    // ── UI 生成 ────────────────────────────────────────────────────────

    /// <summary>
    /// スケール行の右端へ鎖トグルを差し込み、基準値を取り直す。
    ///
    /// グリッドの列は呼び出し側（BuildXYGrid / BuildXYZGrid）を書き換えず、
    /// ここで 1 列だけ足す。位置・回転の行はこの列に何も置かないので、
    /// 列幅は Auto のまま 0 にならず、他の行のレイアウトも変わらない。
    /// </summary>
    /// <param name="grid">スケール行が属するグリッド。</param>
    /// <param name="row">スケール行の行番号。</param>
    /// <param name="kind">スケール行の種別（チャンネル数とトグル状態の置き場を決める）。</param>
    private void AttachScaleLinkToggle(Grid grid, int row, ScaleLinkKind kind)
    {
        _scaleLinkKind     = kind;
        _scaleLinkGesture  = null;
        CaptureScaleLinkBaseline();

        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        int column = grid.ColumnDefinitions.Count - 1;

        var toggle = BuildScaleLinkToggle(kind);
        Grid.SetRow(toggle, row);
        Grid.SetColumn(toggle, column);
        grid.Children.Add(toggle);
    }

    /// <summary>
    /// 鎖トグルの要素を作る。状態はパネル側のフィールドが持ち、この要素は表示だけを担う。
    /// </summary>
    private FrameworkElement BuildScaleLinkToggle(ScaleLinkKind kind)
    {
        var host = new Border
        {
            Background          = Brushes.Transparent,
            Padding             = ScaleLinkTogglePadding,
            Cursor              = Cursors.Hand,
            VerticalAlignment   = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        ApplyScaleLinkToggleVisual(host, kind);

        // クリックでトグル。フィールドのフォーカス移動を起こさないよう必ず握りつぶす。
        host.PreviewMouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            SetScaleLinkEnabled(kind, !IsScaleLinkEnabled(kind));
            // ON にした瞬間の値を基準にする（OFF の間に編集された値から始める）
            CaptureScaleLinkBaseline();
            _scaleLinkGesture = null;
            ApplyScaleLinkToggleVisual(host, kind);
        };

        return host;
    }

    /// <summary>トグルの見た目（アイコン・色・不透明度・ツールチップ）を現在の状態に合わせる。</summary>
    private void ApplyScaleLinkToggleVisual(Border host, ScaleLinkKind kind)
    {
        bool enabled = IsScaleLinkEnabled(kind);

        var icon = AppIcon.Create(
            enabled ? ScaleLinkOnIconKey : ScaleLinkOffIconKey,
            ScaleLinkIconSize);
        // ON のときだけ明示的に着色する（OFF は親の Foreground を継承させる）
        if (enabled) icon.Foreground = new SolidColorBrush(ScaleLinkOnColor);

        host.Child   = icon;
        host.Opacity = enabled ? ScaleLinkOnOpacity : ScaleLinkOffOpacity;
        host.ToolTip = enabled ? ScaleLinkOnToolTip : ScaleLinkOffToolTip;
    }

    // ── 状態アクセス ───────────────────────────────────────────────────

    /// <summary>指定種別の連動が ON か。</summary>
    private bool IsScaleLinkEnabled(ScaleLinkKind kind) => kind switch
    {
        ScaleLinkKind.Transform3D       => _scaleLinkEnabled3D,
        ScaleLinkKind.CanvasTransform2D => _scaleLinkEnabled2D,
        _                               => false,
    };

    /// <summary>指定種別の連動 ON/OFF を設定する。</summary>
    private void SetScaleLinkEnabled(ScaleLinkKind kind, bool enabled)
    {
        switch (kind)
        {
            case ScaleLinkKind.Transform3D:       _scaleLinkEnabled3D = enabled; break;
            case ScaleLinkKind.CanvasTransform2D: _scaleLinkEnabled2D = enabled; break;
        }
    }

    /// <summary>
    /// スケール行の参照が失われた（UI 再構築・選択解除）ときに連動の作業状態を捨てる。
    /// ON/OFF 自体はセッション設定なので残す。
    /// </summary>
    private void ResetScaleLinkState()
    {
        _scaleLinkKind     = ScaleLinkKind.None;
        _scaleLinkBaseline = null;
        _scaleLinkGesture  = null;
    }

    /// <summary>
    /// 現在表示中のスケールチャンネルの TextBox を順に返す。
    /// 1 つでも欠けている（UI 未構築）なら null。
    /// </summary>
    private TextBox[]? GetScaleLinkBoxes()
    {
        switch (_scaleLinkKind)
        {
            case ScaleLinkKind.Transform3D:
                if (_tbSx is null || _tbSy is null || _tbSz is null) return null;
                return [_tbSx, _tbSy, _tbSz];

            case ScaleLinkKind.CanvasTransform2D:
                if (_tbSx is null || _tbSy is null) return null;
                return [_tbSx, _tbSy];

            default:
                return null;
        }
    }

    /// <summary>現在のスケールフィールドの値を基準値として取り込む。</summary>
    private void CaptureScaleLinkBaseline()
    {
        var boxes = GetScaleLinkBoxes();
        if (boxes is null) { _scaleLinkBaseline = null; return; }

        var values = new float[boxes.Length];
        for (int i = 0; i < boxes.Length; i++)
        {
            if (!float.TryParse(boxes[i].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
            {
                // 1 つでも読めなければ基準値を持たない（次の確定時に取り直す）
                _scaleLinkBaseline = null;
                return;
            }
            values[i] = v;
        }
        _scaleLinkBaseline = values;
    }

    // ── 連動の適用 ─────────────────────────────────────────────────────

    /// <summary>
    /// 送信直前に呼び、連動 ON なら他チャンネルのテキストを書き換える。
    ///
    /// <para>呼び出し元は <see cref="InspectorPanel.CommitTransform"/> の入口ただ 1 か所。
    /// 書き換えたあとに CommitTransform が全チャンネルを読んで 1 通の
    /// SET_*_TRANSFORM を送るため、連動しても IPC と Undo の粒度は変わらない。</para>
    ///
    /// <para>位置・回転・ピボット・アンカーの編集でもこの関数は通るが、
    /// その場合スケールは基準値から変化していないので何もしない。</para>
    /// </summary>
    private void ApplyScaleLinkBeforeCommit()
    {
        if (_scaleLinkKind == ScaleLinkKind.None) return;

        var boxes = GetScaleLinkBoxes();
        if (boxes is null) return;

        // 現在値を読む。読めない文字列が混じっている間は連動しない
        // （ParseOrRestore が CommitTransform 側で復元するので、次の確定で追いつく）。
        var current = new float[boxes.Length];
        for (int i = 0; i < boxes.Length; i++)
        {
            if (!float.TryParse(boxes[i].Text, NumberStyles.Float, CultureInfo.InvariantCulture, out current[i]))
                return;
        }

        // 連動 OFF: 基準値だけ最新に保つ（ON にした直後に古い値を使わないため）
        if (!IsScaleLinkEnabled(_scaleLinkKind))
        {
            _scaleLinkBaseline = current;
            _scaleLinkGesture  = null;
            return;
        }

        // ドラッグ中か。NumericDragBehavior は TextBox を、軸ラベルは自身を
        // マウスキャプチャするので、どちらも Mouse.Captured で見分けられる。
        bool gestureActive = _isDraggingTransform || Mouse.Captured is not null;

        float[] baseline;
        int     channel;

        if (gestureActive && _scaleLinkGesture is { } gesture && gesture.Baseline.Length == current.Length)
        {
            // ドラッグ継続中: 最初に捕まえた基準値と編集チャンネルを使い続ける
            baseline = gesture.Baseline;
            channel  = gesture.Channel;
        }
        else
        {
            _scaleLinkGesture = null;

            if (_scaleLinkBaseline is null || _scaleLinkBaseline.Length != current.Length)
            {
                _scaleLinkBaseline = current;
                return;
            }

            // 変化したチャンネルが 1 つに定まらない（0 個＝スケール以外の編集、
            // 2 個以上＝基準値が古い）なら連動しない。基準値だけ取り直す。
            var changed = ScaleLinkCalculator.FindSingleChangedChannel(_scaleLinkBaseline, current);
            if (changed is null)
            {
                _scaleLinkBaseline = current;
                return;
            }

            baseline = _scaleLinkBaseline;
            channel  = changed.Value;
        }

        var next = ScaleLinkCalculator.Compute(baseline, channel, current[channel]);

        // 連動先の表示桁数は編集中フィールドに合わせる
        // （ドラッグ中は刻み幅で桁数が変わるため、固定桁だと見た目が食い違う）。
        int decimals = ScaleLinkCalculator.DecimalPlacesOf(boxes[channel].Text);
        for (int i = 0; i < boxes.Length; i++)
        {
            if (i == channel) continue;
            if (MathF.Abs(next[i] - current[i]) <= ScaleLinkCalculator.ChangeEpsilon) continue;
            boxes[i].Text = ScaleLinkCalculator.Format(next[i], decimals);
        }

        if (gestureActive)
        {
            // ドラッグ中は基準値を動かさない（掛け算の積み重ねによるずれを防ぐ）
            _scaleLinkGesture = (baseline, channel);
        }
        else
        {
            _scaleLinkGesture  = null;
            _scaleLinkBaseline = next;
        }
    }
}
