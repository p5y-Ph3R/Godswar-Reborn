-- Enable the stock Ride skill for existing characters while the original
-- level-40 quest/book award flow is still being reconstructed.
INSERT INTO character_skills (user_id, skill_id, skill_level, source)
SELECT cb.id, st.skill_id, 1, 'mount-compatibility'
FROM character_base cb
JOIN skill_templates st
  ON st.skill_id = 4904
 AND cb.profession = ANY(st.class_ids)
ON CONFLICT (user_id, skill_id) DO NOTHING;
