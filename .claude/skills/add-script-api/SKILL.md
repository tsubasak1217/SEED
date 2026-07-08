---
name: add-script-api
description: 新しい ECS コンポーネント/フィールドを C# スクリプトへ公開する、または既存スクリプト API を追加・変更するときに使用する。「スクリプトから ○○ を触れるようにして」「コンポーネントをスクリプト公開」「script API を追加/変更」といった依頼が発動条件。Rust レジストリ登録・C# ラッパー・docs 同期・両ビルド検証まで一貫して行う。
---

# 新しい API / コンポーネントをスクリプトへ公開する

SEED のスクリプト API は「Rust ECS 側レジストリ」→「FFI」→「C# ラッパー」→「docs（AI 補完の情報源）」の 4 層で成り立つ。
このスキルは、その 4 層を過不足なく・毎回同じ品質で更新するための手順書である。

正典は `docs/scripting_api.md`。ここに書かれていない API は AI インライン補完が知らない。
運用ルールは `.claude/CLAUDE.md` の「[スクリプトAPI（重要な運用ルール）]」節と完全に一致させること。

---

## 0. まず事前判断（どこまで触るか決める）

作業に入る前に、追加したいものについて次の 3 問に答える。答えによって触るファイルが変わる。

**Q1. 数値/文字列フィールドだけを読み書きできれば良いか？（型付き C# プロパティは不要か）**
- はい → **Rust 側レジストリ登録のみ**でスクリプトから `gameObject.HasComponent(...)` +
  `ScriptHost.TryGetFloats/TrySetFloats/TryGetString/...`（名前指定の汎用アクセス）で使える。C# ラッパーは任意。
- いいえ（`transform.Position` のように型付きプロパティで使いたい）→ **C# ラッパー struct も追加**する（手順 3）。

**Q2. データ型は何か？** データ表現規約（`host_api.rs` 冒頭コメント「データ表現」）：
- 数値はすべて `float` 配列。`f32`=1 要素 / `Vector2`=2 / `Vector3`=3 / RGBA カラー=4。
- `bool` は `0.0 / 1.0`。整数（解像度など）は `f32` に変換して受け渡す（`u32` は 2^24 まで無損失）。
- 文字列（テクスチャパス等）は UTF-8 バイト列。→ `read_string / write_string` を使う。

**Q3. そのコンポーネントはアクターのルートエンティティに直付けか、スロット格納型か？**
- ルート直付け（`Transform` / `CanvasTransform`）→ `world.get::<T>(entity)` を直接使う。
- スロット格納型（`SpriteComponent` / `CameraComponent` / `AudioComponent` など、
  エディタの「コンポーネント追加」で足す系）→ 必ず `locate::<T>(world, entity)` でスロットを解決してから `get` する。
  判断に迷ったら、そのコンポーネントがアクター生成時に常にルートへ挿入されるか（Transform 系）で見分ける。追加コンポーネントはスロット型。

以降、例として **`Rigidbody` コンポーネントに `Vector3 velocity`（数値）と `bool use_gravity`（bool）を公開する**ケースを使う。
（実型名は対象に読み替えること。スロット格納型・型付きラッパーありの想定。）

---

## 1. Rust 側レジストリへ分岐を追加する（必須）

**触るファイル**: `runtime/src/engine/core/scripting/host_api.rs`

このファイルの「コンポーネントレジストリ」コメント（`fn read_floats` の直前あたり、
「新しいコンポーネントをスクリプトへ公開するときは…」と書かれた箇所）に手順の要約がある。
文字列でコンポーネント名 → フィールド名を分岐する 4〜5 関数へ、コンポーネント名の分岐を **1 つずつ** 足す。

### 1-1. import を追加

ファイル冒頭の `use crate::engine::components::{ ... };` に対象コンポーネント型を足す。

```rust
use crate::engine::components::{
    AudioComponent, CameraComponent, CanvasTransform, RigidbodyComponent, SpriteComponent, Transform,
};
```

### 1-2. `read_floats` に分岐追加（`fn read_floats` 内の `match component` へ）

スロット格納型なので `locate::<T>` で解決する。`put(out, &[..])` で要素数を返すローカルヘルパを使う。

```rust
// ── 剛体（スロット格納型: locate で解決）──
"Rigidbody" => {
    let e = locate::<RigidbodyComponent>(world, entity)?;
    let r = world.get::<RigidbodyComponent>(e)?;
    match field {
        "velocity"    => put(out, &r.velocity),                              // Vec3 = 3 要素
        "use_gravity" => put(out, &[if r.use_gravity { 1.0 } else { 0.0 }]), // bool = 0/1
        _             => None,
    }
}
```

