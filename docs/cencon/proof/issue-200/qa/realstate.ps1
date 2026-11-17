# Fingerprint of everything the finalizer writes, plus the real app pid/start - one line each.
$sh = New-Object -ComObject WScript.Shell
foreach ($l in @("$env:APPDATA\Microsoft\Windows\Start Menu\Programs\AgentEyes.lnk", "$env:USERPROFILE\Desktop\AgentEyes.lnk")) {
  if (Test-Path $l) { $i = Get-Item $l; "LNK $($i.Name) [$((Split-Path $l -Parent) -replace '.*\\','')] target=$($sh.CreateShortcut($l).TargetPath) mtime=$($i.LastWriteTime.ToString('HH:mm:ss.fff'))" } else { "LNK $l MISSING" }
}
$u = Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\AgentEyes' -ErrorAction SilentlyContinue
"UNINSTALL ver=$($u.DisplayVersion) loc=$($u.InstallLocation) str=$($u.UninstallString)"
"RUNKEY " + (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -ErrorAction SilentlyContinue).AgentEyes
$e = Get-ItemProperty 'HKCU:\Environment'
"BUNDLEVAR $($e.DOTNET_BUNDLE_EXTRACT_BASE_DIR)"
"PATH-AE " + ((($e.Path -split ';') | Where-Object { $_ -match 'AgentEyes' }) -join ' ; ')
Get-CimInstance Win32_Process -Filter "Name='AgentEyesApp.exe'" | ForEach-Object { "APP pid=$($_.ProcessId) started=$($_.CreationDate.ToString('HH:mm:ss')) exe=$($_.ExecutablePath)" }
