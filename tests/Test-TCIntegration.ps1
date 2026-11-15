[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
$project = Split-Path -Parent $PSScriptRoot
. (Join-Path $project 'TC-Core.ps1')

function Assert-True([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Test failed: $Message" }
}

# Parse every shipped PowerShell entry point without executing installation.
foreach ($file in Get-ChildItem -LiteralPath $project -Filter '*.ps1' -File) {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($file.FullName, [ref]$tokens, [ref]$errors)
    Assert-True ($errors.Count -eq 0) ("PowerShell syntax: " + $file.Name)
}

$testDirectory = Join-Path ([System.IO.Path]::GetTempPath()) ('TCFolderIntegration-测试-' + [guid]::NewGuid().ToString('N'))
$registryPath = 'Software\TCFolderIntegration-Test-' + [guid]::NewGuid().ToString('N')
$registry = $null
$createdRegistry = $false
try {
    New-Item -ItemType Directory -Path $testDirectory | Out-Null
    $defaultPath = Join-Path $env:LOCALAPPDATA 'TotalCMD64\TotalCMD64.exe'
    Assert-True ((Get-TCTargetPath $testDirectory) -ceq $defaultPath) 'default TC path'
    $config = Join-Path $testDirectory 'tc-path.txt'
    $customPath = 'C:\测试 目录\Total Commander.exe'
    [System.IO.File]::WriteAllText($config, $customPath, (New-Object System.Text.UTF8Encoding($true)))
    Assert-True ((Get-TCTargetPath $testDirectory) -ceq $customPath) 'custom Unicode TC path'
    [System.IO.File]::WriteAllText($config, 'C:relative.exe')
    $rejected = $false
    try { [void](Get-TCTargetPath $testDirectory) } catch { $rejected = $true }
    Assert-True $rejected 'relative TC path is rejected'

    $iniPath = Join-Path $testDirectory 'Everything.ini'
    $ini = "[Everything]`r`nopen_folder_command2=old folder`r`nopen_path_command2=old path`r`nunrelated=keep`r`n"
    [System.IO.File]::WriteAllText($iniPath, $ini, (New-Object System.Text.UTF8Encoding($true)))

    $registry = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($registryPath)
    if ($null -eq $registry) { throw 'Could not create isolated test registry key.' }
    $createdRegistry = $true
    $command = $registry.CreateSubKey('Directory\shell\open\command')
    try {
        $command.SetValue('', 'original', [Microsoft.Win32.RegistryValueKind]::String)
        $command.SetValue('Unrelated', 'keep', [Microsoft.Win32.RegistryValueKind]::String)
    } finally { $command.Dispose() }

    $changes = @(
        (New-TCRegistryChange $registry 'Directory\shell\open\command' '' $true 'String' 'target'),
        (New-TCRegistryChange $registry 'Drive\shell\open\command' '' $true 'String' 'target drive')
    )
    $iniChanges = @(
        (New-TCIniChange $ini 'open_folder_command2' 'new folder'),
        (New-TCIniChange $ini 'open_path_command2' 'new path')
    )
    $plan = [pscustomobject]@{ Version=2; IniPath=$iniPath; Registry=$changes; Ini=$iniChanges }
    Assert-TCPlan $plan $iniPath
    Invoke-TCPlan $registry $plan 'Install'
    Assert-True ((Get-TCRegistryValue $registry 'Directory\shell\open\command' '').Data -ceq 'target') 'registry install'
    Assert-True ((Get-TCRegistryValue $registry 'Drive\shell\open\command' '').Data -ceq 'target drive') 'drive install'
    $doc = Read-TCIni $iniPath
    Assert-True $doc.HasBom 'INI BOM preserved'
    Assert-True ((Get-TCIniValue $doc.Text 'open_folder_command2').Data -ceq 'new folder') 'INI install'
    Write-TCIni $iniPath $doc ($doc.Text.Replace('unrelated=keep', 'unrelated=changed'))

    Invoke-TCPlan $registry $plan 'Restore'
    Assert-True ((Get-TCRegistryValue $registry 'Directory\shell\open\command' '').Data -ceq 'original') 'registry restore'
    Assert-True (-not (Get-TCRegistryValue $registry 'Drive\shell\open\command' '').Exists) 'restore removes originally absent value'
    Assert-True ((Get-TCRegistryValue $registry 'Directory\shell\open\command' 'Unrelated').Data -ceq 'keep') 'unrelated registry value preserved'
    $restored = Read-TCIni $iniPath
    Assert-True ((Get-TCIniValue $restored.Text 'open_folder_command2').Data -ceq 'old folder') 'INI restore'
    Assert-True ($restored.Text.Contains('unrelated=changed')) 'unrelated INI change preserved'

    $key = $registry.OpenSubKey('Directory\shell\open\command', $true)
    try { $key.SetValue('', 'external change', [Microsoft.Win32.RegistryValueKind]::String) } finally { $key.Dispose() }
    $conflict = $false
    try { Invoke-TCPlan $registry $plan 'Install' } catch { $conflict = $true }
    Assert-True $conflict 'external registry conflict rejected'
    Assert-True (-not (Get-TCRegistryValue $registry 'Drive\shell\open\command' '').Exists) 'conflict causes no partial write'
    Assert-True ((Get-TCIniValue (Read-TCIni $iniPath).Text 'open_folder_command2').Data -ceq 'old folder') 'conflict leaves INI unchanged'
    Write-Host 'PASS: PowerShell syntax, path configuration, isolated registry/INI install, restore, conflict and preservation tests.'
}
finally {
    if ($null -ne $registry) { $registry.Dispose() }
    if ($createdRegistry) { [Microsoft.Win32.Registry]::CurrentUser.DeleteSubKeyTree($registryPath) }
    if (Test-Path -LiteralPath $testDirectory) { Remove-Item -LiteralPath $testDirectory -Recurse -Force }
}
