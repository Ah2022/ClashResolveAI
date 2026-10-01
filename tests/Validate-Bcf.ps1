param([Parameter(Mandatory)][string]$Path)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($Path))
try {
    $count = 0
    foreach($entry in $archive.Entries) {
        $schemaName = switch -Wildcard ($entry.Name) { 'markup.bcf' {'markup'} '*.bcfv' {'visinfo'} 'bcf.version' {'version'} 'project.bcfp' {'project'} }
        if (!$schemaName) { continue }
        $settings = [Xml.XmlReaderSettings]::new()
        $settings.ValidationType = [Xml.ValidationType]::Schema
        $settings.Schemas.Add($null,(Join-Path $PSScriptRoot "schemas\$schemaName.xsd")) | Out-Null
        $stream = $entry.Open()
        $reader = [Xml.XmlReader]::Create($stream,$settings)
        try { while($reader.Read()){}; $count++ } finally { $reader.Dispose(); $stream.Dispose() }
        if ($entry.Name -eq 'markup.bcf') {
            $stream = $entry.Open(); $textReader = [IO.StreamReader]::new($stream)
            try { [xml]$markup = $textReader.ReadToEnd() } finally { $textReader.Dispose() }
            $prefix = $entry.FullName.Substring(0,$entry.FullName.Length-$entry.Name.Length)
            if ($prefix.TrimEnd('/') -ne $markup.Markup.Topic.Guid) { throw 'Topic GUID and folder differ' }
            foreach($view in $markup.Markup.Viewpoints) {
                if (!$archive.GetEntry($prefix + $view.Viewpoint)) { throw "Missing viewpoint: $($view.Viewpoint)" }
            }
        }
    }
    Write-Output "PASS: $count BCF XML files validated against official BCF 2.1 schemas; all viewpoint references exist."
} finally { $archive.Dispose() }
