param([string]$mode, [string]$name)
$S = "C:\Users\User\AppData\Local\Temp\claude\C--Users-User-Documents-microsoft-ui-xaml\71306581-61c4-4cd6-8ff8-55a6d60ec078\scratchpad"
$out = "$S\runs\etw_$name"; Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue; New-Item -ItemType Directory $out | Out-Null
logman stop cdxaml -ets 2>$null | Out-Null
logman start cdxaml -p "{531A35AB-63CE-4BCF-AA98-F88C7A89E455}" 0xFFFFFFFFFFFFFFFF 0xFF -o "$out\xaml.etl" -ets | Out-Null
$app = "$S\CdTest\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\CdTest.exe"
$p = Start-Process $app -ArgumentList "$mode --auto $out\applog.csv" -PassThru
$p.WaitForExit(30000) | Out-Null
logman stop cdxaml -ets | Out-Null
"pid=$($p.Id)" | Set-Content "$out\pid.txt"
tracerpt "$out\xaml.etl" -o "$out\xaml.xml" -of XML -y | Out-Null
"done $out pid=$($p.Id)"
