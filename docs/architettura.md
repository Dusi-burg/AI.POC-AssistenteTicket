# Assistente ticket — architettura

> Stato: **Fase 1 (scaffolding)**. Il documento cresce fase per fase: le sezioni su dati, retrieval e pipeline arrivano con le
> fasi che li realizzano.

## 1. Che cos'è

Un assistente che legge i nuovi messaggi dei clienti in **Zammad** (helpdesk open source), recupera ticket risolti, articoli
della knowledge base e note di rilascio pertinenti, e propone all'operatore una **bozza di risposta con le fonti citate**,
dentro Zammad come bozza condivisa. L'operatore la modifica e la invia con i controlli nativi di Zammad: nessun invio automatico.
Tutto gira in locale: modelli su Ollama, dati su SQL Server LocalDB, Zammad in Docker dentro WSL.

## 2. Topologia

```mermaid
flowchart LR
    C([Cliente / comando demo]) -->|API| Z
    subgraph WSL["WSL Ubuntu-26.04 (Docker)"]
        Z["Zammad 7.2.2 · 127.0.0.1:8080<br/>+ Elasticsearch, PostgreSQL, Redis"]
    end
    OP([Operatore]) -->|vede la bozza, modifica, invia| Z
    Z -->|webhook su 172.24.176.1:5401| AG["Ticket.Agent · :5401<br/>(tutte le interfacce)"]
    AG -->|API REST, token di ai-assistant| Z
    AG --> DB[("LocalDB localdev<br/>database Ticket")]
    AG --> OL{{"Ollama · 127.0.0.1:11434<br/>qwen3.5:9b GPU · embeddinggemma CPU"}}
    W["Ticket.Web · :5402<br/>console"] -->|/api/health| AG
    H["AppHost Aspire<br/>dashboard :17579"] -.-> AG
    H -.-> W
    H -.->|zammad-wsl: wsl … zammad-up.sh| Z
```

| Componente | Indirizzo | Note |
|---|---|---|
| AppHost (dashboard) | `https://localhost:17579` | O2C 17279, Sinistri 17379, Trasporto 17479 |
| `Ticket.Agent` | `http://localhost:5401`, in ascolto su `0.0.0.0` | senza il proxy di Aspire, per ricevere il webhook da WSL |
| `Ticket.Web` | `http://localhost:5402` | |
| Zammad | `http://127.0.0.1:8080` | pubblicato solo su localhost di WSL, inoltrato a Windows |
| Ollama | `http://127.0.0.1:11434` | `127.0.0.1` e non `localhost`: ~2 s risparmiati per connessione |

**Rete fra WSL e Windows.** La rete NAT di WSL ha indirizzi fissi nel registro: Windows visto da WSL (e dai container) è
sempre `172.24.176.1`. Il webhook di Zammad chiama `http://172.24.176.1:5401/...`; la regola del firewall di Windows
`WSL -> Helpdesk Agent (TCP 5401)` ammette quella porta solo dalla rete `172.24.176.0/20`. Le connessioni dai container
arrivano con l'indirizzo di WSL (`172.24.186.126`), quindi dentro la regola. `host.docker.internal` con Docker Engine in WSL
non esiste.

## 3. Progetti

| Progetto | Ruolo |
|---|---|
| `Dusiburg.AI.Ticket.AppHost` | Aspire: Agent, Web, Ollama (se non risponde), stack Zammad in WSL, manopole |
| `Dusiburg.AI.Ticket.ServiceDefaults` | OpenTelemetry, health check, resilienza, service discovery, `ApiProblems` |
| `Dusiburg.AI.Ticket.Shared` | enum del dominio, `DemoCatalog`, probe e report di salute, telemetria, JSON, codici d'errore |
| `Dusiburg.AI.Ticket.Data` | EF Core: `TicketDbContext` con gli schemi `support`, `rag`, `agent`, seed del catalogo demo |
| `Dusiburg.AI.Ticket.Ai` | fabbrica dei client di chat per compito, embedding su CPU, `EmbeddingProfile`, cattura dei prompt |
| `Dusiburg.AI.Ticket.Zammad` | client tipizzato dell'API di Zammad, modelli dell'API, probe |
| `Dusiburg.AI.Ticket.Agent` | Minimal API: `/api/health`; dalle prossime fasi event source, coda e pipeline |
| `Dusiburg.AI.Ticket.Web` | console Razor Pages: stato dei servizi |
| `tools/Dusiburg.AI.Ticket.DbInit` | ricrea il database dal modello e inserisce il catalogo demo |

