# Sync v2 Android native libraries and extensions from OrbbecSDK-Android-Wrapper
# into the Unity project (Step 4, Route A).
#
# libc++_shared.so is taken from Wrapper build output (same file Gradle packages
# when ANDROID_STL=c++_shared). If build artifacts are missing, the script can
# run :obsensor_jni:assembleRelease (requires JDK 17 + NDK ob_ndk_version).

param(
    [switch]$SkipBuild,
    [switch]$ForceBuild
)

$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent $PSScriptRoot
$WrapperRepo = Join-Path (Split-Path -Parent $Root) "OrbbecSDK-Android-Wrapper"
$ObsensorJni = Join-Path $WrapperRepo "obsensor_jni"
$Unity = $Root

$SrcLibs = Join-Path $ObsensorJni "libs\arm64-v8a"
$SrcAssets = Join-Path $ObsensorJni "src\main\assets"
$DstLibs = Join-Path $Unity "Assets\Orbbec\Plugins\Android\libs\arm64-v8a"
$DstAndroidPlugins = Join-Path $Unity "Assets\Orbbec\Plugins\Android"
$DstStream = Join-Path $Unity "Assets\StreamingAssets"

function Get-GradleProperty {
    param(
        [string]$FilePath,
        [string]$Name
    )
    if (-not (Test-Path $FilePath)) { return $null }
    foreach ($line in Get-Content $FilePath) {
        if ($line -match "^\s*$([regex]::Escape($Name))\s*=\s*(.+)\s*$") {
            return $Matches[1].Trim()
        }
    }
    return $null
}

function Get-AndroidSdkRoot {
    foreach ($candidate in @($env:ANDROID_HOME, $env:ANDROID_SDK_ROOT)) {
        if ($candidate -and (Test-Path $candidate)) { return $candidate }
    }
    $defaultSdk = Join-Path $env:LOCALAPPDATA "Android\Sdk"
    if (Test-Path $defaultSdk) { return $defaultSdk }
    return $null
}

function Get-NdkRootForVersion {
    param([string]$Version)
    $sdkRoot = Get-AndroidSdkRoot
    if (-not $sdkRoot) { return $null }
    $ndkRoot = Join-Path $sdkRoot "ndk\$Version"
    if (Test-Path $ndkRoot) { return $ndkRoot }
    return $null
}

function Get-NdkLibcxxPath {
    param([string]$NdkRoot)
    $prebuilt = "windows-x86_64"
    if ($IsLinux) { $prebuilt = "linux-x86_64" }
    elseif ($IsMacOS) { $prebuilt = "darwin-x86_64" }
    $path = Join-Path $NdkRoot "toolchains\llvm\prebuilt\$prebuilt\sysroot\usr\lib\aarch64-linux-android\libc++_shared.so"
    if (Test-Path $path) { return $path }
    return $null
}

