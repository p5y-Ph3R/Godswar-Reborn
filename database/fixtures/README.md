# Local database fixtures

These scripts mutate named development characters for manual testing. They are
not schema migrations and must never be mounted into PostgreSQL's
`docker-entrypoint-initdb.d` directory or discovered by the production
migration runner.

Apply a fixture explicitly only to a disposable/local database after taking a
backup. Production schema changes belong in the checksum-tracked migration
catalog.
