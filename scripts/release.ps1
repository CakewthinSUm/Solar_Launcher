# 发布便携版构建：自包含 Windows App SDK、按架构产出可解压即用的目录，并打成 zip。
# 单文件模式额外产出一个自包含 exe（首次运行解压到 %TEMP%）。
# 用法:
#   pwsh -File scripts\release.ps1
#   pwsh -File scripts\release.ps1 -Runtime win-arm64
#   pwsh -File scripts\release.ps1 -Version 0.1.1-beta -NoZip
#   pwsh -File scripts\release.ps1 -SingleFile -Runtime win-x64,win-arm64
param(
    [string]$Version = "0.1.1-beta",
    [string[]]$Runtime = @("win-x64"),
    [string]$Configuration = "Release",
    [switch]$NoZip,
    [switch]$SingleFile
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$outRoot = Join-Path $root "publish"

# MSBuild/NuGet 只接受数字开头的版本号；dev 这类标签只用于产物命名
$publishVersion = if ($Version -match '^\d+(\.\d+){1,3}') { $Version } else { "0.0.0-$Version" }

foreach ($rid in $Runtime) {
    if ($SingleFile) {
        $assetName = "SolarLauncher-$Version-$rid-singlefile"
        $out = Join-Path $outRoot "$Version-$rid-singlefile"
        Write-Host "==> 发布单文件 $rid -> $out"

        if (Test-Path $out) { Remove-Item $out -Recurse -Force }

        # 程序集名与资产名一致，WinUI 才能按 exe 文件名找到同名 .pri
        dotnet publish (Join-Path $root "src\Solar.App\Solar.App.csproj") `
            -c $Configuration `
            -r $rid `
            --self-contained true `
            -p:WindowsAppSDKSelfContained=true `
            -p:EnableMsixTooling=true `
            -p:IncludeAllContentForSelfExtract=true `
            -p:PublishSingleFile=true `
            -p:EnableCompressionInSingleFile=true `
            -p:DisableTransitiveFrameworkReferences=true `
            -p:SingleFileAssemblyName=$assetName `
            -p:Version=$publishVersion `
            -p:InformationalVersion=$Version `
            -o $out `
            --nologo
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败 ($rid)" }

        # 便携版会把运行数据写在 exe 旁边，发布包里不该带上本地开发产生的数据
        foreach ($junk in 'SolarData', '.minecraft') {
            $path = Join-Path $out $junk
            if (Test-Path $path) { Remove-Item $path -Recurse -Force }
        }

        Get-ChildItem $out -Filter *.pdb -Recurse | Remove-Item -Force -ErrorAction SilentlyContinue

        $exe = Join-Path $out "$assetName.exe"
        if (-not (Test-Path $exe)) { throw "单文件 exe 未生成: $exe" }
        $final = Join-Path $outRoot "$assetName.exe"
        Copy-Item $exe $final -Force
        $sizeMb = [math]::Round((Get-Item $final).Length / 1MB, 1)
        Write-Host "    $final ($sizeMb MB)"
        continue
    }

    $out = Join-Path $outRoot "$Version-$rid"
    Write-Host "==> 发布 $rid -> $out"

    if (Test-Path $out) { Remove-Item $out -Recurse -Force }

    # 只发布 UI 项目；它会带上 Solar.Core 与 Windows App SDK 的自包含运行时
    dotnet publish (Join-Path $root "src\Solar.App\Solar.App.csproj") `
        -c $Configuration `
        -r $rid `
        --self-contained false `
        -p:Version=$publishVersion `
        -p:InformationalVersion=$Version `
        -o $out `
        --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败 ($rid)" }

    # 便携版会把运行数据写在 exe 旁边，发布包里不该带上本地开发产生的数据
    foreach ($junk in 'SolarData', '.minecraft') {
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
