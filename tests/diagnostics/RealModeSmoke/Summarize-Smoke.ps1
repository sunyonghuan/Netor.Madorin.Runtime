param([string]$Root = 'artifacts/real-smoke-20260915')
$ErrorActionPreference = 'Stop'
$utf8 = [Text.UTF8Encoding]::new($false)
function Read-Shared([string]$Path) {
    $stream = [IO.FileStream]::new($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::ReadWrite)
    $reader = [IO.StreamReader]::new($stream, [Text.Encoding]::UTF8)
    try { while (!$reader.EndOfStream) { $reader.ReadLine() } }
    finally { $reader.Dispose() }
}
$all = [Collections.Generic.List[object]]::new()
foreach ($modeDir in (Get-ChildItem -LiteralPath $Root -Directory)) {
    $runs = @(Get-ChildItem -LiteralPath $modeDir.FullName -Filter '*.run.json' -File)
    if ($runs.Count -eq 0) { continue }
    $report = [Text.StringBuilder]::new()
    [void]$report.AppendLine('# ' + $modeDir.Name + ' 实际输入与输出')
    [void]$report.AppendLine('')
    foreach ($file in $runs) {
        $label = $file.Name.Replace('.run.json', '')
        $meta = [IO.File]::ReadAllText($file.FullName) | ConvertFrom-Json
        $inputPath = Join-Path $modeDir.FullName ($label + '.input.json')
        $inputData = [IO.File]::ReadAllText($inputPath) | ConvertFrom-Json
        $eventPath = Join-Path $modeDir.FullName ($meta.runId + '.events.jsonl')
        if (![IO.File]::Exists($eventPath)) { continue }
        $events = @(Read-Shared $eventPath | ForEach-Object { $_ | ConvertFrom-Json })
        if ($events.Count -eq 0) { continue }
        $invocations = @{}
        $textByInvocation = [ordered]@{}
        $tools = [Collections.Generic.List[object]]::new()
        $callArgs = @{}
        foreach ($event in $events) {
            $p = $event.payload
            if ($event.messageType -eq 'invocation.started') { $invocations[$p.invocationId] = $p.snapshot }
            if ($event.messageType -eq 'output.delta') {
                if (!$textByInvocation.Contains($p.invocationId)) { $textByInvocation[$p.invocationId] = [Text.StringBuilder]::new() }
                [void]$textByInvocation[$p.invocationId].Append($p.delta)
            }
            if ($event.messageType -eq 'tool.call.completed') {
                if ($null -ne $p.argumentsJson) { $callArgs[$p.callId] = $p.argumentsJson }
                if ($null -ne $p.success) {
                    $tools.Add([pscustomobject]@{ gsn=$event.gsn; timestamp=$event.timestamp; callId=$p.callId;
                        toolId=$p.toolId; success=$p.success; arguments=$callArgs[$p.callId]; output=$p.output; error=$p.error })
                }
            }
            if ($event.messageType -eq 'tool.call.delta' -and $p.callId) {
                $fragment = if ($null -ne $p.argumentsDelta) { $p.argumentsDelta } else { $p.delta }
                $callArgs[$p.callId] = [string]$callArgs[$p.callId] + [string]$fragment
            }
        }
        [void]$report.AppendLine('## ' + $label)
        [void]$report.AppendLine('')
        [void]$report.AppendLine('Run：`' + $meta.runId + '`；最后事件：`' + $events[-1].messageType + '`。')
        $timeoutPath = Join-Path $modeDir.FullName ($label + '.timeout.json')
        if ([IO.File]::Exists($timeoutPath)) {
            [void]$report.AppendLine('测试宿主等待终态超时，已发出取消请求；超时不等于运行已取消。')
        }
        [void]$report.AppendLine('')
        [void]$report.AppendLine('输入：')
        [void]$report.AppendLine('```text')
        [void]$report.AppendLine($inputData.input)
        [void]$report.AppendLine('```')
        $assistant = [Text.StringBuilder]::new()
        foreach ($id in $textByInvocation.Keys) {
            $snapshot = $invocations[$id]
            $role = "$($snapshot.agentId) / $($snapshot.role) / $($snapshot.workStepId)"
            [void]$report.AppendLine('')
            [void]$report.AppendLine('实际输出（' + $role + '）：')
            [void]$report.AppendLine('````text')
            [void]$report.AppendLine($textByInvocation[$id].ToString())
            [void]$report.AppendLine('````')
            [void]$assistant.AppendLine('[' + $role + ']')
            [void]$assistant.AppendLine($textByInvocation[$id].ToString())
        }
        [void]$report.AppendLine('')
        [void]$report.AppendLine('真实工具结果：')
        [void]$report.AppendLine('```json')
        [void]$report.AppendLine((ConvertTo-Json -InputObject @($tools.ToArray()) -Depth 20))
        [void]$report.AppendLine('```')
        $keyEvents = @($events | Where-Object { $_.messageType -in @('work.plan.updated','invocation.failed','run.failed','run.cancelled','meeting.hitl.request','meeting.hitl.response') })
        [void]$report.AppendLine('')
        [void]$report.AppendLine('流程关键事件：')
        [void]$report.AppendLine('```json')
        [void]$report.AppendLine((ConvertTo-Json -InputObject $keyEvents -Depth 20))
        [void]$report.AppendLine('```')
        [IO.File]::WriteAllText((Join-Path $modeDir.FullName ($label + '.assistant.txt')), $assistant.ToString(), $utf8)
        $all.Add([pscustomobject]@{ mode=$modeDir.Name; label=$label; runId=$meta.runId;
            started=$events[0].timestamp; ended=$events[-1].timestamp; lastEvent=$events[-1].messageType;
            toolCount=$tools.Count; failedTools=@($tools | Where-Object { !$_.success }).Count;
            invocations=@($invocations.Values); output=$assistant.ToString() })
    }
    foreach ($errorFile in (Get-ChildItem -LiteralPath $modeDir.FullName -Filter '*.submit-error.json' -File)) {
        $label = $errorFile.Name.Replace('.submit-error.json', '')
        $inputData = [IO.File]::ReadAllText((Join-Path $modeDir.FullName ($label + '.input.json'))) | ConvertFrom-Json
        [void]$report.AppendLine('## ' + $label + '（提交被拒绝）')
        [void]$report.AppendLine('')
        [void]$report.AppendLine('```text')
        [void]$report.AppendLine($inputData.input)
        [void]$report.AppendLine('```')
        [void]$report.AppendLine('```json')
        [void]$report.AppendLine([IO.File]::ReadAllText($errorFile.FullName))
        [void]$report.AppendLine('```')
    }
    [IO.File]::WriteAllText((Join-Path $modeDir.FullName 'transcript.md'), $report.ToString(), $utf8)
}
[IO.File]::WriteAllText((Join-Path $Root 'summary.json'), (ConvertTo-Json -InputObject @($all.ToArray()) -Depth 20), $utf8)
$all | Select-Object mode,label,lastEvent,toolCount,failedTools | Format-Table -AutoSize
