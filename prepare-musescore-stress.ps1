param(
    [Parameter(Mandatory = $true)]
    [string]$InputFolder,

    [string]$MuseScoreExe = "C:\Program Files\MuseScore 4\bin\MuseScore4.exe",

    [string]$PdfToCairo = "pdftocairo",

    [string]$PdfInfo = "pdfinfo",

    [int]$VariantsPerScore = 1,

    [Nullable[int]]$MaxSystems = $null,

    [Nullable[int]]$MaxMeasures = $null,

    [Nullable[int]]$Seed = $null,

    [switch]$Force,

    [switch]$KeepIntermediates,

    [switch]$Diagnostics
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# -----------------------------------------------------------------------------
# Configuration
# -----------------------------------------------------------------------------

# Interpreted as scale factors relative to the original MuseScore spatium.
$StaffSpaceFactors = @(0.8, 0.9, 1.0)

# MuseScore musicalSymbolFont -> matching musicalTextFont.
$FontPairs = [ordered]@{
    "Emmentaler"     = "Emmentaler Text"
    "Leland"         = "Leland Text"
    "Bravura"        = "Bravura Text"
    "Gonville"       = "Gonville Text"
    "Finale Maestro" = "Finale Maestro Text"
}

$Renderers = @(
    "MuseScore", # direct MuseScore -> SVG
    "Cairo",     # MuseScore -> PDF -> pdftocairo -> SVG
    "SVGO"       # MuseScore -> PDF -> pdftocairo -> SVG -> SVGO
)

# -----------------------------------------------------------------------------
# Helpers
# -----------------------------------------------------------------------------

function Resolve-Tool {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value,
        [Parameter(Mandatory = $true)]
        [string]$FriendlyName
    )

    if (Test-Path -LiteralPath $Value -PathType Leaf) {
        return (Resolve-Path -LiteralPath $Value).Path
    }

    $command = Get-Command $Value -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        return $command.Source
    }

    throw "$FriendlyName not found: '$Value'. Pass an explicit path."
}

function Resolve-Svgo {
    $globalSvgo = Get-Command "svgo.cmd" -ErrorAction SilentlyContinue
    if ($null -eq $globalSvgo) {
        $globalSvgo = Get-Command "svgo" -ErrorAction SilentlyContinue
    }

    if ($null -ne $globalSvgo) {
        return [pscustomobject]@{
            Exe        = $globalSvgo.Source
            PrefixArgs = @()
            Display    = $globalSvgo.Source
        }
    }

    $npx = Get-Command "npx.cmd" -ErrorAction SilentlyContinue
    if ($null -eq $npx) {
        $npx = Get-Command "npx" -ErrorAction SilentlyContinue
    }

    if ($null -ne $npx) {
        return [pscustomobject]@{
            Exe        = $npx.Source
            PrefixArgs = @("--yes", "svgo")
            Display    = "$($npx.Source) --yes svgo"
        }
    }

    throw "SVGO not found. Install it with 'npm install -g svgo' or make npx available."
}

function Convert-ToProcessArgument {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    # Start-Process on Windows PowerShell 5.1 joins ArgumentList into one command
    # line. Quote arguments ourselves so paths with spaces survive intact.
    if ($Value -notmatch '[\s"]') {
        return $Value
    }

    $escaped = $Value -replace '(\\*)"', '$1$1\"'
    $escaped = $escaped -replace '(\\+)$', '$1$1'
    return '"' + $escaped + '"'
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Exe,

        [Parameter(Mandatory = $true)]
        [string[]]$Arguments,

        [Parameter(Mandatory = $true)]
        [string]$Description,

        [string]$LogDirectory = $null
    )

    if ($script:Diagnostics) {
        Write-Host "    $Description" -ForegroundColor DarkGray
    }

    $argumentLine = ($Arguments | ForEach-Object {
        Convert-ToProcessArgument -Value $_
    }) -join ' '

    $stdoutPath = $null
    $stderrPath = $null

    if (-not [string]::IsNullOrWhiteSpace($LogDirectory)) {
        New-Item -ItemType Directory -Path $LogDirectory -Force | Out-Null
        $safeName = ($Description -replace '[^\p{L}\p{Nd}_.-]+', '_').Trim('_')
        if ($safeName.Length -gt 80) {
            $safeName = $safeName.Substring(0, 80)
        }

        $stdoutPath = Join-Path $LogDirectory ($safeName + '.stdout.log')
        $stderrPath = Join-Path $LogDirectory ($safeName + '.stderr.log')
    }

    $startArgs = @{
        FilePath     = $Exe
        ArgumentList = $argumentLine
        Wait         = $true
        PassThru     = $true
        WindowStyle  = 'Hidden'
    }

    if ($null -ne $stdoutPath) {
        $startArgs['RedirectStandardOutput'] = $stdoutPath
        $startArgs['RedirectStandardError'] = $stderrPath
    }

    $process = Start-Process @startArgs

    if ($process.ExitCode -ne 0) {
        $details = New-Object System.Collections.Generic.List[string]

        foreach ($logPath in @($stdoutPath, $stderrPath)) {
            if ($null -ne $logPath -and (Test-Path -LiteralPath $logPath)) {
                $lines = @(Get-Content -LiteralPath $logPath -ErrorAction SilentlyContinue)
                if ($lines.Count -gt 0) {
                    $details.Add((($lines | Select-Object -Last 20) -join [Environment]::NewLine))
                }
            }
        }

        $detailText = if ($details.Count -gt 0) {
            "`n" + ($details -join [Environment]::NewLine)
        }
        else {
            ''
        }

        throw "$Description failed with exit code $($process.ExitCode).$detailText"
    }
}

