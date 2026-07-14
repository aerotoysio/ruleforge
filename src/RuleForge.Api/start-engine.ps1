# RuleForge.Api — local dev engine on http://localhost:4320, serving the rules
# Policy Studio publishes (DocumentForge source, policystudio. collection prefix,
# env "dev"). Companion to ..\RuleForge.Admin\start-admin.ps1.
# No RULEFORGE_API_KEY set → open access (local dev only).
$proj = Join-Path $PSScriptRoot "RuleForge.Api.csproj"
dotnet build $proj --nologo -v q
if (-not $?) { throw "build failed" }
$exe = Join-Path $PSScriptRoot "bin\Debug\net9.0\RuleForge.Api.exe"
# Windows PowerShell 5.1 has no Start-Process -Environment; the child inherits ours.
$env:ASPNETCORE_URLS = "http://localhost:4320"
$env:RULEFORGE_RULE_SOURCE = "df"
$env:RULEFORGE_DF_BASE_URL = "http://localhost:4300"
$env:RULEFORGE_DF_API_KEY = "dev"
$env:RULEFORGE_ENV = "dev"
$env:RULEFORGE_COLLECTION_PREFIX = "policystudio."
Start-Process -FilePath $exe -WorkingDirectory $PSScriptRoot -WindowStyle Hidden
Write-Host "RuleForge.Api starting on http://localhost:4320 (env dev, prefix policystudio.)"
