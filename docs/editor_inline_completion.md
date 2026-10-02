# AI インライン補完（スクリプトエディタ）

スクリプトエディタ（`.cs`）で、カーソルの後ろに AI の予測を薄い文字（ゴースト）で出し、Tab で確定する機能
（Copilot 風）。この文書が仕組みと設定の正典。スクリプトパネル全体の仕様は [editor_script_panel.md](editor_script_panel.md)。

- コード: `editor/src/Panels/ScriptEditor/InlineCompletion/`
- 注入する API リファレンス: [scripting_api.md](scripting_api.md)（スクリプト API の正典。ここに書かれていない API は AI が知らない）
- テスト: `dotnet run --project editor/tests/InlineCompletionTests`（外部の AI サービスは呼ばない）

---

## 1. 使い方と設定

スクリプトエディタの設定（歯車）→「AI 補完」カテゴリ（`editor/settings/script_editor.json`）。

| 項目 | 既定 | 意味 |
|---|---|---|
| インライン補完を有効にする | オフ | 予測 → Tab で確定・Esc で却下 |
| 手動トリガのみ（Alt+/） | オン | 入力中に自動で投げない（Groq のトークン/分の上限に当たりにくい） |
| Groq API キー・モデル | — | 補完の提供元（クラウド・OpenAI 互換。今の提供元は Groq だけ） |

AI へ渡す API リファレンスの量（予算）は環境設定 `editor/settings/editor_preferences.json` の
`"inline_completion_reference_chars"`（§3.5）。設定の画面は無い（ファイルを直接書く）。

## 2. 部品

| ファイル | 役割 |
|---|---|
| `InlineCompletionController.cs` | 入力の停止（デバウンス）・Alt+/ で要求し、予測をゴーストで出す。Tab で確定・Esc で却下 |
| `GhostTextRenderer.cs` | ゴーストの描画 |
| `IInlineCompletionProvider.cs` | 提供元の口（カーソルの前・後の全文を渡して予測を受け取る） |
| `GroqInlineCompletionProvider.cs` | Groq のチャット補完（ストリーミング・429 のクールダウン・`<think>` の除去） |
| `InlineCompletionSystemPrompt.cs` | システムプロンプトの組み立て（基本方針＋選んだリファレンス）と注入のログ。提供元に共通 |
| `ScriptApiReference.cs` | エディタ側の入口: `docs/scripting_api.md` を探して読み、設定と予算で選ばせる（初回だけ読む） |
| `Reference/`（WPF 非依存） | リファレンスの圧縮・節への分割・索引・文脈の抽出・点付けと選択・キャッシュ・設定 |

`Reference/` の中:

| ファイル | 役割 |
|---|---|
| `ApiReferenceCompactor.cs` | Markdown の圧縮（§3.1。抽出の規則の正典） |
| `ApiReferenceSectionSplitter.cs`・`ApiReferencePart.cs` | 節と切れ端への分割（§3.2） |
| `ApiIdentifierTokenizer.cs`・`ApiReferenceIndex.cs` | 語の切り出しと「語 → 切れ端」の索引・珍しさ・接頭辞の検索 |
| `CompletionContextExtractor.cs`・`CompletionContext.cs` | 編集中のファイルの文脈の語（§3.3） |
| `ApiReferenceSelector.cs`・`ApiReferenceSelection.cs` | 点付けと予算内の選択・本文の並べ方（§3.4） |
| `ApiReferenceSelectionCache.cs` | 選択結果の使い回し（§3.6） |
| `ApiReferenceSettings.cs` | `editor/config/inline_completion_reference.json` の読み込みと検証（§4） |
| `ContextualApiReference.cs` | 上をまとめる窓口（全文・カーソル・予算 → 選択） |

## 3. API リファレンスの注入

AI は SEED の API を知らない（Unity と取り違える）ので、システムプロンプトに `docs/scripting_api.md` を
「正典」として入れる。ただし全文（約 45 万字）は大きすぎるので、圧縮したうえで**編集中のファイルの文脈に合う節**を
選び、予算（既定 12000 字）に収める。

