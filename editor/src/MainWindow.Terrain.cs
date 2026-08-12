// ============================================================
//  MainWindow.Terrain.cs — 地形（terrain）編集モードのエディタ UI
//
//  担当:
//   - シーン編集モードコンボ（common / terrain）の切り替え
//   - 地形ツールバー（盛る/掘る/均す/平坦化・半径/強度スライダー・初期化/保存）
//   - 低レベルマウスフックによるブラシ入力
//       terrain モード中、シーンビュー（ランタイム HWND）上の左ドラッグを
//       一定間隔で TERRAIN_BRUSH として送る。左クリックはランタイムの
//       選択/ギズモへ届かないよう飲み込む（＝terrain モード中は選択/ギズモ無効）。
//       右ドラッグ・WASD 等のカメラ操作には一切触れないため従来どおり効く。
//   - 地形コマンド（TERRAIN_INIT / TERRAIN_SAVE）の送信と結果のステータス表示
//
//  【設計メモ — ヒエラルキー整合】
//   地形アクター（terrain ルート → chunk_X_Y_Z → mesh）はランタイムが生成し、
//   handle_terrain_init が send_hierarchy() を呼ぶためエディタのヒエラルキーへ
//   自動反映される。シーン保存はエディタが SAVE_SCENE をランタイムへ送り、
//   ランタイムが自分の scene（terrain アクター＋TerrainChunkComponent を含む）を
//   シリアライズするため、地形アクターは保存で消えない。よってここでは追加の
//   同期機構は不要（ランタイムがシーンの正・エディタは指示役）。
// ============================================================

using System;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SEEDEditor.Runtime;
using static SEEDEditor.Native.NativeInterop;

namespace SEEDEditor;

public partial class MainWindow
{
    // ── 定数 ──────────────────────────────────────────────────

    /// <summary>ブラシ連続送信のスロットル間隔（ミリ秒）。ドラッグ中はこの間隔で TERRAIN_BRUSH を送る。</summary>
    private const long TerrainBrushThrottleMs = 40;

    /// <summary>地形ブラシ演算 op 値（ランタイム BrushOp と一致）。</summary>
    private const int TerrainOpRaise   = 0; // Add     = 盛る
    private const int TerrainOpCarve   = 1; // Subtract= 掘る（洞窟）
    private const int TerrainOpSmooth  = 2; // Smooth  = 均す
    private const int TerrainOpFlatten = 3; // Flatten = 平坦化

    /// <summary>
    /// ペイントツールを表す擬似 op 値（Terrain T2）。
    /// ランタイムの BrushOp には存在せず、この値のときだけ TERRAIN_BRUSH ではなく
    /// TERRAIN_PAINT を送る（密度編集とレイヤペイントで IPC コマンドが別のため）。
    /// </summary>
    private const int TerrainOpPaint = 100;

    /// <summary>
    /// 散布ツールを表す擬似 op 値（Terrain T3）。
    /// ペイントと同様にランタイムの BrushOp には存在せず、この値のときだけ
    /// TERRAIN_SCATTER_BRUSH を送る（形状編集・レイヤペイントとは別コマンドのため）。
    /// </summary>
    private const int TerrainOpScatter = 101;

    /// <summary>
    /// カバーツールを表す擬似 op 値（地表カバー場 I3.1 の手編集）。
    /// ペイント・散布と同様にランタイムの BrushOp には存在せず、この値のときだけ
    /// TERRAIN_COVER_BRUSH を送る（積もった雪・落ち葉を消す／敷く専用コマンド）。
    /// </summary>
    private const int TerrainOpCover = 102;

    /// <summary>ブラシの消去フラグ値（IPC 文字列の erase 引数。ランタイムと一致させる）。</summary>
    private const int TerrainScatterEraseOff = 0;
    private const int TerrainScatterEraseOn  = 1;

    /// <summary>素材選択コンボが空／未選択のときに使うカバーブラシのフォールバック素材 ID（空＝ランタイム側で無効扱い）。</summary>
    private const string TerrainCoverFallbackMaterialId = "";

    /// <summary>目標量スライダーが未生成のときのフォールバック目標量（0〜1）。</summary>
    private const double TerrainCoverAmountFallback = 1.0;

    /// <summary>プロップ選択コンボが空／未選択のときに使う散布ブラシのフォールバック prop_id（空＝ランタイム側で無効扱い）。</summary>
    private const string TerrainScatterFallbackPropId = "";

    /// <summary>散布密度スライダーが未生成のときのフォールバック密度（点/m²）。</summary>
    private const double TerrainScatterDensityFallback = 4.0;

    /// <summary>Ctrl+ホイール 1 ノッチあたりのブラシ半径倍率変化量（乗算式。0.10=±10%）。</summary>
    private const double TerrainRadiusWheelFactor = 0.10;

    /// <summary>Shift+ホイール 1 ノッチあたりのブラシ強度変化量（加算式。0〜1 レンジに対して）。</summary>
    private const double TerrainStrengthWheelStep = 0.05;

    /// <summary>ハイトマップ高さスケール入力欄が空/不正なときのデフォルト値（メートル）。</summary>
    private const double TerrainHeightScaleDefault = 10.0;

    /// <summary>マウスホイールの 1 ノッチ分の mouseData 値（Win32 標準）。</summary>
    private const int WheelDeltaPerNotch = 120;

    // ── 状態 ──────────────────────────────────────────────────

    /// <summary>terrain 編集モードが有効かどうか（モードコンボが terrain）。</summary>
    private bool _terrainMode;

    /// <summary>現在選択中のブラシ演算（TerrainOpRaise 等）。</summary>
    private int _terrainOp = TerrainOpRaise;

    /// <summary>左ボタンでブラシストローク中かどうか（ドラッグ判定）。</summary>
    private bool _terrainStroking;

    /// <summary>セッション中に一度でも地形を初期化したか（再初期化の確認表示に使用）。</summary>
    private bool _terrainInited;

