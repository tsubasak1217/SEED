# 音声辞書（AudioDictionary）

「音の意味（用途）」と「音声ファイルの実体」を分離するための仕組み。
スクリプトや AudioComponent が生のパスを持つのをやめ、
`Player/attack` のような **キー** だけを持たせる。
素材を差し替えたいときは辞書の 1 行を直すだけで、参照している全員に一括で反映される。

- 実装（Rust）: `runtime/src/engine/components/audio_dictionary_component.rs`（データ）／
  `runtime/src/engine/core/audio/dictionary_index.rs`（キー索引・純関数）／
  `runtime/src/engine/core/app_base/app/audio_dictionary_ops.rs`（索引の構築と IPC）
- 実装（エディタ）: `editor/src/Controls/AudioDictionaryCatalog.cs`（ワイヤ表現・純ロジック）／
  `editor/src/Controls/AudioDictionaryKeyWindow.cs`（キー選択ウィンドウ）／
  `editor/src/Panels/InspectorPanel.AudioDictionary.cs`（インスペクタ UI）
- 実装（C# スクリプト）: `scripting/src/Api/AudioDictionary.cs`／`scripting/src/Api/Audio.cs`
- スクリプト API の正典は `docs/scripting_api.md`（第 6.7 節・第 7 節）

---

## 1. データ形式

AudioDictionaryComponent は「グループ」の配列を持ち、グループは「行」の配列を持つ。
キーは `グループ名/用途名`。

`.scene` に保存される JSON（`components` 配列の 1 要素）:

```json
{
  "type": "AudioDictionaryComponent",
  "data": {
    "groups": [
      {
        "name": "Player",
        "entries": [
          { "usage": "attack", "path": "assets://sounds/player/atk.wav", "volume": 1.0 },
          { "usage": "jump",   "path": "assets://sounds/player/jmp.ogg", "volume": 0.8 }
        ]
      },
      {
        "name": "UI",
        "entries": [
          { "usage": "click", "path": "assets://sounds/ui/click.wav", "volume": 0.6 }
        ]
      }
    ]
  }
}
```

上の例が定義するキーは `Player/attack` / `Player/jump` / `UI/click` の 3 つ。

| フィールド | 意味 | 既定値 |
|---|---|---|
| `groups[].name` | グループ名（キーの前半） | `""` |
| `groups[].entries[].usage` | 用途名（キーの後半） | `""` |
| `groups[].entries[].path` | 音声ファイルの `assets://` 仮想パス | `""` |
| `groups[].entries[].volume` | 既定音量（1.0 = 等倍） | `1.0` |

- 全フィールドに `#[serde(default)]` が付いているので、**この機能を知らない旧 `.scene` も読める**。
- 上限は 1 コンポーネントあたりグループ 64・1 グループあたり行 256
  （`MAX_AUDIO_DICT_GROUPS` / `MAX_AUDIO_DICT_ENTRIES_PER_GROUP`）。超過分は読み込み時に切り詰められる。

AudioComponent 側は音源の指定方法が 2 択になり、辞書モードでは `dictionary_key` を持つ:

```json
{
  "type": "AudioComponent",
  "data": {
    "audio_path": "",
    "dictionary_key": "Player/attack",
    "volume": 1.0,
    "loop": false,
    "play_on_start": false,
    "spatial": false,
    "min_distance": 2.0,
    "max_distance": 50.0,
    "pan": 0.0
  }
}
```

`dictionary_key` が空文字列なら従来どおり `audio_path` を使う（完全な後方互換）。

---

## 2. キー解決の規則

> **重要**: 解決できないキーは**警告を出して何も鳴らさない**。無音で握りつぶさない。

1. キーは `グループ名` + `/` + `用途名` の**完全一致**で引く（大文字小文字も区別する）。
2. シーン内（**アクティブ世界線**）の全 AudioDictionary を **DFS 順**（ヒエラルキーの上から順）に走査し、
   最初に見つかった行を採用する（**先勝ち**）。
3. グループ名・用途名・パスのいずれかが空の行は**索引に入らない**（作りかけの行を誤って引かせない）。
4. 同じキーが複数の辞書にあった場合は、索引を作り直したときに **1 度だけ** 警告を出す
   （`[SEED audio] 音声辞書のキーが重複しています…`）。重複が解消されるとまた警告できる状態に戻る。
5. 音量は、呼び出し側が明示しなければ辞書の `volume` を使う。
   明示した場合（`PlayDict(key, 0.5f)` など）はそちらが優先される。

### 索引の再構築タイミング

キー → (パス, 音量) の索引はランタイムが保持し、次のときに作り直す（dirty フラグ方式）。

- シーンのロード・差し替え（起動時・`Scene.Load` / `Scene.Transition`）
- Play 開始時（Edit 中の編集経路を取りこぼしていても、ここで必ず現状へ追いつく）
- インスペクタでの辞書編集（`SET_AUDIO_DICT`）
- コンポーネントの追加・削除・複製、スロットの再構築、Undo / Redo
- スクリプトの `Instantiate` / `Destroy` / シーン遷移

実際の再構築はフレーム先頭（スクリプトフェーズより前）でまとめて行うので、
同じフレーム内で何度変更しても作り直しは 1 回で済む。

---

## 3. インスペクタ操作

### 3-1. 辞書を作る

1. 任意のアクターを選び、「コンポーネント追加 → サウンド → **Audio Dictionary**」。
   1 つのシーンに複数置いてもよい（キーが重複しなければ問題ない）。
   管理しやすさを優先するなら、空アクターを 1 つ作ってそこにまとめるのが分かりやすい。
