// ============================================================
//  AnimationTimelinePanel.xaml.cs — キーフレームアニメーション編集パネル
//
//  .anim クリップ（AnimClip モデル、AnimClipIO で読み書き）を
//  トラックリスト + ドープシート（DopeSheetPanel）で編集する。
//
//  【動作モード】
//  ・アクターモード: 選択中アクターが AnimatorComponent を持つ場合、
//    そのクリップ一覧から選択して編集する。Edit モード中はプレビュー
//    （ANIM_PREVIEW）を Runtime へ送信できる。
//  ・ファイル単独モード:「直接開く」で任意の .anim を開く。アクター文脈が
//    無いためプレビュー不可（ボタンをグレーアウト）。
//
//  【IPC】
//  ・ANIM_PREVIEW:{actor},{clip_path},{time} — プレビュー中は毎 tick 送信
//  ・ANIM_PREVIEW_STOP:{actor} — プレビュー終了時に必ず送信し元値へ復元する
//  ・ANIM_RELOAD:{clip_path} — 保存直後にランタイムのキャッシュを破棄させる
// ============================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using SEEDEditor.Panels.AnimationTimeline;
using SEEDEditor.Runtime;

namespace SEEDEditor.Panels;

/// <summary>
/// アニメーションタイムラインのドッキングパネル本体。
/// トラックリスト・ドープシート・値エディタ・.anim ファイル I/O・プレビュー送信を統括する。
/// </summary>
public partial class AnimationTimelinePanel : UserControl
{
    // ── Runtime 連携 ────────────────────────────────────────────
    private RuntimeManager? _runtime;
    private string          _assetsPath = "";

    /// <summary>プレビュー対象アクターの DFS ID（-1 = アクター文脈なし＝ファイル単独モード）。</summary>
    private int _actorDfsId = -1;

    /// <summary>選択中アクターが持つクリップ一覧（name, kind, path）。ComboBox の元データ。
    /// kind="model"（glTF 内蔵アニメ）のクリップは path が空文字のままで、ドープシート編集の対象外になる。</summary>
    private readonly List<(string Name, string Kind, string Path)> _actorClips = new();

    /// <summary>ファイル単独モード（「直接開く」で開いた）かどうか。true の間は選択アクターの変化を無視する。</summary>
    private bool _isFileOnlyMode;

    /// <summary>現在 ComboBox で選択中のクリップが kind=model（モデル内蔵アニメ）かどうか。
    /// true の間はドープシート・再生プレビューを無効化し、案内文を表示する
    /// （model クリップは .anim を持たず ANIM_PREVIEW/ドープシート編集の対象外のため）。</summary>
    private bool _isModelClipSelected;

    /// <summary>クリップ未選択時の既定の案内文（model クリップ選択時のみ別文言に差し替える）。</summary>
    private const string DefaultNoClipHint = "Animator 付きアクタを選択するか、「直接開く」で .anim を開いてください";
    private const string ModelClipHint     = "モデル内蔵アニメは編集できません（再生設定は Inspector で行ってください）";

    // ── 編集中クリップ ──────────────────────────────────────────
    private AnimClip? _clip;
    /// <summary>編集中ファイルの絶対パス（未保存の新規クリップは null）。</summary>
    private string? _currentFilePath;
    private bool     _isDirty;
    private int      _selectedTrackIndex = -1;

    // ── プレビュー再生 ──────────────────────────────────────────
    private readonly DispatcherTimer _previewTimer;
    private float _previewTime;
    private bool  _isPlaying;
    /// <summary>ANIM_PREVIEW を送信中（=ANIM_PREVIEW_STOP で復元が必要）かどうか。</summary>
    private bool  _previewActive;

    /// <summary>パネルの表示タイトルが変わったことを通知する（MainWindow が LayoutAnchorable.Title へ反映）。</summary>
    public event Action<string>? TitleChanged;