function Find-Java17Home {
    $candidates = @()
    if ($env:JAVA_HOME) { $candidates += $env:JAVA_HOME }

    foreach ($base in @(
        "${env:ProgramFiles}\Java",
        "${env:ProgramFiles}\Eclipse Adoptium",
        "${env:ProgramFiles}\Microsoft",
        "${env:ProgramFiles}\Amazon Corretto",
        "${env:ProgramFiles}\Android\Android Studio\jbr",
        "${env:ProgramFiles}\Android\Android Studio\jbr-17"
    )) {
        if (-not (Test-Path $base)) { continue }
        if ((Split-Path $base -Leaf) -match 'jbr') {
            $candidates += $base
            continue
        }
        $candidates += Get-ChildItem $base -Directory -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -match 'jdk-?17|17\.' } |
            Select-Object -ExpandProperty FullName
    }

    foreach ($javaHome in $candidates | Select-Object -Unique) {
        $java = Join-Path $javaHome "bin\java.exe"
        if (-not (Test-Path $java)) { continue }
        $ver = cmd /c "`"$java`" -version 2>&1"
        if ($ver -match 'version "17\.') { return $javaHome }
    }
    return $null
}

function Get-WrapperLibcxxCandidates {
    param([string]$BuildDir)
    if (-not (Test-Path $BuildDir)) { return @() }

    return Get-ChildItem $BuildDir -Recurse -Filter "libc++_shared.so" -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -match 'arm64-v8a' }
}

function Select-PreferredLibcxx {
    param($Candidates)
    if (-not $Candidates -or $Candidates.Count -eq 0) { return $null }

    $patterns = @(
        'outputs\\aar',
        'library_jni\\release',
        'merged_native_libs\\release',
        'stripped_native_libs\\release'
    )
    foreach ($pattern in $patterns) {
        $match = $Candidates | Where-Object { $_.FullName -match $pattern } |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1
        if ($match) { return $match.FullName }
    }

    return ($Candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
}

function Get-WrapperLibcxxFromAar {
    param([string]$AarDir)
    if (-not (Test-Path $AarDir)) { return $null }

    $aar = Get-ChildItem $AarDir -Filter "*release*.aar" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
    if (-not $aar) { return $null }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($aar.FullName)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -eq 'jni/arm64-v8a/libc++_shared.so' } |
            Select-Object -First 1
        if (-not $entry) { return $null }

        $tempDir = Join-Path ([System.IO.Path]::GetTempPath()) "obsensor_jni_libcxx_extract"
        New-Item -ItemType Directory -Force -Path $tempDir | Out-Null
        $outPath = Join-Path $tempDir "libc++_shared.so"
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $outPath, $true)
        return $outPath
    }
    finally {
        $zip.Dispose()
    }
}

function Get-ObsensorReleaseAar {
    param([string]$ObsensorJniDir)
    $aarDir = Join-Path $ObsensorJniDir "build\outputs\aar"
    if (-not (Test-Path $aarDir)) { return $null }
    return Get-ChildItem $aarDir -Filter "*release*.aar" -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending |
        Select-Object -First 1
}

function Copy-ObsensorJniArtifacts {
    param(
        [string]$ObsensorJniDir,
        [string]$DstLibsDir,
        [string]$DstPluginsDir
    )

    $aar = Get-ObsensorReleaseAar $ObsensorJniDir
    if (-not $aar) {
        Write-Warning "obsensor release AAR not found. Build Wrapper obsensor_jni first to sync libobsensor_jni.so and obsensor-classes.jar."
        return
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [System.IO.Compression.ZipFile]::OpenRead($aar.FullName)
    try {
        $jniEntry = $zip.Entries | Where-Object { $_.FullName -eq "jni/arm64-v8a/libobsensor_jni.so" } | Select-Object -First 1
        if ($jniEntry) {
            $outSo = Join-Path $DstLibsDir "libobsensor_jni.so"
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($jniEntry, $outSo, $true)
            Write-Host "Copied libobsensor_jni.so from $($aar.Name)"
        } else {
            Write-Warning "libobsensor_jni.so not found in $($aar.FullName)"
        }

        $jarEntry = $zip.Entries | Where-Object { $_.FullName -eq "classes.jar" } | Select-Object -First 1
        if ($jarEntry) {
            $outJar = Join-Path $DstPluginsDir "obsensor-classes.jar"
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($jarEntry, $outJar, $true)
            Write-Host "Copied obsensor-classes.jar from $($aar.Name)"
        } else {
            Write-Warning "classes.jar not found in $($aar.FullName)"
        }
    }
    finally {
        $zip.Dispose()
    }
}

function Get-WrapperLibcxxPath {
    param([string]$ObsensorJniDir)

    $buildDir = Join-Path $ObsensorJniDir "build"
    $fromAar = Get-WrapperLibcxxFromAar (Join-Path $buildDir "outputs\aar")
    if ($fromAar) { return $fromAar }

    $candidates = Get-WrapperLibcxxCandidates $buildDir
    return Select-PreferredLibcxx $candidates
}

function Invoke-ObsensorJniBuild {
    param(
        [string]$RepoRoot,
        [string]$NdkVersion
    )

    $ndkRoot = Get-NdkRootForVersion $NdkVersion
    if (-not $ndkRoot) {
        throw @"
NDK $NdkVersion is required but not installed.
Install it via Android Studio SDK Manager or:
  sdkmanager "ndk;$NdkVersion"
Expected path: $(Join-Path (Get-AndroidSdkRoot) "ndk\$NdkVersion")
"@
    }

    $java17 = Find-Java17Home
    if (-not $java17) {
        throw @"
JDK 17 is required to build OrbbecSDK-Android-Wrapper (Gradle 8.0 / AGP 8.1).
Install JDK 17 and set JAVA_HOME, or build once in Android Studio:
  cd $RepoRoot
  .\gradlew.bat :obsensor_jni:assembleRelease
Then re-run this script.
"@
    }

    $gradlew = Join-Path $RepoRoot "gradlew.bat"
    if (-not (Test-Path $gradlew)) {
        throw "gradlew.bat not found: $gradlew"
    }

    Write-Host "Building obsensor_jni (JAVA_HOME=$java17, NDK=$NdkVersion)..."
    $previousJavaHome = $env:JAVA_HOME
    $env:JAVA_HOME = $java17
    Push-Location $RepoRoot
    try {
        $prevEap = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        & $gradlew :obsensor_jni:assembleRelease --no-daemon 2>&1 | Out-Host
        $exitCode = $LASTEXITCODE
        $ErrorActionPreference = $prevEap
        if ($exitCode -ne 0) {
            throw "gradlew :obsensor_jni:assembleRelease failed with exit code $exitCode"
        }
    }
    finally {
        $env:JAVA_HOME = $previousJavaHome
        Pop-Location
    }
}

function Resolve-LibcxxSharedPath {
    param(
        [string]$ObsensorJniDir,
        [string]$WrapperRepoRoot,
        [string]$NdkVersion,
        [switch]$SkipBuild,
        [switch]$ForceBuild
    )

    # Prefer full NDK sysroot libc++ (unstripped, matches libOrbbecSDK link NDK).
    $ndkRoot = Get-NdkRootForVersion $NdkVersion
    if ($ndkRoot) {
        $ndkLibcxx = Get-NdkLibcxxPath $ndkRoot
        if ($ndkLibcxx) {
            Write-Host "Using libc++_shared.so from NDK ${NdkVersion}:"
            Write-Host "  $ndkLibcxx"
            return $ndkLibcxx
        }
    }

    if (-not $ForceBuild) {
        $existing = Get-WrapperLibcxxPath $ObsensorJniDir
        if ($existing) {
            Write-Warning "NDK $NdkVersion not found; falling back to Wrapper build output:"
            Write-Host "  $existing"
            return $existing
        }
    }

    if (-not $SkipBuild) {
        Invoke-ObsensorJniBuild -RepoRoot $WrapperRepoRoot -NdkVersion $NdkVersion
        $ndkLibcxx = if ($ndkRoot) { Get-NdkLibcxxPath $ndkRoot } else { Get-NdkLibcxxPath (Get-NdkRootForVersion $NdkVersion) }
        if ($ndkLibcxx) {
            Write-Host "Using libc++_shared.so from NDK $NdkVersion after Wrapper build:"
            Write-Host "  $ndkLibcxx"
            return $ndkLibcxx
        }
        $built = Get-WrapperLibcxxPath $ObsensorJniDir
        if ($built) {
            Write-Warning "Using stripped libc++_shared.so from Wrapper build output:"
            Write-Host "  $built"
            return $built
        }
        throw "Wrapper build finished but libc++_shared.so was not found"
    }

    throw @"
libc++_shared.so not found.
Install NDK $NdkVersion, or build Wrapper once:
  cd $WrapperRepoRoot && .\gradlew.bat :obsensor_jni:assembleRelease
"@
}

if (-not (Test-Path $SrcLibs)) {
    throw "Source libs not found: $SrcLibs"
}

$ndkVersion = Get-GradleProperty (Join-Path $WrapperRepo "gradle.properties") "ob_ndk_version"
if (-not $ndkVersion) {
    throw "ob_ndk_version not found in $WrapperRepo\gradle.properties"
}

New-Item -ItemType Directory -Force -Path $DstLibs | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $DstStream "arm64-v8a\extensions") | Out-Null

Copy-Item (Join-Path $SrcLibs "libOrbbecSDK.so") $DstLibs -Force
Copy-Item (Join-Path $SrcLibs "libomp.so") $DstLibs -Force

$libcxxSrc = Resolve-LibcxxSharedPath `
    -ObsensorJniDir $ObsensorJni `
    -WrapperRepoRoot $WrapperRepo `
    -NdkVersion $ndkVersion `
    -SkipBuild:$SkipBuild `
    -ForceBuild:$ForceBuild
Copy-Item $libcxxSrc (Join-Path $DstLibs "libc++_shared.so") -Force
Write-Host "Copied libc++_shared.so -> $DstLibs"

Copy-ObsensorJniArtifacts -ObsensorJniDir $ObsensorJni -DstLibsDir $DstLibs -DstPluginsDir $DstAndroidPlugins

Copy-Item (Join-Path $SrcAssets "OrbbecSDKConfig.xml") $DstStream -Force
Copy-Item (Join-Path $SrcAssets "arm64-v8a\extensions\*") (Join-Path $DstStream "arm64-v8a\extensions") -Recurse -Force

Write-Host "Synced Android v2 native libs to:"
Write-Host "  $DstLibs"
Write-Host "  $DstStream\arm64-v8a\extensions"
Write-Host "Done."