    /// <summary>ブラシ送信のスロットル計測用ストップウォッチ。</summary>
    private readonly Stopwatch _terrainBrushThrottle = new();

    /// <summary>ブラシ範囲プレビュー送信のスロットル計測用ストップウォッチ（ホバー用・ストローク用とは別）。</summary>
    private readonly Stopwatch _terrainPreviewThrottle = new();

    /// <summary>プレビュー（ワイヤスフィア）を現在表示中か。ビューポート離脱時に OFF を 1 度だけ送るために追跡する。</summary>
    private bool _terrainPreviewActive;

    /// <summary>
    /// プロップ選択コンボの各行に対応する prop_id（props.json の並び順）。
    /// コンボの表示文字列は「名前 (ID)」なので、IPC へ渡す ID はここから引く。
    /// </summary>
    private readonly System.Collections.Generic.List<string> _terrainPropIds = new();

    /// <summary>
    /// カバー素材選択コンボの各行に対応する素材 ID（cover_materials.json の並び順）。
    /// コンボの表示文字列は「名前 (ID)」なので、IPC へ渡す ID はここから引く。
    /// </summary>
    private readonly System.Collections.Generic.List<string> _terrainCoverMaterialIds = new();

    /// <summary>
    /// ブラシ形状マスク（ブラシテクスチャ）のパス。null／空 = 未指定（従来どおりの円形フォールオフ）。
    ///
    /// 半径・強度と同じ「ツールの現在設定」であり、レイヤペイントブラシとカバーブラシで共有する。
    /// 設定ウィンドウは開閉のたびに作り直されるため、値の実体はこちら（MainWindow）が持つ。
    /// ランタイム側の実体は `TerrainState::brush_mask_path` で、`TERRAIN_BRUSH_MASK` で同期する。
    /// </summary>
    private string? _terrainBrushMaskPath;

    /// <summary>低レベルマウスフックのコールバック（GC 回収防止のためフィールド保持）。</summary>
    private LowLevelMouseProc? _terrainMouseProc;

    /// <summary>低レベルマウスフックのハンドル。</summary>
    private nint _terrainMouseHook;

    /// <summary>
    /// 開いている地形設定ウィンドウ（非モーダル）。多重起動を防ぐために保持する。
    /// 閉じられたら Closed イベントで null に戻す。
    /// </summary>
    private SEEDEditor.Terrain.TerrainSettingsWindow? _terrainSettingsWindow;

    // ── 初期化 / フック設置 ────────────────────────────────────

    /// <summary>地形ツールバー UI（スライダー表示ラベル）の初期同期とイベント接続。</summary>
    private void InitTerrainUi()
    {
        if (SldTerrainRadius != null)
        {
            SldTerrainRadius.ValueChanged += (_, _) => UpdateTerrainRadiusLabel();
            UpdateTerrainRadiusLabel();
        }
        if (SldTerrainStrength != null)
        {
            SldTerrainStrength.ValueChanged += (_, _) => UpdateTerrainStrengthLabel();
            UpdateTerrainStrengthLabel();
        }
        if (SldTerrainDensity != null)
        {
            SldTerrainDensity.ValueChanged += (_, _) => UpdateTerrainDensityLabel();
            UpdateTerrainDensityLabel();
        }
        if (SldTerrainCoverAmount != null)
        {
            SldTerrainCoverAmount.ValueChanged += (_, _) => UpdateTerrainCoverAmountLabel();
            UpdateTerrainCoverAmountLabel();
        }
        // レイヤ選択コンボは layers.json（レイヤ総数自由）から動的に作る。
        RefreshTerrainLayerCombo();
        // プロップ選択コンボは props.json（プロップ総数自由）から動的に作る。
        RefreshTerrainPropCombo();
        // カバー素材コンボは cover_materials.json（素材総数自由）から動的に作る。
        RefreshTerrainCoverMaterialCombo();
        // 既定は消去モードなので、素材／目標量の表示可否を初期状態へ合わせる。
        UpdateTerrainCoverPaintParamVisibility();
    }

    /// <summary>
    /// ツールバーのカバー素材選択コンボを cover_materials.json の内容から作り直す。
    /// <para>
    /// レイヤ／プロップのコンボ（<see cref="RefreshTerrainLayerCombo"/> /
    /// <see cref="RefreshTerrainPropCombo"/>）と同じ流儀。素材総数は自由なので
    /// XAML に固定項目は置かない。選択位置は可能な限り維持する。
    /// IPC へ渡す素材 ID は表示文字列ではなく <see cref="_terrainCoverMaterialIds"/> から引く。
    /// </para>
    /// </summary>
    private void RefreshTerrainCoverMaterialCombo()
    {
        if (CmbTerrainCoverMaterial == null) return;

        int previous = CmbTerrainCoverMaterial.SelectedIndex;
        var doc = SEEDEditor.Terrain.TerrainCoverMaterialsDocument.Load(AssetsPath);

        CmbTerrainCoverMaterial.Items.Clear();
        _terrainCoverMaterialIds.Clear();
        foreach (var m in doc.Materials)
        {
            CmbTerrainCoverMaterial.Items.Add(
                SEEDEditor.Terrain.TerrainCoverMaterialsDocument.FormatComboEntry(m.Name, m.Id));
            _terrainCoverMaterialIds.Add(m.Id);
        }

        CmbTerrainCoverMaterial.SelectedIndex = _terrainCoverMaterialIds.Count == 0
            ? -1
            : Math.Clamp(previous < 0 ? 0 : previous, 0, _terrainCoverMaterialIds.Count - 1);
    }

    /// <summary>
    /// ツールバーのカバー素材選択コンボから、敷く素材の ID を返す。
    /// 未選択・UI 未生成のときは空文字を返す（消去ブラシでは素材が読まれないため無害）。
    /// </summary>
    private string GetSelectedTerrainCoverMaterialId()
    {
        int idx = CmbTerrainCoverMaterial?.SelectedIndex ?? -1;
        return idx >= 0 && idx < _terrainCoverMaterialIds.Count
            ? _terrainCoverMaterialIds[idx]
            : TerrainCoverFallbackMaterialId;
    }