ルート直付け型なら `let r = world.get::<RigidbodyComponent>(entity)?;`（`locate` を挟まない）。Transform の分岐が手本。

### 1-3. `write_floats` に対称な分岐追加（`fn write_floats` 内の `match component` へ）

`take::<N>(v)` が要素数一致を検査して固定長配列にする（要素数が違えば失敗させ、C# 側の実装ミスを丸めず検出する）。

```rust
// ── 剛体（スロット格納型: locate で解決）──
"Rigidbody" => {
    let Some(e) = locate::<RigidbodyComponent>(world, entity) else { return false };
    let Some(r) = world.get_mut::<RigidbodyComponent>(e) else { return false };
    match field {
        "velocity"    => take(v).map(|a| r.velocity = a).is_some(),
        "use_gravity" => take::<1>(v).map(|a| r.use_gravity = a[0] != 0.0).is_some(),
        _             => false,
    }
}
```

読み取りだけ・書き込みだけにしたいフィールドは、片方の match に分岐を書かなければ良い（例: `WorldPosition` は read のみ）。

### 1-4. 文字列フィールドがある場合のみ `read_string` / `write_string` にも追加

数値/bool だけなら**このステップは不要**。文字列（パス等）があるときだけ、`Sprite` の `texture_path` の分岐に倣って追加する。

```rust
// read_string の match component へ
"Rigidbody" => {
    let e = locate::<RigidbodyComponent>(world, entity)?;
    let r = world.get::<RigidbodyComponent>(e)?;
    match field {
        "body_type" => Some(r.body_type.clone()),
        _           => None,
    }
}
// write_string の match component へ（get_mut → 代入 → true）
```

### 1-5. `has_component` に 1 行追加（**忘れやすい。必須**）

`fn has_component` の `match component` へ 1 行足す。これを忘れると `gameObject.HasComponent("Rigidbody")` が常に false を返し、
条件分岐しているスクリプトが動かない。スロット型は `locate`、ルート型は `world.get` を使う。

```rust
"Rigidbody" => locate::<RigidbodyComponent>(world, entity).is_some(),
```

> **FFI 構造体は触らない**: `ScriptHostApi` 構造体・`HOST_API` テーブル・`ffi_*` 関数は
> フィールド単位の追加では**変更不要**。これらを変えるのは新しい FFI 関数（Raycast のような新カテゴリ API）を足すときだけ。
> フィールド追加で構造体を触ろうとしていたら手が滑っている。

**検証**: `cd runtime && cargo build`（後述の手順 6 でまとめて実施）。

---

## 2. コンポーネント名文字列を C# 側と一致させる意識を持つ

Rust の `match component` のキー（`"Rigidbody"`）と、C# ラッパーの `private const string Comp`、
および汎用アクセスで渡す文字列は **完全一致必須**（大文字小文字含む）。不一致だと FFI は黙って失敗（既定値/無視）し、
コンパイルは通るのに実行時に何も起きない。最も多いバグなので、名前は 1 か所（このスキルのメモ）に決めてからコピペする。

---

## 3. C# 型付きラッパーを追加する（Q1 が「いいえ」のときのみ）

汎用アクセスで足りるならスキップして手順 5 へ。型付きプロパティ（`gameObject.Rigidbody.Velocity`）にしたい場合のみ実施。

### 3-1. ラッパー struct を新規作成

**触るファイル**: `scripting/src/Api/Rigidbody.cs`（新規）。`Sprite.cs` / `Transform.cs` が手本。
規約: `namespace SEED` / `public readonly struct` / `internal` コンストラクタ /
`private const string Comp`（**Rust 側キーと一致**）/ get/set は `ScriptHost.TryGet*/TrySet*` の薄い呼び出し /
失敗時は妥当な既定値を返す。

