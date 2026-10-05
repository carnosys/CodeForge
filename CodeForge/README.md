# CodeForge

CodeForge runs as three Compose services backed by PostgreSQL:

- `api` serves the web UI and HTTP API.
- `worker` claims queued jobs and builds repositories.
- `migrate` applies EF Core migrations once, before the API and worker start.

The API and worker use separate images because only the worker needs Git and the
.NET SDK at runtime.

## Run locally

Create the local environment file and replace its example password:

```bash
cp .env.example .env
docker compose up --build
```

Open <http://localhost:8080>. Stop the application with:

```bash
docker compose down
```

Database data remains in the `db_data` named volume. To scale workers after the
application is running:

```bash
docker compose up --detach --scale worker=2
```

The worker image builds submitted repositories as untrusted code. Do not mount
the Docker socket or host directories into it. For an internet-facing deployment,
use disposable per-job sandboxes and restrict outbound network access as well.