    /// <summary>
    /// ツールバーのプロップ選択コンボを props.json の内容から作り直す。
    /// <para>
    /// レイヤコンボ（<see cref="RefreshTerrainLayerCombo"/>）と同じ流儀。
    /// 地形設定ウィンドウの散布タブでプロップを増減・並べ替えて保存した直後にも呼ばれる。
    /// 選択位置は可能な限り維持する（範囲外になった場合は先頭へ戻す）。
    /// IPC へ渡す prop_id は表示文字列ではなく <see cref="_terrainPropIds"/> から引く。
    /// </para>
    /// </summary>
    private void RefreshTerrainPropCombo()
    {
        if (CmbTerrainProp == null) return;

        int previous = CmbTerrainProp.SelectedIndex;
        var doc = SEEDEditor.Terrain.TerrainPropsDocument.Load(AssetsPath);

        CmbTerrainProp.Items.Clear();
        _terrainPropIds.Clear();
        foreach (var p in doc.Props)
        {
            CmbTerrainProp.Items.Add(
                SEEDEditor.Terrain.TerrainSettingsWindow.FormatPropComboEntry(p.Name, p.Id));
            _terrainPropIds.Add(p.Id);
        }

        CmbTerrainProp.SelectedIndex = _terrainPropIds.Count == 0
            ? -1
            : Math.Clamp(previous < 0 ? 0 : previous, 0, _terrainPropIds.Count - 1);
    }

    /// <summary>
    /// ツールバーのプロップ選択コンボから、撒く対象のプロップ ID を返す。
    /// 未選択・UI 未生成のときは空文字を返す（ランタイム側で無効な散布として弾かれる）。
    /// </summary>
    private string GetSelectedTerrainPropId()
    {
        int idx = CmbTerrainProp?.SelectedIndex ?? -1;
        return idx >= 0 && idx < _terrainPropIds.Count
            ? _terrainPropIds[idx]
            : TerrainScatterFallbackPropId;
    }

    /// <summary>
    /// ツールバーのレイヤ選択コンボを layers.json の内容から作り直す。
    /// <para>
    /// レイヤ総数は自由（最大 TerrainLayerDefaults.MaxLayers）なので固定項目は持たない。
    /// 地形設定ウィンドウでレイヤを増減・並べ替えた直後にも呼ばれ、即座に反映される。
    /// 選択レイヤ番号は可能な限り維持する（範囲外になった場合は先頭へ戻す）。
    /// </para>
    /// </summary>
    private void RefreshTerrainLayerCombo()
    {
        if (CmbTerrainLayer == null) return;

        int previous = CmbTerrainLayer.SelectedIndex;
        var doc = SEEDEditor.Terrain.TerrainLayersDocument.Load(AssetsPath);

        CmbTerrainLayer.Items.Clear();
        for (int i = 0; i < doc.Layers.Count; i++)
        {
            CmbTerrainLayer.Items.Add(
                SEEDEditor.Terrain.TerrainSettingsWindow.FormatLayerListEntry(i, doc.Layers[i].Name));
        }

        CmbTerrainLayer.SelectedIndex = doc.Layers.Count == 0
            ? -1
            : Math.Clamp(previous < 0 ? 0 : previous, 0, doc.Layers.Count - 1);
    }

    /// <summary>
    /// 「⚙ 設定」ボタン: 地形設定ウィンドウを開く。
    /// 既に開いている場合は新規生成せず前面化する（多重起動防止）。
    /// 非モーダルなので、開いたままシーンビューでブラシ操作を続けられる。
    /// </summary>
    private void OnTerrainSettings(object sender, RoutedEventArgs e)
    {
        if (_terrainSettingsWindow != null)
        {
            // 最小化されている場合も確実に見えるよう、通常表示へ戻してから前面化する。
            if (_terrainSettingsWindow.WindowState == WindowState.Minimized)
                _terrainSettingsWindow.WindowState = WindowState.Normal;
            _terrainSettingsWindow.Activate();
            return;
        }

        var win = new SEEDEditor.Terrain.TerrainSettingsWindow(
            AssetsPath,
            // ランタイム未起動でも設定編集自体は可能にする（送信だけ黙って捨てる）。
            sendToRuntime: msg => _runtimeManager?.SendToRuntime(msg),
            // 保存後はツールバーのレイヤ／プロップ選択コンボを作り直して構成変更を反映する。
            // 保存後はツールバーの各選択コンボを作り直して構成変更を反映する
            // （カバー素材は設定ウィンドウでは編集しないが、TERRAIN_RELOAD_LAYERS で
            //   ランタイム側が cover_materials.json を読み直すため、表示も揃えておく）。
            onSaved: () =>
            {
                RefreshTerrainLayerCombo();
                RefreshTerrainPropCombo();
                RefreshTerrainCoverMaterialCombo();
            },
            // ブラシ形状マスクは「ツールの現在設定」なので値の実体はここ（MainWindow）が持つ。
            // 設定ウィンドウは開閉のたびに作り直されるため、ウィンドウ側に持たせると選択が消える。
            brushMaskPath: _terrainBrushMaskPath,
            onBrushMaskChanged: SetTerrainBrushMask)
        {
            Owner = this,
        };
        win.Closed += (_, _) => _terrainSettingsWindow = null;
        _terrainSettingsWindow = win;
        win.Show();
    }

    private void UpdateTerrainRadiusLabel()
    {
        if (TxtTerrainRadius != null && SldTerrainRadius != null)
            TxtTerrainRadius.Text = $"{SldTerrainRadius.Value:F1}m";
    }

