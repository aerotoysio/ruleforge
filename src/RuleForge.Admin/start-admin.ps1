# RuleForge.Admin — local dev server on http://localhost:4310
# Companion to C:\Tools\DocumentForge\start-dfdb.ps1 (DocumentForge must be up on :4300).
# Builds if needed, then runs detached from the calling shell.
$proj = Join-Path $PSScriptRoot "RuleForge.Admin.csproj"
dotnet build $proj --nologo -v q
if (-not $?) { throw "build failed" }
$exe = Join-Path $PSScriptRoot "bin\Debug\net9.0\RuleForge.Admin.exe"
# Windows PowerShell 5.1 has no Start-Process -Environment; the child inherits ours.
$env:ASPNETCORE_URLS = "http://localhost:4310"
$env:ASPNETCORE_ENVIRONMENT = "Development"
Start-Process -FilePath $exe -WorkingDirectory $PSScriptRoot -WindowStyle Hidden
Write-Host "RuleForge.Admin starting on http://localhost:4310"