function Invoke-MuseScoreExport {
    param(
        [Parameter(Mandatory = $true)]
        [string]$MuseScore,

        [Parameter(Mandatory = $true)]
        [string]$InputFile,

        [Parameter(Mandatory = $true)]
        [string]$OutputFile,

        [string]$LogDirectory = $null
    )

    Invoke-Checked `
        -Exe $MuseScore `
        -Arguments @('-f', '-o', $OutputFile, $InputFile) `
        -Description "MuseScore: $([IO.Path]::GetFileName($InputFile)) -> $([IO.Path]::GetFileName($OutputFile))" `
        -LogDirectory $LogDirectory
}

function Get-StyleChild {
    param(
        [Parameter(Mandatory = $true)]
        [System.Xml.XmlElement]$Style,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    foreach ($child in $Style.ChildNodes) {
        if ($child -is [System.Xml.XmlElement] -and
            $child.Name.Equals($Name, [StringComparison]::OrdinalIgnoreCase)) {
            return $child
        }
    }

    return $null
}

function Set-StyleValue {
    param(
        [Parameter(Mandatory = $true)]
        [System.Xml.XmlDocument]$Document,

        [Parameter(Mandatory = $true)]
        [System.Xml.XmlElement]$Style,

        [Parameter(Mandatory = $true)]
        [string]$Name,

        [Parameter(Mandatory = $true)]
        [string]$Value
    )

    $node = Get-StyleChild -Style $Style -Name $Name
    if ($null -eq $node) {
        $node = $Document.CreateElement($Name)
        [void]$Style.AppendChild($node)
    }

    $node.InnerText = $Value
}

function Update-MuseScoreStyle {
    param(
        [Parameter(Mandatory = $true)]
        [string]$MscxPath,

        [Parameter(Mandatory = $true)]
        [double]$StaffSpaceFactor,

        [Parameter(Mandatory = $true)]
        [string]$MusicalSymbolFont,

        [Parameter(Mandatory = $true)]
        [string]$MusicalTextFont
    )

    $scoreDirectory = Split-Path -Parent $MscxPath
    $styleSidecar = Join-Path $scoreDirectory "score_style.mss"

    $stylePath = $null
    $doc = New-Object System.Xml.XmlDocument
    $doc.PreserveWhitespace = $true

    if (Test-Path -LiteralPath $styleSidecar -PathType Leaf) {
        # MuseScore 4 uncompressed scores keep the visual style in this sidecar.
        $stylePath = $styleSidecar
        $doc.Load($stylePath)

        $styles = @(
            $doc.SelectNodes("//*[local-name()='Style']")
        )

        if ($styles.Count -eq 0) {
            throw "score_style.mss exists but contains no <Style>: '$stylePath'."
        }

        if ($script:Diagnostics) {
            Write-Host "    style source: score_style.mss" -ForegroundColor DarkGray
        }
    }
    else {
        # Compatibility fallback for older/other MuseScore layouts where Style
        # may still be embedded in the MSCX itself.
        $stylePath = $MscxPath
        $doc.Load($stylePath)

        $styles = @(
            $doc.SelectNodes("//*[local-name()='Score']/*[local-name()='Style']")
        )

        if ($styles.Count -eq 0) {
            throw (
                "No score_style.mss and no embedded <Score><Style> were found for '$MscxPath'. " +
                "MuseScore 4 normally creates score_style.mss next to an uncompressed MSCX."
            )
        }

        if ($script:Diagnostics) {
            Write-Host "    style source: embedded MSCX <Style>" -ForegroundColor DarkGray
        }
    }

    $masterOriginalSpatium = $null
    $masterNewSpatium = $null

    for ($i = 0; $i -lt $styles.Count; $i++) {
        $style = [System.Xml.XmlElement]$styles[$i]

        # MuseScore 4's score_style.mss currently serializes this as lower-case
        # <spatium>; Get-StyleChild is intentionally case-insensitive.
        $spatiumNode = Get-StyleChild -Style $style -Name "spatium"

        if ($null -eq $spatiumNode) {
            throw "Style #$($i + 1) contains no <spatium> in '$stylePath'."
        }

        $originalSpatium = 0.0
        if (-not [double]::TryParse(
            $spatiumNode.InnerText,
            [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture,
            [ref]$originalSpatium)) {
            throw "Invalid spatium '$($spatiumNode.InnerText)' in '$stylePath'."
        }

        $newSpatium = $originalSpatium * $StaffSpaceFactor
        $spatiumNode.InnerText = $newSpatium.ToString(
            "0.########",
            [Globalization.CultureInfo]::InvariantCulture)

        Set-StyleValue `
            -Document $doc `
            -Style $style `
            -Name "musicalSymbolFont" `
            -Value $MusicalSymbolFont

        Set-StyleValue `
            -Document $doc `
            -Style $style `
            -Name "musicalTextFont" `
            -Value $MusicalTextFont

        if ($i -eq 0) {
            $masterOriginalSpatium = $originalSpatium
            $masterNewSpatium = $newSpatium
        }
    }

    $doc.Save($stylePath)

    return [pscustomobject]@{
        OriginalSpatium = $masterOriginalSpatium
        NewSpatium      = $masterNewSpatium
        StyleCount      = $styles.Count
        StylePath       = $stylePath
    }
}

function Get-SvgPageNumber {
    param(
        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo]$File
    )

    $stem = [IO.Path]::GetFileNameWithoutExtension($File.Name)

    if ($stem -match '-(\d+)$') {
        return [int]$Matches[1]
    }

    # Some exporters use an unnumbered SVG for a single page.
    return 1
}

function Normalize-SvgPages {
    param(
        [Parameter(Mandatory = $true)]
        [string]$SourceDirectory,

        [Parameter(Mandatory = $true)]
        [string]$DestinationDirectory,

        [switch]$Move
    )

    $files = @(
        Get-ChildItem -LiteralPath $SourceDirectory -File -Filter "*.svg" |
        Sort-Object `
            @{ Expression = { Get-SvgPageNumber $_ } }, `
            @{ Expression = { $_.Name } }
    )

    if ($files.Count -eq 0) {
        throw "No SVG pages were generated in '$SourceDirectory'."
    }

    $page = 0
    foreach ($file in $files) {
        $page++
        $target = Join-Path $DestinationDirectory ("page-{0:D3}.svg" -f $page)

        if ($Move) {
            Move-Item -LiteralPath $file.FullName -Destination $target -Force
        }
        else {
            Copy-Item -LiteralPath $file.FullName -Destination $target -Force
        }
    }

    return $page
}

function New-MsczPackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ScoreDirectory,

        [Parameter(Mandatory = $true)]
        [string]$OutputFile
    )

    if (-not (Test-Path -LiteralPath $ScoreDirectory -PathType Container)) {
        throw "MuseScore source directory does not exist: '$ScoreDirectory'."
    }

    $containerXml = Join-Path $ScoreDirectory "META-INF\container.xml"
    if (-not (Test-Path -LiteralPath $containerXml -PathType Leaf)) {
        throw "MuseScore source directory has no META-INF\container.xml: '$ScoreDirectory'."
    }

    $mscxFiles = @(Get-ChildItem -LiteralPath $ScoreDirectory -File -Filter "*.mscx")
    if ($mscxFiles.Count -ne 1) {
        throw "Expected exactly one MSCX in '$ScoreDirectory', found $($mscxFiles.Count)."
    }

    $styleFile = Join-Path $ScoreDirectory "score_style.mss"
    if (-not (Test-Path -LiteralPath $styleFile -PathType Leaf)) {
        throw "MuseScore source directory has no score_style.mss: '$ScoreDirectory'."
    }

    if (Test-Path -LiteralPath $OutputFile) {
        Remove-Item -LiteralPath $OutputFile -Force
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem

    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        $ScoreDirectory,
        $OutputFile,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false
    )

    if (-not (Test-Path -LiteralPath $OutputFile -PathType Leaf)) {
        throw "Failed to create MSCZ package '$OutputFile'."
    }
}

