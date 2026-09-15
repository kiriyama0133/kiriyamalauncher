# 清理 ZeroTier 自建宇宙污染，还原官方控制器（my.zerotier.com）模式
# 用途：本地 planet 被换成了不明来源的自建 planet（根 110.42.10.83），
#       导致节点走错宇宙、官方控制台看不到成员请求、配置永远推不下来。
# 原理：ZeroTier 服务在 planet 文件缺失时，会自动从官方根重新下载官方 planet。
# 保留：identity.secret / identity.public（节点身份 08bd1dea74 不变，官方授权继续有效）
# 清理：自建 planet、废 moon（endpoint 127.0.0.1）、自建控制器残留 controller.d、
#       networks.d / peers.d 缓存（这些会按需重建）

$ErrorActionPreference = 'Stop'
$home = 'C:\ProgramData\ZeroTier\One'
$backupDir = Join-Path $home ('selfhosted-backup-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))

Write-Host "ZeroTier 数据目录: $home" -ForegroundColor Cyan

if (-not (Test-Path $home)) {
    Write-Host "数据目录不存在，无需清理。" -ForegroundColor Yellow
    exit 0
}

# 0) 停止服务（若在运行），避免文件被锁
Write-Host "`n[1/5] 停止 ZeroTier 服务……" -ForegroundColor Cyan
try {
    $svc = Get-Service -Name 'ZeroTierOneService' -ErrorAction SilentlyContinue
    if ($svc) {
        if ($svc.Status -ne 'Stopped') {
            Stop-Service -Name 'ZeroTierOneService' -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 2
        }
    }
} catch {
    Write-Host "  停止服务时忽略错误（可能已在运行之外的状态）：$($_.Exception.Message)" -ForegroundColor DarkYellow
}

# 也清理可能的野生前台进程
Get-Process -Name 'zerotier-one_x64' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# 1) 备份要动的文件
Write-Host "`n[2/5] 备份现有文件到 $backupDir ……" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
$targets = @('planet', 'local.conf', 'moons.d', 'controller.d', 'networks.d', 'peers.d', 'identity.secret', 'identity.public')
foreach ($t in $targets) {
    $src = Join-Path $home $t
    if (Test-Path $src) {
        Copy-Item -Path $src -Destination $backupDir -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "  已备份: $t"
    }
}

# 2) 删除自建宇宙污染
Write-Host "`n[3/5] 删除自建宇宙污染文件……" -ForegroundColor Cyan
$toDelete = @(
    'planet',                        # 自建 planet（根 110.42.10.83）→ 删掉后服务自动重下官方 planet
    'moons.d\0000001947244ee4.moon' # 废 moon（endpoint 127.0.0.1）
)
foreach ($t in $toDelete) {
    $p = Join-Path $home $t
    if (Test-Path $p) {
        Remove-Item -Path $p -Force -ErrorAction SilentlyContinue
        Write-Host "  已删除: $t"
    }
}

# 自建控制器残留（普通节点不需要）
$controllerDir = Join-Path $home 'controller.d'
if (Test-Path $controllerDir) {
    Remove-Item -Path $controllerDir -Recurse -Force -ErrorAction SilentlyContinue
    Write-Host "  已删除: controller.d（自建控制器残留）"
}

# 3) 清理网络/对等缓存（会按需重建，避免残留自建宇宙的成员资格）
Write-Host "`n[4/5] 清理网络与对等缓存……" -ForegroundColor Cyan
foreach ($d in @('networks.d', 'peers.d')) {
    $p = Join-Path $home $d
    if (Test-Path $p) {
        Get-ChildItem -Path $p -File -ErrorAction SilentlyContinue | Remove-Item -Force -ErrorAction SilentlyContinue
        Write-Host "  已清空: $d"
    }
}

# 4) 确认结果
Write-Host "`n[5/5] 清理结果确认……" -ForegroundColor Cyan
$planetPath = Join-Path $home 'planet'
if (Test-Path $planetPath) {
    Write-Host "  [警告] planet 文件仍存在，请检查是否删除失败" -ForegroundColor Red
} else {
    Write-Host "  [OK] planet 已删除，服务重启时会自动从官方根重新下载官方 planet" -ForegroundColor Green
}

$identityPath = Join-Path $home 'identity.secret'
if (Test-Path $identityPath) {
    Write-Host "  [OK] identity.secret 保留（节点身份 08bd1dea74 不变）" -ForegroundColor Green
} else {
    Write-Host "  [警告] identity.secret 缺失，服务会生成新身份（需重新在官方授权）" -ForegroundColor Yellow
}

Write-Host "`n完成。备份目录: $backupDir" -ForegroundColor Cyan
Write-Host "下一步：重新运行启动器连接即可（服务启动会自动重建官方 planet；" -ForegroundColor Cyan
Write-Host "        若服务已在运行，重启 ZeroTierOneService 服务，或重启电脑）。" -ForegroundColor Cyan
