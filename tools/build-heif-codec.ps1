[CmdletBinding()]
param(
    [string]$OutputDirectory,
    [string]$BuildDirectory,
    [ValidateRange(1, 16)][int]$Parallel = 2
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (-not $IsWindows) { throw 'This script builds the Windows x64 HEIC worker. See native/heif-worker/README.md for portable CMake commands.' }
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$workerSource = Join-Path $repositoryRoot 'native/heif-worker'
$vendor = Join-Path $workerSource 'vendor'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repositoryRoot 'artifacts/heif-codec/win-x64' }
if (-not $BuildDirectory) { $BuildDirectory = Join-Path $repositoryRoot 'artifacts/heif-codec/build-win-x64' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)

function Invoke-Checked([string]$Executable, [string[]]$Arguments) {
    & $Executable @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Native build command failed ($LASTEXITCODE): $Executable" }
}

# Discover the installed toolchain rather than binding CI to a VS year/version.
$vsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (-not (Test-Path -LiteralPath $vsWhere)) { throw 'Visual Studio Build Tools with the C++ x64 workload are required.' }
$visualStudio = & $vsWhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if ($LASTEXITCODE -ne 0 -or -not $visualStudio) { throw 'No Visual Studio C++ x64 toolchain was found.' }
$visualStudio = [string]($visualStudio | Select-Object -First 1)
$developerCommand = Join-Path $visualStudio 'Common7/Tools/VsDevCmd.bat'
$environmentLines = & $env:ComSpec /d /s /c "`"$developerCommand`" -no_logo -arch=x64 -host_arch=x64 && set"
if ($LASTEXITCODE -ne 0) { throw 'Failed to initialize the Visual Studio x64 developer environment.' }
foreach ($line in $environmentLines) {
    if ($line -match '^([^=]+)=(.*)$') {
        [Environment]::SetEnvironmentVariable($Matches[1], $Matches[2], 'Process')
    }
}

$cmakeCommand = Get-Command cmake.exe -ErrorAction SilentlyContinue
$cmake = if ($cmakeCommand) { $cmakeCommand.Source } else { Join-Path $visualStudio 'Common7/IDE/CommonExtensions/Microsoft/CMake/CMake/bin/cmake.exe' }
if (-not (Test-Path -LiteralPath $cmake)) { throw 'CMake is required (Visual Studio CMake tools or PATH).' }
$ninjaCommand = Get-Command ninja.exe -ErrorAction SilentlyContinue
$ninja = if ($ninjaCommand) { $ninjaCommand.Source } else { Join-Path $visualStudio 'Common7/IDE/CommonExtensions/Microsoft/CMake/Ninja/ninja.exe' }
if (-not (Test-Path -LiteralPath $ninja)) { throw 'Ninja is required (Visual Studio CMake tools or PATH).' }

New-Item -ItemType Directory -Path $BuildDirectory, $OutputDirectory -Force | Out-Null
$sources = Join-Path $BuildDirectory 'sources'
$install = Join-Path $BuildDirectory 'install'
New-Item -ItemType Directory -Path $sources, $install -Force | Out-Null
$manifest = Get-Content -LiteralPath (Join-Path $vendor 'sources.json') -Raw | ConvertFrom-Json
foreach ($source in $manifest) {
    $archive = Join-Path $vendor $source.file
    $actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($actual -ne $source.sha256) { throw "Native source checksum mismatch: $($source.file)" }
    # Hash-verified official release archives contain only their named source root.
    Invoke-Checked 'tar.exe' @('-xzf', $archive, '-C', $sources)
}

$common = @('-G', 'Ninja', "-DCMAKE_MAKE_PROGRAM=$ninja", '-DCMAKE_BUILD_TYPE=Release',
    '-DCMAKE_MSVC_RUNTIME_LIBRARY=MultiThreaded', '-DCMAKE_POLICY_DEFAULT_CMP0091=NEW',
    '-DCMAKE_POLICY_VERSION_MINIMUM=3.5', "-DCMAKE_INSTALL_PREFIX=$install", '-DCMAKE_INSTALL_LIBDIR=lib',
    '-DBUILD_SHARED_LIBS=ON', '-DCMAKE_C_FLAGS=/guard:cf', '-DCMAKE_CXX_FLAGS=/guard:cf')
$de265Build = Join-Path $BuildDirectory 'libde265'
Invoke-Checked $cmake (@('-S', (Join-Path $sources 'libde265-1.1.3'), '-B', $de265Build) + $common + @(
    '-DENABLE_DECODER=OFF', '-DENABLE_ENCODER=OFF', '-DENABLE_SDL=OFF', '-DENABLE_SHERLOCK265=OFF',
    '-DENABLE_INTERNAL_DEVELOPMENT_TOOLS=OFF', '-DWITH_FUZZERS=OFF'))
Invoke-Checked $cmake @('--build', $de265Build, '--parallel', "$Parallel")
Invoke-Checked $cmake @('--install', $de265Build)

$heifBuild = Join-Path $BuildDirectory 'libheif'
$heifOptions = @('-DWITH_LIBDE265=ON', '-DWITH_LIBDE265_PLUGIN=OFF', '-DENABLE_PLUGIN_LOADING=OFF',
    '-DWITH_X265=OFF', '-DWITH_KVAZAAR=OFF', '-DWITH_X264=OFF', '-DWITH_OpenH264_DECODER=OFF',
    '-DWITH_AOM_DECODER=OFF', '-DWITH_AOM_ENCODER=OFF', '-DWITH_DAV1D=OFF', '-DWITH_SvtEnc=OFF',
    '-DWITH_RAV1E=OFF', '-DWITH_JPEG_DECODER=OFF', '-DWITH_JPEG_ENCODER=OFF',
    '-DWITH_OpenJPEG_DECODER=OFF', '-DWITH_OpenJPEG_ENCODER=OFF', '-DWITH_OPENJPH_ENCODER=OFF',
    '-DWITH_FFMPEG_DECODER=OFF', '-DWITH_VVDEC=OFF', '-DWITH_VVENC=OFF', '-DWITH_UVG266=OFF',
    '-DWITH_WEBCODECS=OFF', '-DWITH_UNCOMPRESSED_CODEC=OFF', '-DWITH_HEADER_COMPRESSION=OFF',
    '-DWITH_LIBSHARPYUV=OFF', '-DWITH_EXAMPLES=OFF', '-DWITH_GDK_PIXBUF=OFF',
    '-DBUILD_TESTING=OFF', '-DBUILD_DEVELOPMENT_TOOLS=OFF', '-DENABLE_EXPERIMENTAL_FEATURES=OFF',
    '-DENABLE_MULTITHREADING_SUPPORT=ON', '-DENABLE_PARALLEL_TILE_DECODING=OFF')
Invoke-Checked $cmake (@('-S', (Join-Path $sources 'libheif-1.23.5'), '-B', $heifBuild,
    "-DCMAKE_PREFIX_PATH=$install") + $common + $heifOptions)
Invoke-Checked $cmake @('--build', $heifBuild, '--parallel', "$Parallel")
Invoke-Checked $cmake @('--install', $heifBuild)

$workerBuild = Join-Path $BuildDirectory 'worker'
Invoke-Checked $cmake (@('-S', $workerSource, '-B', $workerBuild, "-DCMAKE_PREFIX_PATH=$install") + $common)
Invoke-Checked $cmake @('--build', $workerBuild, '--parallel', "$Parallel")
Copy-Item -LiteralPath (Join-Path $workerBuild 'PhotoShelf.HeifWorker.exe') -Destination $OutputDirectory -Force
foreach ($dll in @('heif.dll', 'libde265.dll')) {
    $sourceDll = Join-Path $install "bin/$dll"
    if (-not (Test-Path -LiteralPath $sourceDll)) { throw "Required decoder DLL was not produced: $dll" }
    Copy-Item -LiteralPath $sourceDll -Destination $OutputDirectory -Force
}

# Distribute corresponding sources and the exact build recipe next to the
# replaceable LGPL DLLs, including the PhotoShelf helper and all license texts.
$sourceOutput = Join-Path $OutputDirectory 'sources'
$licenseOutput = Join-Path $OutputDirectory 'licenses'
$recipeOutput = Join-Path $sourceOutput 'PhotoShelf-HeifWorker'
New-Item -ItemType Directory -Path $sourceOutput, $licenseOutput, $recipeOutput -Force | Out-Null
foreach ($source in $manifest) {
    Copy-Item -LiteralPath (Join-Path $vendor $source.file) -Destination $sourceOutput -Force
    Copy-Item -LiteralPath (Join-Path $repositoryRoot "docs/licenses/$($source.name)-$($source.version)-COPYING.txt") -Destination $licenseOutput -Force
}
Copy-Item -LiteralPath (Join-Path $vendor 'sources.json') -Destination $sourceOutput -Force
foreach ($file in @('main.cpp', 'CMakeLists.txt', 'README.md', 'NOTICE.txt')) {
    Copy-Item -LiteralPath (Join-Path $workerSource $file) -Destination $recipeOutput -Force
}
Copy-Item -LiteralPath $PSCommandPath -Destination $recipeOutput -Force
Copy-Item -LiteralPath (Join-Path $workerSource 'NOTICE.txt') -Destination $licenseOutput -Force
Copy-Item -LiteralPath (Join-Path $workerSource 'README.md') -Destination $OutputDirectory -Force

$worker = Join-Path $OutputDirectory 'PhotoShelf.HeifWorker.exe'
$version = & $worker --version
if ($LASTEXITCODE -ne 0 -or $version -notmatch 'libheif 1\.23\.5; libde265 1\.1\.3') {
    throw "Native decoder version check failed: $version"
}
# Inspect the executable and both DLLs: all runtime dependencies must be OS DLLs
# or the two replaceable libraries. A /MD build would otherwise work on CI but
# silently require the Visual C++ redistributable on a user's computer.
foreach ($binary in @('PhotoShelf.HeifWorker.exe', 'heif.dll', 'libde265.dll')) {
    $dependencies = & dumpbin.exe /nologo /dependents (Join-Path $OutputDirectory $binary)
    if ($LASTEXITCODE -ne 0) { throw "Could not inspect native imports: $binary" }
    if (($dependencies -join "`n") -match '(?i)(vcruntime|msvcp|concrt|vcomp)\d.*\.dll') {
        throw "Unexpected Visual C++ redistributable dependency in $binary"
    }
    $dependencies | Set-Content -LiteralPath (Join-Path $OutputDirectory "$binary.imports.txt") -Encoding utf8
}
$version | Set-Content -LiteralPath (Join-Path $OutputDirectory 'VERSION.txt') -Encoding utf8
Write-Host "Bundled HEIC worker ready: $OutputDirectory"
