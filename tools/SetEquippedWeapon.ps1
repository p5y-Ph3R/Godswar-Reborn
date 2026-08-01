param(
    [Parameter(Mandatory = $true)]
    [string]$CharacterName,

    [Parameter(Mandatory = $true)]
    [int]$ItemId
)

$ErrorActionPreference = "Stop"

if ($ItemId -le 0) {
    throw "ItemId must be a positive integer."
}

$safeName = $CharacterName.Replace("'", "''")

$sql = @'
DO $$
DECLARE
    v_user_id integer;
BEGIN
    SELECT cb.id
    INTO v_user_id
    FROM character_base cb
    WHERE cb.name = '__CHARACTER_NAME__';

    IF v_user_id IS NULL THEN
        RAISE EXCEPTION 'Character not found: __CHARACTER_NAME__';
    END IF;

    INSERT INTO character_items (
        user_id, item_location, slot_index, prop_id,
        item_quality, item_grade, bound, stack, item_exp, holy_suit_code
    )
    VALUES (v_user_id, 0, 10, __ITEM_ID__, 1, 1, 1, 1, 0, 0)
    ON CONFLICT (user_id, item_location, slot_index) DO UPDATE
    SET prop_id = EXCLUDED.prop_id,
        item_quality = EXCLUDED.item_quality,
        item_grade = EXCLUDED.item_grade,
        bound = EXCLUDED.bound,
        stack = EXCLUDED.stack,
        item_exp = EXCLUDED.item_exp,
        holy_suit_code = EXCLUDED.holy_suit_code,
        updated_at = now();
END $$;

SELECT
    cb.name,
    cb.profession,
    ci.slot_index,
    ci.prop_id,
    ci.item_quality,
    ci.item_grade
FROM character_base cb
JOIN character_items ci
  ON ci.user_id = cb.id
 AND ci.item_location = 0
 AND ci.slot_index = 10
WHERE cb.name = '__CHARACTER_NAME__';
'@

$sql = $sql.Replace('__CHARACTER_NAME__', $safeName).Replace('__ITEM_ID__', $ItemId.ToString())
$sql | docker compose exec -T postgres psql -U godswar -d godswar -v ON_ERROR_STOP=1