```csharp
namespace SEED;

/// <summary>
/// GameObject の剛体（RigidbodyComponent）へのアクセサ。
/// Rust ランタイムのコンポーネントを FFI 経由で読み書きする薄いラッパー（値はエンジンが保持）。
/// 剛体を持たないエンティティに対する読み取りは既定値、書き込みは無視される。
/// </summary>
public readonly struct Rigidbody
{
    /// <summary>この Rigidbody が属するエンティティ。</summary>
    private readonly Entity _entity;

    /// <summary>コンポーネント名（Rust 側レジストリのキーと一致必須）。</summary>
    private const string Comp = "Rigidbody";

    internal Rigidbody(Entity entity) { _entity = entity; }

    /// <summary>速度（ワールド空間・単位/秒）。</summary>
    public Vector3 Velocity
    {
        get => ScriptHost.TryGetVec3(_entity, Comp, "velocity", out var v) ? v : Vector3.Zero;
        set => ScriptHost.TrySetVec3(_entity, Comp, "velocity", value);
    }

    /// <summary>重力の影響を受けるか。</summary>
    public bool UseGravity
    {
        get => ScriptHost.TryGetBool(_entity, Comp, "use_gravity", out var b) && b;
        set => ScriptHost.TrySetBool(_entity, Comp, "use_gravity", value);
    }
}
```

`ScriptHost` の型付きヘルパ（`scripting/src/Api/ScriptHost.cs`）: `TryGetFloat/TrySetFloat`・`TryGetBool/TrySetBool`・
`TryGetVec2/TrySetVec2`・`TryGetVec3/TrySetVec3`・`TryGetColor/TrySetColor`・`TryGetString/TrySetString`。
これらは既にあるので新規追加不要（新しいデータ形状が必要なときだけ足す）。

### 3-2. `GameObject.cs` にアクセサプロパティを追加

**触るファイル**: `scripting/src/Api/GameObject.cs`。「コンポーネントアクセサ」領域へ、既存の `Sprite` 等に倣って 1 行。

```csharp
/// <summary>この GameObject の剛体。</summary>
public Rigidbody Rigidbody => new(_entity);
```

**検証**: `dotnet build scripting/SEEDScripting.csproj`（手順 6 でまとめて実施）。

---

## 4. `docs/scripting_api.md` の第 7 節へ追記する（必須・最重要）

**触るファイル**: `docs/scripting_api.md`。これを忘れると AI インライン補完がその API を一切知らない。

### 4-1. 抽出器の制約を必ず守る（`ScriptApiReference.cs` の `Compact()` が抽出する行だけが AI へ届く）

editor 側 `editor/src/Panels/ScriptEditor/InlineCompletion/ScriptApiReference.cs` の `Compact()` は、md から次の行**だけ**を残す：
- 見出し行（`#`〜`####`）
- ` ```csharp ` フェンス内の全行
- 表の行（`|` 始まり）
- 「重要」を含む `>` 引用行

したがって守るべき制約：
1. **API シグネチャは必ず ` ```csharp ` ブロック内に書く**。散文の本文に `sprite.Layer` 等と書いても AI には届かない。
2. **網羅的な一覧情報は表（`|` 区切り）にする**（第 7 節末尾の「利用可能なコンポーネント一覧」表）。
3. **利用者向け情報を「メンテナ向け」見出しより後に置かない**。`Compact()` は見出しに「メンテナ向け」を含む行**以降を全て捨てる**（第 8 節は丸ごと落ちる）。新しい利用者向け API は必ず第 8 節より前（第 7 節）に書く。

### 4-2. 第 7 節に H3 小節を追加

第 7 節「GameObject とコンポーネント」内、既存の `### Sprite（…）` などに倣って `### 名前（説明）` + `csharp` コードブロックを足す。

```markdown
### Rigidbody（剛体・物理速度）

​```csharp
var rb = gameObject.Rigidbody;
rb.Velocity                // Vector3（get/set。ワールド空間・単位/秒）
rb.UseGravity              // bool（get/set。重力の影響を受けるか）
​```
```

### 4-3. 第 7 節末尾「利用可能なコンポーネント一覧」表へ 1 行追加

```markdown
| `Rigidbody` | `gameObject.Rigidbody` | 速度・重力フラグ |
```

汎用アクセスのみ（C# ラッパーなし）の場合も、名前指定で使えることを利用者へ伝えるため
第 7 節へ「`Rigidbody` は `ScriptHost.TryGetVec3(e, "Rigidbody", "velocity", …)` で汎用アクセス可」等と `csharp` ブロックで明記する。

---

## 5. `docs/scripting_api.html` を手動同期する（必須）

**触るファイル**: `docs/scripting_api.html`（ブラウザ閲覧用。scripting_api.md 7 行目に「HTML 版も同時に更新」と明記）。
自動生成ではないので手作業。`<section id="sec-components">` 内の既存 `<h3 id="cmp-sprite">…` 群に倣って追加する。

