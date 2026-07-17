UPDATE character_items
SET holy_socket_count = LEAST(holy_socket_count, 4),
    holy_socket5_effect_id = NULL,
    holy_socket5_level = NULL,
    holy_socket6_effect_id = NULL,
    holy_socket6_level = NULL
WHERE holy_socket_count > 4
   OR holy_socket5_effect_id IS NOT NULL
   OR holy_socket5_level IS NOT NULL
   OR holy_socket6_effect_id IS NOT NULL
   OR holy_socket6_level IS NOT NULL;