    private void UpdateTerrainStrengthLabel()
    {
        if (TxtTerrainStrength != null && SldTerrainStrength != null)
            TxtTerrainStrength.Text = $"{SldTerrainStrength.Value:F2}";
    }

    private void UpdateTerrainDensityLabel()
    {
        if (TxtTerrainDensity != null && SldTerrainDensity != null)
            TxtTerrainDensity.Text = $"{SldTerrainDensity.Value:F1}";
    }

    private void UpdateTerrainCoverAmountLabel()
    {
        if (TxtTerrainCoverAmount != null && SldTerrainCoverAmount != null)
            TxtTerrainCoverAmount.Text = $"{SldTerrainCoverAmount.Value:F2}";
    }

    /// <summary>
    /// カバーブラシの「消去」チェックが切り替わったときの表示更新。
    /// 消去中は素材・目標量が使われないため隠す
    /// （選択に無関係なパラメータは出さない、というインスペクタ方針に合わせる）。
    /// </summary>
    private void OnTerrainCoverEraseChanged(object sender, RoutedEventArgs e)
        => UpdateTerrainCoverPaintParamVisibility();

    /// <summary>
    /// カバーブラシの塗り専用パラメータ（素材選択・目標量）の表示可否を、
    /// 「消去」チェックの状態から決める。
    /// </summary>
    private void UpdateTerrainCoverPaintParamVisibility()
    {
        bool erasing = ChkTerrainCoverErase?.IsChecked == true;
        var visibility = erasing ? Visibility.Collapsed : Visibility.Visible;
        if (LblTerrainCoverMaterial != null) LblTerrainCoverMaterial.Visibility = visibility;
        if (CmbTerrainCoverMaterial != null) CmbTerrainCoverMaterial.Visibility = visibility;
        if (SldTerrainCoverAmount   != null) SldTerrainCoverAmount.Visibility   = visibility;
        if (TxtTerrainCoverAmount   != null) TxtTerrainCoverAmount.Visibility   = visibility;
        if (LblTerrainCoverAmount   != null) LblTerrainCoverAmount.Visibility   = visibility;
    }

    /// <summary>
    /// 地形ブラシ入力用の低レベルマウスフックを設置する。フックは常設だが、
    /// コールバックは terrain モードかつ Edit 状態のときだけ作用する（それ以外は素通し）。
    /// キーボードフックと同じスレッド（UI スレッド）で設置・処理する。
    /// </summary>
    private void InstallTerrainMouseHook()
    {
        _terrainMouseProc = TerrainMouseCallback;
        var hMod = GetModuleHandle(null);
        _terrainMouseHook = SetWindowsHookExMouse(WH_MOUSE_LL, _terrainMouseProc, hMod, 0);
        EditorLog.Write($"InstallTerrainMouseHook — hook=0x{_terrainMouseHook:X}");
    }

    private void UninstallTerrainMouseHook()
    {
        if (_terrainMouseHook != 0)
        {
            UnhookWindowsHookEx(_terrainMouseHook);
            _terrainMouseHook = 0;
        }
    }

    // ── マウスフックコールバック（ブラシ入力）────────────────────

    /// <summary>
    /// 低レベルマウスフックのコールバック。terrain モード中のみ:
    ///   - ビューポート上の左ボタン押下でストローク開始＋最初のブラシ送信、左クリックを飲み込む
    ///   - ストローク中の移動をスロットル送信し、移動イベントを飲み込む
    ///   - 左ボタン解放でストローク終了、解放イベントを飲み込む
    /// 左ボタン以外（右＝カメラ回転・中＝等）とビューポート外の入力には一切触れない。
    /// </summary>
    private nint TerrainMouseCallback(int nCode, nint wParam, nint lParam)
    {
        if (nCode >= 0 && _terrainMode && _runtimeManager?.State == EditorState.Edit)
        {
            int message = (int)wParam;
            switch (message)
            {
                case WM_LBUTTONDOWN:
                    if (IsMouseOverViewportHwnd())
                    {
                        // ビューポートをクリックした以上、スクリプトエディタ等のテキスト入力に
                        // 残ったキーボードフォーカスは外す（外さないとキャレットが残り、
                        // カメラキー WASD が文字入力になる）。
                        // 通常はランタイム子 HWND への到達で WM_PARENTNOTIFY が出て
                        // OnViewportPointerPressed が処理するが、terrain モードの左押下は
                        // 直後の return 1 でここが握りつぶすため子へ届かない。だからここで補う。
                        // 低レベルフック内から WPF のフォーカス API を直接叩かないよう
                        // ディスパッチャ経由で実行する。
                        Dispatcher.BeginInvoke(ClearTextInputFocusForViewportClick);

                        _terrainStroking = true;
                        _terrainBrushThrottle.Restart();
                        SendTerrainBrushAtCursor();
                        return (nint)1; // 選択/ギズモへ届かせない
                    }
                    break;

                case WM_MOUSEMOVE:
                    if (_terrainStroking)
                    {
                        if (IsMouseOverViewportHwnd()
                            && _terrainBrushThrottle.ElapsedMilliseconds >= TerrainBrushThrottleMs)
                        {
                            _terrainBrushThrottle.Restart();
                            SendTerrainBrushAtCursor();
                        }
                        // 注意: ここで移動イベントを飲み込んではならない。WH_MOUSE_LL で
                        // WM_MOUSEMOVE を握りつぶすと OS のカーソル移動そのものが止まり、
                        // 「押しながらカーソルを動かせない」不具合になる（実機で発生済み）。
                        // 移動は素通しし、ブラシ送信だけを行う（カメラは右ドラッグなので干渉しない）。
                        break;
                    }
                    // 非ストローク時: ホバー位置のブラシ範囲プレビューを更新する
                    // （押していない間も表示する）。移動イベントは飲み込まず素通しする
                    // （カメラ回転・ランタイム hover を妨げない）。
                    UpdateTerrainBrushPreview();
                    break;

                case WM_LBUTTONUP:
                    if (_terrainStroking)
                    {
                        _terrainStroking = false;
                        // ストローク確定＝1 undo エントリとしてランタイム側に区切りを通知する。
                        _runtimeManager?.SendToRuntime("TERRAIN_STROKE_END");
                        return (nint)1;
                    }
                    break;

                case WM_MOUSEWHEEL:
                    // Ctrl+ホイール＝ブラシ半径、Shift+ホイール＝ブラシ強度の調整。
                    // ビューポート上でのみ作用し、修飾キーが無ければ素通し（従来のカメラズーム等）。
                    if (IsMouseOverViewportHwnd())
                    {
                        bool ctrl  = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
                        bool shift = (GetKeyState(VK_SHIFT)   & 0x8000) != 0;
                        if (ctrl ^ shift) // どちらか一方のみ押下時に処理する（両方/無しは素通し）
                        {
                            var msll = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                            short wheelDelta = unchecked((short)((msll.mouseData >> 16) & 0xFFFF));
                            int notches = wheelDelta / WheelDeltaPerNotch;
                            if (notches != 0)
                            {
                                if (ctrl)
                                    AdjustTerrainRadiusByWheel(notches);
                                else if (_terrainOp == TerrainOpScatter)
                                    // 散布ツールでは「強度」が無意味なので、Shift+ホイールは密度に効かせる
                                    // （半径＝Ctrl・第 2 パラメータ＝Shift、という操作規則自体は共通に保つ）。
                                    AdjustTerrainDensityByWheel(notches);
                                else
                                    AdjustTerrainStrengthByWheel(notches);
                                SendTerrainPreviewNow(); // スライダー変更を即プレビューへ反映
                                return (nint)1; // 飲み込む（カメラズーム等を発生させない）
                            }
                        }
                    }
                    break;
            }
        }
        return CallNextHookEx(_terrainMouseHook, nCode, wParam, lParam);
    }

