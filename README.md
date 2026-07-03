# SEED

[![CI Debug](https://github.com/tsubasak1217/SEED/actions/workflows/ci.yml/badge.svg)](https://github.com/tsubasak1217/SEED/actions/workflows/ci.yml)
[![CI Release](https://github.com/tsubasak1217/SEED/actions/workflows/ci-release.yml/badge.svg)](https://github.com/tsubasak1217/SEED/actions/workflows/ci-release.yml)

Rust製ゲームエンジンRuntimeと、.NET 9.0 (WPF) 製エディタのハイブリッドプロジェクト。

## プロジェクト構成

- **runtime/**: Rustによるコアエンジン部分（wgpu 描画 / 自作 ECS / rapier 物理 / CLR ホスティング）。
- **editor/**: C# (WPF) によるエディタ部分（内蔵スクリプトエディタ・AIアシスタントを含む）。
- **scripting/**: C# スクリプトAPI（`SEED.Scripting.ScriptComponent` 基底クラスと CLR ブリッジ）。
- **plugin_api/**: プラグイン作成者向け API クレート（trait 定義のみ）。
- **plugins/**: 各プラグイン DLL クレート。

## ビルド・実行方法

### Rust Runtime
```bash
cd runtime
cargo build
```

### Editor
Visual Studio 2022 または `dotnet build` を使用してください。
```bash
dotnet build editor/SEEDEditor.sln
```

### Scripting（C# スクリプト API）
```bash
dotnet build scripting/SEEDScripting.csproj
```
Runtime 起動時に `SEEDScripting.dll` が存在すると CLR がロードされ、
C# スクリプトが実行可能になります。

## C# スクリプティング

アセットフォルダ内の `.cs` ファイルは Runtime 起動時（および保存時のホットリロードで）
自動的にコンパイル・ロードされます。

- スクリプトは `SEED.Scripting.ScriptComponent` を継承し、
  `Update` などのライフサイクルメソッドを override する
- `[SerializeField]` を付けたフィールドはエディタのインスペクタに表示され、
  値はシーンファイルに保存される
- エディタのプロジェクトパネルで `.cs` をダブルクリックすると
  内蔵スクリプトエディタ（Script タブ）で編集でき、Ctrl+S 保存で
  コンパイルチェックと Play 中ランタイムへのホットリロードが行われる

```csharp
using SEED.Scripting;

public class MyScript : ScriptComponent
{
    [SerializeField(Label = "速度")]
    private float speed = 1.0f;

    public override void Update(ref NativeFrameContext ctx)
    {
        // ctx.DeltaTime : 前フレームからの経過秒
    }
}
```
