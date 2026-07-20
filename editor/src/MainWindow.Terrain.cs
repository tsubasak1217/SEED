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

    /// <summary>低レベルマウスフックのコールバック（GC 回収防止のためフィールド保持）。</summary>
    private LowLevelMouseProc? _terrainMouseProc;

    /// <summary>低レベルマウスフックのハンドル。</summary>
    private nint _terrainMouseHook;

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
                        return (nint)1;
                    }
                    break;
            }
        }
        return CallNextHookEx(_terrainMouseHook, nCode, wParam, lParam);
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

        GetCursorPos(out var cursor);
        GetWindowRect(_viewportHost.ContainerHwnd, out var rect);
        int lx = cursor.X - rect.Left;
        int ly = cursor.Y - rect.Top;
        if (lx < 0 || ly < 0 || lx >= rect.Right - rect.Left || ly >= rect.Bottom - rect.Top)
            return;

        double radius = SldTerrainRadius?.Value ?? 3.0;
        var ci = CultureInfo.InvariantCulture;
        _terrainPreviewActive = true;
        _runtimeManager.SendToRuntime(
            $"TERRAIN_BRUSH_PREVIEW:{lx},{ly},{radius.ToString(ci)}");
    }

    // ── モード / ツールバー イベント ─────────────────────────────

    /// <summary>シーン編集モードコンボ（common / terrain）の切り替え。</summary>
    private void OnSceneModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbSceneMode?.SelectedItem is not ComboBoxItem item) return;
        bool terrain = (item.Tag as string) == "terrain";
        _terrainMode = terrain;

        if (TerrainToolbar != null)
            TerrainToolbar.Visibility = terrain ? Visibility.Visible : Visibility.Collapsed;

        // モードを抜けたら進行中のストロークを打ち切り、ブラシプレビューを消す。
        if (!terrain)
        {
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
    }

    /// <summary>「地形を初期化」ボタン: TERRAIN_INIT を送る（再初期化は確認する）。</summary>
    private void OnTerrainInit(object sender, RoutedEventArgs e)
    {
        if (_runtimeManager?.State != EditorState.Edit)
        {
            SetTerrainStatus("Edit モードで実行してください", ok: false);
            return;
        }
        if (_terrainInited)
        {
            var r = MessageBox.Show(this,
                "既に地形が初期化されています。作り直すと現在の地形（未保存の編集を含む）は破棄されます。続行しますか？",
                "地形の再初期化", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (r != MessageBoxResult.OK) return;
        }
        _runtimeManager.SendToRuntime("TERRAIN_INIT");
        SetTerrainStatus("地形を初期化中...", ok: true);
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
