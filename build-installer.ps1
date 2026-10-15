param(
    [switch]$AcceptWixEula,
    [switch]$PrepareOnly
)

$ErrorActionPreference = 'Stop'
if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 8 SDK (or a newer compatible SDK) and ensure dotnet is on PATH.'
}
if (-not $AcceptWixEula -and -not $PrepareOnly) {
    throw 'Packaging requires explicit acknowledgement of the WiX 7 terms: https://docs.firegiant.com/wix/osmf/. After reviewing them, run .\build-installer.ps1 -AcceptWixEula.'
}

$projectPath = Join-Path $PSScriptRoot 'CodexUsageWidget.csproj'
[xml]$projectXml = Get-Content -LiteralPath $projectPath -Raw
$widgetVersion = [string]$projectXml.Project.PropertyGroup.Version
if ($widgetVersion -notmatch '^\d+\.\d+\.\d+$') { throw 'The project version must be MAJOR.MINOR.PATCH.' }
$parsedVersion = [version]$widgetVersion
if ($parsedVersion.Major -gt 255 -or $parsedVersion.Minor -gt 255 -or $parsedVersion.Build -gt 65535) {
    throw 'The project version exceeds Windows Installer version limits (255.255.65535).'
}

# Each build gets a fresh payload directory so obsolete files cannot enter the MSI.
$artifactRoot = Join-Path $PSScriptRoot 'artifacts'
$stagingRoot = Join-Path $artifactRoot ('staging\' + [guid]::NewGuid().ToString('N'))
$payloadDirectory = Join-Path $stagingRoot 'publish'
$payloadSource = Join-Path $stagingRoot 'Payload.wxs'
$installerOutput = Join-Path $artifactRoot 'installer'
New-Item -ItemType Directory -Path $stagingRoot, $installerOutput -Force | Out-Null
& dotnet publish $projectPath --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false --output $payloadDirectory
if ($LASTEXITCODE -ne 0) { throw "Publishing failed with exit code $LASTEXITCODE." }

# A HKCU key path per component is required for a correctly validated per-user MSI.
function Get-PayloadId([string]$relativePath) {
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($relativePath.ToLowerInvariant())
        return ([BitConverter]::ToString($sha.ComputeHash($bytes))).Replace('-', '').Substring(0, 32)
    } finally { $sha.Dispose() }
}
function Get-ComponentGuid([string]$kind, [string]$relativePath) {
    # File + registry-keypath components require explicit GUIDs. Keep these
    # stable across versions and staging directories for the same installed path.
    return ([guid](Get-PayloadId "vortydev.CodexUsageWidget:win-x64:component:${kind}:$relativePath")).ToString('B').ToUpperInvariant()
}
# Rebuilding the same version must not register a second, unrelated product.
# Released payload changes still require a version bump and a new release.
$widgetProductCode = ([guid](Get-PayloadId "vortydev.CodexUsageWidget:win-x64:$widgetVersion")).ToString('B').ToUpperInvariant()
$xmlOptions = New-Object System.Xml.XmlWriterSettings
$xmlOptions.Indent = $true
$xmlOptions.Encoding = New-Object System.Text.UTF8Encoding($false)
$xmlOptions.NewLineChars = "`n"
$writer = [Xml.XmlWriter]::Create($payloadSource, $xmlOptions)
$wixNamespace = 'http://wixtoolset.org/schemas/v4/wxs'
function Start-Element([string]$name) { $writer.WriteStartElement($name, $wixNamespace) }
function Write-Attribute([string]$name, [string]$value) { $writer.WriteAttributeString($name, $value) }
function Write-KeyPath([string]$id) {
    Start-Element 'RegistryValue'
    Write-Attribute 'Root' 'HKCU'
    Write-Attribute 'Key' 'Software\vortydev\CodexUsageWidget\Installer\Payload'
    Write-Attribute 'Name' $id
    Write-Attribute 'Type' 'integer'
    Write-Attribute 'Value' '1'
    Write-Attribute 'KeyPath' 'yes'
    $writer.WriteEndElement()
}
try {
    $writer.WriteStartDocument()
    Start-Element 'Wix'
    Start-Element 'Fragment'
    $directories = @{ '' = 'INSTALLFOLDER' }
    foreach ($directory in (Get-ChildItem -LiteralPath $payloadDirectory -Directory -Recurse | Sort-Object FullName)) {
        $relative = $directory.FullName.Substring($payloadDirectory.Length + 1)
        $parent = [IO.Path]::GetDirectoryName($relative)
        if ($null -eq $parent) { $parent = '' }
        $id = 'D' + (Get-PayloadId $relative)
        $directories[$relative] = $id
        Start-Element 'DirectoryRef'
        Write-Attribute 'Id' $directories[$parent]
        Start-Element 'Directory'
        Write-Attribute 'Id' $id
        Write-Attribute 'Name' $directory.Name
        $writer.WriteEndElement()
        $writer.WriteEndElement()
    }
    Start-Element 'ComponentGroup'
    Write-Attribute 'Id' 'WidgetPayload'
    foreach ($relative in ($directories.Keys | Sort-Object)) {
        $id = 'Folder' + (Get-PayloadId $relative)
        Start-Element 'Component'
        Write-Attribute 'Id' $id
        Write-Attribute 'Directory' $directories[$relative]
        Write-Attribute 'Guid' (Get-ComponentGuid 'folder' $relative)
        Start-Element 'CreateFolder'
        $writer.WriteEndElement()
        Start-Element 'RemoveFolder'
        Write-Attribute 'Id' $id
        Write-Attribute 'On' 'uninstall'
        $writer.WriteEndElement()
        if ($relative -eq '') {
            Start-Element 'RemoveFolder'
            Write-Attribute 'Id' 'RemoveProgramsDirectory'
            Write-Attribute 'Directory' 'ProgramsDirectory'
            Write-Attribute 'On' 'uninstall'
            $writer.WriteEndElement()
        }
        Write-KeyPath $id
        $writer.WriteEndElement()
    }
    foreach ($file in (Get-ChildItem -LiteralPath $payloadDirectory -File -Recurse | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($payloadDirectory.Length + 1)
        $parent = [IO.Path]::GetDirectoryName($relative)
        if ($null -eq $parent) { $parent = '' }
        $id = Get-PayloadId $relative
        Start-Element 'Component'
        Write-Attribute 'Id' ('C' + $id)
        Write-Attribute 'Directory' $directories[$parent]
        Write-Attribute 'Guid' (Get-ComponentGuid 'file' $relative)
        Start-Element 'File'
        Write-Attribute 'Id' ('F' + $id)
        Write-Attribute 'Source' $file.FullName
        Write-Attribute 'Name' $file.Name
        Write-Attribute 'KeyPath' 'no'
        $writer.WriteEndElement()
        Write-KeyPath ('F' + $id)
        $writer.WriteEndElement()
    }
    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndDocument()
} finally { $writer.Dispose() }

# The standalone launcher uses only .NET and can be built before WiX acknowledgement.
$launcherProject = Join-Path $PSScriptRoot 'installer\launcher\CodexUsageSetup.csproj'
$launcherOutput = Join-Path $stagingRoot 'launcher'
& dotnet publish $launcherProject --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false `
    -p:DebugType=None -p:DebugSymbols=false "-p:WidgetVersion=$widgetVersion" --output $launcherOutput
if ($LASTEXITCODE -ne 0) { throw "Setup launcher publishing failed with exit code $LASTEXITCODE." }
$setupPath = Join-Path $installerOutput 'CodexUsageSetup.exe'
Copy-Item -LiteralPath (Join-Path $launcherOutput 'CodexUsageSetup.exe') -Destination $setupPath
$setupChecksum = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$setupPath.sha256", "$setupChecksum  CodexUsageSetup.exe`n", (New-Object Text.UTF8Encoding($false)))
Write-Host "Setup wizard: $setupPath"

if ($PrepareOnly) {
    Write-Host "Prepared self-contained payload: $payloadDirectory"
    Write-Host "Generated installer source: $payloadSource"
    Write-Host 'Launcher built. No MSI built; WiX terms were not accepted.'
    return
}

$installerProject = Join-Path $PSScriptRoot 'installer\CodexUsageWidget.Installer.wixproj'
& dotnet build $installerProject --configuration Release -p:AcceptEula=wix7 `
    "-p:WidgetVersion=$widgetVersion" "-p:WidgetProductCode=$widgetProductCode" "-p:PayloadDirectory=$payloadDirectory" "-p:PayloadSource=$payloadSource" `
    "-p:OutputPath=$installerOutput/"
if ($LASTEXITCODE -ne 0) { throw "MSI build failed with exit code $LASTEXITCODE." }
$msiPath = Join-Path $installerOutput "CodexUsageWidget-$widgetVersion-win-x64.msi"
if (-not (Test-Path -LiteralPath $msiPath)) { throw "MSI output not found: $msiPath" }
$checksum = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$msiPath.sha256", "$checksum  $([IO.Path]::GetFileName($msiPath))`n", (New-Object Text.UTF8Encoding($false)))
Write-Host "Built unsigned installer: $msiPath"
Write-Host "Checksum: $msiPath.sha256"
Write-Host "Setup wizard: $setupPath"
