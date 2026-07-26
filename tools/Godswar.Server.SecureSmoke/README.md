# Secure Docker live smoke

This bounded development probe verifies the complete loopback secure profile:

1. login and game TLS policy, ALPN, and binary prefaces;
2. a generated transient account and single-use game ticket;
3. authenticated UDP return-path binding and protected confirmation; and
4. one authoritative movement input and acknowledged position snapshot.

The probe accepts only literal loopback endpoints and only the
`godswar_secure_dev` database. It creates a random account and character,
never prints their credentials, and removes both on exit. The total operation
network-operation deadline defaults to 20 seconds and can be set from 5
through 60 seconds with `GODSWAR_SECURE_SMOKE_TIMEOUT_SECONDS`. Teardown then
has a separate two-second offline wait and five-second-per-delete database
cap so cleanup cannot hang indefinitely.

Pass the public development root that issued the certificate mounted into the
currently running secure Docker server:

```powershell
.\tools\InvokeSecureDockerSmoke.ps1 `
  -RootCertificatePath 'C:\private\reborn-development-root.cer'
```

The wrapper reads the untracked `.env` and `.env.secure.local` files, passes
the database credential only through the child-process environment, restores
the prior process environment, and prints only fixed pass/fail evidence.
It does not install certificate trust, expose a non-loopback port, change the
running container, or use an existing player account.
