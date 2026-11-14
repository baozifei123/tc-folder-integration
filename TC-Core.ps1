# Helpers for Total Commander folder integration. No work is performed when sourced.
Set-StrictMode -Version 2.0

function Get-TCRegistryValue {
    param([Microsoft.Win32.RegistryKey]$Root, [string]$Path, [AllowEmptyString()][string]$Name)
    $key = $Root.OpenSubKey($Path, $false)
    if ($null -eq $key) { return [pscustomobject]@{ Exists = $false; Kind = ''; Data = '' } }
    try {
        if (@($key.GetValueNames()) -notcontains $Name) {
            return [pscustomobject]@{ Exists = $false; Kind = ''; Data = '' }
        }
        $kind = $key.GetValueKind($Name).ToString()
        if ($kind -notin @('String', 'ExpandString')) {
            throw "Unsupported registry value type at $Path / '$Name': $kind. No changes were made."
        }
        $data = [string]$key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        return [pscustomobject]@{ Exists = $true; Kind = $kind; Data = $data }
    }
    finally { $key.Dispose() }
}

function Test-TCValueEqual {
    param($Left, $Right)
    if ([bool]$Left.Exists -ne [bool]$Right.Exists) { return $false }
    if (-not [bool]$Left.Exists) { return $true }
    if ([string]$Left.Data -cne [string]$Right.Data) { return $false }
    if ($null -ne $Left.PSObject.Properties['Kind'] -and $null -ne $Right.PSObject.Properties['Kind']) {
        return [string]$Left.Kind -ceq [string]$Right.Kind
    }
    return $true
}

function Set-TCRegistryValue {
    param([Microsoft.Win32.RegistryKey]$Root, [string]$Path, [AllowEmptyString()][string]$Name, $Expected, $Desired)
    $actual = Get-TCRegistryValue $Root $Path $Name
    if (Test-TCValueEqual $actual $Desired) { return }
    if (-not (Test-TCValueEqual $actual $Expected)) {
        throw "Registry value changed since preflight: $Path / '$Name'. Restore the saved backup or inspect it before retrying."
    }
    if ([bool]$Desired.Exists) {
        $kind = [Microsoft.Win32.RegistryValueKind]([System.Enum]::Parse([Microsoft.Win32.RegistryValueKind], [string]$Desired.Kind))
        $key = $Root.CreateSubKey($Path)
        if ($null -eq $key) { throw "Could not open registry key for writing: $Path" }
        try { $key.SetValue($Name, [string]$Desired.Data, $kind) } finally { $key.Dispose() }
    }
    else {
        $key = $Root.OpenSubKey($Path, $true)
        if ($null -ne $key) {
            try { $key.DeleteValue($Name, $false) } finally { $key.Dispose() }
        }
    }
}

function Read-TCIni {
    param([string]$Path)
    $bytes = [System.IO.File]::ReadAllBytes($Path)
    $offset = 0
    if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        $encoding = New-Object System.Text.UTF8Encoding($true, $true); $offset = 3
    }
    elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
        $encoding = New-Object System.Text.UnicodeEncoding($false, $true, $true); $offset = 2
    }
    elseif ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) {
        $encoding = New-Object System.Text.UnicodeEncoding($true, $true, $true); $offset = 2
    }
    else {
        $encoding = New-Object System.Text.UTF8Encoding($false, $true)
        try { $null = $encoding.GetString($bytes) } catch { $encoding = [System.Text.Encoding]::Default }
    }
    $text = $encoding.GetString($bytes, $offset, $bytes.Length - $offset)
    return [pscustomobject]@{ Text = $text; Encoding = $encoding; HasBom = ($offset -gt 0) }
}