> 以前（2026-10-03 まで）は圧縮後の全文の**先頭から 12000 字で切っていた**ため、§2 の途中より後ろ
> （§7.1x の Platform・UI 部品・画面の組み立て・Localization など）は AI に一切届いていなかった。

### 3.1 圧縮（`ApiReferenceCompactor`）

| 残すもの | 落とすもの |
|---|---|
| 見出し行（`#`〜`####`） | 説明の散文・補足の箇条書き |
| コードフェンスの中の全行（フェンス行も） | コードの外の空行 |
| 表の行（縦棒で始まる行） | 「重要」を含まない引用（`>`） |
| 「重要」を含む引用（`>`）の行 | **見出しに「メンテナ向け」を含む節から後ろの全部**（§8 以降） |

改行は LF にそろえる。圧縮後は約 20 万 6 千字（2026-10-03 の docs で 125 節）。

### 3.2 節と切れ端（`ApiReferenceSectionSplitter`）

- 見出し `#`・`##`・`###` で節に分ける。`####`（レシピなど）は親の節の中に残す。コードの中の `#` 行は見出しにしない。
- 節の名前（ログ用）: 前書き（文書の題・Unity ではない等の重要注記）は `§0`、`##` は `§番号`（`§7.18`）、
  `###` は `§親/短い題`（`§7/Transform`・`§7/利用可能なコンポーネント一覧`）。
- **長い節は切れ端に分ける**（`max_part_chars`、既定 2500 字）。§7.18（画面の組み立て）は 1 つのコードブロックだけで
  約 1 万 4 千字あり、そのままでは予算に入らないため。切れ目はコードの空行の段落 → 1 行、表は行、それ以外は行。
  コードの途中から始まる切れ端にはフェンスの開き（` ```csharp `）を、途中で終わる切れ端には閉じを補い、
  表の途中から始まる切れ端には表の頭（見出しの行と区切りの行）を補う。どの切れ端も見出し行から始まる。
- 同じ節の隣り合う切れ端を両方選んだときは、続けて並べる（補った見出し・フェンス・表の頭を省く。
  全部選ぶと元の節と同じ本文になる）。ログでは `§7.18[1-2/7]`（7 つのうち 1〜2）、全部なら `§7.18`。

### 3.3 文脈の語（`CompletionContextExtractor`）

編集中のファイルの全文を正規表現で 1 回だけ走査し、語（ASCII の識別子。大文字小文字は区別しない）と出方を集める。

| 出方 | 例 | 重み（既定） |
|---|---|---|
| ファイルのどこかに出る | 型名・メソッド名・変数名・コメントの英単語 | `file` 1.0 |
| カーソルの前後 N 行（`cursor_window_lines`、既定 20）に出る | | ＋`near_cursor` 2.0 |
| カーソルの直前で書きかけ | `Bind` の直後にカーソルの Bind、`Bind.` の直後の Bind | ＋`typing` 3.0 |
| `using` の名前空間 | `using SEED.Localization;` の Localization | ＋`using` 2.0 |
| `SEED.` に続く語 | `SEED.UI.ScreenStack` の UI・ScreenStack | ＋`engine_qualified` 1.0 |

語の重みは当てはまる出方の重みの和（出た回数は数えない）。捨てる語: 1 文字の語・C# のキーワード
（小文字の綴りだけ。`Get` のような API 名は残る）・設定の `ignored_words`（`SEED`・`System` など）。
**文字列・文字のリテラルの中身は数えない**（`"assets://alarm/prefabs/edit.actor"` の edit・actor のような
API 名でない語が関係の薄い節に点を付けるため）。コメントの中は数える。

### 3.4 点付けと選択（`ApiReferenceSelector`）

切れ端の点 = Σ（文脈の語ごと）語の重み × 語の珍しさ × 一致の倍率

- 語の珍しさ = ln(1 + 切れ端の数 / その語が出る切れ端の数)。`L10n`・`ScreenStack` のように少ない節にしか出ない語ほど強い。
- 一致の倍率: 見出しに出れば `heading_match`（2.0）、本文なら `body_match`（1.0）。
- **接頭辞の一致**: 索引に完全一致の無い語と、書きかけの語は、その語で始まるより長い語（`Bin` → `Bind`・`Binding`・
  `Bindable`）も見る（`min_prefix_length` 文字以上、既定 3）。1 つの文脈の語につき切れ端ごとに最も強い一致 1 つだけを数え、
  `prefix_match`（0.5）を掛ける。

選び方:

1. **常に入れる節**（`always_include`。見出しの文＝先頭の `#` を除いたものがその文字列で始まる節の全切れ端）を
   文書の順に、予算に入る限り入れる。既定は前書き・§1 スクリプトの基本形・§2 ライフサイクル関数・
   §7 GameObject とコンポーネントの先頭・利用可能なコンポーネント一覧（合計 6,762 字）。
