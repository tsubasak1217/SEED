---
paths:
  - "scripting/src/Api/**"
  - "runtime/src/engine/core/scripting/**"
---

# スクリプトAPI領域の危険地帯ルール

スクリプトAPI（Rust ECS レジストリ ⇔ FFI ⇔ C# ラッパー ⇔ docs）を触るときは以下を厳守する。

- **`docs/scripting_api.md` が正典**であり、editor 側 `ScriptApiReference` が読み込んで AI インライン補完へ注入する情報源。
  ここに書かれていない API は AI 補完が知らない。API を追加・変更したら**必ず** `docs/scripting_api.md` と
  `docs/scripting_api.html` を同期する（html は自動生成ではないので手作業）。手順そのものが変わったら
  `.claude/skills/add-script-api` も更新する。
- **Rust ⇔ C# の FFI 構造体は完全一致必須**。`runtime/src/engine/core/scripting/host_api.rs` の `ScriptHostApi` と
  C# 側 `ScriptHost.cs` は、フィールド順・関数シグネチャが 1 つでもずれると実行時に黙って壊れる。
  ただしフィールド単位の追加では FFI 構造体自体は触らない（新カテゴリ API を足すときだけ変更する）。
- **コンポーネント名文字列の一致**。Rust 側レジストリの `match component` のキーと、C# 側 `const string Comp`、
  および汎用アクセスに渡す文字列は大文字小文字まで完全一致必須。不一致だとコンパイルは通るのに実行時に無反応。
- **API 変更後は `SEEDScripting.dll` の再ビルドが必要**。ソースを直しても `dotnet build scripting/SEEDScripting.csproj`
  を実行しないと、runtime が古い DLL をロードして「直したのに動かない」罠にはまる。
- 詳細な追加・変更手順（レジストリ登録／ラッパー／docs 同期／両ビルド検証）は **add-script-api Skill** を使う。
