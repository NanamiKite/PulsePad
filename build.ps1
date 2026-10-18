$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) {
    $compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4 compiler not found.' }
$outputDirectory = Join-Path $root 'build'
New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
$outputExe = Join-Path $outputDirectory 'PulsePad.exe'
$framework = Split-Path -Parent $compiler
$wpf = Join-Path $framework 'WPF'
$assets = Join-Path $root 'assets'
& $compiler /nologo /target:exe /optimize+ /utf8output /codepage:65001 "/out:$outputDirectory\BuildIcon.exe" /reference:System.dll /reference:System.Core.dll /reference:System.Xml.dll /reference:System.Xml.Linq.dll "/reference:$framework\System.Xaml.dll" "/reference:$wpf\WindowsBase.dll" "/reference:$wpf\PresentationCore.dll" (Join-Path $root 'tools\BuildIcon.cs')
if ($LASTEXITCODE -ne 0) { throw 'Icon compiler build failed.' }
& (Join-Path $outputDirectory 'BuildIcon.exe') (Join-Path $assets 'rumble.svg') $assets
if ($LASTEXITCODE -ne 0) { throw 'Icon generation failed.' }
$vendor = Join-Path $root 'vendor\SDL'
$origin = Get-Content -LiteralPath (Join-Path $vendor 'origin.json') -Raw -Encoding UTF8 | ConvertFrom-Json
foreach ($file in $origin.files) {
    $dll = Join-Path $vendor ($file.architecture + '\SDL3.dll')
    if ((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.dllSha256) { throw "SDL checksum mismatch: $dll" }
    foreach ($destinationRoot in @($root, $outputDirectory, (Join-Path $root 'tests'))) {
        $nativeDirectory = Join-Path $destinationRoot ('native\' + $file.architecture)
        New-Item -ItemType Directory -Force -Path $nativeDirectory | Out-Null
        $destinationDll = Join-Path $nativeDirectory 'SDL3.dll'
        if (-not (Test-Path -LiteralPath $destinationDll) -or (Get-FileHash -LiteralPath $destinationDll -Algorithm SHA256).Hash.ToLowerInvariant() -ne $file.dllSha256) {
            Copy-Item -LiteralPath $dll -Destination $destinationDll -Force
        }
        Copy-Item -LiteralPath (Join-Path $vendor 'LICENSE.txt') -Destination (Join-Path $destinationRoot 'native\SDL-LICENSE.txt') -Force
    }
}
$source = @('AssemblyInfo.cs', 'Controller.cs', 'SdlNative.cs', 'SdlController.cs', 'Melody.cs', 'MidiImport.cs', 'AudioImport.cs', 'SongFiles.cs', 'RumbleEngine.cs', 'MainWindow.cs', 'Program.cs') | ForEach-Object { Join-Path $root "src\$_" }
& $compiler /nologo /target:winexe /platform:anycpu /optimize+ /utf8output /codepage:65001 "/out:$outputExe" "/win32icon:$assets\rumble.ico" "/resource:$assets\rumble.ico,XboxRumble.Icon" /reference:System.dll /reference:System.Core.dll "/reference:$framework\System.Xaml.dll" "/reference:$wpf\WindowsBase.dll" "/reference:$wpf\PresentationCore.dll" "/reference:$wpf\PresentationFramework.dll" "/resource:$root\src\MainWindow.xaml,XboxRumble.MainWindow.xaml" $source
if ($LASTEXITCODE -ne 0) { throw 'Application compilation failed.' }
$testDirectory = Join-Path $root 'tests'
& $compiler /nologo /target:exe /optimize+ /utf8output /codepage:65001 "/out:$testDirectory\RumbleTests.exe" /reference:System.dll /reference:System.Core.dll (Join-Path $root 'src\Controller.cs') (Join-Path $root 'src\Melody.cs') (Join-Path $root 'src\RumbleEngine.cs') (Join-Path $testDirectory 'RumbleTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Test compilation failed.' }
& (Join-Path $testDirectory 'RumbleTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
& $compiler /nologo /target:exe /optimize+ /utf8output /codepage:65001 "/out:$testDirectory\SongImportTests.exe" /reference:System.dll /reference:System.Core.dll (Join-Path $root 'src\Melody.cs') (Join-Path $root 'src\MidiImport.cs') (Join-Path $root 'src\AudioImport.cs') (Join-Path $root 'src\SongFiles.cs') (Join-Path $testDirectory 'SongImportTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'Song import test compilation failed.' }
& (Join-Path $testDirectory 'SongImportTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'Song import tests failed.' }
& $compiler /nologo /target:exe /main:RumbleUiTests /optimize+ /utf8output /codepage:65001 "/out:$testDirectory\RumbleUiTests.exe" "/resource:$assets\rumble.ico,XboxRumble.Icon" /reference:System.dll /reference:System.Core.dll "/reference:$framework\System.Xaml.dll" "/reference:$wpf\WindowsBase.dll" "/reference:$wpf\PresentationCore.dll" "/reference:$wpf\PresentationFramework.dll" "/resource:$root\src\MainWindow.xaml,XboxRumble.MainWindow.xaml" $source (Join-Path $testDirectory 'RumbleTests.cs') (Join-Path $testDirectory 'RumbleUiTests.cs')
if ($LASTEXITCODE -ne 0) { throw 'UI test compilation failed.' }
& (Join-Path $testDirectory 'RumbleUiTests.exe')
if ($LASTEXITCODE -ne 0) { throw 'UI tests failed.' }
foreach ($architecture in @('x64', 'x86')) {
    $nativeTest = Join-Path $testDirectory ("SdlControllerTests-$architecture.exe")
    & $compiler /nologo /target:exe "/platform:$architecture" /optimize+ /utf8output /codepage:65001 "/out:$nativeTest" /reference:System.dll /reference:System.Core.dll (Join-Path $root 'src\Controller.cs') (Join-Path $root 'src\SdlNative.cs') (Join-Path $root 'src\SdlController.cs') (Join-Path $root 'src\Melody.cs') (Join-Path $root 'src\RumbleEngine.cs') (Join-Path $testDirectory 'SdlControllerTests.cs')
    if ($LASTEXITCODE -ne 0) { throw "SDL $architecture test compilation failed." }
    & $nativeTest
    if ($LASTEXITCODE -ne 0) { throw "SDL $architecture tests failed." }
}
try {
    Copy-Item -LiteralPath $outputExe -Destination (Join-Path $root 'PulsePad.exe') -Force -ErrorAction Stop
} catch [System.IO.IOException] {
    Write-Output 'Root executable is in use. Close the old app and run the new build below.'
}
$packagePath = Join-Path $outputDirectory 'PulsePad-Windows.zip'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$packageStream = [System.IO.File]::Open($packagePath, [System.IO.FileMode]::Create)
$package = New-Object System.IO.Compression.ZipArchive($packageStream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
try {
    # Explicit runtime allowlist: no local songs, docs, screenshots or build artifacts.
    $packageFiles = @(
        @{ Source = $outputExe; Entry = 'PulsePad.exe' },
        @{ Source = (Join-Path $root 'LICENSE'); Entry = 'LICENSE' },
        @{ Source = (Join-Path $vendor 'x64\SDL3.dll'); Entry = 'native/x64/SDL3.dll' },
        @{ Source = (Join-Path $vendor 'x86\SDL3.dll'); Entry = 'native/x86/SDL3.dll' },
        @{ Source = (Join-Path $vendor 'LICENSE.txt'); Entry = 'native/SDL-LICENSE.txt' }
    )
    foreach ($file in $packageFiles) {
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($package, $file.Source, $file.Entry) | Out-Null
    }
} finally { $package.Dispose(); $packageStream.Dispose() }
Write-Output "Built: $outputExe"
Write-Output "Portable package: $packagePath"
