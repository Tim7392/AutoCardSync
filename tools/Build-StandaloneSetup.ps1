[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $ProductVersion = '1.0.0.0',
    [string] $SetupVersion = '1.0.0',
    [string] $RuntimeMetadataPath = 'eng/installer/dotnet-desktop-runtime-10.0.10-win-x64.json',
    [string] $RuntimeInstallerPath = 'artifacts/prerequisites/windowsdesktop-runtime-10.0.10-win-x64.exe',
    [string] $WebView2MetadataPath = 'eng/installer/webview2-evergreen-standalone-x64.json',
    [string] $WebView2InstallerPath = 'artifacts/prerequisites/MicrosoftEdgeWebView2RuntimeInstallerX64-standalone.exe'
)
$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Resolve-Repo([string] $Path) {
    if ([IO.Path]::IsPathRooted($Path)) { return [IO.Path]::GetFullPath($Path) }
    return [IO.Path]::GetFullPath((Join-Path $repo $Path))
}

function ConvertTo-HexString([byte[]] $Bytes) {
    return (($Bytes | ForEach-Object { $_.ToString('x2') }) -join '').ToUpperInvariant()
}

function New-DeterministicId([string] $Prefix, [string] $Value, [int] $Offset) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($Value.ToUpperInvariant())
    $sha = [Security.Cryptography.SHA256]::Create().ComputeHash($bytes)
    $hex = ConvertTo-HexString $sha
    return $Prefix + '_' + $hex.Substring($Offset, 16)
}

function New-DeterministicGuid([string] $Value) {
    $bytes = [Text.Encoding]::UTF8.GetBytes('AutoCardSync.Standalone.Installer|' + $Value.ToUpperInvariant())
    $sha = [Security.Cryptography.SHA256]::Create().ComputeHash($bytes)
    $hex = ConvertTo-HexString $sha
    return '{' + $hex.Substring(0,8) + '-' + $hex.Substring(8,4) + '-' + $hex.Substring(12,4) + '-' + $hex.Substring(16,4) + '-' + $hex.Substring(20,12) + '}'
}

function Escape-Xml([string] $Value) {
    return [Security.SecurityElement]::Escape($Value)
}

function Clear-RegenerableDirectory([string] $Path, [string] $ExpectedLeaf) {
    $full = [IO.Path]::GetFullPath($Path)
    $root = [IO.Path]::GetFullPath($repo).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $full.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "Refusing to clear outside repo: $full" }
    if ((Split-Path -Leaf $full) -ne $ExpectedLeaf) { throw "Refusing to clear unexpected directory: $full" }
    if (Test-Path -LiteralPath $full) { Remove-Item -LiteralPath $full -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $full | Out-Null
}
function Assert-NativeSuccess([string] $Label) {
    if ($LASTEXITCODE -ne 0) { throw "$Label failed with exit code $LASTEXITCODE." }
}

function Get-ValidatedPrerequisite(
    [string] $InstallerPath,
    [string] $MetadataPath,
    [string] $Label) {
    if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) { throw "$Label installer missing: $InstallerPath" }
    if (-not (Test-Path -LiteralPath $MetadataPath -PathType Leaf)) { throw "$Label metadata missing: $MetadataPath" }
    $metadata = Get-Content -LiteralPath $MetadataPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($metadata.schemaVersion -ne 1 -or $metadata.architecture -ne 'x64') { throw "$Label metadata contract is invalid." }
    if ((Split-Path -Leaf $InstallerPath) -ne [string]$metadata.fileName) { throw "$Label file name does not match metadata." }
    $item = Get-Item -LiteralPath $InstallerPath
    if ($item.Length -ne [long]$metadata.size) { throw "$Label size does not match metadata." }
    if ((Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA512).Hash -ne ([string]$metadata.sha512).ToUpperInvariant()) {
        throw "$Label SHA-512 does not match metadata."
    }
    $signature = Get-AuthenticodeSignature -LiteralPath $InstallerPath
    if ($signature.Status -ne [System.Management.Automation.SignatureStatus]::Valid -or
        $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notlike ('*' + [string]$metadata.expectedSignerSubjectContains + '*')) {
        throw "$Label Authenticode signature is not the expected valid Microsoft signature: $($signature.Status)."
    }
    return $metadata
}

$publishDir = Resolve-Repo 'artifacts/publish/Standalone'
$installerDir = Resolve-Repo 'src/AutoCardSync.Standalone.Installer'
$harvestPath = Join-Path $installerDir 'StandaloneHarvest.wxs'
$msiOutDir = Resolve-Repo 'artifacts/installer/standalone'
$bundleOutDir = Resolve-Repo 'artifacts/setup/standalone'
$iconPath = Resolve-Repo 'src/AutoCardSync.Standalone/Assets/AutoCardSync.ico'
if (-not (Test-Path -LiteralPath $iconPath -PathType Leaf)) { throw "Standalone icon missing: $iconPath" }

