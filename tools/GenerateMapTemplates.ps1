param(
    [string]$ClientRoot = "C:\Godswar Origin",
    [string]$CSharpOutputPath = "C:\Reborn\src\Godswar.Server\State\MapTemplateSeed.Generated.cs",
    [string]$SqlOutputPath = "C:\Reborn\database\postgres\008_maps.sql"
)

. (Join-Path $PSScriptRoot "template-generators\map\Initialize.ps1")
. (Join-Path $PSScriptRoot "template-generators\map\ReadSourceData.ps1")
. (Join-Path $PSScriptRoot "template-generators\map\BuildCSharp.ps1")
. (Join-Path $PSScriptRoot "template-generators\map\BuildSql.ps1")