function Export-DirectMuseScoreSvg {
    param(
        [Parameter(Mandatory = $true)]
        [string]$MuseScore,

        [Parameter(Mandatory = $true)]
        [string]$InputScorePath,

        [Parameter(Mandatory = $true)]
        [string]$VariantDirectory,

        [Parameter(Mandatory = $true)]
        [string]$WorkDirectory
    )

    $directDir = Join-Path $WorkDirectory "musescore-svg"
    New-Item -ItemType Directory -Path $directDir -Force | Out-Null

    $target = Join-Path $directDir "score.svg"

    # In MuseScore 4 the converter handles SVG page-by-page. For a multi-page
    # score, exporting to score.svg produces score-1.svg, score-2.svg, ...
    Invoke-MuseScoreExport `
        -MuseScore $MuseScore `
        -InputFile $InputScorePath `
        -OutputFile $target `
        -LogDirectory $WorkDirectory

    return Normalize-SvgPages `
        -SourceDirectory $directDir `
        -DestinationDirectory $VariantDirectory
}

function Get-PdfPageCount {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PdfInfoExe,

        [Parameter(Mandatory = $true)]
        [string]$PdfPath,

        [Parameter(Mandatory = $true)]
        [string]$WorkDirectory
    )

    $stdout = Join-Path $WorkDirectory 'pdfinfo.stdout.log'
    $stderr = Join-Path $WorkDirectory 'pdfinfo.stderr.log'

    $argumentLine = Convert-ToProcessArgument -Value $PdfPath
    $process = Start-Process `
        -FilePath $PdfInfoExe `
        -ArgumentList $argumentLine `
        -RedirectStandardOutput $stdout `
        -RedirectStandardError $stderr `
        -WindowStyle Hidden `
        -Wait `
        -PassThru

    if ($process.ExitCode -ne 0) {
        $errorText = if (Test-Path -LiteralPath $stderr) {
            Get-Content -LiteralPath $stderr -Raw
        }
        else {
            ''
        }
        throw "pdfinfo failed with exit code $($process.ExitCode).`n$errorText"
    }

    $text = Get-Content -LiteralPath $stdout -Raw
    if ($text -notmatch '(?m)^Pages:\s+(\d+)\s*$') {
        throw 'Could not determine PDF page count from pdfinfo output.'
    }

    return [int]$Matches[1]
}

