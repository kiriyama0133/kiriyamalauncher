# 编译诊断 Hook：diag.cpp -> build\hookdll.dll（x64，/MT 静态 CRT，不依赖 VC 运行库）
#
# 用法：在 PowerShell 里直接运行  .\build-diag.ps1

$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$outputDirectory = Join-Path $root 'build'

if (-not (Test-Path $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}

$vsRoots = @(
    "${env:ProgramFiles}\Microsoft Visual Studio\18\Community",
    "${env:ProgramFiles}\Microsoft Visual Studio\18\Professional",
    "${env:ProgramFiles}\Microsoft Visual Studio\18\Enterprise",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Community",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Professional",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\Enterprise",
    "${env:ProgramFiles}\Microsoft Visual Studio\2022\BuildTools"
)

$vsDevCmd = $null

foreach ($vsRoot in $vsRoots) {
    $candidate = Join-Path $vsRoot 'Common7\Tools\VsDevCmd.bat'

    if (Test-Path $candidate) {
        $vsDevCmd = $candidate
        break
    }
}

if (-not $vsDevCmd) {
    Write-Host "找不到 VsDevCmd.bat（没找到 Visual Studio 的 C++ 工具链）。" -ForegroundColor Red
    return
}

Write-Host "使用编译器环境：$vsDevCmd"

$compileLine = 'call "{0}" -arch=amd64 -no_logo && cd /d "{1}" && cl /nologo /utf-8 /LD /O2 /EHsc /W3 /MT /D_CRT_SECURE_NO_WARNINGS diag.cpp /Fo:"{2}\\" /Fe:"{2}\hookdll.dll"' -f `
    $vsDevCmd, $root, $outputDirectory

$dll = Join-Path $outputDirectory 'hookdll.dll'

# 先把旧产物删掉：这样「编译失败但旧文件还在」不会被误判成成功。
if (Test-Path $dll) {
    try {
        Remove-Item -LiteralPath $dll -Force -ErrorAction Stop
    }
    catch {
        Write-Host "删不掉旧产物（$($_.Exception.Message)）—— 可能正被别的进程占用，继续尝试编译。" -ForegroundColor Yellow
    }
}

cmd /c $compileLine

$exitCode = $LASTEXITCODE

if ((Test-Path $dll) -and $exitCode -eq 0) {
    $info = Get-Item $dll
    Write-Host ("编译成功：{0}（{1:N1} KB，{2}）" -f $info.FullName, ($info.Length / 1KB), $info.LastWriteTime) -ForegroundColor Green
    Write-Host "接下来运行 .\install-diag.ps1 把它装进 kit 目录。" -ForegroundColor Cyan
}
else {
    Write-Host "编译失败（cl.exe 退出码 $exitCode）：没有生成 hookdll.dll。" -ForegroundColor Red
}
