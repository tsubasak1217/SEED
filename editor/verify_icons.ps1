# Icons.xaml が WPF から実際に読めることを検証する（開発時のみ使用）。
#
# XAML リソース辞書の Geometry 文字列は「読み込み時」に型コンバータで解釈されるため、
# dotnet build が通っても実行時に落ちうる。ここで XamlReader に丸ごと食わせて
# 全キーが Geometry として解決できることを起動前に確かめる。
#
# 使い方:  pwsh -File editor/verify_icons.ps1

Add-Type -AssemblyName PresentationCore, PresentationFramework, WindowsBase

$xamlPath = Join-Path $PSScriptRoot 'resources/icons/Icons.xaml'
$stream = [System.IO.File]::OpenRead($xamlPath)
try {
    $dict = [System.Windows.Markup.XamlReader]::Load($stream)
} finally {
    $stream.Dispose()
}

$geometryCount = 0
$brushCount = 0
foreach ($key in $dict.Keys) {
    $value = $dict[$key]
    if ($value -is [System.Windows.Media.Geometry]) {
        # 境界が空＝パスが解釈されていない、を弾く
        if ($value.Bounds.IsEmpty) { throw "空のジオメトリ: $key" }
        $geometryCount++
    } elseif ($value -is [System.Windows.Media.Brush]) {
        $brushCount++
    } else {
        throw "想定外の型: $key -> $($value.GetType().FullName)"
    }
}

Write-Output "Icons.xaml 読み込み成功: Geometry $geometryCount 件 / Brush $brushCount 件"
