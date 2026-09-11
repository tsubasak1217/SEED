using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using SEEDEditor;
using SEEDEditor.Controls;

namespace SEEDEditor.Panels;

/// <summary>
/// InspectorPanel の「音声辞書（AudioDictionaryComponent）」実装と、
/// AudioComponent の「音源 2 択（ファイルパス直接 / 辞書のキー）」欄。
///
/// ■ 音声辞書のインスペクタ
///   グループ（見出し + 行一覧）単位で表示し、グループ追加・削除、
///   行追加・削除、用途名の編集、音声ファイルのドロップ設定、音量編集を行う。
///   編集のたびに <c>SET_AUDIO_DICT:{actor},{slot},{json}</c> でグループ配列を
///   まるごと送る（Animator の SET_ANIMATOR_CLIPS と同じ「全置換」方式）。
///   部分更新にしないのは、追加・削除・並びをすべて 1 コマンドで表現でき、
///   エディタとランタイムの状態が必ず一致するためである。
///
/// ■ AudioComponent の音源欄
///   「ファイルパス」モードは従来どおり。「辞書のキー」モードでは
///   AudioDictionary を持つアクタをドロップすると、その辞書のキー一覧
///   （グループごとにまとめて表示）から選ぶウィンドウが開く。
///   キー一覧は選択中でないアクタの中身なので、<c>GET_ACTOR_COMPONENTS</c> を
///   個別に投げ、その応答を横取りして解決する（参照ピッカーと同じ流儀）。
/// </summary>
public partial class InspectorPanel
{
    // ============================================================
    //  定数（ワイヤ表現・レイアウト）
    // ============================================================

    /// <summary>AudioDictionaryComponent の ACTOR_COMPONENTS "type" 文字列。</summary>
    private const string AudioDictionaryComponentType = "AudioDictionaryComponent";

    /// <summary>AudioComponent の「辞書キー」フィールドの SET キー（Rust 側 serde 名と一致）。</summary>
    private const string AudioDictKeyField = "dictionary_key";

    // ── レイアウト定数 ───────────────────────────────────────
    private const double DictLabelWidth      = 64;
    private const double DictUsageBoxWidth   = 110;
    private const double DictVolumeBoxWidth  = 52;
    private const double DictGroupIndent     = 8;
    private const double DictRowFontSize     = 11;
    private const double DictHintFontSize    = 10;
    private const double DictDropZoneMinHeight = 26;

    /// <summary>音量入力の表示書式（0..1 前後の値なので小数 2 桁）。</summary>
    private const string DictVolumeFormat = "F2";

    /// <summary>行内ボタンのアイコン一辺サイズ（px）。</summary>
    private const double DictButtonIconSize = 10;