$runtimePath = Resolve-Repo $RuntimeInstallerPath
$webView2Path = Resolve-Repo $WebView2InstallerPath
$runtimeMetadataFullPath = Resolve-Repo $RuntimeMetadataPath
$webView2MetadataFullPath = Resolve-Repo $WebView2MetadataPath
$null = Get-ValidatedPrerequisite $runtimePath $runtimeMetadataFullPath 'Windows Desktop Runtime'
$null = Get-ValidatedPrerequisite $webView2Path $webView2MetadataFullPath 'WebView2 Runtime'

$staticValidator = Join-Path $PSScriptRoot 'Test-StandaloneSetupBundle.ps1'
if (-not (Test-Path -LiteralPath $staticValidator -PathType Leaf)) { throw "Standalone setup validator missing: $staticValidator" }

Clear-RegenerableDirectory $publishDir 'Standalone'
Clear-RegenerableDirectory $msiOutDir 'standalone'
New-Item -ItemType Directory -Force -Path $bundleOutDir | Out-Null

# Packaging fails closed when dependency restore or vulnerability auditing cannot complete.
& dotnet publish (Resolve-Repo 'src/AutoCardSync.Standalone/AutoCardSync.Standalone.csproj') -c $Configuration -r win-x64 --self-contained false -o $publishDir '-p:NuGetLockFilePath=obj\standalone-setup\packages.lock.json' -p:NuGetAudit=true -p:RestoreIgnoreFailedSources=false -p:Version=$SetupVersion -p:FileVersion=$ProductVersion -p:AssemblyVersion=$ProductVersion -p:InformationalVersion=$ProductVersion
Assert-NativeSuccess 'Standalone publish'

$files = Get-ChildItem -LiteralPath $publishDir -File -Recurse | Sort-Object FullName
if (-not ($files | Where-Object Name -eq 'AutoCardSync.Standalone.exe')) { throw 'Standalone publish did not produce AutoCardSync.Standalone.exe.' }
$forbidden = $files | Where-Object { $_.FullName -match '(?i)(Agent\.Service|Dashboard|CaSigner|AdminCli|Bootstrapper|Installer)' }
if ($forbidden) { throw ('Standalone publish contains non-V1 components: ' + (($forbidden | Select-Object -ExpandProperty FullName) -join '; ')) }

