# CodeForge

CodeForge runs as three Compose services backed by Azure Database for PostgreSQL:

- `api` serves the web UI and HTTP API.
- `worker` claims queued jobs and builds repositories.
- `migrate` applies EF Core migrations once, before the API and worker start.

The API and worker use separate images because only the worker needs Git and the
.NET SDK at runtime.

## Configure the database

Create the local environment file and fill in the Azure PostgreSQL connection
details:

```bash
cp .env.example .env
```

In the Azure portal, allow the public outbound IP address of the machine that
runs Compose under the PostgreSQL server's **Networking** settings. The database
connection uses TLS with full server-certificate and hostname verification.

## Run locally

```bash
docker compose up --build
```

Open <http://localhost:8080>. Stop the application with:

```bash
docker compose down
```

Database data is stored by Azure PostgreSQL. To scale workers after the
application is running:

```bash
docker compose up --detach --scale worker=2
```

The worker image builds submitted repositories as untrusted code. Do not mount
the Docker socket or host directories into it. For an internet-facing deployment,
use disposable per-job sandboxes and restrict outbound network access as well.
