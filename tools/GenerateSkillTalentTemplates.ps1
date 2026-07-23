param(
    [string]$ClientRoot = "C:\Godswar Origin",
    [string]$CSharpOutputPath = "C:\Reborn\src\Godswar.Server\State\SkillTalentSeed.Generated.cs",
    [string]$SqlOutputPath = "C:\Reborn\database\postgres\006_skills_and_talents.sql"
)

. (Join-Path $PSScriptRoot "template-generators\skill-talent\Initialize.ps1")
. (Join-Path $PSScriptRoot "template-generators\skill-talent\ReadSourceData.ps1")
. (Join-Path $PSScriptRoot "template-generators\skill-talent\BuildCSharp.ps1")
. (Join-Path $PSScriptRoot "template-generators\skill-talent\BuildSqlTemplates.ps1")
. (Join-Path $PSScriptRoot "template-generators\skill-talent\BuildSqlRuntime.ps1")
