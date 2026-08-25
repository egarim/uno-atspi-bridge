$ErrorActionPreference='Continue'
$env:PYTHONUTF8='1'
$key = [Environment]::GetEnvironmentVariable('deep_seek_api','Machine')
$env:OPENAI_API_KEY=$key; $env:OPENAI_BASE_URL='https://api.deepseek.com'
$env:VLLM_API_KEY=$key;   $env:VLLM_BASE_URL='https://api.deepseek.com'
$env:Path = "$env:USERPROFILE\platform-tools;$env:Path"
# pre-start adb daemon quietly so its messages never pollute MCP stdout
& "$env:USERPROFILE\platform-tools\adb.exe" start-server *> $null
$uvCmd = Get-Command uv -ErrorAction SilentlyContinue
if ($uvCmd) { $uv = $uvCmd.Source } else { $uv = Join-Path $env:USERPROFILE '.local\bin\uv.exe' }
# exec the stdio MCP server — ONLY its stdout reaches the client
& $uv tool run --from ghost-in-the-droid android-agent-mcp