2. 「グループを追加」でグループを作り、**グループ名**（例 `Player`）を入力する。
3. グループの「行」ボタンで行を追加し、**用途名**（例 `attack`）を入力する。
4. 行の「音声」欄に、Project パネル（またはエクスプローラー）から音声ファイルをドロップする
   （受け付ける拡張子は AudioComponent の音声パス欄と同じ `.wav` / `.ogg` / `.mp3` / `.flac`）。
   「参照」ボタンからファイル選択ダイアログを開くこともできる。
5. 必要なら**音量**（既定 1.0）を調整する。これが「呼び出し側が音量を指定しなかったときの音量」になる。

各行の下に、その行が定義するキー（`キー: Player/attack`）が表示される。
グループ名・用途名・音声ファイルのどれかが欠けている行は
「この行は未完成です」と警告色で表示され、**キーとして引けない**。

テキスト欄（グループ名・用途名・音量）は **Enter キーまたはフォーカスを外したとき**に確定する
（1 文字ごとに送ると、その都度インスペクタが組み直されて入力できなくなるため）。

### 3-2. AudioComponent から辞書のキーを使う

1. AudioComponent を持つアクターを選ぶ。
2. インスペクタの「**音源**」コンボを「**辞書のキー**」に切り替える。
3. 現れた「キー」欄へ、**AudioDictionary を持つアクター**を Hierarchy パネルからドロップする。
4. その辞書のキー一覧が**グループごとにまとまった**ウィンドウで開くので、使うキーを選ぶ。
5. 選んだキー（例 `Player/attack`）が保存される。

- 辞書モードのあいだ、**音量欄は表示されない**（音量も辞書から解決されるため）。
  代わりに「音量は音声辞書の既定値が使われます」という補足が出る。
- 「解除」ボタンでキーを消すと、ファイルパス指定へ戻る。
- 辞書を持っていないアクターを落とした場合・使えるキーが 1 つも無い場合は警告が出て何も変わらない。

---

## 4. スクリプト API

正典は `docs/scripting_api.md`（第 6.7 節 Audio / 第 7 節 AudioDictionary）。要点だけ再掲する。

### 4-1. シーン全体から引く（静的 API）

```csharp
SEED.Audio.PlayDict("Player/attack");            // 音量は辞書の既定値
SEED.Audio.PlayDict("Player/attack", 0.5f);      // 音量を明示（辞書の既定値より優先）

SEED.Audio.PlayBgmDict("Bgm/stage1");            // 音量は辞書の既定値・ループあり
SEED.Audio.PlayBgmDict("Bgm/jingle", false);     // ループなし
SEED.Audio.PlayBgmDict("Bgm/stage1", 0.8f, loop: true);
```

**パス指定の `Play` / `PlayBgm` とは別名**にしてある。
同じ名前のオーバーロードにすると「パスのつもりでキーを渡した／その逆」を
コンパイラが見抜けず、無音という分かりにくい形で失敗するため。
`Play(path)` の挙動は一切変えていない。

### 4-2. 特定の辞書だけを引く（コンポーネントハンドル）

```csharp
if (gameObject.GetComponent<AudioDictionary>() is { } dict)
{
    if (dict.TryGetPath("Player/attack", out var path)) { /* path は assets:// パス */ }
    float v = dict.DefaultVolume("Player/attack");   // 引けなければ 1.0
    dict.Play("Player/attack");                      // 音量は辞書の既定値
    dict.Play("Player/attack", 0.5f);                // 音量を明示
}

// [SerializeField] で別アクターの辞書を差し込む
[SEEDEditor.Scripting.SerializeField] SEED.AudioDictionary? bank;
void Update() { if (bank is { IsValid: true } b) b.Play("UI/click"); }
```

こちらは**その辞書 1 つだけ**を見る（シーン内の他の辞書は引かない）。
「キャラクターごとに専用の辞書を持たせ、そのキャラのスクリプトは自分の辞書だけを引く」
という設計にすると、キーの衝突を構造的に避けられる。

### 4-3. AudioComponent 経由

```csharp
if (gameObject.GetComponent<AudioSource>() is { } audio)
{
    audio.DictionaryKey = "Player/attack";   // 辞書モードへ切り替え（パスと音量を辞書から解決）
    audio.DictionaryKey = "";                // 解除（Path を直接使う従来動作へ戻る）
    audio.Play();
}
```

---

## 5. 互換性

- 既存の `SEED.Audio.Play` / `PlayBgm`、AudioComponent の `audio_path` は**一切変わっていない**。
- `dictionary_key` を持たない旧 `.scene` は、そのフィールドが空文字列（＝従来動作）として読まれる。
- 辞書を 1 つも置かなければ、この機能は完全に不在と同じ状態になる（索引は空・警告も出ない）。

---

## 6. 設計上の注意

- **辞書モードでは音量も辞書から来る**。AudioComponent の `volume` は使われない。
  「辞書側を直せば全参照に一括で反映される」ことが本機能の目的であり、
  コンポーネント側の音量が勝つとその目的が崩れるため。
  1 か所だけ音量を変えたい場合は、辞書に別の用途名で行を足すか、
  スクリプトから `PlayDict(key, volume)` で明示する。
- **キーの区切りは `/` 固定**（Rust `AUDIO_DICT_KEY_SEPARATOR` / C# `AudioDictionaryCatalog.KeySeparator`）。
  グループ名・用途名に `/` を含めないこと。
- **別世界線（アクター編集タブ・キャンバス編集タブ）の辞書は索引に入らない**。
  シーンの再生に使う辞書は、シーン世界線のアクターに置くこと。
