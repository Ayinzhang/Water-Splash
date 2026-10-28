param([Parameter(Mandatory=$true)][string]$CommandsJson)
$ErrorActionPreference='Stop'
$client=[Net.Sockets.TcpClient]::new('127.0.0.1',6400)
$stream=$client.GetStream(); $stream.ReadTimeout=25000; $stream.WriteTimeout=25000
function Read-Exact([int]$count) { $b=[byte[]]::new($count);$o=0;while($o -lt $count){$n=$stream.Read($b,$o,$count-$o);if($n -eq 0){throw 'Unity disconnected'};$o+=$n};return ,$b }
try { while(($b=$stream.ReadByte()) -ne 10){if($b -lt 0){throw 'No handshake'}};foreach($c in ($CommandsJson|ConvertFrom-Json)){$d=[Text.Encoding]::UTF8.GetBytes(($c|ConvertTo-Json -Compress -Depth 24));$h=[BitConverter]::GetBytes([uint64]$d.Length);if([BitConverter]::IsLittleEndian){[Array]::Reverse($h)};$stream.Write($h,0,8);$stream.Write($d,0,$d.Length);$h=Read-Exact 8;if([BitConverter]::IsLittleEndian){[Array]::Reverse($h)};$len=[BitConverter]::ToUInt64($h,0);if($len -gt 25000000){throw 'Unexpected response size'};$body=Read-Exact ([int]$len);Write-Output ($c.type+': '+[Text.Encoding]::UTF8.GetString($body))}} finally {$client.Close()}
