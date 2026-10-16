# AI.POC-AssistenteTicket

**Draft replies inside Zammad, grounded in past tickets and the knowledge base, with a local model.**

*[Leggi in italiano](README.it.md)*

A proof of concept: when a customer writes to the helpdesk, an assistant retrieves similar resolved tickets, knowledge base
articles and release notes, and proposes a **reply draft with its sources** to the agent, right inside
[Zammad](https://zammad.org) (open source helpdesk) as a shared draft. The agent edits and sends it with Zammad's own controls:
nothing is ever sent automatically. Everything runs locally: models on Ollama, data on SQL Server LocalDB, Zammad in Docker
inside WSL.

> **Status: Phase 5 — indexing and retrieval.** Services, health checks, the database with the simulated helpdesk history (950
> tickets, 46 knowledge base articles, 60 release notes), a ready Zammad (groups, custom fields, technical user `ai-assistant`,
> 40 organizations, 80 imported tickets, demo tickets on demand) and the anonymized index with hybrid retrieval (hit@5 100% on the
> test set, 97% on paraphrased symptoms) are in place; the draft pipeline arrives in the next phase. All data is synthetic and
> fictitious.

What it adds to the other POCs of the series (Order-to-Cash, Claims assistant, Digital transport):

1. integration with a real third-party product (Zammad) instead of a simulated system;
2. human in the loop inside the agent's own tool: no new UI to learn;
3. hybrid retrieval (lexical BM25 + vector, Reciprocal Rank Fusion) with version applicability filters;
4. usefulness measured from the agent's actual behaviour (distance between draft and sent text).

## Architecture

.NET 10 + Aspire. The `Agent` service owns the database, hosts the model clients and talks to Zammad; the `Web` console shows
service health. Zammad runs from the official `zammad-docker-compose` stack inside WSL, started and stopped by the AppHost.
Details in [docs/architettura.md](docs/architettura.md) (Italian).

| Component | Address |
|---|---|
| Aspire dashboard | `https://localhost:17579` |
| Agent | `http://localhost:5401` (all interfaces: Zammad's webhook comes from WSL) |
| Web console | `http://localhost:5402` |
| Zammad | `http://127.0.0.1:8080` |

## How to run

Prerequisites: .NET SDK 10.0.4xx, SQL Server 2025 LocalDB instance `localdev`, [Ollama](https://ollama.com) with
`qwen3.5:9b` and `embeddinggemma`, WSL distro `Ubuntu-26.04` with Docker Engine and Compose, `vm.max_map_count` ≥ 262144
(Elasticsearch).

```powershell
# 1. Connection string in the AppHost user-secrets
dotnet user-secrets set "ConnectionStrings:sql" "Server=(localdb)\localdev;Database=Ticket;Trusted_Connection=True;TrustServerCertificate=True" --project src/Dusiburg.AI.Ticket.AppHost

# 2. Zammad administrator token (Zammad profile > Token Access, permissions admin + ticket.agent)
dotnet user-secrets set "Parameters:zammad-admin-token" "<token>" --project src/Dusiburg.AI.Ticket.AppHost

# 3. Ticket database recreated from the model, with the demo catalog and the synthetic data of data/ (drops the existing one)
dotnet run --project tools/Dusiburg.AI.Ticket.DbInit

# 4. Start: Agent, Web, Zammad in WSL and the Aspire dashboard
dotnet run --project src/Dusiburg.AI.Ticket.AppHost

# 5. With the AppHost running: helpdesk structure, then customers and 80 historical tickets (both idempotent)
dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- zammad bootstrap
dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- zammad seed

# 6. Index: anonymized chunks of resolved tickets, KB articles and release notes with their embeddings (~40 s on CPU)
dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- embed
dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- search --demo 1          # sources found for a demo scenario
dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- eval retrieval           # hit@k and MRR on the test set, report in eval/

# 7. Restart the AppHost (the Agent now uses the ai-assistant token), then create the incoming ticket of a demo scenario
dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- demo 1
```

On the very first run the AppHost clones the Zammad stack inside WSL and Docker pulls ~5 GB of images; create the Zammad
administrator from the setup wizard at `http://127.0.0.1:8080`, then create the token. `zammad bootstrap` creates the technical
user `ai-assistant` and writes its token and the webhook secret into the AppHost user-secrets. Closing the AppHost stops the
Zammad containers (`Zammad:StopOnExit=false` keeps them running).

Model, retrieval and Zammad knobs (`OLLAMA_*`, `MODEL_*_PROVIDER`, `EMBEDDING_*`, `RETRIEVAL_*`, `RRF_K`, `ZAMMAD_URL`, `Zammad:*`) are listed in
[docs/architettura.md](docs/architettura.md#manopole).

### Build and test

```powershell
dotnet build Dusiburg.AI.Ticket.slnx
dotnet test --solution Dusiburg.AI.Ticket.slnx
```

Tests never call Ollama or Zammad (fake HTTP handlers, stub probes); SQL tests use LocalDB `localdev`, each class on its own
database created by `DbInit` and dropped afterwards. The CI (GitHub Actions, Windows) runs the same; when its LocalDB has no
`VECTOR` type it skips the `RequiresSql2025` category.

## Layout

```text
src/       AppHost, ServiceDefaults, Shared, Data, Ai, Zammad, Agent, Web
tools/     DbInit: recreates the database · DataGen: generates data/tickets.json · Cli: Zammad bootstrap/seed, demo tickets
tests/     one NUnit project per area
data/      synthetic data: hand-written cases, KB, release notes, customers; generated tickets (Italian)
infra/     zammad/: stack configuration (zammad.env) and start script for WSL
docs/      architecture (Italian)
```

## License

MIT, see [LICENSE](LICENSE). The Zammad Docker stack (AGPL-3.0) is not part of this repository: it is cloned at runtime.
