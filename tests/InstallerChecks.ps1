param(
    [Parameter(Mandatory = $true)][string]$PayloadSource,
    [string]$MsiPath
)

$ErrorActionPreference = 'Stop'
$script:checks = 0
function Check([bool]$condition, [string]$name) {
    if (-not $condition) { throw "Installer check failed: $name" }
    $script:checks++
}
$sourcePath = (Resolve-Path -LiteralPath $PayloadSource).Path
$payloadDirectory = Join-Path (Split-Path -Parent $sourcePath) 'publish'
[xml]$payload = Get-Content -LiteralPath $sourcePath -Raw
$ns = New-Object Xml.XmlNamespaceManager($payload.NameTable)
$ns.AddNamespace('w', 'http://wixtoolset.org/schemas/v4/wxs')
$components = @($payload.SelectNodes('//w:Component', $ns))
$files = @($payload.SelectNodes('//w:File', $ns))
$publishedFiles = @(Get-ChildItem -LiteralPath $payloadDirectory -File -Recurse)
Check ($files.Count -eq $publishedFiles.Count -and $files.Count -gt 100) 'Every published runtime file is packaged'
Check (@($components | Group-Object Id | Where-Object Count -gt 1).Count -eq 0) 'Unique component identifiers'
Check (@($components | Group-Object Guid | Where-Object Count -gt 1).Count -eq 0) 'Unique explicit component GUIDs'
foreach ($component in $components) {
    $componentGuid = [guid]::Empty
    Check ([guid]::TryParse($component.Guid, [ref]$componentGuid) -and $componentGuid -ne [guid]::Empty) 'Payload component has an explicit GUID'
    $keyPath = $component.SelectSingleNode('w:RegistryValue[@KeyPath="yes"]', $ns)
    Check ($null -ne $keyPath -and $keyPath.Root -eq 'HKCU') 'Per-user component has HKCU key path'
}
$mappedFiles = @{}
foreach ($file in $files) {
    $fullPath = [IO.Path]::GetFullPath($file.Source)
    Check ($fullPath.StartsWith($payloadDirectory + '\', [StringComparison]::OrdinalIgnoreCase)) 'File contained in fresh payload'
    Check ((Test-Path -LiteralPath $fullPath -PathType Leaf) -and -not $mappedFiles.ContainsKey($fullPath)) 'File source exists and is unique'
    Check ($file.Name -ne 'settings.json' -and $file.KeyPath -eq 'no') 'No user settings or file key paths'
    $mappedFiles[$fullPath] = $true
}
foreach ($name in 'CodexUsageWidget.exe', 'coreclr.dll', 'hostfxr.dll', 'System.Windows.Forms.dll') {
    Check (Test-Path -LiteralPath (Join-Path $payloadDirectory $name)) "Bundled $name"
}
$runtime = Get-Content -LiteralPath (Join-Path $payloadDirectory 'CodexUsageWidget.runtimeconfig.json') -Raw | ConvertFrom-Json
Check (@($runtime.runtimeOptions.includedFrameworks).Count -eq 2 -and $null -eq $runtime.runtimeOptions.frameworks) 'Self-contained runtime metadata'
$repository = Split-Path -Parent $PSScriptRoot
[xml]$project = Get-Content -LiteralPath (Join-Path $repository 'CodexUsageWidget.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
$assemblyVersion = [Reflection.AssemblyName]::GetAssemblyName((Join-Path $payloadDirectory 'CodexUsageWidget.dll')).Version.ToString(3)
Check ($assemblyVersion -eq $version) 'Payload uses configured version'
[xml]$uiSource = Get-Content -LiteralPath (Join-Path $repository 'installer\WidgetUI.wxs') -Raw
$uiNs = New-Object Xml.XmlNamespaceManager($uiSource.NameTable)
$uiNs.AddNamespace('w', 'http://wixtoolset.org/schemas/v4/wxs')
Check ($null -ne $uiSource.SelectSingleNode('//w:Control[@Id="Repair"]/w:Publish[@Event="ReinstallMode" and @Value="amus" and @Order="1"]', $uiNs)) 'Confirmation uses forced file restoration'
Check ($null -eq $uiSource.SelectSingleNode('//w:DialogRef[@Id="VerifyReadyDlg"]', $uiNs)) 'Default repair mode cannot override forced restoration'
Check ($null -ne $uiSource.SelectSingleNode('//w:Publish[@Dialog="InstallDirDlg" and @Control="ChangeFolder" and @Event="SpawnDialog" and @Value="BrowseDlg"]', $uiNs)) 'MSI uses native folder browse dialog'
Check ($null -ne $uiSource.SelectSingleNode('//w:Control[@Id="Location" and @Text="Application folder: [INSTALLFOLDER]"]', $uiNs)) 'MSI confirms target before changing files'
Check ($null -ne $uiSource.SelectSingleNode('//w:Control[@Id="ActionDetails"]/w:Subscribe[@Event="ActionData"]', $uiNs)) 'MSI shows actual action details, not synthetic progress'
$cleanup = @($payload.SelectNodes('//w:RemoveFolder', $ns))
Check ($cleanup.Count -eq @(Get-ChildItem -LiteralPath $payloadDirectory -Directory -Recurse).Count + 2) 'Empty payload and parent folders cleaned on uninstall'
Check ($null -ne $payload.SelectSingleNode('//w:RemoveFolder[@Directory="ProgramsDirectory"]', $ns)) 'Programs parent directory has cleanup'
[xml]$packageSource = Get-Content -LiteralPath (Join-Path $repository 'installer\Package.wxs') -Raw
$packageNs = New-Object Xml.XmlNamespaceManager($packageSource.NameTable)
$packageNs.AddNamespace('w', 'http://wixtoolset.org/schemas/v4/wxs')
Check ($packageSource.SelectSingleNode('//w:Package', $packageNs).Scope -eq 'perUser') 'Package has per-user scope'
Check ($null -ne $packageSource.SelectSingleNode('//w:Property[@Id="WINDOWSBUILDNUMBER" and @Secure="yes"]/w:RegistrySearch[@Root="HKLM" and @Name="CurrentBuildNumber" and @Type="raw"]', $packageNs)) 'Actual Windows build is read from registry'
Check ($null -ne $packageSource.SelectSingleNode('//w:Launch[@Condition="Installed OR (VersionNT64 AND WINDOWSBUILDNUMBER >= 10240)"]', $packageNs)) 'OS guard does not use compatibility WindowsBuild'
Check ($null -ne $packageSource.SelectSingleNode('//w:Launch[@Condition="NOT ALLUSERS"]', $packageNs)) 'Per-machine override is rejected'
Check ($null -eq $packageSource.SelectSingleNode('//w:Property[@Id="REINSTALLMODE"]', $packageNs)) 'Repair mode is not a global install override'
[xml]$installerProject = Get-Content -LiteralPath (Join-Path $repository 'installer\CodexUsageWidget.Installer.wixproj') -Raw
Check ($installerProject.Project.PropertyGroup.SuppressIces -eq 'ICE91' -and $installerProject.Project.PropertyGroup.TreatWarningsAsErrors -eq 'true') 'Only documented per-user warning excluded; other warnings fail build'
Check ($null -ne $packageSource.SelectSingleNode('//w:Component[@Id="InstallationIdentity"]/w:RegistryValue[@Name="ProductCode" and @KeyPath="yes" and @Root="HKCU"]', $packageNs)) 'Setup discovers MSI-owned per-user identity'
Check ($null -ne $packageSource.SelectSingleNode('//w:Feature[@Id="DesktopShortcut" and @Level="200"]/w:ComponentRef[@Id="DesktopShortcutComponent"]', $packageNs)) 'Desktop shortcut is an optional feature'
Check ($null -ne $packageSource.SelectSingleNode('//w:Property[@Id="PREVIOUSINSTALLFOLDER"]/w:RegistrySearch[@Root="HKCU" and @Name="InstallFolder"]', $packageNs)) 'Existing custom application location is discovered'
Check ($null -ne $packageSource.SelectSingleNode('//w:SetProperty[@Id="INSTALLFOLDER" and @Sequence="both" and @Before="CostFinalize" and @Condition="PREVIOUSINSTALLFOLDER"]', $packageNs)) 'Update and maintenance preserve previous folder before removal'
Check ($packageSource.SelectSingleNode('//w:Property[@Id="MsiLogging"]', $packageNs).Value -eq 'voicewarmup!') 'Direct MSI logs are enabled and flushed'
Write-Host "Passed $script:checks payload/source checks. These do not prove MSI installation behaviour."

if (-not $MsiPath) { return }
$msi = (Resolve-Path -LiteralPath $MsiPath).Path
$installer = New-Object -ComObject WindowsInstaller.Installer
$database = $null
function Query([string]$sql) {
    $view = $database.GetType().InvokeMember('OpenView', 'InvokeMethod', $null, $database, @($sql))
    try {
        $view.GetType().InvokeMember('Execute', 'InvokeMethod', $null, $view, $null) | Out-Null
        while ($record = $view.GetType().InvokeMember('Fetch', 'InvokeMethod', $null, $view, $null)) {
            try {
                $count = $record.GetType().InvokeMember('FieldCount', 'GetProperty', $null, $record, $null)
                $values = for ($index = 1; $index -le $count; $index++) {
                    $record.GetType().InvokeMember('StringData', 'GetProperty', $null, $record, @($index))
                }
                ,([object[]]$values)
            } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($record) | Out-Null }
        }
    } finally {
        $view.GetType().InvokeMember('Close', 'InvokeMethod', $null, $view, $null) | Out-Null
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($view) | Out-Null
    }
}
try {
    # Mode 0 is read-only: no registration, installation, repair or uninstall.
    $database = $installer.GetType().InvokeMember('OpenDatabase', 'InvokeMethod', $null, $installer, @($msi, 0))
    $properties = @{}
    foreach ($row in (Query 'SELECT `Property`, `Value` FROM `Property`')) { $properties[$row[0]] = $row[1] }
    Check ($properties.ProductVersion -eq $version -and $properties.ProductName -eq 'Codex Usage Widget') 'MSI version and title'
    Check (-not $properties.ContainsKey('ALLUSERS')) 'Strictly per-user installation'
    Check (-not $properties.ContainsKey('REINSTALLMODE')) 'Repair mode is configured by repair action only'
    Check ($properties.MSIRESTARTMANAGERCONTROL -eq 'DisableShutdown') 'No forced application shutdown'
    Check ($properties.MsiLogging -eq 'voicewarmup!' -and $properties.WIXUI_INSTALLDIR -eq 'INSTALLFOLDER') 'Native logging and folder selection are configured'
    $checkboxes = @(Query 'SELECT `Property`, `Value` FROM `CheckBox`')
    Check (@($checkboxes | Where-Object { $_[0] -eq 'WIDGET_SHOWDETAILS' -and $_[1] -eq '1' }).Count -eq 1) 'Details toggle shares one checkbox value across dialogs'
    Check ([int]$properties.INSTALLLEVEL -lt 200) 'Desktop shortcut disabled by default'
    $launchConditions = @(Query 'SELECT `Condition` FROM `LaunchCondition`')
    Check (@($launchConditions | Where-Object { $_[0] -eq 'NOT ALLUSERS' }).Count -eq 1) 'MSI blocks per-machine override'
    Check (@($launchConditions | Where-Object { $_[0] -eq 'Installed OR (VersionNT64 AND WINDOWSBUILDNUMBER >= 10240)' }).Count -eq 1) 'MSI uses real Windows build guard'
    $registrySearch = @(Query 'SELECT `Root`, `Key`, `Name` FROM `RegLocator`')
    Check (@($registrySearch | Where-Object { $_[0] -eq '2' -and $_[1] -eq 'SOFTWARE\Microsoft\Windows NT\CurrentVersion' -and $_[2] -eq 'CurrentBuildNumber' }).Count -eq 1) 'MSI searches correct Windows build value'
    # Safe session (options=1) prevents machine changes. Run only registry
    # search and expression evaluation: never an install/repair/remove action.
    $session = $installer.GetType().InvokeMember('OpenPackage', 'InvokeMethod', $null, $installer, @($msi, 1))
    try {
        function Set-SessionProperty([string]$name, [string]$value) {
            $session.GetType().InvokeMember('Property', 'SetProperty', $null, $session, @($name, $value)) | Out-Null
        }
        $searchResult = $session.GetType().InvokeMember('DoAction', 'InvokeMethod', $null, $session, @('AppSearch'))
        Check ($searchResult -eq 1) 'Safe MSI registry search succeeds'
        $detectedBuild = $session.GetType().InvokeMember('Property', 'GetProperty', $null, $session, @('WINDOWSBUILDNUMBER'))
        $actualBuild = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuildNumber
        Check ($detectedBuild -eq $actualBuild) 'MSI detects actual host build, not compatibility build'
        Set-SessionProperty 'PREVIOUSINSTALLFOLDER' 'C:\Users\Example\Apps\Codex Usage Widget\'
        Check ($session.GetType().InvokeMember('DoAction', 'InvokeMethod', $null, $session, @('SetINSTALLFOLDER')) -eq 1) 'Safe path restoration action succeeds'
        Check ($session.GetType().InvokeMember('Property', 'GetProperty', $null, $session, @('INSTALLFOLDER')) -eq 'C:\Users\Example\Apps\Codex Usage Widget\') 'Saved custom folder overrides default in safe MSI session'
        $osCondition = 'Installed OR (VersionNT64 AND WINDOWSBUILDNUMBER >= 10240)'
        Set-SessionProperty 'Installed' ''
        Set-SessionProperty 'VersionNT64' '603'
        Set-SessionProperty 'WindowsBuild' '9600'
        foreach ($testBuild in '9600', '10240', '26100') {
            Set-SessionProperty 'WINDOWSBUILDNUMBER' $testBuild
            $result = $session.GetType().InvokeMember('EvaluateCondition', 'InvokeMethod', $null, $session, @($osCondition))
            Check ($result -eq [int]([int]$testBuild -ge 10240)) "OS guard boundary: $testBuild (compatibility build remains 9600)"
        }
        Set-SessionProperty 'VersionNT64' ''
        Check ($session.GetType().InvokeMember('EvaluateCondition', 'InvokeMethod', $null, $session, @($osCondition)) -eq 0) '32-bit Windows rejected'
        Set-SessionProperty 'Installed' '1'
        Set-SessionProperty 'WINDOWSBUILDNUMBER' '9600'
        Check ($session.GetType().InvokeMember('EvaluateCondition', 'InvokeMethod', $null, $session, @($osCondition)) -eq 1) 'Existing installation can enter maintenance'
    } finally { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($session) | Out-Null }
    $features = @(Query 'SELECT `Feature`, `Level` FROM `Feature`')
    Check (@($features | Where-Object { $_[0] -eq 'DesktopShortcut' -and $_[1] -eq '200' }).Count -eq 1) 'Optional desktop feature in MSI'
    $fileRows = @(Query 'SELECT `File`, `FileName` FROM `File`')
    Check ($fileRows.Count -eq $files.Count) 'MSI contains all runtime files'
    Check (@($fileRows | Where-Object { $_[1] -match 'settings\.json' }).Count -eq 0) 'MSI leaves settings unmanaged'
    $registries = @(Query 'SELECT `Root` FROM `Registry`')
    Check (@($registries | Where-Object { $_[0] -ne '1' }).Count -eq 0) 'MSI registry writes are HKCU only'
    $sequence = @{}
    foreach ($row in (Query 'SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`')) { $sequence[$row[0]] = [int]$row[1] }
    Check ($sequence.RemoveExistingProducts -gt $sequence.InstallInitialize -and $sequence.RemoveExistingProducts -lt $sequence.ProcessComponents) 'Upgrade removal is inside rollback transaction'
    $upgrade = @(Query 'SELECT `ActionProperty` FROM `Upgrade`')
    Check (@($upgrade | Where-Object { $_[0] -eq 'WIX_DOWNGRADE_DETECTED' }).Count -eq 1) 'Downgrade detection authored'
    $shortcut = @(Query 'SELECT `Name`, `Target` FROM `Shortcut`')
    Check ($shortcut.Count -eq 2 -and @($shortcut | Where-Object { $_[1] -ne '[INSTALLFOLDER]CodexUsageWidget.exe' }).Count -eq 0) 'Start menu and optional desktop shortcuts'
    $events = @(Query 'SELECT `Dialog_`, `Control_`, `Event`, `Argument` FROM `ControlEvent`')
    Check (@($events | Where-Object { $_[0] -eq 'WidgetVerifyReadyDlg' -and $_[1] -eq 'Repair' -and $_[2] -eq 'ReinstallMode' -and $_[3] -eq 'amus' }).Count -eq 1) 'Repair button forces file restoration'
    Check (@($events | Where-Object { $_[0] -eq 'InstallDirDlg' -and $_[1] -eq 'ChangeFolder' -and $_[2] -eq 'SpawnDialog' -and $_[3] -eq 'BrowseDlg' }).Count -eq 1) 'Built MSI opens Windows Installer folder chooser'
    foreach ($outcome in 'ExitDialog', 'FatalError', 'UserExit') {
        Check (@($events | Where-Object { $_[0] -eq "Widget$outcome" -and $_[1] -eq 'OpenLog' -and $_[2] -eq 'DoAction' -and $_[3] -eq 'OpenInstallerLog' }).Count -eq 1) "Open log available after $outcome"
    }
    $customActions = @(Query 'SELECT `Action`, `Source`, `Target` FROM `CustomAction`')
    Check (@($customActions | Where-Object { $_[0] -eq 'OpenInstallerLog' -and $_[1] -eq 'SystemFolder' -and $_[2] -eq 'notepad.exe "[MsiLogFileLocation]"' }).Count -eq 1) 'Log viewer invokes OS Notepad directly'
    Check (-not $sequence.ContainsKey('OpenInstallerLog')) 'Log viewer is never an unattended install action'
    $uiSequence = @{}
    foreach ($row in (Query 'SELECT `Action`, `Condition`, `Sequence` FROM `InstallUISequence`')) { $uiSequence[$row[0]] = $row }
    Check ([int]$uiSequence.WidgetProgressDlg[2] -lt [int]$uiSequence.ExecuteAction[2]) 'Custom native progress precedes execution'
    foreach ($entry in @(@('WidgetExitDialog', -1), @('WidgetUserExit', -2), @('WidgetFatalError', -3))) {
        Check ([int]$uiSequence[$entry[0]][2] -eq $entry[1]) "Custom outcome dialog owns $($entry[0])"
    }
    foreach ($stock in 'ProgressDlg', 'ExitDialog', 'FatalError', 'UserExit') {
        Check ($uiSequence[$stock][1] -eq '0' -and [int]$uiSequence[$stock][2] -gt 0) "Stock $stock does not hide diagnostic controls"
    }
    $expected = ((Get-Content -LiteralPath ($msi + '.sha256') -Raw).Split(' ')[0]).Trim()
    Check ((Get-FileHash -LiteralPath $msi -Algorithm SHA256).Hash.ToLowerInvariant() -eq $expected) 'Artifact checksum matches'
    Write-Host "Passed $script:checks total payload and read-only MSI checks. Lifecycle tests still require a disposable installation."
} finally {
    if ($null -ne $database) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($database) | Out-Null }
    [Runtime.InteropServices.Marshal]::FinalReleaseComObject($installer) | Out-Null
}
