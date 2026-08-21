# Local database fixtures

These scripts mutate named development characters for manual testing. They are
not schema migrations and must never be mounted into PostgreSQL's
`docker-entrypoint-initdb.d` directory or discovered by the production
migration runner.

Apply a fixture explicitly only to a disposable/local database after taking a
backup. Production schema changes belong in the checksum-tracked migration
catalog.

The split `max-combat-characters` fixture is executed only through
`tools/ProvisionLocalDevelopmentMaxCombatFixture.ps1`. The wrapper verifies
the isolated development container topology, requires the server and Origin
to be offline, checks target Redis leases, creates and validates a PostgreSQL
backup, and runs all fragments in one serializable transaction. `Status` is
read-only; `Apply` provisions the immutable account/character IDs 7001-7005.
Zodiac state is intentionally excluded.
