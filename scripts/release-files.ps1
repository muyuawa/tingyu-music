function Test-ReleaseDirectory([string]$Name){
    # Windows App SDK satellite resources use BCP-47 culture directory names.
    # Keep every Chinese/English variant, and leave non-language folders alone.
    if($Name -notmatch '^[a-z]{2,3}-(?:[a-z]{4}-)?(?:[a-z]{2}|[0-9]{3})(?:-[a-z0-9]{5,8})?$'){return $true}
    return $Name -match '^(zh|en)-'
}
function Test-ReleaseFile([IO.FileInfo]$File,[string]$Root){
    $relative=$File.FullName.Substring($Root.TrimEnd('\').Length+1).Replace('\','/')
    $segments=$relative.Split('/')
    if($segments.Length -gt 1 -and -not (Test-ReleaseDirectory $segments[0])){return $false}
    if($relative -eq 'data' -or $relative.StartsWith('data/',[StringComparison]::OrdinalIgnoreCase)){return $false}
    if($File.Extension -eq '.pdb'){return $false}
    if($relative -in @('BUILD_INFO.json','VERIFICATION.json','PACKAGE_MANIFEST.json','IMPLEMENTATION_STATUS.md','UI_RENDERING.md','UI_REQUIREMENTS.md')){return $false}
    return $true
}
