param([string]$dir)
$pidv = [int]((Get-Content "$dir\pid.txt") -replace 'pid=','')
[xml]$x = Get-Content "$dir\xaml.xml" -Raw
$ns = New-Object Xml.XmlNamespaceManager($x.NameTable); $ns.AddNamespace("e","http://schemas.microsoft.com/win/2004/08/events/event")
$names = @{ 84='BeginStoryboard'; 87='StopStoryboard'; 88='EndStoryboard'; 511='CancelTransitions'; 59='RealizeTransition.Begin'; 60='RealizeTransition.End'; 57='ProcessLayoutForTransition.Begin' }
$rows = foreach ($ev in $x.SelectNodes("//e:Event", $ns)) {
  $sys = $ev.System
  if ([int]$sys.Execution.ProcessID -ne $pidv) { continue }
  $id = [int]$sys.EventID
  if (-not $names.ContainsKey($id)) { continue }
  $data = ($ev.EventData.Data | % { "$($_.Name)=0x{0:X}" -f [UInt64]$_.'#text' }) -join ' '
  [pscustomobject]@{ T=[datetime]::Parse($sys.TimeCreated.SystemTime).ToUniversalTime(); Id=$id; Name=$names[$id]; Data=$data }
}
$rows = $rows | sort T
$t0 = $rows[0].T
$rows | % { "{0,9:F1}  {1,-34} {2}" -f ($_.T - $t0).TotalMilliseconds, $_.Name, $_.Data }
