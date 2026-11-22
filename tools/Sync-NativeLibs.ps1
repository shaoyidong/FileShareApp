<#
.SYNOPSIS
    比对「发布产物里的原生库」与「项目 runtimes/<rid>/native 里参与嵌入的那一份」。

.DESCRIPTION
    应用的原生库是以嵌入资源形式打进 exe 的，源头是 runtimes/<rid>/native/；
    而 `dotnet publish` 会同时把 NuGet 包里的同名原生库复制到发布目录。
    两者来自不同地方（前者是仓库里的副本，后者随包版本走），很容易悄悄漂移
    —— 之前就出现过发布产物里 SQLite 是 3.53.4、native 目录里还是 3.50.4 的情况。

    本脚本在发布后做一次哈希比对：
      - 默认只报告差异（构建日志里能看到 [native-sync] 开头的行）；
      - 加 -Sync 时直接用发布产物覆盖 native 目录，实现自动同步。

.PARAMETER Rid
    运行时标识符，例如 win-x64 / win-x86 / linux-x64（仅用于提示信息）。

.PARAMETER PublishDir
    发布输出目录。

.PARAMETER NativeDir
    仓库里的原生库目录，例如 FileShare.Desktop\runtimes\win-x64\native。

.PARAMETER Sync
    发现差异时用发布产物覆盖 NativeDir 中的同名文件。

.PARAMETER Strict
    存在差异且未指定 -Sync 时以退出码 1 结束（默认不失败，避免影响正常发布）。

.EXAMPLE
    .\Sync-NativeLibs.ps1 -Rid win-x64 `
        -PublishDir ..\FileShare.Desktop\bin\Release\net10.0\publish\win-x64 `
        -NativeDir ..\FileShare.Desktop\runtimes\win-x64\native
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Rid,
    [Parameter(Mandatory = $true)][string]$PublishDir,
    [Parameter(Mandatory = $true)][string]$NativeDir,
    [switch]$Sync,
    [switch]$Strict
)

$ErrorActionPreference = 'Stop'

function Write-Line([string]$Text) {
    Write-Output "[native-sync] $Text"
}

# 相对路径按当前工作目录解析，方便手工调用
function Resolve-FullPath([string]$Path) {
    if ([System.IO.Path]::IsPathRooted($Path)) {
        return [System.IO.Path]::GetFullPath($Path)
    }
    return [System.IO.Path]::GetFullPath((Join-Path (Get-Location).Path $Path))
}

$PublishDir = Resolve-FullPath $PublishDir
$NativeDir = Resolve-FullPath $NativeDir

if (-not (Test-Path -LiteralPath $PublishDir)) {
    Write-Line "publish dir not found, skipped ($Rid): $PublishDir"
    exit 0
}
if (-not (Test-Path -LiteralPath $NativeDir)) {
    Write-Line "native dir not found, skipped ($Rid): $NativeDir"
    exit 0
}

Write-Line "checking $Rid"
Write-Line "  publish: $PublishDir"
Write-Line "  native : $NativeDir"

$drift = @()
$same = 0
$missing = @()

# 只看真正的原生库，忽略编辑器/备份留下的杂项文件
$nativeExtensions = @('.dll', '.so', '.dylib')
$skipSuffixes = @('.bak', '.old', '.orig', '.tmp', '.pdb')

foreach ($file in (Get-ChildItem -LiteralPath $NativeDir -File)) {
    if ($nativeExtensions -notcontains $file.Extension.ToLowerInvariant()) { continue }
    if ($skipSuffixes | Where-Object { $file.Name.ToLowerInvariant().EndsWith($_) }) { continue }

    $published = Join-Path $PublishDir $file.Name
    if (-not (Test-Path -LiteralPath $published)) {
        $missing += $file.Name
        continue
    }

    $nativeHash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    $publishHash = (Get-FileHash -LiteralPath $published -Algorithm SHA256).Hash

    if ($nativeHash -eq $publishHash) {
        $same++
        continue
    }

    $drift += [pscustomobject]@{
        Name          = $file.Name
        NativeBytes   = $file.Length
        PublishBytes  = (Get-Item -LiteralPath $published).Length
        Source        = $published
        Destination   = $file.FullName
    }
}

foreach ($name in $missing) {
    Write-Line "  not produced by publish (kept as is): $name"
}

if ($drift.Count -eq 0) {
    Write-Line "  OK - $same file(s) match"
    exit 0
}

foreach ($item in $drift) {
    Write-Line ("  DIFF {0}  native={1} bytes  publish={2} bytes" -f `
        $item.Name, $item.NativeBytes, $item.PublishBytes)
}

if ($Sync) {
    foreach ($item in $drift) {
        Copy-Item -LiteralPath $item.Source -Destination $item.Destination -Force
        Write-Line ("  SYNCED {0}" -f $item.Name)
    }
    Write-Line "  native dir updated - remember to commit it"
    exit 0
}

Write-Line ""
Write-Line "  Native libraries in '$NativeDir' are STALE."
Write-Line "  Re-run publish with -p:SyncNativeLibs=true to update them automatically, or run:"
Write-Line ("    .\tools\Sync-NativeLibs.ps1 -Rid {0} -PublishDir `"{1}`" -NativeDir `"{2}`" -Sync" -f `
    $Rid, $PublishDir, $NativeDir)

if ($Strict) { exit 1 }
exit 0
