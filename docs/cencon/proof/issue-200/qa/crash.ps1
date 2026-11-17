param([string]$Exe, [string]$Bundle, [bool]$NoWindow, [string]$Label)
# Launch the given AgentEyesApp exe with --tray and the given DOTNET_BUNDLE_EXTRACT_BASE_DIR,
# capture stderr/stdout, exit code, and any .NET Runtime / Application Error events.
$t0 = Get-Date
$psi = New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName = $Exe
$psi.Arguments = '--tray'
$psi.UseShellExecute = $false
$psi.CreateNoWindow = $NoWindow
$psi.RedirectStandardError = $true
$psi.RedirectStandardOutput = $true
$psi.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = $Bundle
$p = [System.Diagnostics.Process]::Start($psi)
$errTask = $p.StandardError.ReadToEndAsync()
$outTask = $p.StandardOutput.ReadToEndAsync()
if (-not $p.WaitForExit(30000)) { "[$Label] STILL RUNNING after 30s pid=$($p.Id) - killing it (it is our own scratch pid)"; $p.Kill() ; $p.WaitForExit() }
$err = $errTask.Result; $out = $outTask.Result
"=== [$Label] exe=$Exe bundle=$Bundle CreateNoWindow=$NoWindow"
"pid=$($p.Id) exit=0x{0:X8}" -f $p.ExitCode
$lines = ($err -split "`n")
"stderr lines=$($lines.Count); first 25 (distinct-collapsed):"
$lines | Select-Object -First 25
if ($lines.Count -gt 25) { "... last 5:"; $lines | Select-Object -Last 5 }
"stdout: $($out.Trim())"
Start-Sleep -Milliseconds 1500
"--- events since start"
Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=$t0} -ErrorAction SilentlyContinue |
  Where-Object { $_.ProviderName -in '.NET Runtime','Application Error' -and $_.Message -match 'AgentEyesApp' } |
  ForEach-Object { "$($_.TimeCreated.ToString('HH:mm:ss')) id=$($_.Id) $($_.ProviderName): " + (($_.Message -split "`n" | Select-Object -First 8) -join ' | ') }
"bundle dir contents:"
$bd = $Bundle.Replace('/','\')
if (Test-Path $bd) { Get-ChildItem $bd -Recurse -File | ForEach-Object { "  $($_.FullName.Substring($bd.Length)) $($_.Length)" } } else { "  (none)" }
