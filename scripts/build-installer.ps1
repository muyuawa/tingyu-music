param([string]$Source=(Join-Path $PSScriptRoot '..\artifacts\portable'),[string]$Output,[switch]$BuildOnly)
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot
$sourceRoot=(Resolve-Path -LiteralPath $Source).Path
. (Join-Path $PSScriptRoot 'release-files.ps1')
$null=& (Join-Path $PSScriptRoot 'verify-package-input.ps1') -Source $sourceRoot -BuildOnly:$BuildOnly
$wix=Join-Path (Split-Path $repo) '.tools\wix\wix.exe'
$uiExtension='WixToolset.UI.wixext/6.0.2'
& $wix extension add $uiExtension
if($LASTEXITCODE -ne 0){throw 'Unable to prepare installer UI extension'}
$cmake='C:\Program Files\CMake\bin\cmake.exe'
$installerNative=Join-Path $repo 'artifacts\installer-native'
& $cmake -S (Join-Path $PSScriptRoot 'installer-native') -B $installerNative -A x64
if($LASTEXITCODE -ne 0){throw 'Installer helper configuration failed'}
& $cmake --build $installerNative --config Release
if($LASTEXITCODE -ne 0){throw 'Installer helper compilation failed'}
[xml]$properties=Get-Content (Join-Path $repo 'Directory.Build.props')
$version=$properties.Project.PropertyGroup.Version
function EscapeXml([string]$Text){[Security.SecurityElement]::Escape($Text)}
function Id([string]$Text){'I'+[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).Substring(0,24)}
function ComponentGuid([string]$Text){$digest=[Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes('MuyuMusic.InstallFile/'+$Text.ToLowerInvariant()));([guid]::new([byte[]]$digest[0..15])).ToString().ToUpperInvariant()}
$directories=@{'.'='INSTALLFOLDER'}
$directoryXml=[Collections.Generic.List[string]]::new()
function RenderDirectory([string]$Path,[string]$Relative){
    $children=Get-ChildItem -LiteralPath $Path -Directory | Where-Object {$Relative -ne '.' -or ($_.Name -ne 'data' -and (Test-ReleaseDirectory $_.Name))} | Sort-Object Name
    foreach($child in $children){$relativePath=if($Relative -eq '.'){ $child.Name }else{Join-Path $Relative $child.Name};$directoryId=Id $relativePath;$directories[$relativePath]=$directoryId;$directoryXml.Add('<Directory Id="'+$directoryId+'" Name="'+(EscapeXml $child.Name)+'">');RenderDirectory $child.FullName $relativePath;$directoryXml.Add('</Directory>')}
}
RenderDirectory $sourceRoot '.'
$components=[Collections.Generic.List[string]]::new();$refs=[Collections.Generic.List[string]]::new()
foreach($file in Get-ChildItem -LiteralPath $sourceRoot -Recurse -File | Where-Object {$_.Name -ne 'portable.marker' -and (Test-ReleaseFile $_ $sourceRoot)} | Sort-Object FullName){
    $relative=$file.FullName.Substring($sourceRoot.Length+1);$folder=Split-Path $relative -Parent;if(-not $folder){$folder='.'};$fileId=Id $relative;$componentId='C'+$fileId
    $components.Add('<Component Id="'+$componentId+'" Directory="'+$directories[$folder]+'" Guid="'+(ComponentGuid $relative)+'"><File Id="'+$fileId+'" Source="'+(EscapeXml $file.FullName)+'"/><RegistryValue Root="HKCU" Key="Software\MuyuMusic\InstallerFiles" Name="'+$fileId+'" Value="'+(EscapeXml $relative)+'" Type="string" KeyPath="yes"/><RemoveFolder Id="R'+$fileId+'" On="uninstall"/></Component>');$refs.Add('<ComponentRef Id="'+$componentId+'"/>')
}
$exeId=Id 'Muyu.App.exe'
$components.Add('<Component Id="InstallLocation" Directory="INSTALLFOLDER" Guid="73738363-A7F0-44F5-B923-550A8962C529"><RegistryValue Root="HKCU" Key="Software\MuyuMusic" Name="InstallDir" Value="[INSTALLFOLDER]" Type="string" KeyPath="yes"/></Component>');$refs.Add('<ComponentRef Id="InstallLocation"/>')
$components.Add('<Component Id="StartMenuShortcut" Directory="ProgramMenuFolder" Guid="AFA64F2E-F57A-4FB4-99DA-393D47D63A55"><Shortcut Id="MuyuShortcut" Name="听屿" Target="[#'+$exeId+']" WorkingDirectory="INSTALLFOLDER"/><RegistryValue Root="HKCU" Key="Software\MuyuMusic" Name="Installed" Value="1" Type="integer" KeyPath="yes"/></Component>');$refs.Add('<ComponentRef Id="StartMenuShortcut"/>')
$xml=@"
<Wix xmlns="http://wixtoolset.org/schemas/v4/wxs">
  <Package Name="听屿" Manufacturer="Muyu Music" Version="$version" UpgradeCode="3F0165E1-33B1-41E2-9C92-29AB5143C3C2" Scope="perUser" InstallerVersion="500" Language="2052">
    <Property Id="DISABLEROLLBACK" Value="1" Secure="yes"/>
    <InstallExecuteSequence><DisableRollback Before="InstallValidate"/></InstallExecuteSequence>
    <MajorUpgrade AllowSameVersionUpgrades="yes" DowngradeErrorMessage="已安装更新版本的 听屿。"/>
    <MediaTemplate EmbedCab="yes" CompressionLevel="high"/>
    <Icon Id="TingyuAppIcon" SourceFile="$(EscapeXml (Join-Path $sourceRoot 'Assets\app.ico'))"/>
    <Property Id="ARPPRODUCTICON" Value="TingyuAppIcon"/>
    <Property Id="MUYU_OS_BUILD" Secure="yes">
      <RegistrySearch Id="MuyuWindowsBuild" Root="HKLM" Key="SOFTWARE\Microsoft\Windows NT\CurrentVersion" Name="CurrentBuildNumber" Type="raw" Bitness="always64"/>
    </Property>
    <Launch Condition="Installed OR (VersionNT64 AND MUYU_OS_BUILD &gt;= 19041)" Message="需要 Windows 10 2004 或更新版本的 64 位系统。"/>
    <StandardDirectory Id="LocalAppDataFolder"><Directory Id="INSTALLROOT" Name="Programs"><Directory Id="INSTALLFOLDER" Name="Tingyu">$($directoryXml -join '')</Directory></Directory></StandardDirectory>
    <StandardDirectory Id="ProgramMenuFolder"/>
    <Property Id="WIXUI_INSTALLDIR" Value="INSTALLROOT"/>
    <Property Id="INSTALLROOT" Secure="yes"/>
    <Property Id="INSTALLFOLDER" Secure="yes"/>
    <Property Id="MUYU_EXISTING_INSTALL" Secure="yes"/>
    <Property Id="MUYU_PREVIOUS_INSTALL" Secure="yes"/>
    <Binary Id="TingyuInstallerHelper" SourceFile="$(EscapeXml (Join-Path $installerNative 'Release\TingyuInstaller.dll'))"/>
    <CustomAction Id="DetectTingyuInstall" BinaryRef="TingyuInstallerHelper" DllEntry="DetectTingyuInstall" Execute="firstSequence" Return="check"/>
    <CustomAction Id="RestoreTingyuInstallPath" BinaryRef="TingyuInstallerHelper" DllEntry="RestoreTingyuInstallPath" Execute="immediate" Return="check"/>
    <CustomAction Id="CleanupTingyuData" BinaryRef="TingyuInstallerHelper" DllEntry="CleanupTingyuData" Execute="deferred" Impersonate="yes" Return="check"/>
    <SetProperty Id="CleanupTingyuData" Value="[INSTALLFOLDER]" Before="CleanupTingyuData" Sequence="execute" Condition="Installed AND REMOVE~=&quot;ALL&quot; AND NOT UPGRADINGPRODUCTCODE"/>
    <InstallExecuteSequence><Custom Action="CleanupTingyuData" Before="InstallFinalize" Condition="Installed AND REMOVE~=&quot;ALL&quot; AND NOT UPGRADINGPRODUCTCODE"/></InstallExecuteSequence>
    <InstallUISequence><Custom Action="DetectTingyuInstall" After="AppSearch" Condition="1"/><Custom Action="RestoreTingyuInstallPath" After="CostFinalize" Condition="MUYU_PREVIOUS_INSTALL"/></InstallUISequence>
    <InstallExecuteSequence><Custom Action="DetectTingyuInstall" After="AppSearch" Condition="1"/><Custom Action="RestoreTingyuInstallPath" After="CostFinalize" Condition="MUYU_PREVIOUS_INSTALL"/></InstallExecuteSequence>
    <SetProperty Id="ARPINSTALLLOCATION" Value="[INSTALLFOLDER]" After="RestoreTingyuInstallPath" Sequence="both"/>
    <Property Id="ARPNOMODIFY" Value="1"/>
    <UIRef Id="TingyuInstallDir"/>
    $($components -join '')
    <Feature Id="Main" Title="听屿" Level="1">$($refs -join '')</Feature>
  </Package>
</Wix>
"@
$authoring=Join-Path $repo 'artifacts\MuyuMusic.wxs'
[IO.File]::WriteAllText($authoring,$xml,[Text.UTF8Encoding]::new($false))
if(-not $Output){$Output=Join-Path $repo ('artifacts\MuyuMusic-'+$version+'-win-x64.msi')}
& $wix build $authoring (Join-Path $PSScriptRoot 'installer-ui.wxs') -loc (Join-Path $PSScriptRoot 'installer-zh-CN.wxl') -ext $uiExtension -culture zh-CN -arch x64 -o $Output
if($LASTEXITCODE -ne 0){throw 'Installer compilation failed'}
Get-FileHash -LiteralPath $Output -Algorithm SHA256
