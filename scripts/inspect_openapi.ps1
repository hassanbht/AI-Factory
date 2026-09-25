$json = Get-Content 'C:\AI-Factory\scripts\openhands-openapi.json' -Raw | ConvertFrom-Json
$list = @()
foreach ($prop in $json.paths.PSObject.Properties) {
    if ($prop.Name -like "*conv*" -or $prop.Name -like "*task*" -or $prop.Name -like "*run*" -or $prop.Name -like "*event*" -or $prop.Name -like "*stop*" -or $prop.Name -like "*cancel*") {
        $path = $prop.Name
        $methods = ($prop.Value.PSObject.Properties | ForEach-Object { $_.Name.ToUpper() }) -join ', '
        $summary = ($prop.Value.PSObject.Properties | Select-Object -First 1).Value.summary
        $list += [PSCustomObject]@{ Path = $path; Methods = $methods; Summary = $summary }
    }
}
$list | Format-Table -AutoSize
