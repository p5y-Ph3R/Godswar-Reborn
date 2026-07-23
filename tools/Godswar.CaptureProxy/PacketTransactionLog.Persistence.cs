sealed partial class PacketTransactionLog
{
    private async Task UpsertNpcSpawnAsync(CapturedNpcSpawnRecord spawn, DateTimeOffset capturedAt)
    {
        await using var command = _dataSource.CreateCommand(UpsertNpcSpawnSql);
        command.Parameters.AddWithValue("map_id", spawn.MapId);
        command.Parameters.AddWithValue("scene_key", spawn.SceneKey);
        command.Parameters.AddWithValue("npc_key", spawn.NpcKey);
        command.Parameters.AddWithValue("template_key", spawn.TemplateKey);
        command.Parameters.AddWithValue("object_id", (long)spawn.ObjectId);
        command.Parameters.AddWithValue("pos_x", spawn.X);
        command.Parameters.AddWithValue("pos_z", spawn.Z);
        command.Parameters.AddWithValue("clear_bytes", spawn.Packet);
        command.Parameters.AddWithValue("captured_at", capturedAt);
        await command.ExecuteNonQueryAsync();
    }

    private async Task UpsertMonsterSpawnAsync(
        CapturedMonsterSpawnRecord spawn,
        CapturedMonsterTemplate template,
        DateTimeOffset capturedAt)
    {
        await using var command = _dataSource.CreateCommand(UpsertMonsterSpawnSql);
        command.Parameters.AddWithValue("map_id", template.MapId);
        command.Parameters.AddWithValue("scene_key", template.SceneKey);
        command.Parameters.AddWithValue("template_key", spawn.TemplateKey);
        command.Parameters.AddWithValue("display_name", template.DisplayName);
        command.Parameters.AddWithValue("object_id", (long)spawn.ObjectId);
        command.Parameters.AddWithValue("pos_x", spawn.X);
        command.Parameters.AddWithValue("pos_z", spawn.Z);
        command.Parameters.AddWithValue("clear_bytes", spawn.Packet);
        command.Parameters.AddWithValue("captured_at", capturedAt);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<CapturedMonsterTemplate?> ResolveMonsterTemplateAsync(string templateKey)
    {
        if (_monsterMapId is not short monsterMapId)
        {
            return null;
        }

        if (_monsterTemplateCache.TryGetValue(templateKey, out var cached))
        {
            return cached;
        }

        await using var command = _dataSource.CreateCommand("""
            SELECT source_map_id, scene_key, display_name
            FROM monster_templates
            WHERE template_key = @template_key
              AND source_map_id = @map_id
            ORDER BY source_key
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("template_key", templateKey);
        command.Parameters.AddWithValue("map_id", monsterMapId);

        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            Console.Error.WriteLine(
                $"[db] skipped monster template={templateKey}: no template exists for explicit map {monsterMapId}");
            _monsterTemplateCache[templateKey] = null;
            return null;
        }

        var template = new CapturedMonsterTemplate(
            reader.GetInt16(0),
            reader.GetString(1),
            reader.GetString(2));
        _monsterTemplateCache[templateKey] = template;
        return template;
    }

    private async Task UpdateNpcDetailAsync(CapturedNpcDetailRecord detail, DateTimeOffset capturedAt)
    {
        await using var command = _dataSource.CreateCommand(UpdateNpcDetailSql);
        command.Parameters.AddWithValue("opcode", detail.Opcode);
        command.Parameters.AddWithValue("object_id", (long)detail.ObjectId);
        command.Parameters.AddWithValue("clear_bytes", detail.Packet);
        command.Parameters.AddWithValue("captured_at", capturedAt);
        await command.ExecuteNonQueryAsync();
    }
}
