param(
    [string]$ClientRoot = "C:\Godswar Origin",
    [string]$CSharpOutputPath = "C:\Reborn\src\Godswar.Server\State\NpcTemplateSeed.Generated.cs",
    [string]$SqlOutputPath = "C:\Reborn\database\postgres\007_npcs.sql"
)

. (Join-Path $PSScriptRoot "template-generators\npc\Initialize.ps1")
. (Join-Path $PSScriptRoot "template-generators\npc\ReadSourceData.ps1")
. (Join-Path $PSScriptRoot "template-generators\npc\BuildCSharp.ps1")
. (Join-Path $PSScriptRoot "template-generators\npc\BuildSql.ps1")