    // ── 配色（ダークテーマ）──────────────────────────────────
    private static readonly Brush DictBrushLabel   = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));
    private static readonly Brush DictBrushHint    = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66));
    private static readonly Brush DictBrushGroup   = new SolidColorBrush(Color.FromRgb(0x7E, 0xC8, 0xE3));
    private static readonly Brush DictBrushKey     = new SolidColorBrush(Color.FromRgb(0x88, 0xBB, 0x88));
    private static readonly Brush DictBrushWarn    = new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x3A));
    private static readonly Brush DictBrushBoxBg   = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
    private static readonly Brush DictBrushBoxFg   = new SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private static readonly Brush DictBrushBorder  = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x46));
    private static readonly Brush DictBrushDropOk  = new SolidColorBrush(Color.FromRgb(0x44, 0xAA, 0x44));
    private static readonly Brush DictBrushDropBg  = new SolidColorBrush(Color.FromRgb(0x1A, 0x33, 0x1A));
    private static readonly Brush DictBrushUnset   = new SolidColorBrush(Color.FromRgb(0x88, 0x66, 0x44));
    private static readonly Brush DictBrushUnsetBd = new SolidColorBrush(Color.FromRgb(0x55, 0x44, 0x22));

    // ============================================================
    //  辞書キー選択の保留状態（GET_ACTOR_COMPONENTS の応答待ち）
    // ============================================================

    /// <summary>辞書キー選択の解決待ち 1 件分。</summary>
    /// <param name="DroppedActorDfsId">問い合わせ中のアクタ DFS ID（辞書を持つアクタ）。</param>
    /// <param name="OwnerActorId">ドロップを受けた時点で選択されていたアクタ（＝AudioComponent の持ち主）。</param>
    /// <param name="SlotIdx">キーを書き込む AudioComponent のスロット添字。</param>
    private sealed record PendingAudioDictKeyPick(int DroppedActorDfsId, int OwnerActorId, int SlotIdx);

    /// <summary>現在解決待ちの辞書キー選択（無ければ null）。</summary>
    private PendingAudioDictKeyPick? _pendingAudioDictKey;

    /// <summary>
    /// GET_ACTOR_COMPONENTS の応答で、保留中の辞書キー選択を解決する。
    ///
    /// 呼び出し側（OnActorComponentsReceived）が「保留中かつ ID 一致」を確認してから呼ぶこと。
    /// 応答待ちの間に選択が変わっていた場合は、別アクタへ誤って書き込まないよう破棄する。
    /// </summary>
    private void ResolvePendingAudioDictKeyPick(string json)
    {
        // 保留をローカルへ退避してから即リセットする（再帰・取りこぼし防止）
        var pending = _pendingAudioDictKey;
        _pendingAudioDictKey = null;
        if (pending is null || _runtime is null) return;
        if (pending.OwnerActorId != _currentActorId) return;

        var snapshot  = ActorComponentSnapshot.TryParse(json);
        var actorName = snapshot?.ActorName ?? "";

        // 辞書の中身（groups 配列）は ActorComponentSnapshot が運ばないので応答 JSON から直接読む
        var groupsJson = AudioDictionaryCatalog.ExtractGroupsJson(json, AudioDictionaryComponentType);
        if (groupsJson is null)
        {
            MessageBox.Show(
                $"「{actorName}」は Audio Dictionary を持っていません。",
                "音源設定エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var keyGroups = AudioDictionaryCatalog.BuildKeyGroups(groupsJson);
        if (keyGroups.Count == 0)
        {
            MessageBox.Show(
                $"「{actorName}」の音声辞書に、使用できるキーがありません。\n"
                + "グループ名・用途名・音声ファイルがすべて設定された行が必要です。",
                "音源設定エラー", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var picked = AudioDictionaryKeyWindow.Show(Window.GetWindow(this), actorName, keyGroups);
        if (picked is null) return;

        _runtime.SendToRuntime(
            $"SET_AUDIO_FIELD:{pending.OwnerActorId},{pending.SlotIdx},{AudioDictKeyField},{picked.Key}");
    }

    // ============================================================
    //  AudioComponent の音源欄（ファイルパス / 辞書のキー）
    // ============================================================

    /// <summary>
    /// AudioComponent の「音源」欄を構築して返す。
    ///
    /// 音源の指定方法は 2 択で、辞書キーが空なら「ファイルパス」、非空なら「辞書のキー」。
    /// モード切替コンボで切り替え、選ばれていないほうの入力欄は表示しない
    /// （無関係なパラメータを出さない方針）。
    /// </summary>
    /// <param name="info">対象スロットの情報。</param>
    /// <param name="sendField">SET_AUDIO_FIELD を送るローカル関数（key, value）。</param>
    /// <param name="onModeChanged">
    /// モードが切り替わったときに呼ぶコールバック（true = 辞書モード）。
    /// 呼び出し側が「辞書モードでは無関係になる行（音量）」の表示を切り替えるために使う。
    /// </param>
    private UIElement BuildAudioSourceSection(
        SlotInfo info, Action<string, string> sendField, Action<bool> onModeChanged)
    {
        var sp = new StackPanel();
        var useDictionary = !string.IsNullOrEmpty(info.AudioDictKey);

        // ── モード選択 ─────────────────────────────────────────
        var modeRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 2, 0, 2),
        };
        modeRow.Children.Add(new TextBlock
        {
            Text              = "音源",
            Foreground        = DictBrushLabel,
            FontSize          = DictRowFontSize,
            Width             = DictLabelWidth,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var modeCombo = new ComboBox
        {
            FontSize          = DictRowFontSize,
            Height            = 22,
            MinWidth          = 150,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip           = "ファイルパス: 音声ファイルを直接指定します。\n"
                              + "辞書のキー: Audio Dictionary の「グループ/用途」で指定し、\n"
                              + "パスと音量を辞書から解決します（辞書を直せば一括で反映）。",
        };
        modeCombo.Items.Add(new ComboBoxItem { Content = "ファイルパス", Tag = false });
        modeCombo.Items.Add(new ComboBoxItem { Content = "辞書のキー",   Tag = true  });
        modeCombo.SelectedIndex = useDictionary ? 1 : 0;
        modeRow.Children.Add(modeCombo);
        sp.Children.Add(modeRow);

        // -- モードごとの入力欄 --
        // 両方を作っておき表示だけを切り替える。こうしないと
        // 「辞書モードへ切り替えたがキーが未設定 ＝ ランタイムへ送る値がまだ無い」ときに
        // インスペクタを組み直すきっかけが無く、ドロップゾーンを出せない。
        var fileRow = BuildAudioFilePathRow(info, sendField);
        var dictRow = BuildAudioDictKeyRow(info);
        fileRow.Visibility = useDictionary ? Visibility.Collapsed : Visibility.Visible;
        dictRow.Visibility = useDictionary ? Visibility.Visible   : Visibility.Collapsed;
        sp.Children.Add(fileRow);
        sp.Children.Add(dictRow);

        modeCombo.SelectionChanged += (_, _) =>
        {
            var wantDictionary = (modeCombo.SelectedItem as ComboBoxItem)?.Tag as bool? ?? false;
            fileRow.Visibility = wantDictionary ? Visibility.Collapsed : Visibility.Visible;
            dictRow.Visibility = wantDictionary ? Visibility.Visible   : Visibility.Collapsed;
            onModeChanged(wantDictionary);

            // 「ファイルパス」へ戻すときだけ、辞書キーを空にして辞書モードを解除する。
            //（「辞書のキー」へ切り替えた時点では送る値が無い。
            //   ドロップゾーンでキーが選ばれた時点で初めて SET_AUDIO_FIELD が飛ぶ）
            if (!wantDictionary && !string.IsNullOrEmpty(info.AudioDictKey))
                sendField(AudioDictKeyField, "");
        };

        return sp;
    }


    /// <summary>音声ファイルを直接指定する行（ドロップ・参照ボタン対応）。</summary>
    private UIElement BuildAudioFilePathRow(SlotInfo info, Action<string, string> sendField)
        => FileRefBuilder.Build(
            "ファイル",
            info.AudioPath,
            AudioDictionaryCatalog.AudioExtensions,
            () =>
            {
                var dlg = new OpenFileDialog
                {
                    Title  = "音声ファイルを選択",
                    Filter = "音声ファイル|*.wav;*.ogg;*.mp3;*.flac|すべてのファイル|*.*",
                };
                return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
            },
            path =>
            {
                if (_currentActorId < 0) return;
                // 絶対パスを assets:// 仮想パスに変換してからランタイムへ送信する
                sendField("path", VirtualPath.ToVirtual(path, _assetsPath));
            },
            labelWidth: DictLabelWidth);

    /// <summary>
    /// 辞書キーの表示 + アクタのドロップゾーン + 解除ボタンの行。
    /// </summary>
    private UIElement BuildAudioDictKeyRow(SlotInfo info)
    {
        var hasKey = !string.IsNullOrEmpty(info.AudioDictKey);

        var grid = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(DictLabelWidth) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text              = "キー",
            Foreground        = DictBrushLabel,
            FontSize          = DictRowFontSize,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(label, 0);
        grid.Children.Add(label);

        var normalBorder = hasKey ? DictBrushBorder : DictBrushUnsetBd;
        var dropZone = new Border
        {
            Background      = DictBrushBoxBg,
            BorderBrush     = normalBorder,
            BorderThickness = new Thickness(1),
            Padding         = new Thickness(4, 2, 4, 2),
            CornerRadius    = new CornerRadius(2),
            Margin          = new Thickness(2, 0, 2, 0),
            MinHeight       = DictDropZoneMinHeight,
            AllowDrop       = true,
            ToolTip         = "Audio Dictionary を持つアクターを Hierarchy からドロップすると、\n"
                            + "その辞書のキー一覧から選べます。",
        };
        dropZone.Child = new TextBlock
        {
            Text              = hasKey ? info.AudioDictKey : "（未設定：辞書を持つアクターをドロップ）",
            Foreground        = hasKey ? DictBrushKey : DictBrushUnset,
            FontSize          = DictRowFontSize,
            TextTrimming      = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };

        // ドラッグ中のハイライト（アクタドラッグのときだけ受け付ける）
        void SetHover(bool active)
        {
            dropZone.Background  = active ? DictBrushDropBg : DictBrushBoxBg;
            dropZone.BorderBrush = active ? DictBrushDropOk : normalBorder;
        }
        void OnDragOver(object? _, DragEventArgs e)
        {
            var accept = ReferenceDragData.TryGetActorDfsId(e.Data) is not null;
            SetHover(accept);
            e.Effects = accept
                ? ((e.AllowedEffects & DragDropEffects.Copy) != 0
                    ? DragDropEffects.Copy : DragDropEffects.Move)
                : DragDropEffects.None;
            e.Handled = true;
        }
        dropZone.DragEnter += OnDragOver;
        dropZone.DragOver  += OnDragOver;
        dropZone.DragLeave += (_, e) => { SetHover(false); e.Handled = true; };
        dropZone.Drop      += (_, e) =>
        {
            SetHover(false);
            e.Handled = true;
            if (ReferenceDragData.TryGetActorDfsId(e.Data) is not { } dfsId) return;
            if (_runtime is null || _currentActorId < 0) return;
            // 辞書の中身（groups）が要るので、そのアクタの構成を個別に問い合わせる。
            // 応答は OnActorComponentsReceived が横取りして ResolvePendingAudioDictKeyPick へ渡す。
            _pendingAudioDictKey = new PendingAudioDictKeyPick(dfsId, _currentActorId, info.SlotIdx);
            _runtime.SendToRuntime($"GET_ACTOR_COMPONENTS:{dfsId}");
        };
        Grid.SetColumn(dropZone, 1);
        grid.Children.Add(dropZone);

        var clearBtn = new Button
        {
            Content           = "解除",
            FontSize          = DictHintFontSize,
            Padding           = new Thickness(6, 2, 6, 2),
            Margin            = new Thickness(2, 0, 0, 0),
            Cursor            = Cursors.Hand,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled         = hasKey,
            ToolTip           = "辞書のキーを解除してファイルパス指定へ戻す",
        };
        clearBtn.Click += (_, _) =>
        {
            if (_currentActorId < 0) return;
            _runtime?.SendToRuntime(
                $"SET_AUDIO_FIELD:{_currentActorId},{info.SlotIdx},{AudioDictKeyField},");
        };
        Grid.SetColumn(clearBtn, 2);
        grid.Children.Add(clearBtn);

        return grid;
    }

    // ============================================================
    //  AudioDictionaryComponent のインスペクタ
    // ============================================================

    /// <summary>
    /// AudioDictionaryComponent のインスペクター UI を構築して返す。
    ///
    /// グループ見出し + その行一覧という形でまとめて表示し、
    /// 変更のたびに <c>SET_AUDIO_DICT:{actor},{slot},{json}</c> でグループ配列を一括送信する。
    /// </summary>
    private UIElement BuildAudioDictionarySlotContent(SlotInfo info)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 4, 0, 4) };

        // ── 編集中の状態（コミット時にまとめて JSON 化する）──
        var groups = AudioDictionaryCatalog.ParseGroups(info.AudioDictGroupsJson);

        // グループ配列をまるごと送信するローカル関数
        void Commit()
        {
            if (_currentActorId < 0) return;
            var json = AudioDictionaryCatalog.ToPayloadJson(groups);
            _runtime?.SendToRuntime($"SET_AUDIO_DICT:{_currentActorId},{info.SlotIdx},{json}");
        }

        sp.Children.Add(new TextBlock
        {
            Text         = "「グループ名 / 用途名」がキーになります（例 Player/attack）。\n"
                         + "キーは AudioComponent の音源欄や SEED.Audio.PlayDict から参照します。",
            Foreground   = DictBrushHint,
            FontSize     = DictHintFontSize,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(0, 0, 0, 6),
        });

        // ── グループごとのブロック ────────────────────────────
        for (var gi = 0; gi < groups.Count; gi++)
        {
            sp.Children.Add(BuildAudioDictGroupBlock(groups, gi, Commit));
        }

        if (groups.Count == 0)
        {
            sp.Children.Add(new TextBlock
            {
                Text         = "グループがありません。「グループを追加」から作成してください。",
                Foreground   = DictBrushHint,
                FontSize     = DictHintFontSize,
                TextWrapping = TextWrapping.Wrap,
                Margin       = new Thickness(0, 2, 0, 4),
            });
        }

        // ── グループ追加ボタン ────────────────────────────────
        var addGroupBtn = new Button
        {
            Content             = AppIcon.WithText("Icon.Add", "グループを追加", DictButtonIconSize),
            FontSize            = DictRowFontSize,
            Padding             = new Thickness(8, 3, 8, 3),
            Margin              = new Thickness(0, 6, 0, 0),
            Cursor              = Cursors.Hand,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        addGroupBtn.Click += (_, _) =>
        {
            groups.Add(new AudioDictionaryCatalog.Group
            {
                Name = MakeUniqueGroupName(groups, AudioDictionaryCatalog.NewGroupName),
            });
            Commit();
        };
        sp.Children.Add(addGroupBtn);

        return sp;
    }

    /// <summary>
    /// 既存グループ名と衝突しない名前を作る（"NewGroup" → "NewGroup2" → …）。
    /// 重複グループ名はキーの重複に直結するので、追加時点で避ける。
    /// </summary>
    private static string MakeUniqueGroupName(
        IReadOnlyList<AudioDictionaryCatalog.Group> groups, string baseName)
    {
        var exists = new HashSet<string>(StringComparer.Ordinal);
        foreach (var g in groups) exists.Add(g.Name);
        if (!exists.Contains(baseName)) return baseName;
        // 2 から順に試す（1 は付けない ＝ 素の名前が 1 番目という読み方）
        for (var n = 2; ; n++)
        {
            var candidate = baseName + n.ToString(CultureInfo.InvariantCulture);
            if (!exists.Contains(candidate)) return candidate;
        }
    }

    /// <summary>グループ 1 つ分（見出し行 + 行一覧 + 行追加ボタン）を構築する。</summary>
    private UIElement BuildAudioDictGroupBlock(
        List<AudioDictionaryCatalog.Group> groups, int groupIndex, Action commit)
    {
        var group = groups[groupIndex];
        var block = new StackPanel { Margin = new Thickness(0, 4, 0, 6) };

        // ── 見出し行: グループ名 + 行追加 + グループ削除 ──
        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin      = new Thickness(0, 0, 0, 2),
        };
        header.Children.Add(new TextBlock
        {
            Text              = "グループ",
            Foreground        = DictBrushGroup,
            FontSize          = DictRowFontSize,
            FontWeight        = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin            = new Thickness(0, 0, 4, 0),
        });
        header.Children.Add(BuildDictTextBox(
            group.Name, DictUsageBoxWidth,
            "グループ名（キーの前半。例 Player）",
            v => { group.Name = v; commit(); }));

        var addRowBtn = new Button
        {
            Content  = AppIcon.WithText("Icon.Add", "行", DictButtonIconSize),
            FontSize = DictHintFontSize,
            Padding  = new Thickness(6, 2, 6, 2),
            Margin   = new Thickness(6, 0, 0, 0),
            Cursor   = Cursors.Hand,
            ToolTip  = "このグループに用途を 1 行追加する",
        };
        addRowBtn.Click += (_, _) =>
        {
            group.Entries.Add(new AudioDictionaryCatalog.Entry
            {
                Usage  = AudioDictionaryCatalog.NewEntryUsage,
                Volume = AudioDictionaryCatalog.DefaultVolume,
            });
            commit();
        };
        header.Children.Add(addRowBtn);

        var delGroupBtn = new Button
        {
            Content  = AppIcon.WithText("Icon.Delete", "グループ", DictButtonIconSize),
            FontSize = DictHintFontSize,
            Padding  = new Thickness(6, 2, 6, 2),
            Margin   = new Thickness(4, 0, 0, 0),
            Cursor   = Cursors.Hand,
            ToolTip  = "このグループを行ごと削除する",
        };
        delGroupBtn.Click += (_, _) =>
        {
            // 行が入っているグループの削除は取り返しがつかないので確認する
            if (group.Entries.Count > 0)
            {
                var answer = MessageBox.Show(
                    $"グループ「{group.Name}」を {group.Entries.Count} 行ごと削除します。よろしいですか？",
                    "グループの削除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
                if (answer != MessageBoxResult.OK) return;
            }
            // 添字ではなく実体で消す。コミットのたびに UI は組み直されるが、
            // 組み直しが届く前に別の削除ボタンが押されると添字が古くなり、
            // 隣のグループを消してしまう（identity なら必ず意図した 1 件だけ消える）。
            groups.Remove(group);
            commit();
        };
        header.Children.Add(delGroupBtn);
        block.Children.Add(header);

        // ── 行一覧 ────────────────────────────────────────────
        var rows = new StackPanel { Margin = new Thickness(DictGroupIndent, 0, 0, 0) };
        for (var ei = 0; ei < group.Entries.Count; ei++)
        {
            rows.Children.Add(BuildAudioDictEntryRow(group, ei, commit));
        }
        if (group.Entries.Count == 0)
        {
            rows.Children.Add(new TextBlock
            {
                Text       = "（行なし：「行」ボタンで追加）",
                Foreground = DictBrushHint,
                FontSize   = DictHintFontSize,
                Margin     = new Thickness(0, 2, 0, 2),
            });
        }
        block.Children.Add(rows);

        return block;
    }

    /// <summary>辞書の 1 行（用途名・音声ファイル・音量・削除）を構築する。</summary>
    private UIElement BuildAudioDictEntryRow(
        AudioDictionaryCatalog.Group group, int entryIndex, Action commit)
    {
        var entry = group.Entries[entryIndex];
        var row = new StackPanel { Margin = new Thickness(0, 2, 0, 4) };

        // ── 1 段目: 用途名 / 音量 / 削除 ──
        var top = new StackPanel { Orientation = Orientation.Horizontal };
        top.Children.Add(new TextBlock
        {
            Text              = "用途",
            Foreground        = DictBrushLabel,
            FontSize          = DictRowFontSize,
            Width             = 32,
            VerticalAlignment = VerticalAlignment.Center,
        });
        top.Children.Add(BuildDictTextBox(
            entry.Usage, DictUsageBoxWidth,
            "用途名（キーの後半。例 attack）",
            v => { entry.Usage = v; commit(); }));

        top.Children.Add(new TextBlock
        {
            Text              = "音量",
            Foreground        = DictBrushLabel,
            FontSize          = DictRowFontSize,
            Margin            = new Thickness(8, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
        });
        top.Children.Add(BuildDictTextBox(
            entry.Volume.ToString(DictVolumeFormat, CultureInfo.InvariantCulture),
            DictVolumeBoxWidth,
            "既定音量（1.0 = 等倍）。再生側が音量を指定しなかったときに使われる",
            v =>
            {
                // 数値として読めないときは編集前の値を保つ（入力途中で値を壊さない）
                if (!float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) return;
                entry.Volume = MathF.Max(AudioDictionaryCatalog.MinVolume, f);
                commit();
            }));

        var delBtn = new Button
        {
            Content  = AppIcon.Create("Icon.Delete", DictButtonIconSize),
            FontSize = DictHintFontSize,
            Padding  = new Thickness(6, 2, 6, 2),
            Margin   = new Thickness(6, 0, 0, 0),
            Cursor   = Cursors.Hand,
            ToolTip  = "この行を削除する",
        };
        // 添字ではなく実体で消す（上のグループ削除と同じ理由）。
        delBtn.Click += (_, _) => { group.Entries.Remove(entry); commit(); };
        top.Children.Add(delBtn);
        row.Children.Add(top);

        // ── 2 段目: 音声ファイル（Project パネルからのドロップ受付は音声パス欄と同一）──
        row.Children.Add(FileRefBuilder.Build(
            "音声",
            entry.Path,
            AudioDictionaryCatalog.AudioExtensions,
            () =>
            {
                var dlg = new OpenFileDialog
                {
                    Title  = "音声ファイルを選択",
                    Filter = "音声ファイル|*.wav;*.ogg;*.mp3;*.flac|すべてのファイル|*.*",
                };
                return dlg.ShowDialog(Window.GetWindow(this)) == true ? dlg.FileName : null;
            },
            path => { entry.Path = VirtualPath.ToVirtual(path, _assetsPath); commit(); },
            onClear: () => { entry.Path = ""; commit(); },
            labelWidth: DictLabelWidth));

        // ── 3 段目: このキー（完成していれば緑、未完成なら警告色）──
        var complete = AudioDictionaryCatalog.IsCompleteKey(group.Name, entry.Usage)
                       && !string.IsNullOrEmpty(entry.Path);
        row.Children.Add(new TextBlock
        {
            Text = complete
                ? "キー: " + AudioDictionaryCatalog.MakeKey(group.Name, entry.Usage)
                : "この行は未完成です（グループ名・用途名・音声ファイルが必要）",
            Foreground   = complete ? DictBrushKey : DictBrushWarn,
            FontSize     = DictHintFontSize,
            TextWrapping = TextWrapping.Wrap,
            Margin       = new Thickness(DictLabelWidth + 2, 0, 0, 0),
        });

        return row;
    }

    /// <summary>
    /// 辞書編集用の 1 行テキストボックスを作る。
    ///
    /// 確定は <b>Enter キーとフォーカス喪失のみ</b>（1 文字ごとに送らない）。
    /// 送信のたびにランタイムが ACTOR_COMPONENTS を返してインスペクタを組み直すため、
    /// 入力中に確定するとキャレットが飛んで文字が打てなくなる。
    /// </summary>
    /// <param name="initial">初期テキスト。</param>
    /// <param name="width">ボックス幅（px）。</param>
    /// <param name="tooltip">ツールチップ。</param>
    /// <param name="onCommit">確定時に呼ぶコールバック（確定後のテキスト）。</param>
    private static TextBox BuildDictTextBox(
        string initial, double width, string tooltip, Action<string> onCommit)
    {
        var box = new TextBox
        {
            Text              = initial,
            Width             = width,
            FontSize          = DictRowFontSize,
            Background        = DictBrushBoxBg,
            Foreground        = DictBrushBoxFg,
            BorderBrush       = DictBrushBorder,
            BorderThickness   = new Thickness(1),
            Padding           = new Thickness(3, 1, 3, 1),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip           = tooltip,
        };

        // 値が変わっていないときは送らない（無意味な IPC と Undo 記録を増やさない）
        void Commit()
        {
            if (box.Text == initial) return;
            onCommit(box.Text);
        }
        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Return or Key.Enter)) return;
            Commit();
            e.Handled = true;
        };
        return box;
    }
}
