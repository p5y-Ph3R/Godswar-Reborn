UPDATE item_templates
SET stats = jsonb_set(
        jsonb_set(
            jsonb_set(
                jsonb_set(
                    jsonb_set(
                        jsonb_set(
                            stats,
                            '{Attack}',
                            to_jsonb('1055,1142,1229,1316,1407,1496,1579,1668,1757,1846,1935'::text)
                        ),
                        '{AttackRadius}',
                        to_jsonb('2.5,2.5,2.5,2.5,2.5,2.5,2.5,2.5,2.5,2.5,2.5'::text)
                    ),
                    '{AttackSpeed}',
                    to_jsonb('1.3,1.3,1.3,1.3,1.3,1.3,1.3,1.3,1.3,1.3,1.3'::text)
                ),
                '{BaseFraction}',
                to_jsonb('0,8,18,28,40,54,74,100,140,200,200'::text)
            ),
            '{ArmEffFraction}',
            to_jsonb('40,100,180,240,300,460,600,1200,4000,-1,-1'::text)
        ),
        '{ArmEff}',
        to_jsonb('1,2,3,4,5,5,5,6,8,5,5'::text)
    )
WHERE id = 1435;
