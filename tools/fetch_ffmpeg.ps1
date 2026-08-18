<#
.SYNOPSIS
    下载 FFmpeg 6.x（shared / win64）原生库到 csharp\native\ffmpeg\x64\。

.DESCRIPTION
    架构文档 §6.1 版本配对铁律：
        FFmpeg.AutoGen 6.0.0.2  <->  FFmpeg 6.x shared
        avutil-58.dll / avcodec-60.dll / swscale-7.dll / swresample-4.dll

    错配的表现是运行时静默崩溃或函数签名错位，务必不要擅自换成 7.x。

    脚本只提取 bin\ 下的 DLL，不需要 ffmpeg.exe / ffprobe.exe。
    这些 DLL 会由 MultiScrcpyPanel.csproj 的 <None Include="..\native\ffmpeg\x64\*.dll">
    自动复制到输出目录的 ffmpeg\x64\。

.PARAMETER Url
    自定义下载地址（zip）。默认使用 GyanD 的 6.1.1 shared 构建。

.PARAMETER Force
    即使目标目录已有完整 DLL 也强制重新下载。

.EXAMPLE
    pwsh tools\fetch_ffmpeg.ps1

.EXAMPLE
    pwsh tools\fetch_ffmpeg.ps1 -Url "https://example.com/ffmpeg-6.1-full_build-shared.zip" -Force
#>

[CmdletBinding()]
param(
    [string] $Url = 'https://github.com/GyanD/codexffmpeg/releases/download/6.1.1/ffmpeg-6.1.1-full_build-shared.zip',
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- 路径

$ScriptDir  = Split-Path -Parent $MyInvocation.MyCommand.Path
$CsharpRoot = Split-Path -Parent $ScriptDir
$TargetDir  = Join-Path $CsharpRoot 'native\ffmpeg\x64'

# FFmpeg 6.x shared 必备 DLL（版本号后缀必须完全一致）
$RequiredDlls = @(
    'avutil-58.dll',
    'avcodec-60.dll',
    'swscale-7.dll',
    'swresample-4.dll'
)

# 一并复制的依赖（部分构建中 avcodec 会间接依赖）
$OptionalDlls = @(
    'avformat-60.dll',
    'avfilter-9.dll',
    'avdevice-60.dll',
    'postproc-57.dll'
)

function Write-Step([string] $Message) {
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Ok([string] $Message) {
    Write-Host "    $Message" -ForegroundColor Green
}

function Write-Warn2([string] $Message) {
    Write-Host "    $Message" -ForegroundColor Yellow
}

function Test-AllRequiredPresent {
    param([string] $Dir)

    if (-not (Test-Path -LiteralPath $Dir)) { return $false }
    foreach ($dll in $RequiredDlls) {
        if (-not (Test-Path -LiteralPath (Join-Path $Dir $dll))) { return $false }
    }
    return $true
}

# ---------------------------------------------------------------- 主流程

Write-Step "目标目录：$TargetDir"

if ((Test-AllRequiredPresent -Dir $TargetDir) -and (-not $Force)) {
    Write-Ok 'FFmpeg 6.x 原生库已存在，跳过下载（加 -Force 可强制重下）。'
    Get-ChildItem -LiteralPath $TargetDir -Filter '*.dll' |
        ForEach-Object { Write-Ok ("  {0}  ({1:N1} MB)" -f $_.Name, ($_.Length / 1MB)) }
    exit 0
}

New-Item -ItemType Directory -Path $TargetDir -Force | Out-Null

$TempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("mscp_ffmpeg_" + [System.Guid]::NewGuid().ToString('N'))
$ZipPath  = Join-Path $TempRoot 'ffmpeg.zip'
$Extract  = Join-Path $TempRoot 'extract'

New-Item -ItemType Directory -Path $TempRoot -Force | Out-Null
New-Item -ItemType Directory -Path $Extract  -Force | Out-Null

try {
    Write-Step "下载：$Url"
    Write-Warn2 '压缩包约 80-100 MB，请耐心等待……'

    # 关闭进度条可显著提升 Invoke-WebRequest 的下载速度
    $oldProgress = $ProgressPreference
    $ProgressPreference = 'SilentlyContinue'
    try {
        [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $Url -OutFile $ZipPath -UseBasicParsing -MaximumRedirection 10
    }
    finally {
        $ProgressPreference = $oldProgress
    }

    $zipSize = (Get-Item -LiteralPath $ZipPath).Length
    if ($zipSize -lt 1MB) {
        throw "下载内容异常（仅 $zipSize 字节），可能是重定向页面而非 zip。请检查 -Url 参数。"
    }
    Write-Ok ("下载完成：{0:N1} MB" -f ($zipSize / 1MB))

    Write-Step '解压……'
    Expand-Archive -LiteralPath $ZipPath -DestinationPath $Extract -Force

    Write-Step '提取 DLL……'
    $wanted = $RequiredDlls + $OptionalDlls
    $copied = 0

    foreach ($name in $wanted) {
        $found = Get-ChildItem -LiteralPath $Extract -Filter $name -Recurse -File -ErrorAction SilentlyContinue |
                 Select-Object -First 1
        if ($null -eq $found) {
            if ($RequiredDlls -contains $name) {
                throw "压缩包中未找到必备文件 $name。请确认下载的是 FFmpeg 6.x 的 **shared** 构建（非 static/essentials-static）。"
            }
            continue
        }

        Copy-Item -LiteralPath $found.FullName -Destination (Join-Path $TargetDir $name) -Force
        Write-Ok ("{0}  ({1:N1} MB)" -f $name, ($found.Length / 1MB))
        $copied++
    }

    if (-not (Test-AllRequiredPresent -Dir $TargetDir)) {
        throw '提取后仍缺少必备 DLL，请检查压缩包内容。'
    }

    Write-Host ''
    Write-Ok "完成：已放置 $copied 个 DLL 到 $TargetDir"
    Write-Host ''
    Write-Host '下一步：' -ForegroundColor Cyan
    Write-Host '  dotnet build MultiScrcpyPanel.sln -c Debug'
    Write-Host '  （DLL 会自动复制到输出目录的 ffmpeg\x64\）'
}
catch {
    Write-Host ''
    Write-Host "下载失败：$($_.Exception.Message)" -ForegroundColor Red
    Write-Host ''
    Write-Host '手动方案：' -ForegroundColor Yellow
    Write-Host '  1. 访问 https://github.com/GyanD/codexffmpeg/releases 或 https://www.gyan.dev/ffmpeg/builds/'
    Write-Host '  2. 下载 6.x 的 **full_build-shared**（win64，必须是 shared，不是 static）'
    Write-Host "  3. 把 bin\ 下的 $($RequiredDlls -join '、') 复制到："
    Write-Host "     $TargetDir"
    exit 1
}
finally {
    if (Test-Path -LiteralPath $TempRoot) {
        Remove-Item -LiteralPath $TempRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
