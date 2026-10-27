# 发布便携版构建：自包含 Windows App SDK、按架构产出可解压即用的目录，并打成 zip。
# 用法:
#   pwsh -File scripts\release.ps1
#   pwsh -File scripts\release.ps1 -Runtime win-arm64
#   pwsh -File scripts\release.ps1 -Version 0.1.0-beta -NoZip
param(
    [string]$Version = "0.1.0-beta",
    [string[]]$Runtime = @("win-x64"),
    [string]$Configuration = "Release",
    [switch]$NoZip
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$outRoot = Join-Path $root "publish"

foreach ($rid in $Runtime) {
    $out = Join-Path $outRoot "$Version-$rid"
    Write-Host "==> 发布 $rid -> $out"

    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    # 只发布 UI 项目；它会带上 Solar.Core 与 Windows App SDK 的自包含运行时
    dotnet publish (Join-Path $root "src\Solar.App\Solar.App.csproj") `
        -c $Configuration `
        -r $rid `
        --self-contained false `
        -p:Version=$Version `
        -p:InformationalVersion=$Version `
        -o $out `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败 ($rid)" }

    # 便携版会把运行数据写在 exe 旁边，发布包里不该带上本地开发产生的数据
    foreach ($junk in 'SolarData', 'SolarShared', 'SolarInstances', '.minecraft') {
        $path = Join-Path $out $junk
        if (Test-Path $path) { Remove-Item $path -Recurse -Force }
    }

    # 保留 pdb 之外的调试文件没必要，体积能小一半
    Get-ChildItem $out -Filter *.pdb -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue

    if (-not $NoZip) {
        $zip = Join-Path $outRoot "SolarLauncher-$Version-$rid.zip"
        if (Test-Path $zip) { Remove-Item $zip -Force }
        Write-Host "==> 打包 $zip"
        Compress-Archive -Path (Join-Path $out '*') -DestinationPath $zip -CompressionLevel Optimal
        $sizeMb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
        Write-Host "    $zip ($sizeMb MB)"
    }
}

Write-Host "完成。"
