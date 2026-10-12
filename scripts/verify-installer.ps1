param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference='Stop'
$installer=New-Object -ComObject WindowsInstaller.Installer
$database=$installer.OpenDatabase((Resolve-Path -LiteralPath $Path).Path,0)
function Rows([string]$Table){
    $view=$database.OpenView('SELECT * FROM `'+$Table+'`')
    $null=$view.Execute()
    try {
        while($record=$view.Fetch()){
            $fieldCount=switch($Table){'Property'{2};'Directory'{3};'ControlEvent'{6};'Registry'{6};'CustomAction'{4};'InstallUISequence'{3};'InstallExecuteSequence'{3};'File'{8};default{throw 'Unsupported verification table'}}
            $values=for($i=1;$i -le $fieldCount;$i++){$record.StringData($i)}
            ,$values
        }
    } finally {$null=$view.Close()}
}
$properties=@{};foreach($row in (Rows 'Property')){$properties[$row[0]]=$row[1]}
if($properties.DISABLEROLLBACK -ne '1'){throw 'Rollback backups must be disabled for this package'}
$execute=@{};foreach($row in (Rows 'InstallExecuteSequence')){$execute[$row[0]]=[int]$row[2]}
if(-not $execute.ContainsKey('DisableRollback') -or $execute.DisableRollback -ge $execute.InstallValidate -or $execute.DisableRollback -ge $execute.RemoveExistingProducts){throw 'Rollback must be disabled before installation starts'}
if($execute.RemoveExistingProducts -le $execute.InstallExecute -or $execute.RemoveExistingProducts -ge $execute.InstallFinalize){throw 'Upgrade must install new components before removing old registration'}
$directories=@{};foreach($row in (Rows 'Directory')){$directories[$row[0]]=$row}
if($properties.WIXUI_INSTALLDIR -ne 'INSTALLROOT'){throw 'Directory picker must select the parent folder'}
if($directories.INSTALLFOLDER[1] -ne 'INSTALLROOT' -or $directories.INSTALLFOLDER[2] -ne 'Tingyu'){throw 'Missing automatic Tingyu subdirectory'}
$events=@(Rows 'ControlEvent')
foreach($case in @(
    @('WelcomeDlg','Next','NewDialog','InstallDirDlg'),
    @('InstallDirDlg','Next','SetTargetPath','[WIXUI_INSTALLDIR]'),
    @('InstallDirDlg','Next','NewDialog','VerifyReadyDlg'),
    @('InstallDirDlg','ChangeFolder','SpawnDialog','BrowseDlg'),
    @('BrowseDlg','OK','SetTargetPath','[_BrowseProperty]')
)){
    if(-not @($events | Where-Object {$_[0] -eq $case[0] -and $_[1] -eq $case[1] -and $_[2] -eq $case[2] -and $_[3] -eq $case[3]}).Count){throw ('Missing installer navigation: '+($case -join ', '))}
}
$registry=@(Rows 'Registry')
if(-not @($registry | Where-Object {$_[2] -eq 'Software\MuyuMusic' -and $_[3] -eq 'InstallDir' -and $_[4] -eq '[INSTALLFOLDER]'}).Count){throw 'Install directory is not persisted'}
if(-not @($events | Where-Object {$_[0] -eq 'WelcomeDlg' -and $_[3] -eq 'VerifyReadyDlg' -and $_[4] -match 'MUYU_EXISTING_INSTALL'}).Count){throw 'Missing original-location upgrade flow'}
foreach($sequenceName in 'InstallUISequence','InstallExecuteSequence'){
    $sequence=@{};foreach($row in (Rows $sequenceName)){$sequence[$row[0]]=[int]$row[2]}
    if(-not $sequence.ContainsKey('DetectTingyuInstall') -or -not $sequence.ContainsKey('RestoreTingyuInstallPath') -or $sequence.DetectTingyuInstall -le $sequence.AppSearch -or $sequence.RestoreTingyuInstallPath -le $sequence.CostFinalize){throw ('Invalid upgrade path sequence: '+$sequenceName)}
}
foreach($file in (Rows 'File')){
    $name=($file[2] -split '\|')[-1]
    if($name -match '\.pdb$' -or $name -in @('BUILD_INFO.json','VERIFICATION.json','IMPLEMENTATION_STATUS.md','UI_RENDERING.md','UI_REQUIREMENTS.md','portable.marker')){throw ('Development file in installer: '+$name)}
}
& {
    $cleanup=@(Rows 'CustomAction' | Where-Object {$_[0] -eq 'CleanupTingyuData'})
    if($cleanup.Count -ne 1 -or [int]$cleanup[0][1] -ne 1025 -or $cleanup[0][3] -ne 'CleanupTingyuData'){throw 'Cleanup must be an impersonated deferred action'}
    $cleanupSequence=@(Rows 'InstallExecuteSequence' | Where-Object {$_[0] -eq 'CleanupTingyuData'})
    if($cleanupSequence.Count -ne 1 -or $cleanupSequence[0][1] -ne 'Installed AND REMOVE~="ALL" AND NOT UPGRADINGPRODUCTCODE'){throw 'Cleanup must run only on explicit uninstall, never upgrades'}
}
[pscustomobject]@{package=(Resolve-Path -LiteralPath $Path).Path;directory_selection=$true;automatic_subdirectory='Tingyu';dialog_navigation=$true;previous_directory_persisted=$true;upgrade_path_sequence_verified=$true;development_files_excluded=$true;rollback_backups_disabled=$true;ui_automation_used=$false;actual_installation_tested=$false;sha256=(Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash}