$base = $publishDir.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$fileRows = @()
foreach ($file in $files) {
    $relative = $file.FullName.Substring($base.Length).Replace('\','/')
    $dir = [IO.Path]::GetDirectoryName($relative).Replace('\','/')
    if ($dir -eq '.') { $dir = '' }
    $fileRows += [pscustomobject]@{
        Relative = $relative
        Directory = $dir
        Name = [IO.Path]::GetFileName($relative)
        ComponentId = New-DeterministicId 'cmp' $relative 0
        FileId = New-DeterministicId 'fil' $relative 16
        ComponentGuid = New-DeterministicGuid $relative
        Source = '..\..\artifacts\publish\Standalone\' + $relative.Replace('/','\')
    }
}

$componentRefs = New-Object System.Collections.Generic.List[string]
function Append-HarvestDirectory([System.Text.StringBuilder] $Builder, [string] $RelativeDir, [string] $Indent) {
    $directFiles = $fileRows | Where-Object { $_.Directory -eq $RelativeDir }
    foreach ($row in $directFiles) {
        $componentRefs.Add($row.ComponentId)
        [void]$Builder.AppendLine("$Indent<Component Id=`"$($row.ComponentId)`" Guid=`"$($row.ComponentGuid)`">")
        [void]$Builder.AppendLine("$Indent  <File Id=`"$($row.FileId)`" Name=`"$(Escape-Xml $row.Name)`" Source=`"$(Escape-Xml $row.Source)`" />`r`n$Indent  <RegistryValue Root=`"HKCU`" Key=`"Software\AutoCardSync\Standalone\Files`" Name=`"$($row.ComponentId)`" Type=`"integer`" Value=`"1`" KeyPath=`"yes`" />")
        [void]$Builder.AppendLine("$Indent</Component>")
    }

    $prefix = if ($RelativeDir.Length -eq 0) { '' } else { $RelativeDir + '/' }
    $childDirs = $fileRows | ForEach-Object {
        if ($_.Directory.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
            $remainder = $_.Directory.Substring($prefix.Length)
            if ($remainder.Length -gt 0) {
                $segment = $remainder.Split('/')[0]
                if ($RelativeDir.Length -eq 0) { $segment } else { $RelativeDir + '/' + $segment }
            }
        }
    } | Select-Object -Unique | Sort-Object

    foreach ($child in $childDirs) {
        $name = if ($child.Contains('/')) { $child.Substring($child.LastIndexOf('/') + 1) } else { $child }
        $dirId = New-DeterministicId 'dir' $child 0
        [void]$Builder.AppendLine("$Indent<Directory Id=`"$dirId`" Name=`"$(Escape-Xml $name)`">")
        Append-HarvestDirectory $Builder $child ($Indent + '  ')
        [void]$Builder.AppendLine("$Indent</Directory>")
    }
}

$xml = New-Object System.Text.StringBuilder
[void]$xml.AppendLine('<?xml version="1.0" encoding="UTF-8"?>')
[void]$xml.AppendLine('<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">')
[void]$xml.AppendLine('  <Fragment>')
[void]$xml.AppendLine('    <DirectoryRef Id="INSTALLFOLDER">')
Append-HarvestDirectory $xml '' '      '
[void]$xml.AppendLine('    </DirectoryRef>')
[void]$xml.AppendLine('  </Fragment>')
[void]$xml.AppendLine('  <Fragment>')
[void]$xml.AppendLine('    <ComponentGroup Id="StandaloneFiles">')
if ($componentRefs.Count -ne $fileRows.Count -or @($componentRefs | Select-Object -Unique).Count -ne $fileRows.Count) {
    $referenced = @{}
    foreach ($componentId in $componentRefs) { $referenced[$componentId] = $true }
    $missing = @($fileRows | Where-Object { -not $referenced.ContainsKey($_.ComponentId) } | Select-Object -ExpandProperty Relative)
    throw "Standalone harvest did not reference every published file. Published=$($fileRows.Count), referenced=$($componentRefs.Count), missing=$($missing -join '; ')"
}
foreach ($componentId in $componentRefs) {
    [void]$xml.AppendLine("      <ComponentRef Id=`"$componentId`" />")
}
[void]$xml.AppendLine('    </ComponentGroup>')
[void]$xml.AppendLine('  </Fragment>')
[void]$xml.AppendLine('</Wix>')
[IO.File]::WriteAllText($harvestPath, $xml.ToString(), [Text.UTF8Encoding]::new($false))

$installerProject = Resolve-Repo 'src/AutoCardSync.Standalone.Installer/AutoCardSync.Standalone.Installer.csproj'
& dotnet build $installerProject -c $Configuration -p:ProductVersion=$ProductVersion -p:IconSourceFile=$iconPath -p:OutputPath=$msiOutDir -p:NuGetAudit=true -p:RestoreIgnoreFailedSources=false
Assert-NativeSuccess 'Standalone MSI build'
$msiPath = Join-Path $msiOutDir 'AutoCardSync-Standalone.msi'
if (-not (Test-Path -LiteralPath $msiPath -PathType Leaf)) { throw "MSI not found: $msiPath" }

$bundleProject = Resolve-Repo 'src/AutoCardSync.Bootstrapper/AutoCardSync.Bootstrapper.csproj'
& dotnet build $bundleProject -c $Configuration `
    -p:ProductVersion=$ProductVersion `
    -p:SetupVersion=$SetupVersion `
    -p:MsiPath=$msiPath `
    -p:RuntimeInstallerPath=$runtimePath `
    -p:WebView2RuntimeInstallerPath=$webView2Path `
    -p:BundleName="AutoCardSync Standalone Setup" `
    -p:BundleUpgradeCode="{7D3642D8-03D7-459B-8E26-9B9C3C155B62}" `
    -p:MsiPackageName="AutoCardSync-Standalone.msi" `
    -p:NuGetAudit=true `
    -p:RestoreIgnoreFailedSources=false `
    -p:LaunchTarget="[LocalAppDataFolder]Programs\AutoCardSync\AutoCardSync.Standalone.exe" `
    -p:LaunchWorkingFolder="[LocalAppDataFolder]Programs\AutoCardSync" `
    -p:CreateDesktopShortcutDefault=1 `
    -p:IconSourceFile=$iconPath `
    -p:OutputPath=$bundleOutDir
Assert-NativeSuccess 'Standalone Setup build'
$setup = Join-Path $bundleOutDir "AutoCardSync-Setup-$SetupVersion.exe"
if (-not (Test-Path -LiteralPath $setup -PathType Leaf)) { throw "Setup EXE not found: $setup" }
$staticEvidencePath = Resolve-Repo 'artifacts/verification/standalone-setup-static-latest.json'
& $staticValidator `
    -SetupPath $setup `
    -MsiPath $msiPath `
    -RuntimeMetadataPath $runtimeMetadataFullPath `
    -WebView2MetadataPath $webView2MetadataFullPath `
    -ProductVersion $ProductVersion `
    -SetupVersion $SetupVersion `
    -EvidencePath $staticEvidencePath

[pscustomobject]@{
    PublishDirectory = $publishDir
    InternalMsiPath = $msiPath
    SetupExe = $setup
    SetupSha256 = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash
    StaticEvidencePath = $staticEvidencePath
} | ConvertTo-Json -Depth 4
