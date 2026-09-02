$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Get-ChildItem -Recurse 'D:\Code\NitroGateway\tests\NitroGateway.UnitTests' -Filter '*.cs' -File |
  Where-Object { $_.Name -match 'Subscription|Reliable|OpcUa|Pipeline|DeviceCollector|Collector' } |
  Select-Object -ExpandProperty Name
Write-Output '=====IntegrationTests====='
Get-ChildItem -Recurse 'D:\Code\NitroGateway\tests\NitroGateway.IntegrationTests' -Filter '*.cs' -File |
  Select-Object -ExpandProperty Name