    public AnimationTimelinePanel()
    {
        InitializeComponent();

        // トラック追加用ドロップダウンをレジストリから構築する
        CmbNewTrackProperty.ItemsSource       = AnimPropertyRegistry.Entries;
        CmbNewTrackProperty.DisplayMemberPath = nameof(AnimPropertyEntry.DisplayName);
        if (AnimPropertyRegistry.Entries.Count > 0) CmbNewTrackProperty.SelectedIndex = 0;

        DopeSheet.KeyAddRequested      += OnDopeSheetKeyAddRequested;
        DopeSheet.KeyMoved             += OnDopeSheetKeyMoved;
        DopeSheet.KeyDeleteRequested   += OnDopeSheetKeyDeleteRequested;
        DopeSheet.KeySelectionChanged  += OnDopeSheetKeySelectionChanged;
        DopeSheet.PlayheadScrubbed     += OnPlayheadScrubbed;
        DopeSheet.PlayheadScrubEnded   += OnPlayheadScrubEnded;

        // プレビュー再生タイマ（約 60fps）。Play 中のみ Tick で時間を進める。
        _previewTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(AnimationTimelineConstants.PreviewTickIntervalMs),
        };
        _previewTimer.Tick += OnPreviewTick;

        // パネルが非表示になったら必ずプレビューを止めて元値を復元する。
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is false) StopPreview();
        };

        // Delete キーでのキー削除（OnKeyDown）を受けるため、パネル内クリックでキーボードフォーカスを取得する。
        PreviewMouseDown += (_, _) => Focus();

        UpdateEmptyState();
    }

    // ── 外部からの初期化 ────────────────────────────────────────

    /// <summary>RuntimeManager を注入し、選択・コンポーネント通知を購読する。</summary>
    public void SetRuntime(RuntimeManager runtime)
    {
        if (_runtime is not null)
        {
            _runtime.SelectionChanged        -= OnSelectionChanged;
            _runtime.ActorComponentsReceived -= OnActorComponentsReceived;
            _runtime.StateChanged            -= OnRuntimeStateChanged;
        }
        _runtime = runtime;
        _runtime.SelectionChanged        += OnSelectionChanged;
        _runtime.ActorComponentsReceived += OnActorComponentsReceived;
        _runtime.StateChanged            += OnRuntimeStateChanged;
        UpdatePreviewAvailability();
    }

    /// <summary>アセットルート（仮想パス変換用）を設定する。</summary>
    public void SetAssetsPath(string assetsPath) => _assetsPath = assetsPath;

    /// <summary>
    /// 指定した絶対パスの .anim ファイルをファイル単独モードで開く。
    /// ProjectPanel からのダブルクリック・Inspector からの「タイムラインで編集」等の外部呼び出し用。
    /// </summary>
    public void LoadAnimFile(string fullPath)
    {
        StopPreview();
        try
        {
            _clip             = AnimClipIO.Load(fullPath);
            _currentFilePath  = fullPath;
            _isFileOnlyMode   = true;
            _isDirty          = false;
            _selectedTrackIndex = -1;
            _isModelClipSelected = false;
            RefreshAll();
        }
        catch (Exception ex)
        {
            EditorLog.Write($"AnimationTimelinePanel: .anim 読み込み失敗: {ex.Message}");
            MessageBox.Show(Window.GetWindow(this), $".anim の読み込みに失敗しました:\n{ex.Message}",
                "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 現在アクター文脈で保持しているクリップ一覧から、指定パスのクリップを選択して開く。
    /// Inspector の「タイムラインで編集」ボタンから呼ばれる（アクター文脈は選択連動で既に取得済み前提）。
    /// </summary>
    public void OpenClipByPath(string virtualOrAbsolutePath)
    {
        _isFileOnlyMode = false;
        var abs = VirtualPath.ToAbsolute(virtualOrAbsolutePath, _assetsPath);
        LoadClipFromActorContext(abs);
    }

    // ── Runtime イベント ────────────────────────────────────────

    /// <summary>
    /// RuntimeManager.ChangeState は非UIスレッド（StartEditAsync 等のバックグラウンド継続）から
    /// 発火されることがあるため、他ハンドラ（OnSelectionChanged 等）と同様に Dispatcher.InvokeAsync で
    /// UIスレッドへマーシャリングしてから UI 要素（BtnPlayPause 等）を触る。
    /// </summary>
    private void OnRuntimeStateChanged(EditorState state) =>
        Dispatcher.InvokeAsync(UpdatePreviewAvailability);

    private void OnSelectionChanged(int id)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_isFileOnlyMode) return; // ファイル単独モード中はアクター選択変化を無視する

            // 選択が外れた／別アクターへ移った → 進行中のプレビューを止めて元値へ復元する
            StopPreview();

            if (id < 0)
            {
                _actorDfsId = -1;
                _actorClips.Clear();
                UpdateEmptyState();
            }
            // 実データは ACTOR_COMPONENTS 側で届くのでここでは文脈クリアのみ行う
        });
    }

    private void OnActorComponentsReceived(string json)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (_isFileOnlyMode) return;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                var dfsId = root.TryGetProperty("id", out var idEl) ? idEl.GetInt32() : -1;
                if (dfsId < 0) return;

                AnimatorComponentInfo? animator = null;
                if (root.TryGetProperty("components", out var comps))
                {
                    foreach (var comp in comps.EnumerateArray())
                    {
                        var type = comp.TryGetProperty("type", out var tp) ? tp.GetString() ?? "" : "";
                        if (type != "AnimatorComponent") continue;
                        animator = ParseAnimatorComponent(comp);
                        break;
                    }
                }

                _actorDfsId = dfsId;
                _actorClips.Clear();
                if (animator is not null) _actorClips.AddRange(animator.Clips);
                // clips が空（Animator 無し、または clips 未設定）の場合は
                // RebuildClipCombo 内部で UpdateEmptyState が呼ばれ編集領域が空になる。
                RebuildClipCombo(animator?.DefaultClip ?? "");
            }
            catch (Exception ex)
            {
                EditorLog.Write($"AnimationTimelinePanel: ACTOR_COMPONENTS 解析失敗: {ex.Message}");
            }
        });
    }

    /// <summary>ACTOR_COMPONENTS 内の 1 コンポーネント（type=="AnimatorComponent"）を解析する。</summary>
    private sealed record AnimatorComponentInfo(List<(string Name, string Kind, string Path)> Clips, string DefaultClip);

    private static AnimatorComponentInfo ParseAnimatorComponent(JsonElement comp)
    {
        var clips = new List<(string, string, string)>();
        if (comp.TryGetProperty("clips", out var clipsEl) && clipsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var c in clipsEl.EnumerateArray())
            {
                var name = c.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
                // kind 省略時は keyframe（旧シーン後方互換。Rust 側 #[serde(default)] と同じ既定値）
                var kind = c.TryGetProperty("kind", out var kp) ? kp.GetString() ?? "keyframe" : "keyframe";
                var path = c.TryGetProperty("path", out var pp) ? pp.GetString() ?? "" : "";
                clips.Add((name, kind, path));
            }
        }
        var defaultClip = comp.TryGetProperty("default_clip", out var dp) ? dp.GetString() ?? "" : "";
        return new AnimatorComponentInfo(clips, defaultClip);
    }

    // ── クリップ選択 UI ─────────────────────────────────────────

    /// <summary>クリップ ComboBox の Tag（kind/path を選択変更ハンドラへ渡す）。</summary>
    private sealed record ClipComboTag(string Kind, string Path);

    /// <summary>アクターの clips 一覧から ComboBox を再構築する。可能なら defaultClip を選択状態にする。
    /// kind=model のクリップは 🎬 アイコン付きで列挙するが、選択してもドープシート編集はできない
    /// （<see cref="OnClipComboSelectionChanged"/> 側で案内表示に切り替える）。</summary>
    private void RebuildClipCombo(string defaultClip)
    {
        CmbClip.SelectionChanged -= OnClipComboSelectionChanged;
        CmbClip.Items.Clear();
        foreach (var (name, kind, path) in _actorClips)
        {
            var label = kind == "model"
                ? $"🎬 {(string.IsNullOrEmpty(name) ? "(無名アニメ)" : name)}"
                : (string.IsNullOrEmpty(name) ? path : name);
            CmbClip.Items.Add(new ComboBoxItem { Content = label, Tag = new ClipComboTag(kind, path) });
        }
        CmbClip.SelectionChanged += OnClipComboSelectionChanged;

        if (_actorClips.Count == 0)
        {
            UpdateEmptyState();
            return;
        }

        NoSelectionVisible(false);
        // default_clip は AnimClipRef.name を指す識別子（path ではない）。Inspector 側の一致判定と揃える。
        var idx = _actorClips.FindIndex(c => c.Name == defaultClip);
        CmbClip.SelectedIndex = idx >= 0 ? idx : 0;
    }

    private void OnClipComboSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CmbClip.SelectedItem is not ComboBoxItem item || item.Tag is not ClipComboTag tag) return;
        StopPreview();

        if (tag.Kind == "model")
        {
            // モデル内蔵アニメは .anim を持たない（ANIM_PREVIEW / ドープシート編集の対象外）ため、
            // 実クリップの読み込みは行わず案内表示のみに切り替える。
            LoadModelClipPlaceholder();
            return;
        }

        _isModelClipSelected = false;
        var abs = VirtualPath.ToAbsolute(tag.Path, _assetsPath);
        LoadClipFromActorContext(abs);
    }

    private void LoadClipFromActorContext(string absolutePath)
    {
        try
        {
            _clip               = File.Exists(absolutePath) ? AnimClipIO.Load(absolutePath) : new AnimClip();
            _currentFilePath    = absolutePath;
            _isDirty            = false;
            _selectedTrackIndex = -1;
            _isModelClipSelected = false;
            RefreshAll();
        }
        catch (Exception ex)
        {
            EditorLog.Write($"AnimationTimelinePanel: クリップ読み込み失敗: {ex.Message}");
        }
    }

    /// <summary>
    /// kind=model のクリップが選択されたときの表示状態にする。編集対象の実クリップは存在しないため
    /// _clip を null にし、RefreshAll の「クリップ無し」経路（ドープシート非表示・保存/再生無効化）へ乗せる。
    /// ComboBox 自体（_actorClips）はクリアしない点が UpdateEmptyState と異なる。
    /// </summary>
    private void LoadModelClipPlaceholder()
    {
        _clip                = null;
        _currentFilePath     = null;
        _isDirty             = false;
        _selectedTrackIndex  = -1;
        _isModelClipSelected = true;
        RefreshAll();
    }

    // ── 空状態表示 ──────────────────────────────────────────────

    /// <summary>アクター未選択・Animator 無しのとき編集領域を空にする。</summary>
    private void UpdateEmptyState()
    {
        if (_actorClips.Count == 0 && !_isFileOnlyMode)
        {
            _clip            = null;
            _currentFilePath = null;
            _isDirty         = false;
            _isModelClipSelected = false;
            CmbClip.Items.Clear();
            NoSelectionVisible(true);
            RefreshAll();
        }
    }

    private void NoSelectionVisible(bool empty)
    {
        // Animator 未所持時は「直接開く」以外の操作を無効化する（クリップが存在しないため）。
        BtnSaveClip.IsEnabled = !empty;
        BtnPlayPause.IsEnabled = !empty && CanPreview();
        CmbClip.IsEnabled = !empty;
        BtnNewClip.IsEnabled = true; // 新規作成は常時可能（保存時にパスを指定する）
        TbNoClipHint.Text       = DefaultNoClipHint;
        TbNoClipHint.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        DopeSheet.Visibility    = empty ? Visibility.Collapsed : Visibility.Visible;
    }

    // ── 新規 / 開く / 保存 ──────────────────────────────────────

    private void OnNewClip(object sender, RoutedEventArgs e)
    {
        StopPreview();
        _clip = new AnimClip { Name = "new_clip", Duration = 1f, LoopMode = AnimLoopMode.Once };
        _currentFilePath = null;
        _isDirty = true;
        _selectedTrackIndex = -1;
        _isFileOnlyMode = true; // 保存先未確定のため、明示的に保存するまでファイル単独扱いにする
        _isModelClipSelected = false;
        RefreshAll();
    }

    private void OnOpenClip(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title           = ".anim ファイルを開く",
            Filter          = "アニメーションクリップ|*.anim|すべてのファイル|*.*",
            InitialDirectory = Directory.Exists(_assetsPath) ? _assetsPath : "",
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
        LoadAnimFile(dlg.FileName);
    }

    private void OnSaveClip(object sender, RoutedEventArgs e)
    {
        if (_clip is null) return;

        var path = _currentFilePath;
        if (path is null)
        {
            var dlg = new SaveFileDialog
            {
                Title           = ".anim を保存",
                Filter          = "アニメーションクリップ|*.anim",
                InitialDirectory = Directory.Exists(_assetsPath) ? _assetsPath : "",
                FileName        = string.IsNullOrEmpty(_clip.Name) ? "new_clip.anim" : _clip.Name + ".anim",
            };
            if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;
            path = dlg.FileName;
        }

        try
        {
            AnimClipIO.Save(_clip, path);
            _currentFilePath = path;
            _isDirty = false;
            UpdateTitle();

            // ランタイム側のクリップキャッシュを破棄させ、次回参照時に再読込させる
            var virtualPath = VirtualPath.ToVirtual(path, _assetsPath);
            _runtime?.SendToRuntime($"ANIM_RELOAD:{virtualPath}");
        }
        catch (Exception ex)
        {
            EditorLog.Write($"AnimationTimelinePanel: .anim 保存失敗: {ex.Message}");
            MessageBox.Show(Window.GetWindow(this), $".anim の保存に失敗しました:\n{ex.Message}",
                "エラー", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    // ── duration / loop_mode 編集 ───────────────────────────────

    private void OnDurationKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Return or Key.Enter) { CommitDuration(); e.Handled = true; }
    }
    private void OnDurationLostFocus(object sender, RoutedEventArgs e) => CommitDuration();

    private void CommitDuration()
    {
        if (_clip is null) return;
        var v = AnimClipIO.ParseFloatOr(TbDuration.Text, _clip.Duration);
        _clip.Duration = Math.Max(v, AnimationTimelineConstants.MinDuration);
        TbDuration.Text = _clip.Duration.ToString(CultureInfo.InvariantCulture);
        MarkDirty();
        DopeSheet.NotifyClipChanged();
    }

    private void OnLoopModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_clip is null) return;
        if (CmbLoopMode.SelectedItem is not ComboBoxItem item || item.Tag is not string mode) return;
        if (_clip.LoopMode == mode) return;
        _clip.LoopMode = mode;
        MarkDirty();
    }

    // ── トラック追加/削除 ───────────────────────────────────────

    private void OnAddTrack(object sender, RoutedEventArgs e)
    {
        if (_clip is null) return;
        if (CmbNewTrackProperty.SelectedItem is not AnimPropertyEntry entry) return;

        var track = new AnimTrack
        {
            Target    = new AnimTarget { ActorPath = TbNewTrackActorPath.Text.Trim(), Component = entry.Component, Property = entry.Property },
            ValueType = entry.ValueType,
        };
        _clip.Tracks.Add(track);
        MarkDirty();
        RefreshTrackList();
        DopeSheet.NotifyClipChanged();
    }

    private void OnTrackListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Delete) return;
        if (_clip is null || _selectedTrackIndex < 0 || _selectedTrackIndex >= _clip.Tracks.Count) return;
        _clip.Tracks.RemoveAt(_selectedTrackIndex);
        _selectedTrackIndex = -1;
        MarkDirty();
        RefreshTrackList();
        DopeSheet.SetSelectedTrackIndex(-1);
        DopeSheet.NotifyClipChanged();
        e.Handled = true;
    }

    private void OnTrackListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedTrackIndex = LstTracks.SelectedIndex;
        DopeSheet.SetSelectedTrackIndex(_selectedTrackIndex);
    }

    /// <summary>トラックの表示文字列（"actor_path : component.property (value_type)"）を作る。</summary>
    private static string DescribeTrack(AnimTrack t)
    {
        var actor = string.IsNullOrEmpty(t.Target.ActorPath) ? "(自分)" : t.Target.ActorPath;
        return $"{actor} : {t.Target.Component}.{t.Target.Property}  [{t.ValueType}]";
    }

    private void RefreshTrackList()
    {
        LstTracks.SelectionChanged -= OnTrackListSelectionChanged;
        LstTracks.Items.Clear();
        if (_clip is not null)
            foreach (var t in _clip.Tracks)
                LstTracks.Items.Add(DescribeTrack(t));
        if (_selectedTrackIndex >= 0 && _selectedTrackIndex < LstTracks.Items.Count)
            LstTracks.SelectedIndex = _selectedTrackIndex;
        LstTracks.SelectionChanged += OnTrackListSelectionChanged;
    }

    // ── ドープシートからのキー編集イベント ──────────────────────

    private void OnDopeSheetKeyAddRequested(int trackIndex, float time)
    {
        if (_clip is null || trackIndex < 0 || trackIndex >= _clip.Tracks.Count) return;
        var track = _clip.Tracks[trackIndex];

        // 直前のキー（時刻がそれ以下で最大のもの）の値を初期値として複製する。無ければ 0 埋め。
        var prev = track.Keys.Where(k => k.Time <= time).OrderBy(k => k.Time).LastOrDefault();
        var count = AnimPropertyRegistry.ComponentCount(track.ValueType);
        var values = prev is not null ? (float[])prev.Values.Clone() : new float[count];

        track.Keys.Add(new AnimKey { Time = time, Values = values, Interp = AnimInterp.Linear });
        track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        MarkDirty();
        DopeSheet.NotifyClipChanged();
    }

    private void OnDopeSheetKeyMoved(int trackIndex, int keyIndex, float newTime)
    {
        MarkDirty();
        // 時刻変更でソート順が崩れる可能性があるため、選択キーを追跡しつつ並べ替える。
        if (_clip is null) return;
        var track = _clip.Tracks[trackIndex];
        var key   = track.Keys[keyIndex];
        track.Keys.Sort((a, b) => a.Time.CompareTo(b.Time));
        RefreshValueEditor(trackIndex, track.Keys.IndexOf(key));
    }

    private void OnDopeSheetKeyDeleteRequested(int trackIndex, int keyIndex)
    {
        if (_clip is null) return;
        var track = _clip.Tracks[trackIndex];
        if (keyIndex < 0 || keyIndex >= track.Keys.Count) return;
        track.Keys.RemoveAt(keyIndex);
        MarkDirty();
        DopeSheet.NotifyClipChanged();
        ClearValueEditor();
    }

    private void OnDopeSheetKeySelectionChanged(int trackIndex, int keyIndex) => RefreshValueEditor(trackIndex, keyIndex);

    // ── Delete キーでの選択キー削除 ─────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Delete) return;
        var (ti, ki) = DopeSheet.SelectedKey;
        if (ti < 0 || ki < 0) return;
        OnDopeSheetKeyDeleteRequested(ti, ki);
        e.Handled = true;
    }

    // ── 値エディタ ──────────────────────────────────────────────

    private void ClearValueEditor() => ValueEditorHost.Children.Clear();

    /// <summary>選択中キーの time / value / interp / tangent を編集する行を再構築する。</summary>
    private void RefreshValueEditor(int trackIndex, int keyIndex)
    {
        ValueEditorHost.Children.Clear();
        if (_clip is null || trackIndex < 0 || trackIndex >= _clip.Tracks.Count) return;
        var track = _clip.Tracks[trackIndex];
        if (keyIndex < 0 || keyIndex >= track.Keys.Count) return;
        var key = track.Keys[keyIndex];

        void AddLabel(string text) => ValueEditorHost.Children.Add(new TextBlock
        {
            Text = text, Foreground = new SolidColorBrush(AnimationTimelineConstants.SubTextColor),
            FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 4, 0),
        });

        // ── 時刻 ──
        AddLabel("時刻");
        var tbTime = NewNumberBox(key.Time.ToString("0.###", CultureInfo.InvariantCulture));
        tbTime.LostFocus += (_, _) =>
        {
            var t = AnimClipIO.ParseFloatOr(tbTime.Text, key.Time);
            key.Time = Math.Clamp(t, 0f, Math.Max(_clip.Duration, 0f));
            MarkDirty();
            OnDopeSheetKeyMoved(trackIndex, track.Keys.IndexOf(key), key.Time);
            DopeSheet.NotifyClipChanged();
        };
        ValueEditorHost.Children.Add(tbTime);

        // ── 値（value_type の要素数ぶん）──
        AddLabel("値");
        for (int i = 0; i < key.Values.Length; i++)
        {
            var idx = i;
            var tb = NewNumberBox(key.Values[i].ToString("0.###", CultureInfo.InvariantCulture));
            tb.LostFocus += (_, _) =>
            {
                key.Values[idx] = AnimClipIO.ParseFloatOr(tb.Text, key.Values[idx]);
                MarkDirty();
                DopeSheet.NotifyClipChanged();
            };
            ValueEditorHost.Children.Add(tb);
        }

        // ── 補間方式 ──
        AddLabel("補間");
        var cmbInterp = new ComboBox { Width = 80, FontSize = 11, Height = 20, VerticalAlignment = VerticalAlignment.Center };
        foreach (var interp in AnimInterp.All)
            cmbInterp.Items.Add(new ComboBoxItem { Content = interp, Tag = interp });
        cmbInterp.SelectedIndex = Array.IndexOf(AnimInterp.All, key.Interp);
        cmbInterp.SelectionChanged += (_, _) =>
        {
            if (cmbInterp.SelectedItem is not ComboBoxItem item || item.Tag is not string interp) return;
            key.Interp = interp;
            MarkDirty();
        };
        ValueEditorHost.Children.Add(cmbInterp);

        // ── ベジェタンジェント（bezier のときのみ意味を持つが、常に編集可能にしておく）──
        if (key.Interp == AnimInterp.Bezier)
        {
            AddLabel("Tan In/Out");
            key.InTangent  ??= new float[key.Values.Length];
            key.OutTangent ??= new float[key.Values.Length];
            AddTangentBoxes(key.InTangent);
            AddTangentBoxes(key.OutTangent);
        }

        // ── 削除ボタン ──
        var btnDelete = new Button
        {
            Content = "削除", Background = new SolidColorBrush(Color.FromRgb(0x40, 0x28, 0x28)),
            Foreground = new SolidColorBrush(AnimationTimelineConstants.TextColor), BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 2, 8, 2), FontSize = 11, Margin = new Thickness(12, 0, 0, 0), Cursor = Cursors.Hand,
        };
        btnDelete.Click += (_, _) => OnDopeSheetKeyDeleteRequested(trackIndex, track.Keys.IndexOf(key));
        ValueEditorHost.Children.Add(btnDelete);

        void AddTangentBoxes(float[] arr)
        {
            for (int i = 0; i < arr.Length; i++)
            {
                var idx = i;
                var tb = NewNumberBox(arr[i].ToString("0.###", CultureInfo.InvariantCulture));
                tb.Width = 44;
                tb.LostFocus += (_, _) => { arr[idx] = AnimClipIO.ParseFloatOr(tb.Text, arr[idx]); MarkDirty(); };
                ValueEditorHost.Children.Add(tb);
            }
        }
    }

    private static TextBox NewNumberBox(string text) => new()
    {
        Text = text, Width = 54, FontSize = 11,
        Background = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A)),
        Foreground = new SolidColorBrush(AnimationTimelineConstants.TextColor),
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46)),
        VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 2, 0),
    };

    // ── 再生プレビュー ──────────────────────────────────────────

    /// <summary>プレビューが可能な状態か（アクター文脈あり・Edit モード・クリップロード済み）。</summary>
    private bool CanPreview() =>
        !_isFileOnlyMode && _actorDfsId >= 0 && _clip is not null &&
        _currentFilePath is not null && _runtime?.State == EditorState.Edit;

    private void UpdatePreviewAvailability()
    {
        BtnPlayPause.IsEnabled = CanPreview();
        if (!CanPreview()) StopPreview();
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_isPlaying) { StopPreview(); return; }
        if (!CanPreview()) return;

        _isPlaying = true;
        BtnPlayPause.Content = "⏸";
        _previewTimer.Start();
    }

    private void OnPreviewTick(object? sender, EventArgs e)
    {
        if (_clip is null) return;
        var dt = AnimationTimelineConstants.PreviewTickIntervalMs / 1000f;
        _previewTime = AdvancePreviewTime(_previewTime, dt, _clip.Duration, _clip.LoopMode);
        DopeSheet.SetPlayheadTime(_previewTime);
        SendPreview(_previewTime);
    }

    /// <summary>loop_mode に従って再生時刻を進める（once=末端で停止、loop=先頭へ折返し、ping_pong=往復）。</summary>
    private float AdvancePreviewTime(float current, float dt, float duration, string loopMode)
    {
        if (duration <= 0f) return 0f;
        var next = current + dt;
        switch (loopMode)
        {
            case AnimLoopMode.Loop:
                return next % duration;
            case AnimLoopMode.PingPong:
                var cycle = duration * 2f;
                var m = next % cycle;
                return m <= duration ? m : cycle - m;
            default: // once
                if (next >= duration) { StopPreview(); return duration; }
                return next;
        }
    }

    private void OnPlayheadScrubbed(float time)
    {
        _previewTime = time;
        SendPreview(time);
    }

    private void OnPlayheadScrubEnded() => StopPreview();

    /// <summary>プレビュー再生・スクラブを停止し、必要なら ANIM_PREVIEW_STOP を送信して元値を復元する。</summary>
    private void StopPreview()
    {
        _isPlaying = false;
        BtnPlayPause.Content = "▶";
        _previewTimer.Stop();

        if (_previewActive && _actorDfsId >= 0)
            _runtime?.SendToRuntime($"ANIM_PREVIEW_STOP:{_actorDfsId}");
        _previewActive = false;
    }

    private void SendPreview(float time)
    {
        if (!CanPreview() || _currentFilePath is null) return;
        var virtualPath = VirtualPath.ToVirtual(_currentFilePath, _assetsPath);
        _runtime?.SendToRuntime(FormattableString.Invariant(
            $"ANIM_PREVIEW:{_actorDfsId},{virtualPath},{time}"));
        _previewActive = true;
    }

    // ── 全体再描画・ダーティ管理 ─────────────────────────────────

    private void MarkDirty()
    {
        if (_isDirty) return;
        _isDirty = true;
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var name = _clip?.Name ?? "アニメーション";
        TitleChanged?.Invoke(_isDirty ? $"アニメーション: {name} *" : $"アニメーション: {name}");
        TbTitleStatus.Text = _isDirty ? "未保存の変更あり" : "";
    }

    /// <summary>クリップ差し替え・保存・トラック増減時に UI 全体を再構築する。</summary>
    private void RefreshAll()
    {
        TbDuration.Text = (_clip?.Duration ?? 0f).ToString(CultureInfo.InvariantCulture);

        CmbLoopMode.SelectionChanged -= OnLoopModeChanged;
        var mode = _clip?.LoopMode ?? AnimLoopMode.Once;
        CmbLoopMode.SelectedIndex = Array.IndexOf(AnimLoopMode.All, mode);
        CmbLoopMode.SelectionChanged += OnLoopModeChanged;

        RefreshTrackList();
        DopeSheet.SetClip(_clip);
        DopeSheet.SetPlayheadTime(0f);
        ClearValueEditor();
        UpdatePreviewAvailability();
        UpdateTitle();

        var hasClip = _clip is not null;
        BtnSaveClip.IsEnabled = hasClip;
        // CmbClip 自体は「選択可能なクリップが 1 つでもあるか」で有効/無効を決める（model クリップ選択中も
        // 他クリップへ切り替えられるようにするため、hasClip=false でも disable しない）。
        CmbClip.IsEnabled     = _actorClips.Count > 0 && !_isFileOnlyMode;
        // model クリップ選択中は専用の案内文に差し替える（それ以外は既定文言のまま）。
        TbNoClipHint.Text       = _isModelClipSelected ? ModelClipHint : DefaultNoClipHint;
        TbNoClipHint.Visibility = hasClip ? Visibility.Collapsed : Visibility.Visible;
        DopeSheet.Visibility    = hasClip ? Visibility.Visible   : Visibility.Collapsed;
        // duration / loop_mode は _clip 前提の値のため、model クリップ選択中は編集不可にする
        // （ドープシート同様「モデル内蔵アニメはInspectorで編集」の対象）。
        TbDuration.IsEnabled  = hasClip;
        CmbLoopMode.IsEnabled = hasClip;
    }
}