2. 残りは**点の高い順**（同点なら文書の順）に、予算に入るものを入れる。次の切れ端は予算が余っていても入れない:
   点が 0、`min_score`（6.0）未満、または「常に入れる節以外で最も高い点 × `relative_min_score`（0.4）」未満。
   後者は、ScreenStack に強く当たるファイルで、見出しに「UI」と書いてあるだけの節（§7.21 の「UI への結び付け」など）が
   残りの予算を埋めないため。関係の薄い節でトークンを使わない。
3. 選んだ切れ端は**文書の順（見出しの番号順）**に並べる。本文の文字数は必ず予算以下。

実際の docs での例（予算 12000。`InlineCompletionTests` が出す）:

| 編集中のファイル | 常に入れる節のほかに入る節 |
|---|---|
| `UiScreen` で `ScreenStack` に `Push` する画面 | §7.18[1-2/7]（画面のスタック・画面のスクリプト）。§7.21 は入らない |
| `L10n.Get` で文字を引く | §7.21[1-2/4]・§7.21[4/4] |
| `Input.GetKey`・`transform.Position` で動かす | §6.5 Input・§6.6 キャラクターコントローラー・§7/ControlPointPath |
| `Bind.` と書きかけ（§7.22 Binding のある docs） | §7.22[2-3/3] |

### 3.5 予算

- 予算 = 注入するリファレンスの最大文字数。環境設定 `editor_preferences.json` の
  `"inline_completion_reference_chars"`（整数）が優先し、無い・`null` なら `inline_completion_reference.json` の
  `budget_chars`（既定 12000＝以前の固定値と同じ）。0〜2,000,000 に丸める。**0 ならリファレンスを入れない**。
- Groq の無料枠はトークン/分・トークン/日の上限が厳しいので既定は 12000 のまま。
  **上限の広い提供元（ローカル LLM・有料の API など）なら大きくしてよい**（例: 60000 で関係する節がほぼ全部入る）。
  大きくしても点の下限（§3.4 の 2）は変わらないので、関係の薄い節までは入らない。
- 予算が常に入れる節の合計より小さいときは、入る分だけ入れてログに「一部を省いた」と出す。

### 3.6 キャッシュと重さ

- 補完は打鍵ごとに走るので、選択は軽くしてある（文脈の抽出は正規表現 1 回、点付けは索引を引いて足すだけ）。
  2,160 行・5 万字のファイルで約 5〜13 ms。
- ファイルの内容（SHA-256）・カーソルの位置・予算が同じなら、前回の選択をそのまま使う（最近の 16 件）。
  カーソルの位置も鍵に入れるのは、近くの語を重く数えるため（同じ内容でも位置で選ぶ節が変わりうる）。
- 索引（圧縮・分割・語の索引）は初回の補完の要求で 1 回だけ作る（約 20〜140 ms。UI スレッド）。
  docs や設定を変えたらエディタを再起動する。

### 3.7 ログ（`[インライン補完]` で追える）

```text
[インライン補完] APIリファレンス索引: 節 125・切れ端 168・圧縮後 206,337 文字・常に入れる節 6,762 文字・予算 12,000 文字（設定 …\editor\config\inline_completion_reference.json）
[インライン補完] 注入: §0 §1 §2 §7 §7/利用可能なコンポーネント一覧 §7.18[1-2/7]（11425 文字）
[インライン補完] 注入: 前回と同じ（6 節・11425 文字）
```

設定の問題（壊れた JSON・範囲外の値・どの節にも当たらない `always_include`）も同じ接頭辞で出る。