```html
<h3 id="cmp-rigidbody">Rigidbody（剛体・物理速度）</h3>
<div class="api"><code class="sig">rb.Velocity</code><span class="desc">Vector3（get/set。ワールド空間・単位/秒）</span></div>
<div class="api"><code class="sig">rb.UseGravity</code><span class="desc">bool（get/set。重力の影響を受けるか）</span></div>
```

そのすぐ下の「利用可能なコンポーネント一覧」`<table>` にも 1 行足す：

```html
<tr><td><code>Rigidbody</code></td><td><code>gameObject.Rigidbody</code></td><td>速度・重力フラグ</td></tr>
```

対象 `<section>` の `data-keywords` に検索語（例 `Rigidbody 剛体 速度 重力 物理`）を足しておくと HTML 版の検索でヒットする。

---

## 6. 検証する

自動テストは無い。両言語のビルドが通ることを確認する（Windows・PowerShell/bash どちらでも可）。

```bash
cd runtime && cargo build
```
```bash
dotnet build scripting/SEEDScripting.csproj
```

- Rust⇔C# の FFI 構造体（`ScriptHostApi`）はフィールド追加では不変なので、片側だけの再ビルドで足りるが、両方通すのが安全。
- エディタ実行中でも `dotnet build` は可能（シャドウコピー方式で再ビルドできる）。
- **手動確認の指針**（可能なら）: 実機で確認する場合、テスト用スクリプト（例 `runtime/assets/scripts/Test.cs`）で
  `gameObject.HasComponent("Rigidbody")` と get/set 往復を `SEED.Debug.Log` で出力し、Play モードで期待値が出るか見る。
  set した値が反映されない → コンポーネント名不一致 or `has_component` 漏れ or スロット型を `locate` せず `world.get` した、を疑う。

---

## 7. よくある失敗（着手前にチェックリストとして読む）

- **コンポーネント名文字列の不一致**: Rust の `match` キーと C# の `Comp` 定数が 1 文字でも違うと、コンパイルは通るのに実行時に無反応。名前を 1 か所に決めて全箇所へコピペする。
- **`has_component` への追加漏れ**: read/write だけ足して `has_component` を忘れると `HasComponent(...)` が常に false。
- **スロット型を `locate` せず `world.get` した**: 追加コンポーネント（Sprite/Camera/Audio/新規物理系）はスロット格納型。ルートエンティティに直接は無いので `locate::<T>` 必須。直付けは Transform / CanvasTransform だけ。
- **md の csharp ブロック外へシグネチャを書いた**: 散文に書いた API は `Compact()` に拾われず AI 補完へ届かない。必ず ` ```csharp ` ブロック・見出し・`|`表・`>重要`引用のいずれかに入れる。
- **「メンテナ向け」見出し以降へ利用者向け情報を書いた**: 第 8 節以降は `Compact()` が丸ごと破棄する。利用者向けは必ず第 7 節（第 8 節より前）に。
- **html の同期漏れ**: md だけ更新して `docs/scripting_api.html` を忘れる。API ガイドボタンから開く HTML が古くなる。
- **FFI 構造体を不要に触った**: フィールド追加で `ScriptHostApi` / `HOST_API` / `ffi_*` を変更する必要はない。触っていたら設計を見直す。
- **要素数ミスマッチ**: Vec3 フィールドに `take::<1>` を使う等。Rust 側 `take::<N>` の N と `put(out, &[..])` の要素数、C# 側 `TryGetVec3`(=3要素) 等を一致させる。

---

## 完了時に確認する成果物

1. `runtime/src/engine/core/scripting/host_api.rs` — import + `read_floats`/`write_floats`(必要なら `read_string`/`write_string`) + `has_component` の分岐追加
2. （型付きの場合）`scripting/src/Api/<Name>.cs` 新規 + `scripting/src/Api/GameObject.cs` のアクセサ追加
3. `docs/scripting_api.md` 第 7 節の H3 小節 + 一覧表の行
4. `docs/scripting_api.html` の `<h3>`/`<div class="api">` + 一覧表 `<tr>` + `data-keywords`
5. `cargo build` と `dotnet build scripting/SEEDScripting.csproj` が両方成功

`.claude/CLAUDE.md` の 3 ステップ運用ルール（レジストリ登録 → C# ラッパー任意 → docs 追記）と整合していること。
