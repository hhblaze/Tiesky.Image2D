#requires -Version 5.1

[CmdletBinding()]
param(
    [string]$KeyPath = 'D:\VS\Tiesky.Image2D.Realm\Keys generator\tiesky_image2d.snk',
    [string]$OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

function Get-NuspecValue {
    param(
        [Parameter(Mandatory)][xml]$Document,
        [Parameter(Mandatory)][string]$ElementName
    )

    $namespaceManager = [System.Xml.XmlNamespaceManager]::new($Document.NameTable)
    $namespaceManager.AddNamespace('n', $Document.DocumentElement.NamespaceURI)
    $node = $Document.SelectSingleNode("/n:package/n:metadata/n:$ElementName", $namespaceManager)
    if ($null -eq $node -or [string]::IsNullOrWhiteSpace($node.InnerText)) {
        throw "The nuspec metadata element '$ElementName' is missing or empty."
    }

    return $node.InnerText.Trim()
}

$deploymentDirectory = $PSScriptRoot
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $deploymentDirectory '..'))
$projectPath = Join-Path $repositoryRoot 'src\Tiesky.Image2D\Tiesky.Image2D.csproj'
$nuspecPath = Join-Path $deploymentDirectory 'Tiesky.Image2D.nuspec'
$logoPath = Join-Path $deploymentDirectory 'logo.png'
$workDirectory = Join-Path $deploymentDirectory 'work'
$baseOutputPath = [System.IO.Path]::GetFullPath((Join-Path $workDirectory 'bin')) + [System.IO.Path]::DirectorySeparatorChar
$baseIntermediateOutputPath = [System.IO.Path]::GetFullPath((Join-Path $workDirectory 'obj')) + [System.IO.Path]::DirectorySeparatorChar

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $deploymentDirectory 'packages'
}

$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$KeyPath = [System.IO.Path]::GetFullPath($KeyPath)

if (-not (Test-Path -LiteralPath $KeyPath -PathType Leaf)) {
    throw "The strong-name key was not found at '$KeyPath'."
}

if (-not (Test-Path -LiteralPath $projectPath -PathType Leaf)) {
    throw "The library project was not found at '$projectPath'."
}

if (-not (Test-Path -LiteralPath $nuspecPath -PathType Leaf)) {
    throw "The nuspec was not found at '$nuspecPath'."
}

if (-not (Test-Path -LiteralPath $logoPath -PathType Leaf)) {
    throw "The package logo was not found at '$logoPath'."
}

$logoBytes = [System.IO.File]::ReadAllBytes($logoPath)
$maximumIconBytes = 1MB
if ($logoBytes.Length -gt $maximumIconBytes) {
    throw "The package logo is $($logoBytes.Length) bytes; NuGet package icons must not exceed $maximumIconBytes bytes."
}

$pngSignature = [byte[]](0x89, 0x50, 0x4e, 0x47, 0x0d, 0x0a, 0x1a, 0x0a)
if ($logoBytes.Length -lt $pngSignature.Length) {
    throw "The package logo at '$logoPath' is not a valid PNG file."
}

for ($index = 0; $index -lt $pngSignature.Length; $index++) {
    if ($logoBytes[$index] -ne $pngSignature[$index]) {
        throw "The package logo at '$logoPath' is not a valid PNG file."
    }
}

$sha256 = [System.Security.Cryptography.SHA256]::Create()
try {
    $sourceLogoHash = [System.BitConverter]::ToString($sha256.ComputeHash($logoBytes)).Replace('-', '').ToLowerInvariant()
}
finally {
    $sha256.Dispose()
}

[xml]$nuspec = Get-Content -LiteralPath $nuspecPath -Raw
$packageId = Get-NuspecValue -Document $nuspec -ElementName 'id'
$packageVersion = Get-NuspecValue -Document $nuspec -ElementName 'version'
$packageIcon = Get-NuspecValue -Document $nuspec -ElementName 'icon'

if ($packageId -ne 'Tiesky.Image2D') {
    throw "Unexpected package ID '$packageId'."
}

if ($packageIcon -cne 'logo.png') {
    throw "Unexpected package icon path '$packageIcon'."
}

$parsedVersion = $null
if (-not [System.Version]::TryParse($packageVersion, [ref]$parsedVersion) -or
    $parsedVersion.Build -lt 0 -or $parsedVersion.Revision -lt 0) {
    throw "Package version '$packageVersion' must be a four-part numeric assembly version."
}

New-Item -ItemType Directory -Path $workDirectory -Force | Out-Null
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$commonProperties = @(
    "-p:TieskySignedDeploymentBuild=true",
    "-p:TieskySigningKeyFile=$KeyPath",
    "-p:Version=$packageVersion",
    "-p:AssemblyVersion=$packageVersion",
    "-p:FileVersion=$packageVersion",
    "-p:InformationalVersion=$packageVersion",
    "-p:PackageVersion=$packageVersion",
    "-p:IncludeSourceRevisionInInformationalVersion=false",
    "-p:BaseOutputPath=$baseOutputPath",
    "-p:BaseIntermediateOutputPath=$baseIntermediateOutputPath"
)