function Get-TCIniValue {
    param([AllowEmptyString()][string]$Text, [string]$Name)
    $pattern = '(?m)^' + [regex]::Escape($Name) + '=(?<value>[^\r\n]*)'
    $matches = [regex]::Matches($Text, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($matches.Count -gt 1) { throw "Duplicate Everything.ini entry: $Name" }
    if ($matches.Count -eq 0) { return [pscustomobject]@{ Exists = $false; Data = '' } }
    return [pscustomobject]@{ Exists = $true; Data = $matches[0].Groups['value'].Value }
}

function Set-TCIniValue {
    param([AllowEmptyString()][string]$Text, [string]$Name, $Desired)
    $pattern = '(?m)^' + [regex]::Escape($Name) + '=(?<value>[^\r\n]*)'
    $matches = [regex]::Matches($Text, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
    if ($matches.Count -gt 1) { throw "Duplicate Everything.ini entry: $Name" }
    if ($matches.Count -eq 0) {
        if (-not [bool]$Desired.Exists) { return $Text }
        $newline = if ($Text.Contains("`r`n")) { "`r`n" } elseif ($Text.Contains("`n")) { "`n" } else { "`r`n" }
        if ($Text.Length -gt 0 -and -not $Text.EndsWith("`n")) { $Text += $newline }
        return $Text + $Name + '=' + [string]$Desired.Data + $newline
    }
    $match = $matches[0]
    if (-not [bool]$Desired.Exists) {
        $length = $match.Length
        $end = $match.Index + $length
        if ($end -lt $Text.Length -and $Text[$end] -eq "`r") { $length++; $end++ }
        if ($end -lt $Text.Length -and $Text[$end] -eq "`n") { $length++ }
        return $Text.Remove($match.Index, $length)
    }
    $group = $match.Groups['value']
    return $Text.Substring(0, $group.Index) + [string]$Desired.Data + $Text.Substring($group.Index + $group.Length)
}

function Write-TCIni {
    param([string]$Path, $Document, [AllowEmptyString()][string]$Text)
    $body = $Document.Encoding.GetBytes($Text)
    if ($Document.HasBom) {
        $prefix = $Document.Encoding.GetPreamble()
        $result = New-Object byte[] ($prefix.Length + $body.Length)
        [Array]::Copy($prefix, 0, $result, 0, $prefix.Length)
        [Array]::Copy($body, 0, $result, $prefix.Length, $body.Length)
    }
    else { $result = $body }
    $temp = $Path + '.tc-' + [guid]::NewGuid().ToString('N') + '.tmp'
    $temporaryBackup = $temp + '.previous'
    try {
        [System.IO.File]::WriteAllBytes($temp, $result)
        [System.IO.File]::Replace($temp, $Path, $temporaryBackup)
    }
    finally {
        if ([System.IO.File]::Exists($temp)) { [System.IO.File]::Delete($temp) }
        if ([System.IO.File]::Exists($temporaryBackup)) { [System.IO.File]::Delete($temporaryBackup) }
    }
}

function New-TCRegistryChange {
    param([Microsoft.Win32.RegistryKey]$Root, [string]$Path, [AllowEmptyString()][string]$Name, [bool]$Exists, [string]$Kind, [AllowEmptyString()][string]$Data)
    return [pscustomobject]@{
        Path = $Path; Name = $Name
        Before = Get-TCRegistryValue $Root $Path $Name
        After = [pscustomobject]@{ Exists = $Exists; Kind = $Kind; Data = $Data }
    }
}

function New-TCIniChange {
    param([string]$Text, [string]$Name, [string]$Value)
    return [pscustomobject]@{
        Name = $Name
        Before = Get-TCIniValue $Text $Name
        After = [pscustomobject]@{ Exists = $true; Data = $Value }
    }
}

function Assert-TCPlan {
    param($Plan, [string]$ExpectedIni)
    if ([int]$Plan.Version -ne 2) { throw 'Unsupported backup format.' }
    $allowed = @{
        'Folder\shell\open\command' = @('', 'DelegateExecute')
        'Directory\shell\open\command' = @('', 'DelegateExecute')
        'Drive\shell\open\command' = @('', 'DelegateExecute')
        'Directory\shell\OpenWithExplorer' = @('', 'Icon')
        'Directory\shell\OpenWithExplorer\command' = @('')
    }
    $seen = @{}
    foreach ($item in @($Plan.Registry)) {
        $path = [string]$item.Path; $name = [string]$item.Name
        if (-not $allowed.ContainsKey($path) -or $allowed[$path] -cnotcontains $name) { throw "Unexpected registry entry in backup: $path / $name" }
        $id = $path.ToLowerInvariant() + '|' + $name.ToLowerInvariant()
        if ($seen.ContainsKey($id)) { throw "Duplicate registry entry in backup: $id" }
        $seen[$id] = $true
        foreach ($value in @($item.Before, $item.After)) {
            if ([bool]$value.Exists -and [string]$value.Kind -notin @('String', 'ExpandString')) { throw 'Unsupported registry type in backup.' }
        }
    }
    $iniNames = @{}
    foreach ($item in @($Plan.Ini)) {
        $name = [string]$item.Name
        if (@('open_folder_command2', 'open_path_command2') -cnotcontains $name -or $iniNames.ContainsKey($name)) {
            throw "Unexpected Everything.ini entry in backup: $name"
        }
        $iniNames[$name] = $true
    }
    if (@($Plan.Ini).Count -gt 0) {
        if (-not [string]::Equals([System.IO.Path]::GetFullPath([string]$Plan.IniPath), [System.IO.Path]::GetFullPath($ExpectedIni), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'The backup refers to a different Everything.ini.'
        }
    }
}

function Assert-TCNoUserOverrides {
    param([Microsoft.Win32.RegistryKey]$UserClasses, $Plan)
    foreach ($item in @($Plan.Registry)) {
        $value = Get-TCRegistryValue $UserClasses ([string]$item.Path) ([string]$item.Name)
        if ($value.Exists) { throw "A per-user override exists at $($item.Path) / '$($item.Name)'; no changes were made." }
    }
}

function Invoke-TCPlan {
    param([Microsoft.Win32.RegistryKey]$Root, $Plan, [ValidateSet('Install','Restore')][string]$Action)
    $from = if ($Action -eq 'Install') { 'Before' } else { 'After' }
    $to = if ($Action -eq 'Install') { 'After' } else { 'Before' }
    # Preflight all entries before making the first change. Before or after is accepted
    # so a partially completed run can be recovered safely.
    foreach ($item in @($Plan.Registry)) {
        $actual = Get-TCRegistryValue $Root ([string]$item.Path) ([string]$item.Name)
        if (-not (Test-TCValueEqual $actual $item.$from) -and -not (Test-TCValueEqual $actual $item.$to)) {
            throw "Registry conflict at $($item.Path) / '$($item.Name)'; no changes were made in this run."
        }
    }
    $doc = $null
    if (@($Plan.Ini).Count -gt 0) {
        $doc = Read-TCIni ([string]$Plan.IniPath)
        foreach ($item in @($Plan.Ini)) {
            $actual = Get-TCIniValue $doc.Text ([string]$item.Name)
            if (-not (Test-TCValueEqual $actual $item.$from) -and -not (Test-TCValueEqual $actual $item.$to)) {
                throw "Everything.ini conflict at $($item.Name); no changes were made in this run."
            }
        }
    }
    foreach ($item in @($Plan.Registry)) {
        Set-TCRegistryValue $Root ([string]$item.Path) ([string]$item.Name) $item.$from $item.$to
    }
    if ($null -ne $doc) {
        # Re-read to preserve unrelated settings and detect changes after preflight.
        $doc = Read-TCIni ([string]$Plan.IniPath)
        $text = $doc.Text
        foreach ($item in @($Plan.Ini)) {
            $actual = Get-TCIniValue $text ([string]$item.Name)
            if (Test-TCValueEqual $actual $item.$to) { continue }
            if (-not (Test-TCValueEqual $actual $item.$from)) { throw "Everything.ini changed during the operation: $($item.Name)." }
            $text = Set-TCIniValue $text ([string]$item.Name) $item.$to
        }
        if ($text -cne $doc.Text) { Write-TCIni ([string]$Plan.IniPath) $doc $text }
    }
}

