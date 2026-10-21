<#
  미리보기: Folderss 없이 플러그인만 사용자 화면에 띄워 직접 써 본다(tools/PluginPreview).
  - publish한 zip을 Folderss와 같은 방식(PluginLoadContext)으로 불러 CreateView()를 창에 담는다. 플러그인 설정 탭은 창 위 [플러그인 설정…].
  - 설정·접속·임시 저장은 작업 폴더의 plugin-data에 남아 다시 띄워도 이어진다. 사용 중인 Folderss의 데이터는 건드리지 않는다.
  - 테마를 주지 않으면 Folderss에서 쓰는 테마(%LOCALAPPDATA%\Folderss\theme.txt), 없으면 Black.

  사용: pwsh scripts/preview.ps1 [-Theme Black|Light|GitHub|Nord|Solarized|Dracula|Catppuccin] [-NoPublish] [-Fresh] [-Work <폴더>]
#>
param(
    [ValidateSet('', 'Black', 'Light', 'GitHub', 'Nord', 'Solarized', 'Dracula', 'Catppuccin')]
    [string]$Theme = '',
    [switch]$NoPublish,
    # 작업 폴더의 plugin-data(설정·접속·임시 저장)를 지우고 처음 상태로 띄운다
    [switch]$Fresh,
    [string]$Work = (Join-Path ([IO.Path]::GetTempPath()) 'folderss-preview')
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repo 'tools\PluginPreview'
$zip = Join-Path $repo 'src\MyPlugin\bin\Release\net8.0-windows\win-x64\MyPlugin.zip'

if (-not $Theme) {
    $folderssTheme = Join-Path $env:LOCALAPPDATA 'Folderss\theme.txt'
    $Theme = if (Test-Path $folderssTheme) { (Get-Content $folderssTheme -Raw).Trim() } else { 'Black' }
}

if (-not $NoPublish) {
    dotnet publish (Join-Path $repo 'src\MyPlugin') -c Release --nologo -v q | Out-Host
    if ($LASTEXITCODE -ne 0) { throw '플러그인 publish 실패' }
}
if (-not (Test-Path $zip)) { throw "플러그인 zip이 없습니다: $zip (-NoPublish 없이 다시 실행)" }
dotnet build $project -c Release --nologo -v q | Out-Host
if ($LASTEXITCODE -ne 0) { throw '미리보기 빌드 실패' }

$data = Join-Path $Work 'plugin-data'
if ($Fresh -and (Test-Path $data)) { Remove-Item $data -Recurse -Force }

$exe = Join-Path $project 'bin\Release\net8.0-windows\PluginPreview.exe'
$process = Start-Process -FilePath $exe -ArgumentList @("`"$zip`"", "`"$Work`"", $Theme) -PassThru
Write-Host "미리보기를 띄웠습니다 (pid $($process.Id), 테마 $Theme) — 데이터: $data"