    /// <summary>
    /// Ctrl+ホイールでブラシ半径を乗算式に変化させる（1 ノッチ ±10%）。
    /// スライダーの Minimum/Maximum でクランプする。
    /// </summary>
    private void AdjustTerrainRadiusByWheel(int notches)
    {
        if (SldTerrainRadius == null) return;
        double val = SldTerrainRadius.Value;
        double newVal = val * (1.0 + TerrainRadiusWheelFactor * notches);
        SldTerrainRadius.Value = Math.Clamp(newVal, SldTerrainRadius.Minimum, SldTerrainRadius.Maximum);
    }

    /// <summary>
    /// Shift+ホイールでブラシ強度を加算式に変化させる（1 ノッチ ±0.05）。
    /// スライダーの Minimum/Maximum でクランプする。
    /// </summary>
    private void AdjustTerrainStrengthByWheel(int notches)
    {
        if (SldTerrainStrength == null) return;
        double val = SldTerrainStrength.Value;
        double newVal = val + TerrainStrengthWheelStep * notches;
        SldTerrainStrength.Value = Math.Clamp(newVal, SldTerrainStrength.Minimum, SldTerrainStrength.Maximum);
    }

    /// <summary>
    /// Shift+ホイールで散布密度を乗算式に変化させる（1 ノッチ ±10%）。
    /// 密度は 0.1〜32 と桁が広いレンジなので、強度（加算式）ではなく
    /// 半径と同じ乗算式にして、どの値域でも同じ感覚で調整できるようにする。
    /// </summary>
    private void AdjustTerrainDensityByWheel(int notches)
    {
        if (SldTerrainDensity == null) return;
        double val = SldTerrainDensity.Value;
        double newVal = val * (1.0 + TerrainRadiusWheelFactor * notches);
        SldTerrainDensity.Value = Math.Clamp(newVal, SldTerrainDensity.Minimum, SldTerrainDensity.Maximum);
    }

    /// <summary>
    /// 現在のカーソル位置（ビューポートローカル物理ピクセル）と、ツールバーの
    /// ブラシ半径/強度を用いて TERRAIN_BRUSH を送信する。
    /// カーソルがビューポート矩形外なら送信しない。
    /// </summary>
    private void SendTerrainBrushAtCursor()
    {
        if (_viewportHost == null || _runtimeManager == null) return;

        GetCursorPos(out var cursor);
        GetWindowRect(_viewportHost.ContainerHwnd, out var rect);
        int lx = cursor.X - rect.Left;
        int ly = cursor.Y - rect.Top;
        if (lx < 0 || ly < 0 || lx >= rect.Right - rect.Left || ly >= rect.Bottom - rect.Top)
            return;

        double radius   = SldTerrainRadius?.Value   ?? 3.0;
        double strength = SldTerrainStrength?.Value ?? 0.5;
        var ci = CultureInfo.InvariantCulture;

        // ペイントツールはレイヤ重みだけを編集する別コマンド（TERRAIN_PAINT）へ振り分ける。
        // 半径/強度スライダーとストローク（undo 単位）の扱いは密度ブラシと共通。
        if (_terrainOp == TerrainOpPaint)
        {
            _runtimeManager.SendToRuntime(
                $"TERRAIN_PAINT:{GetSelectedTerrainLayer()},{lx},{ly},{radius.ToString(ci)},{strength.ToString(ci)}");
            return;
        }

        // 散布ツールはプロップを撒く／消す別コマンド（TERRAIN_SCATTER_BRUSH）へ振り分ける。
        // 強度スライダーは使わず、専用の密度スライダーと消去チェックを使う。
        if (_terrainOp == TerrainOpScatter)
        {
            double density = SldTerrainDensity?.Value ?? TerrainScatterDensityFallback;
            int erase = ChkTerrainScatterErase?.IsChecked == true
                ? TerrainScatterEraseOn
                : TerrainScatterEraseOff;
            _runtimeManager.SendToRuntime(
                $"TERRAIN_SCATTER_BRUSH:{GetSelectedTerrainPropId()},{lx},{ly},"
                + $"{radius.ToString(ci)},{density.ToString(ci)},{erase.ToString(ci)}");
            return;
        }

        // カバーツールは積もったカバー場を消す／敷く別コマンド（TERRAIN_COVER_BRUSH）へ振り分ける。
        // 半径／強度スライダーとストローク（undo 単位）の扱いは密度ブラシと共通で、
        // 素材と目標量は塗りモードのときだけ意味を持つ（消去時はランタイムが無視する）。
        if (_terrainOp == TerrainOpCover)
        {
            double amount = SldTerrainCoverAmount?.Value ?? TerrainCoverAmountFallback;
            int erase = ChkTerrainCoverErase?.IsChecked == true
                ? TerrainScatterEraseOn
                : TerrainScatterEraseOff;
            _runtimeManager.SendToRuntime(
                $"TERRAIN_COVER_BRUSH:{GetSelectedTerrainCoverMaterialId()},{lx},{ly},"
                + $"{radius.ToString(ci)},{strength.ToString(ci)},{amount.ToString(ci)},{erase.ToString(ci)}");
            return;
        }

        _runtimeManager.SendToRuntime(
            $"TERRAIN_BRUSH:{_terrainOp},{lx},{ly},{radius.ToString(ci)},{strength.ToString(ci)}");
    }

