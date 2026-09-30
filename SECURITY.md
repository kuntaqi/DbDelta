# Security

DbDelta has no authentication. Its access control is the address it binds: it refuses to start if it would
listen beyond loopback, and anyone who can open the page can apply changes to any database the process can
reach. Keep it on loopback, and run it as an account that can only reach the databases you intend to sync.

## Reporting a vulnerability

Please report security issues privately, not in a public issue: use **Security → Report a vulnerability**
on this repository's GitHub page. Include what you found, how to reproduce it, and what an attacker could
do with it.

Things that are in scope include a way to make DbDelta write to a server on `Safety:ReadOnlyServers`, a way
to bypass the loopback guard without `Safety:AllowRemoteAccess`, a filter predicate that escapes its
allowlist, a sync script that writes to the wrong database or object, and any path that stores or logs a
password.

You will get an acknowledgement, and a fix will be credited to you unless you prefer otherwise.