Arrivano con le loro fasi: `Cli` (Fase 4), `Rag` (Fase 5), `Core` (Fase 6).

Regole di confine: l'Agent è l'unico proprietario del database; la Web parla solo con l'Agent via HTTP; solo `Ai` conosce i
provider dei modelli e solo `Zammad` conosce l'API di Zammad.

### Database

LocalDB `(localdb)\localdev`, database `Ticket`, un solo `DbContext` (`TicketDbContext`) per tre schemi: l'Agent è l'unico
proprietario, e così le FK fra schemi sono vere. Nessuna migration: `tools/Dusiburg.AI.Ticket.DbInit` cancella e ricrea il
database dal modello; le query vettoriali (Fase 5) sono Dapper con `SqlVector<float>`.

| Schema | Tabelle | Lookup dagli enum |
|---|---|---|
| `support` | `Product`, `ProductVersion` (`Sequence` = ordine di rilascio), `Customer` (chiave `EmailDomain`), `HistoricalTicket` (chiave `Number`, `IsTestSet`), `TicketMessage`, `Resolution`, `KbArticle` (chiave `Code`), `ReleaseNote` (chiave `Code`) | `DeploymentMode`, `SlaLevel`, `TicketPriority`, `TicketState`, `MessageAuthor`, `ReleaseNoteType` |
| `rag` | `Chunk` (fonte, metadati di applicabilità, testo, `Embedding VECTOR(n)`), `EmbeddingInfo` (riga unica: modello e dimensione dei vettori salvati) | `ChunkSource` |
| `agent` | `AgentRun` (univoco su ticket + articolo di Zammad: idempotenza), `AgentDraft` (segnali e confidenza, testo, esito del feedback), `AgentDraftSource` | `RunTrigger`, `AgentRunState`, `DraftKind`, `DraftOutcome` |

- Le chiavi di business (`Number`, `Code`) sono gli id con cui il modello cita le fonti, e la verifica a valle li confronta con
  quelli recuperati.
- Un `Chunk` ha una sola FK verso la fonte (ticket, articolo o nota di rilascio), coerente con `ChunkSourceId`
  (`CK_Chunk_Source`).
- La dimensione di `Embedding` viene da `EMBEDDING_DIMENSIONS` (default 768) al momento di `DbInit`: è un parametro del modello EF,
  che la include nella chiave della cache (i test usano `VECTOR(4)`). Il controllo 4 di `/api/health` segnala se non coincide.

```powershell
dotnet run --project tools/Dusiburg.AI.Ticket.DbInit [-- "<connection string>"] [--no-seed] [--allow-non-local]
```

Senza argomenti usa `ConnectionStrings__sql` o `(localdb)\localdev`; fuori da LocalDB chiede `--allow-non-local`. Il seed
inserisce il catalogo demo (`DemoCatalog`: prodotti FattureFlow e DocuArchive con le versioni, clienti dei sei scenari);
storico, knowledge base e note di rilascio arrivano con i dati simulati (Fase 3).

## 4. Modelli

| Compito | Default | Manopola |
|---|---|---|
| Classificazione del ticket | `ollama` · `qwen3.5:9b` | `MODEL_CLASSIFY_PROVIDER` |
| Bozza di risposta | `ollama` · `qwen3.5:9b` | `MODEL_DRAFT_PROVIDER` |
| Embedding | `embeddinggemma` su CPU, 768 dimensioni, prefissi query/documento | `EMBEDDING_MODEL`, `EMBEDDING_DIMENSIONS` |

