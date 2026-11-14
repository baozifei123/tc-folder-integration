[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][ValidateSet('Install','Restore')][string]$Action,
    [switch]$Factory,
    [string]$OriginalSid,
    [switch]$ElevatedChild
)

$ErrorActionPreference = 'Stop'
if ($Factory) {
    [Console]::Error.WriteLine('已禁用 -Factory：旧版没有足够信息来安全恢复 Windows 出厂设置。未更改任何配置。')
    exit 2
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$sid = $identity.User.Value
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    try {
        $powershell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $argumentLine = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Action {1} -OriginalSid "{2}" -ElevatedChild' -f $PSCommandPath, $Action, $sid
        $child = Start-Process -FilePath $powershell -Verb RunAs -ArgumentList $argumentLine -Wait -PassThru
        exit $child.ExitCode
    }
    catch {
        [Console]::Error.WriteLine('未获得管理员权限或无法启动：' + $_.Exception.Message)
        exit 1
    }
}
if ($OriginalSid -and $OriginalSid -ne $sid) {
    [Console]::Error.WriteLine('提权后用户账户发生变化；为避免修改其他账户的设置，已停止。')
    if ($ElevatedChild) { $null = Read-Host '按回车关闭此窗口' }
    exit 1
}

$exitCode = 0
$machineClasses = $null
$userClasses = $null
$machineBase = $null
$userBase = $null
$mutex = $null
$haveMutex = $false
$backupCreated = $null
try {
    . (Join-Path $PSScriptRoot 'TC-Core.ps1')
    $mutex = New-Object System.Threading.Mutex($false, ('Local\TCFolderIntegration-' + $sid))
    $haveMutex = $mutex.WaitOne(0)
    if (-not $haveMutex) { throw '另一个安装或还原窗口正在运行。' }

    $tc = Join-Path $env:LOCALAPPDATA 'TotalCMD64\TotalCMD64.exe'
    $evExe = Join-Path $env:ProgramFiles 'Everything\Everything.exe'
    $evIni = Join-Path $env:APPDATA 'Everything\Everything.ini'
    $backupRoot = Join-Path $PSScriptRoot 'backup'
    $pointer = Join-Path $backupRoot 'active-v2.txt'

    # Everything rewrites its INI on exit. Never force-kill it or edit a live INI.
    $running = @(Get-Process -Name Everything -ErrorAction SilentlyContinue | Where-Object {
        try { [string]::Equals($_.Path, $evExe, [StringComparison]::OrdinalIgnoreCase) } catch { $false }
    })
    if ($running.Count -gt 0) {
        throw 'Everything 正在运行。请先在系统托盘右键 Everything 图标并选择“退出”，再重新双击 CMD；本次没有改动配置。'
    }

    $machineBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::LocalMachine, [Microsoft.Win32.RegistryView]::Registry64)
    $userBase = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser, [Microsoft.Win32.RegistryView]::Registry64)
    $machineClasses = $machineBase.OpenSubKey('Software\Classes', $true)
    $userClasses = $userBase.OpenSubKey('Software\Classes', $false)
    if ($null -eq $machineClasses -or $null -eq $userClasses) { throw '无法打开注册表 Classes 分支。' }

    if ($Action -eq 'Install') {
        if (-not (Test-Path -LiteralPath $tc -PathType Leaf)) { throw ('未找到 Total Commander：' + $tc) }
        if (Test-Path -LiteralPath $pointer) {
            throw '已有一次新版安装及其备份。请先使用“还原.cmd”，不要覆盖原备份。'
        }

        $open = '"' + $tc + '" /O /T /L="%1"'
        $registry = New-Object System.Collections.Generic.List[object]
        foreach ($path in @('Directory\shell\open\command', 'Drive\shell\open\command')) {
            $registry.Add((New-TCRegistryChange $machineClasses $path '' $true 'String' $open))
            $registry.Add((New-TCRegistryChange $machineClasses $path 'DelegateExecute' $false '' ''))
        }
        # Do not overwrite a menu item which may belong to another program.
        $fallbackPath = 'Directory\shell\OpenWithExplorer'
        $existingMachineFallback = $machineClasses.OpenSubKey($fallbackPath, $false)
        $existingMachineCommand = $machineClasses.OpenSubKey(($fallbackPath + '\command'), $false)
        $existingUserFallback = $userClasses.OpenSubKey($fallbackPath, $false)
        try {
            $available = $null -eq $existingUserFallback
            if ($null -ne $existingMachineFallback -and
                (@($existingMachineFallback.GetValueNames()) -contains '' -or @($existingMachineFallback.GetValueNames()) -contains 'Icon')) { $available = $false }
            if ($null -ne $existingMachineCommand -and @($existingMachineCommand.GetValueNames()) -contains '') { $available = $false }
            if ($available) {
                $registry.Add((New-TCRegistryChange $machineClasses $fallbackPath '' $true 'String' '用资源管理器打开'))
                $registry.Add((New-TCRegistryChange $machineClasses $fallbackPath 'Icon' $true 'ExpandString' '%SystemRoot%\explorer.exe'))
                $registry.Add((New-TCRegistryChange $machineClasses ($fallbackPath + '\command') '' $true 'ExpandString' '"%SystemRoot%\Explorer.exe" "%1"'))
            }
            else { Write-Host '已存在同名右键菜单，保留原样。' }
        }
        finally {
            if ($null -ne $existingMachineFallback) { $existingMachineFallback.Dispose() }
            if ($null -ne $existingMachineCommand) { $existingMachineCommand.Dispose() }
            if ($null -ne $existingUserFallback) { $existingUserFallback.Dispose() }
        }
        $ini = New-Object System.Collections.Generic.List[object]
        if (Test-Path -LiteralPath $evIni -PathType Leaf) {
            $iniText = (Read-TCIni $evIni).Text
            $ini.Add((New-TCIniChange $iniText 'open_folder_command2' ('$exec("' + $tc + '" /O /T /L="%1")')))
            $ini.Add((New-TCIniChange $iniText 'open_path_command2' ('$exec("' + $tc + '" /O /A /S "%1")')))
        }
        else { Write-Warning '未找到 Everything.ini；仅配置 Windows 文件夹处理程序。' }
        $plan = [pscustomobject]@{ Version=2; Source='V2'; Sid=$sid; IniPath=$evIni; Registry=@($registry.ToArray()); Ini=@($ini.ToArray()) }
        Assert-TCPlan $plan $evIni
        Assert-TCNoUserOverrides $userClasses $plan
        $hasChanges = $false
        foreach ($entry in @($plan.Registry) + @($plan.Ini)) {
            if (-not (Test-TCValueEqual $entry.Before $entry.After)) { $hasChanges = $true }
        }
        if (-not $hasChanges) { Write-Host '当前已是目标设置，本次没有更改配置或覆盖备份。'; return }

        $name = (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
        $backupCreated = Join-Path $backupRoot $name
        New-Item -ItemType Directory -Path $backupCreated -Force | Out-Null
        if (@($plan.Ini).Count -gt 0) { Copy-Item -LiteralPath $evIni -Destination (Join-Path $backupCreated 'Everything.before.ini') }
        $plan | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $backupCreated 'state.json') -Encoding UTF8
        # Save the recovery pointer before the first registry or INI change.
        $bytes = [System.Text.Encoding]::ASCII.GetBytes($name)
        $stream = [System.IO.File]::Open($pointer, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::Write, [System.IO.FileShare]::None)
        try { $stream.Write($bytes, 0, $bytes.Length) } finally { $stream.Dispose() }
        Invoke-TCPlan $machineClasses $plan 'Install'
        Write-Host ('安装完成。备份：' + $backupCreated)
        Write-Host '请重新启动 Everything，然后验证“打开文件夹”和“打开路径”。'
    }
    else {
        if (-not (Test-Path -LiteralPath $pointer -PathType Leaf)) {
            throw '未找到安装备份指针 active-v2.txt；无法确定要还原哪次安装。请保留 backup 目录并检查。'
        }
        $name = [System.IO.File]::ReadAllText($pointer).Trim()
        if ($name -notmatch '^\d{8}-\d{6}-[a-f0-9]{8}$') { throw '备份指针格式无效；未更改配置。' }
        $backup = Join-Path $backupRoot $name
        $statePath = Join-Path $backup 'state.json'
        if (-not (Test-Path -LiteralPath $statePath -PathType Leaf)) { throw '备份记录缺失；未更改配置。' }
        $plan = Get-Content -LiteralPath $statePath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ([string]$plan.Sid -ne $sid) { throw '备份属于其他 Windows 用户；未更改配置。' }
        Assert-TCPlan $plan $evIni
        Assert-TCNoUserOverrides $userClasses $plan

        $name = 'before-restore-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
        $backupCreated = Join-Path $backupRoot $name
        New-Item -ItemType Directory -Path $backupCreated -Force | Out-Null
        if (@($plan.Ini).Count -gt 0) { Copy-Item -LiteralPath $evIni -Destination (Join-Path $backupCreated 'Everything.before-restore.ini') }
        $current = foreach ($entry in @($plan.Registry)) {
            [pscustomobject]@{ Path=$entry.Path; Name=$entry.Name; Value=(Get-TCRegistryValue $machineClasses ([string]$entry.Path) ([string]$entry.Name)) }
        }
        @($current) | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $backupCreated 'registry.before-restore.json') -Encoding UTF8
        Invoke-TCPlan $machineClasses $plan 'Restore'
        Remove-Item -LiteralPath $pointer
        Write-Host ('已还原可验证的设置。还原前备份：' + $backupCreated)
        Write-Host 'Everything 的其他设置未整体覆盖。请重新启动 Everything。'
    }
}
catch {
    $exitCode = 1
    [Console]::Error.WriteLine('操作停止：' + $_.Exception.Message)
    if ($backupCreated) { [Console]::Error.WriteLine('本次备份：' + $backupCreated + '。如已发生部分更改，请保留备份并检查后再重试还原。') }
}
finally {
    if ($null -ne $machineClasses) { $machineClasses.Dispose() }
    if ($null -ne $userClasses) { $userClasses.Dispose() }
    if ($null -ne $machineBase) { $machineBase.Dispose() }
    if ($null -ne $userBase) { $userBase.Dispose() }
    if ($haveMutex) { $mutex.ReleaseMutex() }
    if ($null -ne $mutex) { $mutex.Dispose() }
    if ($ElevatedChild) { $null = Read-Host '按回车关闭此窗口' }
}
exit $exitCode
