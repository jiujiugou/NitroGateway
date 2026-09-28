param(
    [string]$BaseUrl = 'http://127.0.0.1:5100',
    [string]$Username = 'admin',
    [string]$Password = 'admin123',
    [string]$DeviceId = '',
    [string]$OutFile = 'opcua-tree.json',
    [int]$MaxDepth = 60
)

$ErrorActionPreference = 'Stop'

# 1. login -> JWT
$loginBody = @{ username = $Username; password = $Password } | ConvertTo-Json
$login = Invoke-RestMethod -Uri "$BaseUrl/api/auth/login" -Method Post -ContentType 'application/json' -Body $loginBody
if (-not $login.success) { throw "login failed: $($login.error.message)" }
$headers = @{ Authorization = "Bearer $($login.data.token)" }

# 2. auto-pick first OPC UA device if not specified
if ([string]::IsNullOrWhiteSpace($DeviceId)) {
    $devices = (Invoke-RestMethod -Uri "$BaseUrl/api/devices" -Headers $headers).data
    $ua = $devices | Where-Object { $_.protocol.name -eq 'OPC UA' } | Select-Object -First 1
    if (-not $ua) { throw "no OPC UA device found" }
    $DeviceId = $ua.id
    Write-Output "device: $($ua.name) [$DeviceId] $($ua.connection.endpoint)"
}

$script:variables = New-Object System.Collections.Generic.List[object]
$script:pending = @{}
$script:duplicateRefs = 0

# 3. recursive browse (pending guard breaks cycles, per-level dedupe drops repeated refs)
function Get-Node {
    param([string]$Parent, [string]$Path, [int]$Depth)
    if ($Depth -ge $MaxDepth) { return , @() }
    if ($script:pending.ContainsKey($Parent)) { return , @() }
    $script:pending[$Parent] = $true

    $encoded = [uri]::EscapeDataString($Parent)
    $resp = Invoke-RestMethod -Uri "$BaseUrl/api/devices/$DeviceId/browse?parent=$encoded" -Headers $headers
    if (-not $resp.success) { throw "browse failed: $($resp.error.message)" }

    $result = @()
    $seen = @{}
    foreach ($n in $resp.data) {
        if ($seen.ContainsKey($n.nodeId)) { $script:duplicateRefs++; continue }
        $seen[$n.nodeId] = $true

        $nodePath = if ([string]::IsNullOrEmpty($Path)) { $n.name } else { "$Path/$($n.name)" }
        $node = [ordered]@{
            nodeId     = $n.nodeId
            name       = $n.name
            typeName   = $n.typeName
            isVariable = $n.isVariable
            access     = $n.access
        }
        if ($n.isVariable) {
            $script:variables.Add([pscustomobject]@{
                    Path     = $nodePath
                    NodeId   = $n.nodeId
                    DataType = $n.typeName
                    Access   = $n.access
                })
        }
        else {
            $node.children = Get-Node -Parent $n.nodeId -Path $nodePath -Depth ($Depth + 1)
        }
        $result += , $node
    }

    $script:pending.Remove($Parent)
    return , $result
}

$tree = Get-Node -Parent '' -Path '' -Depth 0
$json = ConvertTo-Json -InputObject $tree -Depth 100
Set-Content -Path $OutFile -Value $json -Encoding UTF8

$csvFile = [System.IO.Path]::Combine(
    [System.IO.Path]::GetDirectoryName($OutFile),
    ([System.IO.Path]::GetFileNameWithoutExtension($OutFile) + '-vars.csv')
)
$script:variables | Sort-Object Path | Export-Csv -Path $csvFile -NoTypeInformation -Encoding UTF8

Write-Output "variables: $($script:variables.Count)  duplicate refs dropped: $($script:duplicateRefs)"
Write-Output "saved: $((Resolve-Path $OutFile).Path)"
Write-Output "saved: $csvFile"
