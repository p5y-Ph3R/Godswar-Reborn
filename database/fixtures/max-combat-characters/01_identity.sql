-- Transaction root and exact identity ownership for the local max-combat fixture.
BEGIN;
SET TRANSACTION ISOLATION LEVEL SERIALIZABLE;
SELECT pg_advisory_xact_lock(5572168742097701710);

DO $guard$
BEGIN
  IF current_database() <> 'godswar' THEN
    RAISE EXCEPTION 'max-combat fixture requires the godswar database';
  END IF;
  IF to_regclass('public.character_items') IS NULL
     OR to_regclass('public.character_pets') IS NULL
     OR to_regclass('public.character_zodiac_skill_grids') IS NULL THEN
    RAISE EXCEPTION 'max-combat fixture schema is incomplete';
  END IF;
  IF NOT EXISTS (
      SELECT 1 FROM server WHERE id=1 AND name='Tempest'
       AND identifier='KAL3jcIzqGgKvOf1dbYZKC8cS') THEN
    RAISE EXCEPTION 'unexpected realm identity';
  END IF;
END
$guard$;

CREATE TEMP TABLE fixture_characters (
  fixture_id integer PRIMARY KEY,
  username text UNIQUE NOT NULL,
  password_verifier text NOT NULL,
  character_name varchar(32) UNIQUE NOT NULL,
  profession smallint NOT NULL,
  camp smallint NOT NULL,
  map_id smallint NOT NULL,
  pos_x real NOT NULL,
  pos_z real NOT NULL,
  build_key text NOT NULL
) ON COMMIT DROP;

INSERT INTO fixture_characters VALUES
 (7001,'dummy_ares_bulwark', :'ares_bulwark_verifier', 'AresBulwark',
  0,1,0,148,-154,'warrior'),
 (7002,'dummy_ares_mirage', :'ares_mirage_verifier', 'AresMirage',
  1,1,0,148,-162,'champion_dodge'),
 (7003,'dummy_athena_bulwark', :'athena_bulwark_verifier', 'AthenaBulwark',
  0,0,1,148,-154,'warrior'),
 (7004,'dummy_athena_mirage', :'athena_mirage_verifier', 'AthenaMirage',
  1,0,1,148,-162,'champion_dodge'),
 (7005,'test25', :'test25_verifier', 'AresTempest',
  1,0,0,136,-150,'champion_glass');

DO $identity_guard$
BEGIN
  IF EXISTS (
      SELECT 1 FROM fixture_characters f JOIN accounts a
        ON a.id=f.fixture_id OR a.username=f.username
      WHERE a.id<>f.fixture_id OR a.username<>f.username) THEN
    RAISE EXCEPTION 'a requested account ID or username belongs elsewhere';
  END IF;
  IF EXISTS (
      SELECT 1 FROM fixture_characters f
      JOIN character_base c ON c.name=f.character_name
      JOIN accounts a ON a.id=c.account_id
      WHERE c.id<>f.fixture_id OR a.username<>f.username) THEN
    RAISE EXCEPTION 'a requested character ID or name belongs elsewhere';
  END IF;
  IF EXISTS (
      SELECT 1 FROM fixture_characters f JOIN character_base c
        ON c.id=f.fixture_id OR c.name=f.character_name
      WHERE c.id<>f.fixture_id OR c.name<>f.character_name) THEN
    RAISE EXCEPTION 'a requested character ID or name is already occupied';
  END IF;
  IF EXISTS (
      SELECT 1 FROM fixture_characters f
      JOIN accounts a ON a.username=f.username
      JOIN character_base c ON c.account_id=a.id
      WHERE c.name<>f.character_name) THEN
    RAISE EXCEPTION 'a requested account already owns another character';
  END IF;
END
$identity_guard$;

INSERT INTO accounts (id,username,password,status,login_status)
SELECT fixture_id,username,password_verifier,0,0 FROM fixture_characters
ON CONFLICT (username) DO UPDATE
SET password=EXCLUDED.password, status=0, login_status=0;

INSERT INTO character_base (
  id,account_id,server_id,name,gender,"GM",camp,profession,
  fighter_job_lv,scholar_job_lv,fighter_job_exp,scholar_job_exp,
  "curHP","curMP",status,belief,prestige,earl_rank,
  "Map","Pos_X","Pos_Z","Money","Stone","SkillPoint","SkillExp",
  "MaxHP","MaxMP",holy_suit_points,zodiac_type,zodiac_level,
  zodiac_energy,zodiac_accumulated_exp_x100,
  zodiac_accumulated_talent_exp_x100,zodiac_energy_remainder_x100,
  character_slot,lifecycle_state,lifecycle_version,pet_shed_capacity)
SELECT
  f.fixture_id,a.id,1,f.character_name,'male',0,f.camp,f.profession,
  160,0,0,0,1500,177,0,1,0,0,
  f.map_id,f.pos_x,f.pos_z,10000,10,10,0,
  1500,177,0,
  0,1,0,0,0,0,0,'active',1,2
FROM fixture_characters f JOIN accounts a ON a.username=f.username
ON CONFLICT (name) DO UPDATE SET
  account_id=EXCLUDED.account_id,server_id=1,gender='male',"GM"=0,
  camp=EXCLUDED.camp,profession=EXCLUDED.profession,fighter_job_lv=160,
  scholar_job_lv=0,fighter_job_exp=0,scholar_job_exp=0,
  status=0,belief=1,prestige=0,earl_rank=0,"Map"=EXCLUDED."Map",
  "Pos_X"=EXCLUDED."Pos_X","Pos_Z"=EXCLUDED."Pos_Z",
  "MaxHP"=1500,"MaxMP"=177,
  holy_suit_points=EXCLUDED.holy_suit_points,
  zodiac_type=0,zodiac_level=1,zodiac_energy=0,
  zodiac_accumulated_exp_x100=0,zodiac_accumulated_talent_exp_x100=0,
  zodiac_energy_remainder_x100=0,zodiac_lucky_status=0,
  zodiac_lucky_expires_at=NULL,zodiac_online_day=NULL,
  zodiac_online_duration_ticks=0,zodiac_last_online_at=NULL,
  zodiac_last_compensation_day=NULL,character_slot=0,
  lifecycle_state='active',lifecycle_version=1,
  deleted_at=NULL,restore_until=NULL,
  purge_after=NULL,fighter_level_sealed=false,pet_shed_capacity=2;

CREATE TEMP TABLE fixture_context ON COMMIT DROP AS
SELECT f.*,a.id AS account_id,c.id AS character_id
FROM fixture_characters f
JOIN accounts a ON a.username=f.username
JOIN character_base c ON c.account_id=a.id AND c.name=f.character_name;

DO $identity_readback$
BEGIN
  IF (SELECT count(*) FROM fixture_context)<>5
     OR EXISTS (SELECT 1 FROM fixture_context
                WHERE account_id<>fixture_id OR character_id<>fixture_id
                   OR length(username)>32 OR length(character_name)>31) THEN
    RAISE EXCEPTION 'fixture identities did not materialize exactly';
  END IF;
END
$identity_readback$;

SELECT setval(pg_get_serial_sequence('accounts','id'),
  GREATEST((SELECT max(id) FROM accounts),
           (SELECT last_value FROM accounts_id_seq)),true);
SELECT setval(pg_get_serial_sequence('character_base','id'),
  GREATEST((SELECT max(id) FROM character_base),
           (SELECT last_value FROM character_base_id_seq)),true);

DELETE FROM character_zodiac_skill_grids z
USING fixture_context f WHERE z.user_id=f.character_id;
