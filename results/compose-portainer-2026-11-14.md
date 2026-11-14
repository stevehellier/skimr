# Compose files in Portainer (#25), 2026-11-14

## Reported

A Portainer stack using `deploy/docker-compose.yml` failed with:
`failed to parse compose file: (root) Additional property name is not allowed`.

## Reproduced

- `docker stack config -c deploy/docker-compose.yml` (Docker 29.9.3, Swarm stack loader) failed with
  `(root): additional property 'name' is not allowed`.
- In a throwaway Portainer CE 2.45.3 (`skimr-portainer-test`, standalone Docker environment), the old files
  **deployed fine**. Recent Portainer on standalone Docker accepts `name:`; the error comes from the Swarm loader,
  or from older Portainer versions that use it.

## After the fix (top-level `name:` removed, volumes named explicitly)

| Check | Result |
|---|---|
| `docker stack config`, SQLite file | Passes; volume `name: skimr_skimr-data` |
| `docker compose config`, both files | Pass; same volume names as before |
| Portainer 2.45.3 stack, SQLite file (copy with the volume renamed so it couldn't touch the existing `skimr_skimr-data`) | Deployed; `/health/ready` 200 |
| Portainer 2.45.3 stack, Postgres file (volumes renamed the same way) | Deployed; both containers healthy; `/health/ready` 200 |
| Upgrade: old file (project `skimrup`) up, then down; new file with volume `name: skimrup_skimr-data` under project `skimrupnew` | Reused the old volume (mount `skimrup_skimr-data -> /data`, no new volume created); compose printed a warning only: "already exists but was created for project ..." |

## Known gap (not fixed here)

`docker stack config -c deploy/docker-compose.postgres.yml` fails with `services.skimr.depends_on: must be a list`:
Swarm doesn't support `condition: service_healthy`. On a Swarm environment the Postgres file still won't load.

## Cleanup

I removed only what I created, by name: the Portainer container and its anonymous volume, the stacks,
the volumes `skimrup_skimr-data`, `skimrfix_skimr-data`, `skimrfixpg_skimr-data` and `skimrfixpg_skimr-db`,
and the `portainer/portainer-ce` and `alpine` images. I didn't touch the existing `skimr-skimr-1` container or
the `skimr_skimr-data` volume.