$buildArguments = @(
    'build',
    $projectPath,
    '--configuration',
    'Release',
    '--nologo'
) + $commonProperties

Invoke-DotNet -Arguments $buildArguments

$releaseDirectory = Join-Path $baseOutputPath 'Release\net8.0'
$assemblyPath = Join-Path $releaseDirectory 'Tiesky.Image2D.dll'
$documentationPath = Join-Path $releaseDirectory 'Tiesky.Image2D.xml'

foreach ($requiredFile in @($assemblyPath, $documentationPath)) {
    if (-not (Test-Path -LiteralPath $requiredFile -PathType Leaf)) {
        throw "Expected build output was not created: '$requiredFile'."
    }
}

$assemblyName = [System.Reflection.AssemblyName]::GetAssemblyName($assemblyPath)
if ($assemblyName.Version.ToString() -ne $packageVersion) {
    throw "Assembly version '$($assemblyName.Version)' does not match package version '$packageVersion'."
}

$publicKeyToken = -join ($assemblyName.GetPublicKeyToken() | ForEach-Object { $_.ToString('x2') })
$expectedPublicKeyToken = '70046dc50329325b'
if ($publicKeyToken -ne $expectedPublicKeyToken) {
    throw "Assembly public-key token '$publicKeyToken' does not match '$expectedPublicKeyToken'."
}

$packArguments = @(
    'pack',
    $projectPath,
    '--configuration',
    'Release',
    '--no-build',
    '--no-restore',
    '--output',
    $OutputDirectory,
    '--nologo',
    "-p:NuspecFile=$nuspecPath",
    "-p:NuspecBasePath=$repositoryRoot",
    '-p:NoBuild=true',
    '-p:IncludeBuildOutput=false'
) + $commonProperties

Invoke-DotNet -Arguments $packArguments

$packagePath = Join-Path $OutputDirectory "$packageId.$packageVersion.nupkg"
if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
    throw "Expected NuGet package was not created: '$packagePath'."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $entryNames = @($archive.Entries | ForEach-Object { $_.FullName })
    $requiredEntries = @(
        'lib/net8.0/Tiesky.Image2D.dll',
        'lib/net8.0/Tiesky.Image2D.xml',
        'logo.png',
        'README.md',
        'LICENSE',
        'Tiesky.Image2D.nuspec'
    )

    foreach ($requiredEntry in $requiredEntries) {
        if ($entryNames -cnotcontains $requiredEntry) {
            throw "NuGet package is missing '$requiredEntry'."
        }
    }

    $forbiddenEntries = @($entryNames | Where-Object {
        $_ -match '(?i)(^|/)work/' -or
        $_ -match '(?i)\.snk$' -or
        $_ -match '(?i)keys generator'
    })
    if ($forbiddenEntries.Count -ne 0) {
        throw "NuGet package contains forbidden entries: $($forbiddenEntries -join ', ')."
    }

    $packedNuspecEntry = $archive.GetEntry('Tiesky.Image2D.nuspec')
    $packedNuspecStream = $packedNuspecEntry.Open()
    $packedNuspecReader = [System.IO.StreamReader]::new($packedNuspecStream)
    try {
        [xml]$packedNuspec = $packedNuspecReader.ReadToEnd()
    }
    finally {
        $packedNuspecReader.Dispose()
        $packedNuspecStream.Dispose()
    }

    $packedIcon = Get-NuspecValue -Document $packedNuspec -ElementName 'icon'
    if ($packedIcon -cne 'logo.png') {
        throw "Packed nuspec icon path '$packedIcon' does not match 'logo.png'."
    }

    $packedLogoEntry = $archive.GetEntry('logo.png')
    if ($packedLogoEntry.Length -ne $logoBytes.Length) {
        throw "Packed logo length '$($packedLogoEntry.Length)' does not match source logo length '$($logoBytes.Length)'."
    }

    $packedLogoStream = $packedLogoEntry.Open()
    $packedLogoSha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        $packedLogoHash = [System.BitConverter]::ToString($packedLogoSha256.ComputeHash($packedLogoStream)).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $packedLogoSha256.Dispose()
        $packedLogoStream.Dispose()
    }

    if ($packedLogoHash -cne $sourceLogoHash) {
        throw "Packed logo SHA-256 '$packedLogoHash' does not match source logo SHA-256 '$sourceLogoHash'."
    }
}
finally {
    $archive.Dispose()
}

Write-Host "Created $packagePath"
Write-Host "Version: $packageVersion"
Write-Host "Assembly public-key token: $publicKeyToken"
Write-Host "Package logo SHA-256: $sourceLogoHash"
Write-Host 'The NuGet package is not uploaded or certificate-signed by this script.'