    /// <summary>
    /// 非ストローク時のホバーで、カーソル位置のブラシ範囲プレビュー（ワイヤスフィア）を
    /// スロットル付きで更新する。ビューポート上なら TERRAIN_BRUSH_PREVIEW を送り、
    /// ビューポート外へ出た瞬間に一度だけ TERRAIN_BRUSH_PREVIEW_OFF を送る。
    /// ランタイムはヒットが無ければ自動で非表示にするため、ここでは座標送信のみでよい。
    /// </summary>
    private void UpdateTerrainBrushPreview()
    {
        if (_viewportHost == null || _runtimeManager == null) return;

        if (!IsMouseOverViewportHwnd())
        {
            // ビューポート外へ出たら 1 度だけ非表示指示を送る（重複送信を避ける）。
            if (_terrainPreviewActive)
            {
                _terrainPreviewActive = false;
                _runtimeManager.SendToRuntime("TERRAIN_BRUSH_PREVIEW_OFF");
            }
            return;
        }

        // スロットル（ブラシと同じ 40ms）。初回（未計測）は即送信する。
        if (_terrainPreviewThrottle.IsRunning
            && _terrainPreviewThrottle.ElapsedMilliseconds < TerrainBrushThrottleMs)
            return;
        _terrainPreviewThrottle.Restart();

        SendTerrainPreviewAtCursor();
    }

    /// <summary>
    /// ホイール操作（半径/強度変更）直後など、スロットルを無視して現在カーソル位置に
    /// 1 回だけブラシプレビューを再送する。ビューポート外なら何もしない。
    /// スライダーの見た目（球の大きさ・色）を即座に反映させるために使う。
    /// </summary>
    private void SendTerrainPreviewNow()
    {
        if (_viewportHost == null || _runtimeManager == null) return;
        if (!IsMouseOverViewportHwnd()) return;

        _terrainPreviewThrottle.Restart(); // 直後の hover 更新が二重送信しないようスロットルも同期
        SendTerrainPreviewAtCursor();
    }

    /// <summary>
    /// 現在のカーソル位置（ビューポートローカル物理ピクセル）とツールバーの半径/強度を用いて
    /// TERRAIN_BRUSH_PREVIEW を送信する共通処理。UpdateTerrainBrushPreview / SendTerrainPreviewNow
    /// から呼ばれる。ビューポート矩形外なら送信しない。
    /// </summary>
    private void SendTerrainPreviewAtCursor()
    {
        if (_viewportHost == null || _runtimeManager == null) return;

        GetCursorPos(out var cursor);
        GetWindowRect(_viewportHost.ContainerHwnd, out var rect);
        int lx = cursor.X - rect.Left;
        int ly = cursor.Y - rect.Top;
        if (lx < 0 || ly < 0 || lx >= rect.Right - rect.Left || ly >= rect.Bottom - rect.Top)
            return;

        double radius   = SldTerrainRadius?.Value   ?? 3.0;
        double strength = SldTerrainStrength?.Value ?? 0.5;
        var ci = CultureInfo.InvariantCulture;
        _terrainPreviewActive = true;
        _runtimeManager.SendToRuntime(
            $"TERRAIN_BRUSH_PREVIEW:{lx},{ly},{radius.ToString(ci)},{strength.ToString(ci)}");
    }

    // ── モード / ツールバー イベント ─────────────────────────────

