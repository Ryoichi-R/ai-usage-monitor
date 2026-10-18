Set-StrictMode -Version Latest

function ConvertTo-CanonicalDirectoryPath {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path)
    $filesystemRoot = [IO.Path]::GetPathRoot($fullPath)
    if ($fullPath -ieq $filesystemRoot) {
        return $filesystemRoot
    }

    return $fullPath.TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
}

function Assert-AiUsageMonitorOutputParent {
    param([Parameter(Mandatory)][string]$Path)

    $resolved = ConvertTo-CanonicalDirectoryPath -Path $Path
    if (-not (Test-Path -LiteralPath $resolved -PathType Container)) {
        throw "OutputRoot must be an existing directory: $resolved"
    }
    if ((Get-Item -LiteralPath $resolved -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) {
        throw "OutputRoot cannot be a symbolic link or junction: $resolved"
    }

    return $resolved
}

function Resolve-AiUsageMonitorBuildPaths {
    param(
        [Parameter(Mandatory)][string]$ProjectRoot,
        [AllowNull()][AllowEmptyString()][string]$OutputRoot
    )

    $resolvedProjectRoot = ConvertTo-CanonicalDirectoryPath -Path $ProjectRoot
    $requestedOutputRoot = if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
        $resolvedProjectRoot
    }
    elseif ([IO.Path]::IsPathFullyQualified($OutputRoot.Trim())) {
        $OutputRoot.Trim()
    }
    else {
        Join-Path $resolvedProjectRoot $OutputRoot.Trim()
    }

    $resolvedOutputRoot = ConvertTo-CanonicalDirectoryPath -Path $requestedOutputRoot
    $usesExternalRoot = $resolvedOutputRoot -ine $resolvedProjectRoot
    if ($usesExternalRoot -and (Split-Path -Leaf $resolvedOutputRoot) -ieq 'AiUsageMonitorBuilds') {
        throw "Specify the parent folder in which AiUsageMonitorBuilds will be created, not AiUsageMonitorBuilds itself: $resolvedOutputRoot"
    }
    $managedRoot = if ($usesExternalRoot) {
        Join-Path $resolvedOutputRoot 'AiUsageMonitorBuilds'
    }
    else {
        Join-Path $resolvedProjectRoot 'artifacts'
    }

    [pscustomobject]@{
        OutputParent     = $resolvedOutputRoot
        ManagedRoot      = ConvertTo-CanonicalDirectoryPath -Path $managedRoot
        UsesExternalRoot = $usesExternalRoot
    }
}

function Assert-AiUsageMonitorTestOutputParent {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$ProjectRoot
    )

    $resolvedProjectRoot = ConvertTo-CanonicalDirectoryPath -Path $ProjectRoot
    $resolved = Assert-AiUsageMonitorOutputParent -Path $Path
    if ($resolved -ieq $resolvedProjectRoot -or
        $resolved.StartsWith($resolvedProjectRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Test OutputRoot must be outside the project tree, not inside it: $resolved"
    }

    return $resolved
}

function New-AiUsageMonitorTestOutputRoot {
    param(
        [Parameter(Mandatory)][string]$OutputParent,
        [Parameter(Mandatory)][ValidatePattern('^[A-Za-z0-9-]+$')][string]$Purpose
    )

    $resolvedParent = ConvertTo-CanonicalDirectoryPath -Path $OutputParent
    $child = Join-Path $resolvedParent ("AiUsageMonitorTests-$Purpose-" + [Guid]::NewGuid().ToString('N'))
    if (Test-Path -LiteralPath $child) { throw "Test output child directory already exists: $child" }
    $null = New-Item -ItemType Directory -Path $child -Force

    [pscustomobject]@{
        OutputRoot    = ConvertTo-CanonicalDirectoryPath -Path $child
        ArtifactsPath = Join-Path $child 'artifacts'
        ResultsPath   = Join-Path $child 'results'
    }
}

function Remove-AiUsageMonitorTestOutputRoot {
    param(
        [Parameter(Mandatory)][string]$OutputRoot,
        [Parameter(Mandatory)][string]$OutputParent
    )

    $resolvedParent = ConvertTo-CanonicalDirectoryPath -Path $OutputParent
    $resolvedChild = ConvertTo-CanonicalDirectoryPath -Path $OutputRoot
    if ($resolvedChild -ieq $resolvedParent -or
        -not $resolvedChild.StartsWith($resolvedParent + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or
        (Split-Path -Leaf $resolvedChild) -notlike 'AiUsageMonitorTests-*') {
        throw "Refusing to remove a path that is not an owned test output child: $resolvedChild"
    }
    if (Test-Path -LiteralPath $resolvedChild) {
        Remove-Item -LiteralPath $resolvedChild -Recurse -Force
    }
}
