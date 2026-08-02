# LocalDevelopment item-content v4 rollback

This procedure is a fail-closed recovery boundary for the local Docker stack.
It changes only the official `items` publication pointer from one exact
manifest-v5 revision to one retained manifest-v4 revision. It never edits or
deletes immutable item-content rows.

It is not a production administration tool and it must not be used while a
server is running.

## What the tool proves

[`tools/SetLocalDevelopmentItemContentV4Publication.ps1`](../tools/SetLocalDevelopmentItemContentV4Publication.ps1)
refuses to continue unless all of these conditions hold:

- the named server container exists, is stopped, declares
  `GODSWAR_RUNTIME_PROFILE=LocalDevelopment`, and targets the exact database;
- no local `Godswar.Server` process is visible;
- PostgreSQL is running and healthy;
- migration `20260801_046_holy_suit_content_release` is installed;
- no character has an active PostgreSQL checkpoint owner;
- the current official pointer equals the operator-supplied revision, is
  manifest v5, sealed, and complete;
- the target exists, is manifest v4, is sealed, has exact declared row counts,
  and contains no v5-only Holy Suit content;
- when no target hash is supplied, exactly one v4 revision exists. Zero or
  multiple v4 headers are refused as missing or ambiguous.

The change runs in a serializable transaction, takes the same
`ITEMSCON` advisory transaction lock as the item publisher, and updates the
pointer with an exact compare-and-swap. The database publication trigger
revalidates the v4 revision before commit.

## Before rollback

Drain players and cleanly stop the server. Do not merely kill it while it owns
character checkpoints.

Read and retain the exact current and target revisions:

```powershell
docker exec godswar-postgres psql -X -A -t -U godswar -d godswar -c @"
SELECT release.manifest_version, release.revision, release.sealed_at
FROM public.item_template_content_revisions release
LEFT JOIN public.item_template_content_publication publication
  ON publication.family = 'items'
 AND publication.revision = release.revision
WHERE release.manifest_version IN (4, 5)
ORDER BY release.manifest_version, release.revision;
"@
```

Confirm that the expected v5 row is the published row and that the selected
v4 row is the reviewed release retained from the preceding deployment. Then
run the pointer change with both hashes:

```powershell
.\tools\SetLocalDevelopmentItemContentV4Publication.ps1 `
  -ExpectedCurrentV5Revision '<64-hex-v5-revision>' `
  -TargetV4Revision '<64-hex-v4-revision>' `
  -Confirm:$false
```

Omitting `-TargetV4Revision` is allowed only when there is exactly one v4
revision. Supplying the reviewed hash is preferred because it makes operator
intent explicit.

The receipt must report `status = repointed`, the expected v4 hash,
`manifestVersion = 4`, and `contentRowsMutated = false`.

The pointer rollback intentionally leaves the non-authoritative manifest-v5
Holy Suit compatibility identities in legacy `item_templates`. Those rows are
needed by the `character_items.prop_id` foreign key and are not part of the
immutable v4 publication. Leaving them in place neither adds Holy Suit policy
to v4 nor changes what a v4-pinned process reads. The rollback tool does not
delete them, because doing so could invalidate owned inventory and would turn
a pointer operation into a destructive data migration.

## Binary rollback limitation

Repointing content does not reverse migration 046. The repository migration
runner deliberately rejects a database whose migration history is ahead of a
binary. Therefore an image whose migration catalog ends at migration 045 still
cannot start after migration 046 has been applied, even with the v4 pointer.

Do not delete or falsify `schema_migrations` to bypass that protection.

A post-046 application rollback requires one of these separately prepared
assets:

1. a tested v4-application compatibility image that recognizes the exact 046
   schema while loading manifest v4; or
2. a verified full PostgreSQL restore from the pre-046 backup, paired with the
   pre-046 binary.

Until one of those assets exists, the pointer tool is useful for validating
and preparing content state, but it is not by itself authorization to launch
the old image.

## Re-forward to v5

Keep the server stopped. Start the tested v5 image against the same database.
Its normal `PostgresItemTemplateBaselinePublisher.EnsurePublishedAsync`
startup path takes the publication lock, verifies the retained v4 release,
reconstructs or verifies the canonical v5 release, and atomically republishes
v5. Do not update the pointer by hand.

After startup, verify that the official pointer is sealed v5:

```powershell
docker exec godswar-postgres psql -X -A -t -U godswar -d godswar -c @"
SELECT release.manifest_version, publication.revision,
       release.sealed_at IS NOT NULL
FROM public.item_template_content_publication publication
JOIN public.item_template_content_revisions release
  ON release.revision = publication.revision
WHERE publication.family = 'items';
"@
```

Only admit players after the server readiness checks report the v5 content
revision expected by the release.

## Compatibility conflict recovery

The current v5 server validates Holy Suit compatibility identities only at
startup. If one of IDs 9010 through 9016 or 9020 through 9025 exists in
`item_templates` but differs from its sealed v5 definition, startup fails
closed and does not overwrite it. Missing-row inserts from that same startup
attempt are rolled back as well.

Recover only while the server is stopped:

1. Take and verify a PostgreSQL backup.
2. Compare the exact conflicting `item_templates` row with the same ID in the
   sealed revision referenced by `item_template_content_publication`.
3. Establish why the mutable row changed and confirm that the ID is one of the
   explicit Holy Suit compatibility IDs.
4. Remove only that confirmed conflicting compatibility row. Do not issue a
   broad range delete and do not copy over or update the row in place.
5. Restart the current v5 server. Its normal startup transaction copies the
   missing identity from the sealed definition and validates the exact set.
6. Verify readiness and the pinned content revision before admitting players.

If the foreign key refuses the exact-row removal because owned inventory
already references it, stop. Do not delete player items, disable the foreign
key, or broaden the maintenance statement. Keep the server offline and use a
reviewed recovery transaction or restore the verified backup.

Never edit or delete sealed publication rows, the publication pointer, or
`schema_migrations` to recover from this condition. If the exact difference
cannot be explained, keep the server stopped and restore the verified backup.
Write access to legacy `item_templates` is an operational trust boundary and
must remain limited to migrations, publication startup, and approved offline
maintenance.

## Disposable verification

The automated check creates a random database whose name is constrained to
`godswar_v4rollback_<12 hex>`, creates a stopped LocalDevelopment server
marker, publishes real v5 and retained v4 content, and proves:

- missing, non-v4, unsealed, and ambiguous targets fail closed;
- a running server container is refused before any database operation;
- the exact v5-to-v4 pointer move succeeds;
- immutable content fingerprints do not change during rollback;
- a repeated rollback with a stale expected v5 is refused; and
- normal v5 startup republishes v5.

It always removes its disposable database and marker container:

```powershell
.\tools\TestLocalDevelopmentItemContentV4Rollback.ps1
```

The test never invokes the rollback tool against the `godswar` Tempest
database.