function Export-CairoSvg {
    param(
        [Parameter(Mandatory = $true)]
        [string]$MuseScore,

        [Parameter(Mandatory = $true)]
        [string]$PdfToCairoExe,

        [Parameter(Mandatory = $true)]
        [string]$PdfInfoExe,

        [Parameter(Mandatory = $true)]
        [string]$InputScorePath,

        [Parameter(Mandatory = $true)]
        [string]$VariantDirectory,

        [Parameter(Mandatory = $true)]
        [string]$WorkDirectory,

        [Parameter(Mandatory = $true)]
        [bool]$OptimizeWithSvgo,

        [Parameter(Mandatory = $true)]
        $Svgo
    )

    $pdf = Join-Path $WorkDirectory "score.pdf"
    Invoke-MuseScoreExport `
        -MuseScore $MuseScore `
        -InputFile $InputScorePath `
        -OutputFile $pdf `
        -LogDirectory $WorkDirectory

    $cairoDir = Join-Path $WorkDirectory "cairo"
    New-Item -ItemType Directory -Path $cairoDir -Force | Out-Null

    $pageCount = Get-PdfPageCount `
        -PdfInfoExe $PdfInfoExe `
        -PdfPath $pdf `
        -WorkDirectory $WorkDirectory

    for ($page = 1; $page -le $pageCount; $page++) {
        $rawSvg = Join-Path $cairoDir ("page-{0:D3}.svg" -f $page)

        # pdftocairo SVG output is safest one page at a time. Depending on
        # Poppler version, a multi-page '-svg' invocation may emit only page 1.
        Invoke-Checked `
            -Exe $PdfToCairoExe `
            -Arguments @(
                "-svg",
                "-f", $page.ToString(),
                "-l", $page.ToString(),
                $pdf,
                $rawSvg
            ) `
            -Description ("pdftocairo: PDF page {0}/{1} -> SVG" -f $page, $pageCount)

        if (-not (Test-Path -LiteralPath $rawSvg -PathType Leaf)) {
            throw "pdftocairo did not create '$rawSvg'."
        }

        $target = Join-Path $VariantDirectory ("page-{0:D3}.svg" -f $page)

        if (-not $OptimizeWithSvgo) {
            Copy-Item -LiteralPath $rawSvg -Destination $target -Force
            continue
        }

        $args = @()
        $args += $Svgo.PrefixArgs
        $args += @($rawSvg, "-o", $target)

        Invoke-Checked `
            -Exe $Svgo.Exe `
            -Arguments $args `
            -Description "SVGO: $([IO.Path]::GetFileName($rawSvg)) -> $([IO.Path]::GetFileName($target))"
    }

    return $pageCount
}

function Limit-MusicXmlScope {
    param(
        [Parameter(Mandatory = $true)]
        [string]$MusicXmlPath,

        [Nullable[int]]$SystemLimit = $null,

        [Nullable[int]]$MeasureLimit = $null
    )

    if ($null -eq $SystemLimit -and $null -eq $MeasureLimit) {
        throw "At least one of SystemLimit or MeasureLimit must be provided."
    }

    if ($null -ne $SystemLimit -and $SystemLimit -lt 1) {
        throw "SystemLimit must be >= 1."
    }

    if ($null -ne $MeasureLimit -and $MeasureLimit -lt 1) {
        throw "MeasureLimit must be >= 1."
    }

    $doc = New-Object System.Xml.XmlDocument
    $doc.PreserveWhitespace = $true
    $doc.XmlResolver = $null

    # MusicXML commonly contains an external Recordare DOCTYPE. XmlDocument.Load
    # may try to resolve that DTD over HTTP, which makes an offline/local dataset
    # preparation step fail with a seemingly unrelated 404. Parse the document
    # while explicitly disabling external resource resolution.
    $readerSettings = New-Object System.Xml.XmlReaderSettings
    $readerSettings.DtdProcessing = [System.Xml.DtdProcessing]::Parse
    $readerSettings.XmlResolver = $null

    $reader = [System.Xml.XmlReader]::Create(
        $MusicXmlPath,
        $readerSettings
    )

    try {
        $doc.Load($reader)
    }
    finally {
        $reader.Dispose()
    }

    $parts = @(
        $doc.SelectNodes("/*[local-name()='score-partwise']/*[local-name()='part']")
    )

    if ($parts.Count -eq 0) {
        throw "No <part> nodes found in MusicXML '$MusicXmlPath'."
    }

    # MuseScore writes visual system breaks as <print new-system="yes"/>.
    # A new page also necessarily starts a new system, so count that too.
    $firstPartMeasures = @(
        $parts[0].SelectNodes("./*[local-name()='measure']")
    )

    if ($firstPartMeasures.Count -eq 0) {
        throw "First MusicXML part contains no measures in '$MusicXmlPath'."
    }

    $systemStarts = New-Object System.Collections.Generic.List[int]
    $systemStarts.Add(0)

    for ($i = 1; $i -lt $firstPartMeasures.Count; $i++) {
        $measure = $firstPartMeasures[$i]
        $prints = @(
            $measure.SelectNodes("./*[local-name()='print']")
        )

        $startsNewSystem = $false

        foreach ($print in $prints) {
            if ($print.GetAttribute("new-system") -eq "yes" -or
                $print.GetAttribute("new-page") -eq "yes") {
                $startsNewSystem = $true
                break
            }
        }

        if ($startsNewSystem) {
            $systemStarts.Add($i)
        }
    }

    $detectedSystems = $systemStarts.Count
    $systemBasedKeepMeasureCount = $firstPartMeasures.Count

    if ($null -ne $SystemLimit -and $SystemLimit -lt $detectedSystems) {
        $systemBasedKeepMeasureCount = $systemStarts[$SystemLimit]
    }

    $measureBasedKeepMeasureCount = $firstPartMeasures.Count
    if ($null -ne $MeasureLimit) {
        $measureBasedKeepMeasureCount = [Math]::Min(
            $MeasureLimit,
            $firstPartMeasures.Count
        )
    }

    $keepMeasureCount = [Math]::Min(
        $systemBasedKeepMeasureCount,
        $measureBasedKeepMeasureCount
    )

    if ($keepMeasureCount -le 0) {
        throw "Failed to determine a positive number of measures to keep."
    }

    foreach ($part in $parts) {
        $measures = @(
            $part.SelectNodes("./*[local-name()='measure']")
        )

        for ($i = $measures.Count - 1; $i -ge $keepMeasureCount; $i--) {
            [void]$part.RemoveChild($measures[$i])
        }
    }

    $doc.Save($MusicXmlPath)

    $keptSystems = 0
    foreach ($systemStart in $systemStarts) {
        if ($systemStart -lt $keepMeasureCount) {
            $keptSystems++
        }
        else {
            break
        }
    }

    return [pscustomobject]@{
        RequestedSystems          = $SystemLimit
        RequestedMeasures         = $MeasureLimit
        DetectedSystems           = $detectedSystems
        OriginalMeasures          = $firstPartMeasures.Count
        SystemBasedMeasureLimit   = $systemBasedKeepMeasureCount
        MeasureBasedMeasureLimit  = $measureBasedKeepMeasureCount
        KeptMeasures              = $keepMeasureCount
        KeptSystems               = $keptSystems
    }
}


function Write-VariantReadme {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [System.IO.FileInfo]$SourceScore,

        [Parameter(Mandatory = $true)]
        [int]$RandomSeed,

        [Parameter(Mandatory = $true)]
        [int]$VariantIndex,

        [Nullable[int]]$MaxSystems,

        [Nullable[int]]$MaxMeasures,

        [Nullable[int]]$ReferenceMeasureCount,

        [Parameter(Mandatory = $true)]
        [double]$StaffSpaceFactor,

        [Parameter(Mandatory = $true)]
        [double]$OriginalSpatium,

        [Parameter(Mandatory = $true)]
        [double]$NewSpatium,

        [Parameter(Mandatory = $true)]
        [string]$MusicalSymbolFont,

        [Parameter(Mandatory = $true)]
        [string]$MusicalTextFont,

        [Parameter(Mandatory = $true)]
        [string]$Renderer,

        [Parameter(Mandatory = $true)]
        [int]$PageCount,

        [Parameter(Mandatory = $true)]
        [string]$MuseScore,

        [Parameter(Mandatory = $true)]
        [string]$PdfToCairoExe,

        [Parameter(Mandatory = $true)]
        [string]$PdfInfoExe,

        [Parameter(Mandatory = $true)]
        $Svgo
    )

    $hash = (Get-FileHash -LiteralPath $SourceScore.FullName -Algorithm SHA256).Hash
    $generated = (Get-Date).ToString("yyyy-MM-dd HH:mm:ss zzz")

    $pipeline = switch ($Renderer) {
        "MuseScore" { "MuseScore -> SVG" }
        "Cairo"     { "MuseScore -> PDF -> pdftocairo -> SVG" }
        "SVGO"      { "MuseScore -> PDF -> pdftocairo -> SVG -> SVGO" }
        default     { $Renderer }
    }

    $text = @"
# SVG Music stress-test variant

Source score: $($SourceScore.Name)
Source SHA256: $hash
Generated: $generated
Random seed: $RandomSeed
Variant index: $VariantIndex
Max systems: $(if ($null -ne $MaxSystems) { $MaxSystems } else { "all" })
Max measures: $(if ($null -ne $MaxMeasures) { $MaxMeasures } else { "all" })
Reference measures: $(if ($null -ne $ReferenceMeasureCount) { $ReferenceMeasureCount } else { "all" })

## Style

Staff-space factor: $StaffSpaceFactor
Original spatium: $($OriginalSpatium.ToString("0.########", [Globalization.CultureInfo]::InvariantCulture))
Effective spatium: $($NewSpatium.ToString("0.########", [Globalization.CultureInfo]::InvariantCulture))
Musical symbol font: $MusicalSymbolFont
Musical text font: $MusicalTextFont

## Rendering

Renderer: $Renderer
Pipeline: $pipeline
SVG pages: $PageCount

Files:
- reference.musicxml
- page-001.svg ... page-$("{0:D3}" -f $PageCount).svg

## Tools

MuseScore: $MuseScore
pdftocairo: $PdfToCairoExe
pdfinfo: $PdfInfoExe
SVGO: $($Svgo.Display)

## Notes

- Staff-space values 0.8 / 0.9 / 1.0 are treated as multipliers of the imported score's spatium.
- If MaxSystems and/or MaxMeasures are set, reference.musicxml is truncated before any rendering variant is produced.
- When both limits are present, the script keeps the smaller scope: first N systems OR first M measures, whichever ends earlier.
- MuseScore 4 style is edited in the generated score_style.mss sidecar; the complete uncompressed score folder is then packed into render-source.mscz before rendering.
- MuseScore converter calls use -f/--force, the CLI equivalent of GUI 'Open anyway', so recoverable score-validation warnings do not abort rendering.
- MuseScore first exports the source MSCZ to reference.musicxml.
- The rendering source is then re-imported from that same MusicXML into a temporary MSCX.
- This deliberately avoids comparing an MSCZ rendering against MuseScore's lossy/normalized MSCZ -> MusicXML export.
- Style values are modified directly in that temporary MSCX:
  - spatium / Spatium
  - musicalSymbolFont
  - musicalTextFont
- reference.musicxml is exported once from the source MSCZ; every rendered variant is then re-imported from that exact MusicXML.
- No MuseScore SVG class/id metadata is intentionally used by the recognition pipeline.
"@

    Set-Content -LiteralPath $Path -Value $text -Encoding UTF8
}

# -----------------------------------------------------------------------------
# Preflight
# -----------------------------------------------------------------------------

if (-not (Test-Path -LiteralPath $InputFolder -PathType Container)) {
    throw "Input folder does not exist: '$InputFolder'."
}

if ($VariantsPerScore -lt 1) {
    throw "VariantsPerScore must be >= 1."
}

if ($null -ne $MaxSystems -and $MaxSystems -lt 1) {
    throw "MaxSystems must be >= 1."
}

if ($null -ne $MaxMeasures -and $MaxMeasures -lt 1) {
    throw "MaxMeasures must be >= 1."
}

$maxCombinations = $StaffSpaceFactors.Count * $FontPairs.Count * $Renderers.Count
if ($VariantsPerScore -gt $maxCombinations) {
    throw "VariantsPerScore=$VariantsPerScore exceeds the $maxCombinations unique combinations available."
}

$InputFolder = (Resolve-Path -LiteralPath $InputFolder).Path
$MuseScoreExe = Resolve-Tool -Value $MuseScoreExe -FriendlyName "MuseScore"
$PdfToCairo = Resolve-Tool -Value $PdfToCairo -FriendlyName "pdftocairo"
$PdfInfo = Resolve-Tool -Value $PdfInfo -FriendlyName "pdfinfo"
$Svgo = Resolve-Svgo

$actualSeed = if ($null -ne $Seed) {
    [int]$Seed
}
else {
    [int]([DateTime]::UtcNow.Ticks -band 0x7fffffff)
}

$rng = [System.Random]::new($actualSeed)

$scores = @(
    Get-ChildItem -LiteralPath $InputFolder -File -Filter "*.mscz" |
    Sort-Object Name
)

if ($scores.Count -eq 0) {
    throw "No *.mscz files found directly in '$InputFolder'."
}

Write-Host ""
Write-Host "MuseScore stress-test dataset generator" -ForegroundColor Cyan
Write-Host "Folder:             $InputFolder"
Write-Host "Scores:             $($scores.Count)"
Write-Host "Variants per score: $VariantsPerScore"
Write-Host "Max systems:        $(if ($null -ne $MaxSystems) { $MaxSystems } else { 'all' })"
Write-Host "Max measures:       $(if ($null -ne $MaxMeasures) { $MaxMeasures } else { 'all' })"
Write-Host "Random seed:        $actualSeed"

if ($Diagnostics) {
    Write-Host "MuseScore:          $MuseScoreExe"
    Write-Host "pdftocairo:         $PdfToCairo"
    Write-Host "pdfinfo:            $PdfInfo"
    Write-Host "SVGO:               $($Svgo.Display)"
}

Write-Host ""

# -----------------------------------------------------------------------------
# Generate
# -----------------------------------------------------------------------------

foreach ($score in $scores) {
    Write-Host "=== $($score.Name) ===" -ForegroundColor Yellow

    $scoreOutput = Join-Path $InputFolder $score.BaseName
    New-Item -ItemType Directory -Path $scoreOutput -Force | Out-Null

    # Export the semantic reference exactly once. Every visual variant is rendered
    # from this MusicXML after MuseScore re-imports it, so reference and picture
    # describe the same MuseScore interpretation.
    $scoreReferenceWork = Join-Path $scoreOutput ("_reference_" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Path $scoreReferenceWork -Force | Out-Null
    $canonicalReference = Join-Path $scoreReferenceWork "reference.musicxml"

    try {
        Invoke-MuseScoreExport `
            -MuseScore $MuseScoreExe `
            -InputFile $score.FullName `
            -OutputFile $canonicalReference

        if (-not (Test-Path -LiteralPath $canonicalReference -PathType Leaf)) {
            throw "MuseScore did not create MusicXML reference for '$($score.Name)'."
        }

        $referenceTrim = $null

        if ($null -ne $MaxSystems -or $null -ne $MaxMeasures) {
            $referenceTrim = Limit-MusicXmlScope `
                -MusicXmlPath $canonicalReference `
                -SystemLimit $MaxSystems `
                -MeasureLimit $MaxMeasures

            Write-Host (
                "    reference: {0} system(s), {1} measure(s)" -f
                $referenceTrim.KeptSystems,
                $referenceTrim.KeptMeasures
            ) -ForegroundColor DarkGray
        }

        $usedCombinations = New-Object 'System.Collections.Generic.HashSet[string]'

        for ($variantIndex = 1; $variantIndex -le $VariantsPerScore; $variantIndex++) {
        do {
            $staffFactor = $StaffSpaceFactors[$rng.Next($StaffSpaceFactors.Count)]
            $fontName = @($FontPairs.Keys)[$rng.Next($FontPairs.Count)]
            $textFont = $FontPairs[$fontName]
            $renderer = $Renderers[$rng.Next($Renderers.Count)]

            $combinationKey = "$fontName|$staffFactor|$renderer"
        }
        while (-not $usedCombinations.Add($combinationKey))

        $fontToken = ($fontName -replace '\s+', '_')
        $staffToken = ([int][Math]::Round($staffFactor * 10)).ToString("00")
        $variantName = "${fontToken}_ss${staffToken}_${renderer}"
        $variantDirectory = Join-Path $scoreOutput $variantName

        Write-Host "  -> $variantName" -ForegroundColor Green

        if (Test-Path -LiteralPath $variantDirectory) {
            if (-not $Force) {
                throw "Variant folder already exists: '$variantDirectory'. Use -Force to replace it."
            }

            Remove-Item -LiteralPath $variantDirectory -Recurse -Force
        }

        New-Item -ItemType Directory -Path $variantDirectory -Force | Out-Null

        $workDirectory = Join-Path $variantDirectory ("_work_" + [Guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Path $workDirectory -Force | Out-Null
        $variantSucceeded = $false

        try {
            # 1. Copy the score-level semantic reference into this variant.
            $musicXml = Join-Path $variantDirectory "reference.musicxml"
            Copy-Item -LiteralPath $canonicalReference -Destination $musicXml -Force

            # 2. Re-import exactly that MusicXML through MuseScore into an
            #    isolated uncompressed MuseScore score folder.
            $scoreSourceDirectory = Join-Path $workDirectory "score-source"
            New-Item -ItemType Directory -Path $scoreSourceDirectory -Force | Out-Null

            $styledMscx = Join-Path $scoreSourceDirectory "styled.mscx"
            Invoke-MuseScoreExport `
                -MuseScore $MuseScoreExe `
                -InputFile $canonicalReference `
                -OutputFile $styledMscx `
                -LogDirectory $workDirectory

            # 3. Modify only representation/layout style in score_style.mss.
            $styleInfo = Update-MuseScoreStyle `
                -MscxPath $styledMscx `
                -StaffSpaceFactor $staffFactor `
                -MusicalSymbolFont $fontName `
                -MusicalTextFont $textFont

            if ($Diagnostics) {
                Write-Host (
                    "    style: {0} / {1}; spatium {2:0.###} -> {3:0.###}" -f
                    $fontName,
                    $textFont,
                    $styleInfo.OriginalSpatium,
                    $styleInfo.NewSpatium
                ) -ForegroundColor DarkGray
            }

            # 4. Package the complete uncompressed score folder ourselves.
            #    The render source is now a self-contained normal MSCZ, so the
            #    edited score_style.mss is guaranteed to travel with the score.
            $renderSource = Join-Path $workDirectory "render-source.mscz"
            New-MsczPackage `
                -ScoreDirectory $scoreSourceDirectory `
                -OutputFile $renderSource

            if ($Diagnostics) {
                Write-Host "    packaged render source: render-source.mscz" `
                    -ForegroundColor DarkGray
            }

            # 5. Render SVG pages through one randomly selected pipeline.
            $pageCount = switch ($renderer) {
                "MuseScore" {
                    Export-DirectMuseScoreSvg `
                        -MuseScore $MuseScoreExe `
                        -InputScorePath $renderSource `
                        -VariantDirectory $variantDirectory `
                        -WorkDirectory $workDirectory
                }

                "Cairo" {
                    Export-CairoSvg `
                        -MuseScore $MuseScoreExe `
                        -PdfToCairoExe $PdfToCairo `
                        -PdfInfoExe $PdfInfo `
                        -InputScorePath $renderSource `
                        -VariantDirectory $variantDirectory `
                        -WorkDirectory $workDirectory `
                        -OptimizeWithSvgo $false `
                        -Svgo $Svgo
                }

                "SVGO" {
                    Export-CairoSvg `
                        -MuseScore $MuseScoreExe `
                        -PdfToCairoExe $PdfToCairo `
                        -PdfInfoExe $PdfInfo `
                        -InputScorePath $renderSource `
                        -VariantDirectory $variantDirectory `
                        -WorkDirectory $workDirectory `
                        -OptimizeWithSvgo $true `
                        -Svgo $Svgo
                }

                default {
                    throw "Unknown renderer: '$renderer'."
                }
            }

            # 5. Reproducibility note.
            Write-VariantReadme `
                -Path (Join-Path $variantDirectory "README.md") `
                -SourceScore $score `
                -RandomSeed $actualSeed `
                -VariantIndex $variantIndex `
                -MaxSystems $MaxSystems `
                -MaxMeasures $MaxMeasures `
                -ReferenceMeasureCount $(if ($null -ne $referenceTrim) { $referenceTrim.KeptMeasures } else { $null }) `
                -StaffSpaceFactor $staffFactor `
                -OriginalSpatium $styleInfo.OriginalSpatium `
                -NewSpatium $styleInfo.NewSpatium `
                -MusicalSymbolFont $fontName `
                -MusicalTextFont $textFont `
                -Renderer $renderer `
                -PageCount $pageCount `
                -MuseScore $MuseScoreExe `
                -PdfToCairoExe $PdfToCairo `
                -PdfInfoExe $PdfInfo `
                -Svgo $Svgo

            if ($KeepIntermediates) {
                Copy-Item `
                    -LiteralPath $renderSource `
                    -Destination (Join-Path $variantDirectory "render-source.mscz") `
                    -Force

                Copy-Item `
                    -LiteralPath $styledMscx `
                    -Destination (Join-Path $variantDirectory "styled.mscx") `
                    -Force

                $scoreStyle = Join-Path $scoreSourceDirectory "score_style.mss"
                if (Test-Path -LiteralPath $scoreStyle) {
                    Copy-Item `
                        -LiteralPath $scoreStyle `
                        -Destination (Join-Path $variantDirectory "score_style.mss") `
                        -Force
                }

                $pdf = Join-Path $workDirectory "score.pdf"
                if (Test-Path -LiteralPath $pdf) {
                    Copy-Item `
                        -LiteralPath $pdf `
                        -Destination (Join-Path $variantDirectory "rendered.pdf") `
                        -Force
                }
            }

            Write-Host "    OK: $pageCount SVG page(s)" -ForegroundColor Green
            $variantSucceeded = $true
        }
        catch {
            Write-Host "    FAILED: $($_.Exception.Message)" -ForegroundColor Red
            Write-Host "    Preserving diagnostics in: $workDirectory" -ForegroundColor Yellow

            $failureReadme = Join-Path $variantDirectory "FAILED.txt"
            $failureText = @"
Generation failed.

Source: $($score.FullName)
Variant: $variantName
Seed: $actualSeed

Error:
$($_.Exception.ToString())

Diagnostic work directory:
$workDirectory
"@
            Set-Content -LiteralPath $failureReadme -Value $failureText -Encoding UTF8
            throw
        }
        finally {
            if ($variantSucceeded -and (Test-Path -LiteralPath $workDirectory)) {
                Remove-Item -LiteralPath $workDirectory -Recurse -Force
            }
        }
        }
    }
    finally {
        if (Test-Path -LiteralPath $scoreReferenceWork) {
            Remove-Item -LiteralPath $scoreReferenceWork -Recurse -Force
        }
    }

    Write-Host ""
}

Write-Host "Done." -ForegroundColor Cyan
Write-Host "Seed: $actualSeed"