Il cloud (`anthropic`, `claude-haiku-5-5`) serve solo al confronto su comando della Fase 8: senza una manopola che lo chieda
nessun dato esce dalla macchina, anche se la chiave è configurata.

Chat con `think: false`, `num_ctx` 8192 e `num_gpu` 99: con Ollama 0.40 il modello sta tutto in GPU (~49 token/s in
generazione). L'embedding usa `num_gpu: 0` e non toglie VRAM alla chat.

### Manopole

Si impostano sull'AppHost (user-secrets, riga di comando, variabili d'ambiente) e arrivano all'Agent; senza valore vale il
default del codice.

| Manopola | Default |
|---|---|
| `OLLAMA_ENDPOINT` | `http://127.0.0.1:11434` |
| `OLLAMA_CHAT_MODEL`, `OLLAMA_NUM_CTX`, `OLLAMA_NUM_GPU` | `qwen3.5:9b`, `8192`, `99` (`auto` = scelta di Ollama) |
| `MODEL_CLASSIFY_PROVIDER`, `MODEL_DRAFT_PROVIDER` | `ollama` |
| `ANTHROPIC_MODEL` | `claude-haiku-5-5` |
| `EMBEDDING_MODEL`, `EMBEDDING_DIMENSIONS` | `embeddinggemma`, `768` |
| `TICKET_PROMPT_CAPTURE_DIR` | — (cattura dei prompt su file, per la diagnostica) |
| `ZAMMAD_URL` | `http://127.0.0.1:8080` |
| `Zammad:Managed` | `true` (l'AppHost avvia lo stack) |
| `Zammad:StopOnExit` | `true` (alla chiusura i container si fermano) |
| `Zammad:WslDistro`, `Zammad:StackTag`, `Zammad:ComposeDir` | `Ubuntu-26.04`, `v17.5.2`, `infra/zammad` |

Segreti (user-secrets dell'AppHost): `ConnectionStrings:sql`, `Parameters:zammad-api-token`, `Parameters:anthropic-api-key`
(facoltativa).

## 5. Controlli di salute

`GET /api/health` dell'Agent (cache di 30 s); gli stessi controlli diventano l'health check della risorsa `agent` nel dashboard
(errori → rosso, avvisi → giallo). La pagina "Stato" della Web li mostra.

| # | Controllo | Ok quando |
|---|---|---|
| 1 | Connessione SQL | `ConnectionStrings:sql` configurata e il server risponde (letto da `master`) |
| 2 | Versione SQL Server | ≥ 17 (SQL Server 2025) |
| 3 | Supporto VECTOR | `VECTOR_DISTANCE` disponibile |
| 4 | Database applicativo | il database `Ticket` esiste e `rag.Chunk.Embedding` è `VECTOR(EMBEDDING_DIMENSIONS)` (avviso altrimenti: rieseguire `DbInit`) |
| 5 | Ollama raggiungibile | `GET /api/version` |
| 6 | Modello chat presente | `OLLAMA_CHAT_MODEL` installato |
| 7 | Modello embedding presente | `EMBEDDING_MODEL` installato |
| 8 | Dimensione embedding | un embedding di prova ha `EMBEDDING_DIMENSIONS` elementi |
| 9 | Chiave Anthropic | informativo se nessun compito è sul cloud; errore se un compito c'è e la chiave manca |
| 10 | Posizionamento modelli | informativo: chat in GPU, embedding fuori dalla VRAM |
| 11 | Zammad raggiungibile | la pagina iniziale di `ZAMMAD_URL` risponde 200 (502 mentre il railsserver riparte) |
| 12 | Token Zammad | `GET /api/v1/users/me` riconosce il token; avviso se è di un amministratore |

## 6. Zammad in WSL

Lo stack è quello ufficiale, [`zammad-docker-compose`](https://github.com/zammad/zammad-docker-compose), al tag **`v17.5.2`**
(Zammad **7.2.2**). È sotto licenza AGPL-3.0 e **non è copiato nel repository**: lo script `infra/zammad/zammad-up.sh` lo clona
al tag fissato in `~/zammad/zammad-docker-compose` dentro WSL se manca (serve la rete solo la prima volta). Dal repository
arriva solo la configurazione, `infra/zammad/zammad.env`:

| Valore | Perché |
|---|---|
| `VERSION=7.2.2-0000` | versione fissata |
| `RESTART=unless-stopped` | dopo la migrazione di un campo personalizzato Zammad ferma railsserver, scheduler e websocket e conta sul restart di Docker; con `no` restano fermi, con `on-failure` lo scheduler (codice 0) non riparte |
| `NGINX_EXPOSE_PORT=127.0.0.1:8080` | Zammad raggiungibile solo dalla macchina |
| `TZ=Europe/Rome`, `ELASTICSEARCH_JAVA_OPTS=-Xms1g -Xmx1g` | fuso orario; heap di Elasticsearch (lo stack usa ~2,7 GB di RAM in WSL) |

L'AppHost lancia la risorsa eseguibile `zammad-wsl`:
`wsl -d Ubuntu-26.04 -- bash infra/zammad/zammad-up.sh <zammad.env> v17.5.2 stop`. Lo script esegue
`docker compose --project-name zammad --env-file zammad.env up` in primo piano: finché gira, la VM di WSL resta accesa e i log
dello stack sono nel dashboard. La risorsa è sana quando `http://127.0.0.1:8080/` risponde 200: circa 30 s con i container
fermi.

**Arresto.** Quando l'AppHost si chiude, Aspire termina `wsl.exe` e lo script riceve `SIGHUP`. Una trap esegue
`docker compose stop` (log in `~/zammad/zammad-stop.log` dentro WSL, perché a quel punto stdout e stderr sono già chiusi). Senza
la trap i container resterebbero accesi e, con `unless-stopped`, ripartirebbero al prossimo avvio di Docker in WSL. Il
callback `ApplicationStopping` dell'AppHost non basterebbe: con `aspire stop` o con lo stop del debugger non viene eseguito.
Per lasciare Zammad acceso: `Zammad:StopOnExit=false`.

Fermarlo a mano: `wsl -d Ubuntu-26.04 -- docker compose --project-name zammad stop`. I dati stanno nei volumi
`zammad_postgresql-data`, `zammad_elasticsearch-data`, `zammad_redis-data`, `zammad_zammad-storage`, `zammad_zammad-backup`.

### Fatti verificati sull'API (Fase 0)

| Tema | Comportamento |
|---|---|
| Token | `Authorization: Token token=…`; `GET /api/v1/users/me?expand=true` restituisce i ruoli per nome |
| Pagina iniziale | con `Accept: application/json` risponde 500 (`UnknownFormat`): va chiesta come HTML |
| Bozza condivisa | `PUT /api/v1/tickets/{id}/shared_draft` con `new_article` (HTML) e `ticket_attributes`; una per ticket; l'interfaccia la mostra con la formattazione; all'invio Zammad la cancella |
| Webhook | payload personalizzato con `#{ticket.id}`, `#{article.id}` (tipi nativi nel JSON); firma `X-Hub-Signature: sha1=<HMAC-SHA1 del corpo>`; `X-Zammad-Delivery` univoco per consegna |
| Campi personalizzati | `POST /object_manager_attributes` + `POST /object_manager_attributes_execute_migrations`, che riavvia il railsserver (~11 s) |
| Date storiche | solo con il setting `import_mode` acceso |
| Ricerca | `GET /tickets/search?query=updated_at:[<ISO> TO *]`; un nuovo articolo è ricercabile dopo 2–3,5 s |
