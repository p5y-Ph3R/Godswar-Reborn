[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS skill_templates (")
[void]$sql.AppendLine("    skill_id integer PRIMARY KEY,")
[void]$sql.AppendLine("    display_name varchar(128) NOT NULL,")
[void]$sql.AppendLine("    base_name varchar(128) NOT NULL,")
[void]$sql.AppendLine("    skill_level smallint,")
[void]$sql.AppendLine("    class_ids smallint[] NOT NULL DEFAULT '{}',")
[void]$sql.AppendLine("    previous_skill_id integer,")
[void]$sql.AppendLine("    min_level integer,")
[void]$sql.AppendLine("    max_level integer,")
[void]$sql.AppendLine("    description text NOT NULL DEFAULT '',")
[void]$sql.AppendLine("    target integer NOT NULL DEFAULT 0,")
[void]$sql.AppendLine("    affect_obj integer NOT NULL DEFAULT 0,")
[void]$sql.AppendLine("    distance numeric NOT NULL DEFAULT 0,")
[void]$sql.AppendLine("    effect_range numeric NOT NULL DEFAULT 0,")
[void]$sql.AppendLine("    property integer NOT NULL DEFAULT 0,")
[void]$sql.AppendLine("    mp integer NOT NULL DEFAULT 0,")
[void]$sql.AppendLine("    power1 numeric NOT NULL DEFAULT 0,")
[void]$sql.AppendLine("    power2 numeric NOT NULL DEFAULT 0,")
[void]$sql.AppendLine("    stats jsonb NOT NULL DEFAULT '{}'::jsonb")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("ALTER TABLE skill_templates ADD COLUMN IF NOT EXISTS target integer NOT NULL DEFAULT 0;")
[void]$sql.AppendLine("ALTER TABLE skill_templates ADD COLUMN IF NOT EXISTS affect_obj integer NOT NULL DEFAULT 0;")
[void]$sql.AppendLine("ALTER TABLE skill_templates ADD COLUMN IF NOT EXISTS distance numeric NOT NULL DEFAULT 0;")
[void]$sql.AppendLine("ALTER TABLE skill_templates ADD COLUMN IF NOT EXISTS effect_range numeric NOT NULL DEFAULT 0;")
[void]$sql.AppendLine("ALTER TABLE skill_templates ADD COLUMN IF NOT EXISTS property integer NOT NULL DEFAULT 0;")
[void]$sql.AppendLine("ALTER TABLE skill_templates ADD COLUMN IF NOT EXISTS mp integer NOT NULL DEFAULT 0;")
[void]$sql.AppendLine("ALTER TABLE skill_templates ADD COLUMN IF NOT EXISTS power1 numeric NOT NULL DEFAULT 0;")
[void]$sql.AppendLine("ALTER TABLE skill_templates ADD COLUMN IF NOT EXISTS power2 numeric NOT NULL DEFAULT 0;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_skill_templates_class_ids ON skill_templates USING gin (class_ids);")
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_skill_templates_base_name ON skill_templates (base_name);")
[void]$sql.AppendLine()
[void]$sql.AppendLine("INSERT INTO skill_templates (skill_id, display_name, base_name, skill_level, class_ids, previous_skill_id, min_level, max_level, description, target, affect_obj, distance, effect_range, property, mp, power1, power2, stats)")
[void]$sql.AppendLine("VALUES")
for ($i = 0; $i -lt $skills.Count; $i++) {
    $skill = $skills[$i]
    $suffix = if ($i -eq $skills.Count - 1) { "" } else { "," }
    [void]$sql.AppendLine(
        "    (" +
        "$($skill.SkillId), $(ConvertTo-SqlString $skill.DisplayName), $(ConvertTo-SqlString $skill.BaseName), $(ConvertTo-SqlNullableSmallint $skill.SkillLevel), " +
        "$(ConvertTo-SqlSmallintArray $skill.ClassIds), $(ConvertTo-SqlNullableInt $skill.PreviousSkillId), $(ConvertTo-SqlNullableInt $skill.MinLevel), $(ConvertTo-SqlNullableInt $skill.MaxLevel), " +
        "$(ConvertTo-SqlString $skill.Description), $($skill.Target), $($skill.AffectObj), $(ConvertTo-SqlNumeric $skill.Distance), $(ConvertTo-SqlNumeric $skill.Range), " +
        "$($skill.Property), $($skill.Mp), $(ConvertTo-SqlNumeric $skill.Power1), $(ConvertTo-SqlNumeric $skill.Power2), $(ConvertTo-SqlString $skill.StatsJson)::jsonb)$suffix")
}
[void]$sql.AppendLine("ON CONFLICT (skill_id) DO UPDATE")
[void]$sql.AppendLine("SET display_name = EXCLUDED.display_name,")
[void]$sql.AppendLine("    base_name = EXCLUDED.base_name,")
[void]$sql.AppendLine("    skill_level = EXCLUDED.skill_level,")
[void]$sql.AppendLine("    class_ids = EXCLUDED.class_ids,")
[void]$sql.AppendLine("    previous_skill_id = EXCLUDED.previous_skill_id,")
[void]$sql.AppendLine("    min_level = EXCLUDED.min_level,")
[void]$sql.AppendLine("    max_level = EXCLUDED.max_level,")
[void]$sql.AppendLine("    description = EXCLUDED.description,")
[void]$sql.AppendLine("    target = EXCLUDED.target,")
[void]$sql.AppendLine("    affect_obj = EXCLUDED.affect_obj,")
[void]$sql.AppendLine("    distance = EXCLUDED.distance,")
[void]$sql.AppendLine("    effect_range = EXCLUDED.effect_range,")
[void]$sql.AppendLine("    property = EXCLUDED.property,")
[void]$sql.AppendLine("    mp = EXCLUDED.mp,")
[void]$sql.AppendLine("    power1 = EXCLUDED.power1,")
[void]$sql.AppendLine("    power2 = EXCLUDED.power2,")
[void]$sql.AppendLine("    stats = EXCLUDED.stats;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS skill_book_templates (")
[void]$sql.AppendLine("    item_id integer PRIMARY KEY,")
[void]$sql.AppendLine("    name_key varchar(128) NOT NULL,")
[void]$sql.AppendLine("    display_name varchar(128) NOT NULL,")
[void]$sql.AppendLine("    skill_id integer NOT NULL REFERENCES skill_templates(skill_id),")
[void]$sql.AppendLine("    base_name varchar(128) NOT NULL,")
[void]$sql.AppendLine("    skill_level smallint,")
[void]$sql.AppendLine("    class_ids smallint[] NOT NULL DEFAULT '{}',")
[void]$sql.AppendLine("    min_level integer,")
[void]$sql.AppendLine("    max_level integer,")
[void]$sql.AppendLine("    previous_skill_id integer,")
[void]$sql.AppendLine("    stats jsonb NOT NULL DEFAULT '{}'::jsonb")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_skill_book_templates_skill_id ON skill_book_templates (skill_id);")
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_skill_book_templates_class_ids ON skill_book_templates USING gin (class_ids);")
[void]$sql.AppendLine()
[void]$sql.AppendLine("INSERT INTO skill_book_templates (item_id, name_key, display_name, skill_id, base_name, skill_level, class_ids, min_level, max_level, previous_skill_id, stats)")
[void]$sql.AppendLine("VALUES")
for ($i = 0; $i -lt $skillBooks.Count; $i++) {
    $book = $skillBooks[$i]
    $suffix = if ($i -eq $skillBooks.Count - 1) { "" } else { "," }
    [void]$sql.AppendLine(
        "    (" +
        "$($book.ItemId), $(ConvertTo-SqlString $book.NameKey), $(ConvertTo-SqlString $book.DisplayName), $($book.SkillId), $(ConvertTo-SqlString $book.BaseName), " +
        "$(ConvertTo-SqlNullableSmallint $book.SkillLevel), $(ConvertTo-SqlSmallintArray $book.ClassIds), $(ConvertTo-SqlNullableInt $book.MinLevel), $(ConvertTo-SqlNullableInt $book.MaxLevel), " +
        "$(ConvertTo-SqlNullableInt $book.PreviousSkillId), $(ConvertTo-SqlString $book.StatsJson)::jsonb)$suffix")
}
[void]$sql.AppendLine("ON CONFLICT (item_id) DO UPDATE")
[void]$sql.AppendLine("SET name_key = EXCLUDED.name_key,")
[void]$sql.AppendLine("    display_name = EXCLUDED.display_name,")
[void]$sql.AppendLine("    skill_id = EXCLUDED.skill_id,")
[void]$sql.AppendLine("    base_name = EXCLUDED.base_name,")
[void]$sql.AppendLine("    skill_level = EXCLUDED.skill_level,")
[void]$sql.AppendLine("    class_ids = EXCLUDED.class_ids,")
[void]$sql.AppendLine("    min_level = EXCLUDED.min_level,")
[void]$sql.AppendLine("    max_level = EXCLUDED.max_level,")
[void]$sql.AppendLine("    previous_skill_id = EXCLUDED.previous_skill_id,")
[void]$sql.AppendLine("    stats = EXCLUDED.stats;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS character_skills (")
[void]$sql.AppendLine("    user_id integer NOT NULL REFERENCES character_base(id) ON DELETE CASCADE,")
[void]$sql.AppendLine("    skill_id integer NOT NULL REFERENCES skill_templates(skill_id),")
[void]$sql.AppendLine("    skill_level smallint NOT NULL DEFAULT 1,")
[void]$sql.AppendLine("    acquired_at timestamptz NOT NULL DEFAULT now(),")
[void]$sql.AppendLine("    source varchar(64) NOT NULL DEFAULT 'manual',")
[void]$sql.AppendLine("    PRIMARY KEY (user_id, skill_id)")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_character_skills_skill_id ON character_skills (skill_id);")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE TABLE IF NOT EXISTS character_talents (")
[void]$sql.AppendLine("    user_id integer NOT NULL REFERENCES character_base(id) ON DELETE CASCADE,")
[void]$sql.AppendLine("    talent_id integer NOT NULL REFERENCES talent_templates(id),")
[void]$sql.AppendLine("    rank smallint NOT NULL DEFAULT 0,")
[void]$sql.AppendLine("    updated_at timestamptz NOT NULL DEFAULT now(),")
[void]$sql.AppendLine("    PRIMARY KEY (user_id, talent_id)")
[void]$sql.AppendLine(");")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE INDEX IF NOT EXISTS ix_character_talents_talent_id ON character_talents (talent_id);")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE OR REPLACE VIEW class_talents AS")
[void]$sql.AppendLine("SELECT")
[void]$sql.AppendLine("    ct.id AS class_id,")
[void]$sql.AppendLine("    ct.display_name AS class_name,")
[void]$sql.AppendLine("    tt.tree_order,")
[void]$sql.AppendLine("    tt.id AS talent_id,")
[void]$sql.AppendLine("    tt.name,")
[void]$sql.AppendLine("    tt.prefix_id,")
[void]$sql.AppendLine("    tt.required_prefix_rank,")
[void]$sql.AppendLine("    tt.required_total_rank,")
[void]$sql.AppendLine("    tt.equip_request,")
[void]$sql.AppendLine("    tt.effect_type,")
[void]$sql.AppendLine("    tet.display_name AS effect_name,")
[void]$sql.AppendLine("    tt.effect_value,")
[void]$sql.AppendLine("    tt.is_percent")
[void]$sql.AppendLine("FROM talent_templates tt")
[void]$sql.AppendLine("JOIN class_templates ct ON ct.id = tt.class_id")
[void]$sql.AppendLine("JOIN talent_effect_templates tet ON tet.id = tt.effect_id;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE OR REPLACE VIEW class_skills AS")
[void]$sql.AppendLine("SELECT")
[void]$sql.AppendLine("    ct.id AS class_id,")
[void]$sql.AppendLine("    ct.display_name AS class_name,")
[void]$sql.AppendLine("    st.skill_id,")
[void]$sql.AppendLine("    st.display_name,")
[void]$sql.AppendLine("    st.base_name,")
[void]$sql.AppendLine("    st.skill_level,")
[void]$sql.AppendLine("    st.previous_skill_id,")
[void]$sql.AppendLine("    st.min_level,")
[void]$sql.AppendLine("    st.max_level,")
[void]$sql.AppendLine("    st.description")
[void]$sql.AppendLine("FROM skill_templates st")
[void]$sql.AppendLine("CROSS JOIN LATERAL unnest(st.class_ids) AS skill_class(class_id)")
[void]$sql.AppendLine("JOIN class_templates ct ON ct.id = skill_class.class_id;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE OR REPLACE VIEW class_skill_books AS")
[void]$sql.AppendLine("SELECT")
[void]$sql.AppendLine("    ct.id AS class_id,")
[void]$sql.AppendLine("    ct.display_name AS class_name,")
[void]$sql.AppendLine("    sbt.item_id,")
[void]$sql.AppendLine("    sbt.name_key,")
[void]$sql.AppendLine("    sbt.display_name,")
[void]$sql.AppendLine("    sbt.skill_id,")
[void]$sql.AppendLine("    sbt.base_name,")
[void]$sql.AppendLine("    sbt.skill_level,")
[void]$sql.AppendLine("    sbt.min_level,")
[void]$sql.AppendLine("    sbt.max_level,")
[void]$sql.AppendLine("    sbt.previous_skill_id")
[void]$sql.AppendLine("FROM skill_book_templates sbt")
[void]$sql.AppendLine("CROSS JOIN LATERAL unnest(sbt.class_ids) AS book_class(class_id)")
[void]$sql.AppendLine("JOIN class_templates ct ON ct.id = book_class.class_id;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE OR REPLACE VIEW character_available_talents AS")
[void]$sql.AppendLine("SELECT")
[void]$sql.AppendLine("    cb.id AS user_id,")
[void]$sql.AppendLine("    cb.name AS character_name,")
[void]$sql.AppendLine("    ct.display_name AS class_name,")
[void]$sql.AppendLine("    tt.tree_order,")
[void]$sql.AppendLine("    tt.id AS talent_id,")
[void]$sql.AppendLine("    tt.name,")
[void]$sql.AppendLine("    COALESCE(chtt.rank, 0)::smallint AS current_rank,")
[void]$sql.AppendLine("    tt.required_prefix_rank,")
[void]$sql.AppendLine("    tt.required_total_rank,")
[void]$sql.AppendLine("    tt.effect_type,")
[void]$sql.AppendLine("    tt.effect_value,")
[void]$sql.AppendLine("    tt.is_percent")
[void]$sql.AppendLine("FROM character_base cb")
[void]$sql.AppendLine("JOIN class_templates ct ON ct.id = cb.profession")
[void]$sql.AppendLine("JOIN talent_templates tt ON tt.class_id = cb.profession")
[void]$sql.AppendLine("LEFT JOIN character_talents chtt ON chtt.user_id = cb.id AND chtt.talent_id = tt.id;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("CREATE OR REPLACE VIEW character_available_skills AS")
[void]$sql.AppendLine("SELECT")
[void]$sql.AppendLine("    cb.id AS user_id,")
[void]$sql.AppendLine("    cb.name AS character_name,")
[void]$sql.AppendLine("    ct.display_name AS class_name,")
[void]$sql.AppendLine("    st.skill_id,")
[void]$sql.AppendLine("    st.display_name,")
[void]$sql.AppendLine("    st.base_name,")
[void]$sql.AppendLine("    st.skill_level,")
[void]$sql.AppendLine("    st.previous_skill_id,")
[void]$sql.AppendLine("    st.min_level,")
[void]$sql.AppendLine("    cb.fighter_job_lv AS character_level,")
[void]$sql.AppendLine("    (COALESCE(st.min_level, 1) <= cb.fighter_job_lv) AS level_unlocked,")
[void]$sql.AppendLine("    (chs.skill_id IS NOT NULL) AS learned,")
[void]$sql.AppendLine("    chs.source AS learned_source")
[void]$sql.AppendLine("FROM character_base cb")
[void]$sql.AppendLine("JOIN class_templates ct ON ct.id = cb.profession")
[void]$sql.AppendLine("JOIN skill_templates st ON cb.profession = ANY(st.class_ids)")
[void]$sql.AppendLine("LEFT JOIN character_skills chs ON chs.user_id = cb.id AND chs.skill_id = st.skill_id;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("INSERT INTO character_skills (user_id, skill_id, skill_level, source)")
[void]$sql.AppendLine("SELECT")
[void]$sql.AppendLine("    cb.id,")
[void]$sql.AppendLine("    st.skill_id,")
[void]$sql.AppendLine("    st.skill_level,")
[void]$sql.AppendLine("    'starter'")
[void]$sql.AppendLine("FROM character_base cb")
[void]$sql.AppendLine("JOIN skill_templates st ON cb.profession = ANY(st.class_ids)")
[void]$sql.AppendLine("WHERE st.previous_skill_id IS NULL")
[void]$sql.AppendLine("  AND COALESCE(st.min_level, 1) <= cb.fighter_job_lv")
[void]$sql.AppendLine("  AND st.skill_level = 1")
[void]$sql.AppendLine("ON CONFLICT (user_id, skill_id) DO NOTHING;")
[void]$sql.AppendLine()
[void]$sql.AppendLine("-- Temporary compatibility grant while the level-40 quest/book acquisition")
[void]$sql.AppendLine("-- flow is not yet implemented by the replacement server.")
[void]$sql.AppendLine("INSERT INTO character_skills (user_id, skill_id, skill_level, source)")
[void]$sql.AppendLine("SELECT cb.id, st.skill_id, 1, 'mount-compatibility'")
[void]$sql.AppendLine("FROM character_base cb")
[void]$sql.AppendLine("JOIN skill_templates st")
[void]$sql.AppendLine("  ON st.skill_id = 4904")
[void]$sql.AppendLine(" AND cb.profession = ANY(st.class_ids)")
[void]$sql.AppendLine("ON CONFLICT (user_id, skill_id) DO NOTHING;")

$generatedCSharpPaths =
    [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::OrdinalIgnoreCase)
foreach ($generatedFile in $csharpGeneratedFiles.GetEnumerator()) {
    [System.IO.File]::WriteAllText(
        $generatedFile.Key,
        $generatedFile.Value,
        [System.Text.Encoding]::UTF8)
    [void]$generatedCSharpPaths.Add(
        [System.IO.Path]::GetFullPath($generatedFile.Key))
}

[System.IO.File]::WriteAllText(
    $SqlOutputPath,
    $sql.ToString(),
    [System.Text.Encoding]::UTF8)

$removedStaleCSharpChunks = 0
foreach (
    $candidate in
        [System.IO.Directory]::EnumerateFiles(
            $csharpChunkOutputDirectory,
            "*.cs",
            [System.IO.SearchOption]::TopDirectoryOnly)
) {
    $candidatePath = [System.IO.Path]::GetFullPath($candidate)
    if ($generatedCSharpPaths.Contains($candidatePath)) {
        continue
    }

    $candidateName = [System.IO.Path]::GetFileName($candidatePath)
    if (-not [System.Text.RegularExpressions.Regex]::IsMatch(
        $candidateName,
        $csharpChunkFileNamePattern,
        [System.Text.RegularExpressions.RegexOptions]::IgnoreCase
    )) {
        continue
    }

    $reader = $null
    try {
        $reader = [System.IO.File]::OpenText($candidatePath)
        $firstLine = $reader.ReadLine()
    } finally {
        if ($null -ne $reader) {
            $reader.Dispose()
        }
    }

    if ($firstLine -ne $csharpChunkOwnershipMarker) {
        continue
    }

    [System.IO.File]::Delete($candidatePath)
    $removedStaleCSharpChunks++
}

Write-Host "Generated $($classes.Count) classes, $($talentEffects.Count) talent effects, $($talents.Count) talents, $($skills.Count) skills, and $($skillBooks.Count) skill books."
Write-Host "C#:  $CSharpOutputPath + $($csharpGeneratedFiles.Count - 1) chunks"
Write-Host "Removed stale owned C# chunks: $removedStaleCSharpChunks"
Write-Host "SQL: $SqlOutputPath"
