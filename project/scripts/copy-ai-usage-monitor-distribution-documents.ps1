Set-StrictMode -Version Latest

# Documents shipped next to the Windows executable. Third-party license texts referenced by
# THIRD-PARTY-NOTICES.md ship verbatim under licenses\ (the macOS bundle uses
# Contents/Resources/licenses/). The copied bytes are compared to the source by SHA-256.
function Get-AiUsageMonitorDistributionDocuments {
    param([Parameter(Mandatory)][string]$ProjectRoot)

    $root = [IO.Path]::GetFullPath($ProjectRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $documents = [Collections.Generic.List[object]]::new()
    foreach ($name in @('LICENSE', 'THIRD-PARTY-NOTICES.md')) {
        $documents.Add([pscustomobject]@{ RelativePath = $name; Source = Join-Path $root $name })
    }

    $licenseRoot = Join-Path $root 'licenses'
    if (-not (Test-Path -LiteralPath $licenseRoot -PathType Container)) {
        throw "License texts directory missing: $licenseRoot"
    }
    $entries = @(Get-ChildItem -LiteralPath $licenseRoot -Force | Where-Object Name -cne '.gitattributes')
    $directory = $entries | Where-Object PSIsContainer | Select-Object -First 1
    if ($null -ne $directory) {
        # Nested directories would otherwise be skipped silently and never shipped.
        throw "License texts directory must not contain subdirectories: $($directory.FullName)"
    }
    if ($entries.Count -eq 0) {
        throw "License texts missing: $licenseRoot"
    }
    foreach ($license in $entries | Sort-Object Name) {
        $documents.Add([pscustomobject]@{
                RelativePath = Join-Path 'licenses' $license.Name
                Source = $license.FullName
            })
    }

    foreach ($document in $documents) {
        if (-not (Test-Path -LiteralPath $document.Source -PathType Leaf)) {
            throw "Distribution document missing: $($document.Source)"
        }
        if ((Get-Item -LiteralPath $document.Source -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "Distribution document cannot be a symbolic link: $($document.Source)"
        }
    }
    return $documents.ToArray()
}

function Assert-AiUsageMonitorDistributionDocuments {
    param(
        [Parameter(Mandatory)][string]$ProjectRoot,
        [Parameter(Mandatory)][string]$OutputDir
    )

    $output = [IO.Path]::GetFullPath($OutputDir).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $documents = Get-AiUsageMonitorDistributionDocuments -ProjectRoot $ProjectRoot
    foreach ($document in $documents) {
        $destination = Join-Path $output $document.RelativePath
        if (-not (Test-Path -LiteralPath $destination -PathType Leaf)) {
            throw "Distribution document missing from output: $($document.RelativePath)"
        }
        $sourceHash = (Get-FileHash -LiteralPath $document.Source -Algorithm SHA256).Hash
        $destinationHash = (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash
        if ($sourceHash -ne $destinationHash) {
            throw "Distribution document copy verification failed: $($document.RelativePath)"
        }
    }

    # A stale text left by an earlier publish into the same directory would ship a license
    # that THIRD-PARTY-NOTICES.md no longer lists, so the shipped set must match exactly.
    $expected = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($document in $documents) { [void]$expected.Add($document.RelativePath) }
    $licenseOutput = Join-Path $output 'licenses'
    foreach ($entry in @(Get-ChildItem -LiteralPath $licenseOutput -Force)) {
        $relativePath = Join-Path 'licenses' $entry.Name
        if ($entry.PSIsContainer -or -not $expected.Contains($relativePath)) {
            throw "Unexpected entry in distributed license texts: $relativePath"
        }
    }
}

function Copy-AiUsageMonitorDistributionDocuments {
    param(
        [Parameter(Mandatory)][string]$ProjectRoot,
        [Parameter(Mandatory)][string]$OutputDir
    )

    $output = [IO.Path]::GetFullPath($OutputDir).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $licenseOutput = Join-Path $output 'licenses'
    if (Test-Path -LiteralPath $licenseOutput) {
        $item = Get-Item -LiteralPath $licenseOutput -Force
        if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw "Distributed license texts path must be a plain directory: $licenseOutput"
        }
    }
    New-Item -ItemType Directory -Path $licenseOutput -Force | Out-Null
    foreach ($document in Get-AiUsageMonitorDistributionDocuments -ProjectRoot $ProjectRoot) {
        Copy-Item -LiteralPath $document.Source -Destination (Join-Path $output $document.RelativePath) -Force
    }
    Assert-AiUsageMonitorDistributionDocuments -ProjectRoot $ProjectRoot -OutputDir $output
}