### 3.8 文脈を渡さない呼び出し（互換）

`ScriptApiReference.Load()` は従来どおり、圧縮後の全文の**先頭から予算まで**を返す（今のエディタの中では使っていない）。
変わった点: 圧縮後の改行が CRLF から LF になった（同じ予算で入る文字が少し増える）。

## 4. 設定ファイル `editor/config/inline_completion_reference.json`

無い・壊れているときは同じ中身の組み込み既定（`ApiReferenceSettings.BuiltIn()`）で動く。コメント・末尾カンマ可。
範囲外の値は丸めて、理由をログに出す。

| 鍵 | 既定 | 意味 |
|---|---|---|
| `budget_chars` | 12000 | 予算の既定（環境設定が優先。§3.5） |
| `max_part_chars` | 2500 | 節をこれより長ければ切れ端に分ける（下限 500） |
| `cursor_window_lines` | 20 | カーソルの前後この行数を「近く」とする |
| `min_prefix_length` | 3 | 接頭辞の一致を見る語の最短の文字数 |
| `min_score` | 6.0 | 文脈で選ぶ節の点の下限 |
| `relative_min_score` | 0.4 | 文脈で選ぶ節の点の下限（最も点の高い節に対する割合。0〜1） |
| `always_include` | 前書き・§1・§2・§7 の先頭・コンポーネント一覧 | 常に入れる節（見出しの文の先頭。`"7."` と書くと §7.18 にも当たるので `"7. GameObject とコンポーネント"` のように題まで書く）。`[]` なら無し |
| `ignored_words` | SEED・SEEDEditor・Scripting・SEEDScript・System・Collections・Generic・Linq | 文脈の語から捨てる語 |
| `weights` | §3.3・§3.4 の表 | `file`・`near_cursor`・`typing`・`using`・`engine_qualified`・`heading_match`・`body_match`・`prefix_match` |

## 5. リファレンスを書く人へ（docs/scripting_api.md）

- API のシグネチャは ` ```csharp ` の中・一覧は表に書く（圧縮で残るのはこれだけ）。利用者向けの節を「メンテナ向け」の節より後ろに置かない。
- **節の見出しに API の名前を入れる**（`### ListView（…）`）。見出しの語は本文の 2 倍の重みで当たる。
- **コードの例に型名・メソッド名を書く**（`UiWidget.Of<ScreenStack>(…)`）。編集中のファイルの語と同じ綴りで当たる。
- `always_include` は見出しの文の先頭一致なので、§1・§2・§7 の先頭や「利用可能なコンポーネント一覧」の見出しを変えたら
  `inline_completion_reference.json` も直す（外れるとログに警告が出る。テスト `InlineCompletionTests` も落ちる）。

## 6. テスト

`dotnet run --project editor/tests/InlineCompletionTests`（56 件。2026-10-03）。

- 見本の小さなリファレンスで: 圧縮・節と切れ端（フェンスと表の頭の補い・続けて並べると元に戻る）・語の索引・接頭辞・
  文脈の語（using・SEED. 修飾・近く・書きかけ・リテラル）・選択（ScreenStack → §7.18、Localization を使わない → §7.21 が落ちる、
  `Bind` → §7.22、`Bin` の接頭辞 → §7.22、常に入れる節、予算を超えない、文書の順）・キャッシュ・設定。
- 実際の `docs/scripting_api.md` で: 全節の文字数（一覧を出す）・常に入れる節が 12000 に収まる・見本ごとの選択・
  数千行のファイルでの時間。§7.22 が docs に無いときは Bind の確認を飛ばす。
- 偽物の提供元（通信しない）から `InlineCompletionSystemPrompt.Build` を呼び、プロンプトに選んだ節が入ること・
  注入のログ・環境設定の予算が効くこと。

## 7. 制限（docs/backlog.md「AI インライン補完の API リファレンスの選択」）

- 選び方は字面（識別子の一致）だけ。日本語のコメントや、型の解決（Roslyn の意味情報）は使っていない。
- 補完の質が実際に上がったか（Groq での比較）・エディタ GUI での確認はしていない。
- 予算を変える画面が無い（`editor_preferences.json` を直接書く）。
