$ErrorActionPreference = 'SilentlyContinue'
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
Get-ChildItem 'D:\Code\NitroGateway' -Recurse -Filter '*.md' -File |
  Where-Object { $_.FullName -notmatch '\\(obj|bin|node_modules|dist)\\' } |
  Select-Object -ExpandProperty FullName
