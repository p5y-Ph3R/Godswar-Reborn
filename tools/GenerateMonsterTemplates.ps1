param(
    [string]$ClientRoot = "C:\Godswar Origin",
    [string]$CSharpOutputPath = "C:\Reborn\src\Godswar.Server\State\MonsterTemplateSeed.Generated.cs",
    [string]$SqlOutputPath = "C:\Reborn\database\postgres\009_monsters.sql"
)

. (Join-Path $PSScriptRoot "template-generators\monster\Initialize.ps1")
. (Join-Path $PSScriptRoot "template-generators\monster\ReadSourceData.ps1")
. (Join-Path $PSScriptRoot "template-generators\monster\BuildCSharp.ps1")
. (Join-Path $PSScriptRoot "template-generators\monster\BuildSql.ps1")
