Set-StrictMode -Version Latest

function Get-DevelopmentCloneLeaseCountSql {
    @'
select
    (select count(*) from public.outbox_events
     where lease_owner is not null
        or lease_token is not null
        or lease_expires_at is not null) +
    (select count(*) from public.outbox_consumer_positions
     where lease_owner is not null
        or lease_token is not null
        or lease_expires_at is not null);
'@
}

function Get-DevelopmentCloneUnreviewedEventSql {
    @'
select coalesce(string_agg(distinct event_type, ',' order by event_type), '')
from public.outbox_events
where delivered_at is null
  and event_type not in (
      'character.created',
      'character.deleted',
      'character.purged',
      'character.restored',
      'inventory.class_suit_changed',
      'inventory.developer_bag_cleared',
      'inventory.developer_item_granted',
      'inventory.developer_material_granted',
      'inventory.equipment_bag_transferred',
      'inventory.equipment_forged',
      'inventory.gear_mentor_attribute_added',
      'inventory.gear_mentor_attribute_deleted',
      'inventory.gear_mentor_attribute_enhanced',
      'inventory.gear_mentor_attribute_stone_made',
      'inventory.gear_mentor_crystal_transformed',
      'inventory.gear_mentor_gear_decomposed',
      'inventory.gear_mentor_gem_pieces_combined',
      'inventory.holy_stone_changed',
      'inventory.holy_suit_experience_stored',
      'inventory.holy_suit_experience_transferred',
      'inventory.holy_suit_experience_transformed',
      'inventory.holy_suit_ware_consumed',
      'inventory.kit_bag_item_deleted',
      'inventory.kit_bag_item_moved',
      'inventory.pet_bag_item_activated',
      'pet.bag_item_activated',
      'pet.level_upgraded',
      'pet.presence_changed',
      'progression.monster_reward_settled',
      'progression.online_interval_settled',
      'talent.upgraded',
      'zodiac.skill_grid_activated',
      'zodiac.skill_grid_selected',
      'zodiac.skill_grid_upgraded'
  );
'@
}

function Get-DevelopmentCloneMigrationSql {
    @'
select migration_id || '|' || checksum
from public.schema_migrations
order by migration_id;
'@
}

function Get-DevelopmentCloneCountSql {
    @'
select (select count(*) from public.accounts) || '|' ||
       (select count(*) from public.character_base) || '|' ||
       (select count(*) from public.character_items);
'@
}

Export-ModuleMember -Function @(
    'Get-DevelopmentCloneLeaseCountSql'
    'Get-DevelopmentCloneUnreviewedEventSql'
    'Get-DevelopmentCloneMigrationSql'
    'Get-DevelopmentCloneCountSql'
)