    /// <summary>シーン編集モードコンボ（common / terrain）の切り替え。</summary>
    private void OnSceneModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbSceneMode?.SelectedItem is not ComboBoxItem item) return;
        bool terrain = (item.Tag as string) == "terrain";
        _terrainMode = terrain;

        // terrain モードの UI は 2 つある（どちらも同じ条件で出し入れする）:
        //   ・上部の横バー（TerrainToolbar）  = 地形全体に効くグローバル操作
        //   ・左の縦パネル（TerrainSidePanel）= ブラシパラメータ＋ツール選択
        // 縦パネルは Auto 幅の列に置いてあるので、Collapsed にすると列ごと 0 幅に潰れ、
        // common モードではビューポートが従来どおりの広さに戻る。
        var terrainUiVisibility = terrain ? Visibility.Visible : Visibility.Collapsed;
        if (TerrainToolbar != null)
            TerrainToolbar.Visibility = terrainUiVisibility;
        if (TerrainSidePanel != null)
            TerrainSidePanel.Visibility = terrainUiVisibility;

        // モードを抜けたら進行中のストロークを打ち切り、ブラシプレビューを消す。
        if (!terrain)
        {
            // 進行中ストロークがあれば、打ち切る前に確定通知を送る
            // （未確定ストロークが undo スタックにリークするのを防ぐ）。
            if (_terrainStroking)
                _runtimeManager?.SendToRuntime("TERRAIN_STROKE_END");
            _terrainStroking = false;
            if (_terrainPreviewActive)
            {
                _terrainPreviewActive = false;
                _runtimeManager?.SendToRuntime("TERRAIN_BRUSH_PREVIEW_OFF");
            }
        }

        if (TxtTerrainStatus != null) TxtTerrainStatus.Text = "";
    }

    /// <summary>ブラシツール（盛る/掘る/均す/平坦化）トグルの選択変更。</summary>
    private void OnTerrainToolChanged(object sender, RoutedEventArgs e)
    {
        if (BtnTerrainRaise?.IsChecked == true)        _terrainOp = TerrainOpRaise;
        else if (BtnTerrainCarve?.IsChecked == true)   _terrainOp = TerrainOpCarve;
        else if (BtnTerrainSmooth?.IsChecked == true)  _terrainOp = TerrainOpSmooth;
        else if (BtnTerrainFlatten?.IsChecked == true) _terrainOp = TerrainOpFlatten;
        else if (BtnTerrainPaint?.IsChecked == true)   _terrainOp = TerrainOpPaint;
        else if (BtnTerrainScatter?.IsChecked == true) _terrainOp = TerrainOpScatter;
        else if (BtnTerrainCover?.IsChecked == true)   _terrainOp = TerrainOpCover;

        // レイヤ選択 UI はペイントツールのときだけ意味を持つため、それ以外では隠す
        // （選択に無関係なパラメータは出さない、というインスペクタ方針に合わせる）。
        if (TerrainLayerPanel != null)
        {
            TerrainLayerPanel.Visibility =
                _terrainOp == TerrainOpPaint ? Visibility.Visible : Visibility.Collapsed;
        }

        // プロップ選択・密度・消去は散布ツールのときだけ意味を持つため、同じ方針で隠す。
        if (TerrainScatterPanel != null)
        {
            TerrainScatterPanel.Visibility =
                _terrainOp == TerrainOpScatter ? Visibility.Visible : Visibility.Collapsed;
        }

        // 素材選択・目標量・消去はカバーツールのときだけ意味を持つため、同じ方針で隠す。
        if (TerrainCoverPanel != null)
        {
            TerrainCoverPanel.Visibility =
                _terrainOp == TerrainOpCover ? Visibility.Visible : Visibility.Collapsed;
        }

        // 強度スライダーは散布ツールでは使わない（密度スライダーが代わりを務める）。
        // カバーツールでは「1 発でどれだけ消す／敷くか」として使うので表示したままにする。
        if (TerrainStrengthPanel != null)
        {
            TerrainStrengthPanel.Visibility =
                _terrainOp == TerrainOpScatter ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    /// <summary>
    /// ブラシ形状マスク（ブラシテクスチャ）を設定・解除し、ランタイムへ同期する。
    ///
    /// 地形設定ウィンドウの「ブラシ」タブから呼ばれる。`path` が null／空なら解除で、
    /// ランタイムは従来どおりの円形フォールオフへ戻る。
    /// ブラシ 1 発ごとの IPC（TERRAIN_PAINT / TERRAIN_COVER_BRUSH）には載せない
    /// ——あちらはカンマ区切りであり、カンマを含みうる Windows のパスを混ぜられないため。
    /// </summary>
    private void SetTerrainBrushMask(string? path)
    {
        _terrainBrushMaskPath = string.IsNullOrEmpty(path) ? null : path;
        _runtimeManager?.SendToRuntime(
            "TERRAIN_BRUSH_MASK:"
            + SEEDEditor.Terrain.TerrainSettingsWindow.NormalizeBrushMaskForIpc(_terrainBrushMaskPath));
    }

    /// <summary>
    /// ツールバーのレイヤ選択コンボから、塗る対象レイヤ番号（0 起点）を返す。
    /// 未選択・UI 未生成のときは 0（先頭レイヤ）にフォールバックする。
    /// </summary>
    private int GetSelectedTerrainLayer()
    {
        int idx = CmbTerrainLayer?.SelectedIndex ?? 0;
        return idx < 0 ? 0 : idx;
    }

    /// <summary>
    /// 「地形を初期化」ボタン: 地形設定ウィンドウで保存したチャンク構成
    /// （chunk_config.json）を読み、新形式の TERRAIN_INIT を送る（再初期化は確認する）。
    /// </summary>
    private void OnTerrainInit(object sender, RoutedEventArgs e)
    {
        if (_runtimeManager?.State != EditorState.Edit)
        {
            SetTerrainStatus("Edit モードで実行してください", ok: false);
            return;
        }

        var cfg = SEEDEditor.Terrain.TerrainChunkConfigDocument.Load(AssetsPath);
        string desc = FormatTerrainChunkConfigDescription(cfg);

        if (_terrainInited)
        {
            var r = MessageBox.Show(this,
                $"既に地形が初期化されています。作り直すと現在の地形（未保存の編集を含む）は破棄されます。\n"
                + $"これから作られるチャンク構成: {desc}\n続行しますか？",
                "地形の再初期化", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (r != MessageBoxResult.OK) return;
        }

        var ci = CultureInfo.InvariantCulture;
        _runtimeManager.SendToRuntime(
            $"TERRAIN_INIT:{cfg.ChunksX.ToString(ci)},{cfg.ChunksZ.ToString(ci)},{cfg.ChunkCells.ToString(ci)},{cfg.VoxelSize.ToString(ci)}");
        SetTerrainStatus($"地形を初期化中...（{desc}）", ok: true);
    }

    /// <summary>
    /// チャンク構成を確認ダイアログ／ステータス表示向けに整形する
    /// （例: "6×6 チャンク / 分割数 32 / ボクセル 0.5m"）。
    /// </summary>
    private static string FormatTerrainChunkConfigDescription(SEEDEditor.Terrain.TerrainChunkConfigDocument cfg)
    {
        var ci = CultureInfo.InvariantCulture;
        return $"{cfg.ChunksX.ToString(ci)}×{cfg.ChunksZ.ToString(ci)} チャンク / "
             + $"分割数 {cfg.ChunkCells.ToString(ci)} / ボクセル {cfg.VoxelSize.ToString(ci)}m";
    }

    /// <summary>「地形を保存」ボタン: TERRAIN_SAVE を送る。</summary>
    private void OnTerrainSave(object sender, RoutedEventArgs e)
    {
        if (_runtimeManager?.State != EditorState.Edit)
        {
            SetTerrainStatus("Edit モードで実行してください", ok: false);
            return;
        }
        _runtimeManager.SendToRuntime("TERRAIN_SAVE");
        SetTerrainStatus("地形を保存中...", ok: true);
    }

    /// <summary>
    /// 「ハイトマップ読込」ボタン: 画像ファイルを選択し、高さスケール（m）とともに
    /// TERRAIN_HEIGHTMAP をランタイムへ送る。既存地形は読み込んだ高さマップで上書きされる。
    /// </summary>
    private void OnTerrainHeightmap(object sender, RoutedEventArgs e)
    {
        if (_runtimeManager?.State != EditorState.Edit)
        {
            SetTerrainStatus("Edit モードで実行してください", ok: false);
            return;
        }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "画像ファイル|*.png;*.jpg;*.jpeg",
            Title = "ハイトマップ画像を選択",
        };
        if (dialog.ShowDialog(this) != true) return;

        // 高さスケール欄をパース。空/不正/範囲外（0 以下）はデフォルト値にフォールバックする。
        double heightScale = TerrainHeightScaleDefault;
        if (TxtTerrainHeightScale != null
            && double.TryParse(TxtTerrainHeightScale.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            && parsed > 0)
        {
            heightScale = parsed;
        }

        var cfg = SEEDEditor.Terrain.TerrainChunkConfigDocument.Load(AssetsPath);
        var ci = CultureInfo.InvariantCulture;
        // 新形式: チャンク構成を明示して送る。path は必ず末尾に置くため、
        // パス中にカンマが含まれていてもランタイム側の分割処理を壊さない。
        _runtimeManager.SendToRuntime(
            $"TERRAIN_HEIGHTMAP:{cfg.ChunksX.ToString(ci)},{cfg.ChunksZ.ToString(ci)},{cfg.ChunkCells.ToString(ci)},"
            + $"{cfg.VoxelSize.ToString(ci)},{heightScale.ToString(ci)},{dialog.FileName}");
        SetTerrainStatus($"ハイトマップ読込中...（{FormatTerrainChunkConfigDescription(cfg)}）", ok: true);
    }

    // ── ランタイム応答ハンドラ（IPC スレッド → Dispatcher）──────────

    private void OnTerrainInitCompleted()
    {
        Dispatcher.BeginInvoke(() =>
        {
            _terrainInited = true;
            SetTerrainStatus("地形を初期化しました", ok: true);
        });
    }

    private void OnTerrainSaveCompleted(bool ok, string arg)
    {
        Dispatcher.BeginInvoke(() =>
            SetTerrainStatus(ok ? $"地形を保存しました（{arg} チャンク）" : $"保存失敗: {arg}", ok));
    }

    /// <summary>
    /// ハイトマップ反映完了通知（TERRAIN_HEIGHTMAP_OK:ms / TERRAIN_HEIGHTMAP_ERROR:msg）。
    /// 成功時は arg=処理時間(ms)、失敗時は arg=エラーメッセージ。
    /// </summary>
    private void OnTerrainHeightmapCompleted(bool ok, string arg)
    {
        Dispatcher.BeginInvoke(() =>
            SetTerrainStatus(ok ? $"ハイトマップを反映しました（{arg} ms）" : $"読込失敗: {arg}", ok));
    }

    /// <summary>
    /// チャンク追加完了通知（TERRAIN_ADD_CHUNKS_OK:追加数,再メッシュ数 / TERRAIN_ADD_CHUNKS_ERROR:msg）。
    /// 地形設定ウィンドウの「チャンクを追加」ボタンから送った TERRAIN_ADD_CHUNKS への応答。
    /// </summary>
    private void OnTerrainAddChunksCompleted(bool ok, string arg)
    {
        Dispatcher.BeginInvoke(() =>
            SetTerrainStatus(ok ? $"チャンクを追加しました（{arg}）" : $"チャンク追加失敗: {arg}", ok));
    }

    /// <summary>
    /// 散布完了通知（TERRAIN_SCATTER_OK:総インスタンス数 / TERRAIN_SCATTER_ERROR:msg）。
    /// 地形設定ウィンドウの「ルールで再散布」から送った TERRAIN_SCATTER_RULES への応答。
    /// </summary>
    private void OnTerrainScatterCompleted(bool ok, string arg)
    {
        Dispatcher.BeginInvoke(() =>
            SetTerrainStatus(ok ? $"散布しました（{arg} インスタンス）" : $"散布失敗: {arg}", ok));
    }

    /// <summary>
    /// ブラシ結果通知。ドラッグ中は高頻度で届くため UI 更新は行わない
    /// （命中/非命中の逐次表示は Dispatcher スパムになるため意図的に無視する）。
    /// </summary>
    private void OnTerrainBrushResult(bool hit, string arg)
    {
    }

    // ── ステータス表示 ────────────────────────────────────────

    private void SetTerrainStatus(string text, bool ok)
    {
        if (TxtTerrainStatus == null) return;
        TxtTerrainStatus.Text = text;
        TxtTerrainStatus.Foreground = new SolidColorBrush(
            ok ? Color.FromRgb(0x88, 0xCC, 0x88) : Color.FromRgb(0xE0, 0x6C, 0x6C));
    }
}
