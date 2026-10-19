# AI.POC-AssistenteTicket

**Bozze di risposta dentro Zammad, basate sullo storico dei ticket e sulla knowledge base, con un modello locale.**

*[Read in English](README.md)*

Un proof of concept: quando un cliente scrive all'helpdesk, un assistente recupera ticket simili già risolti, articoli della
knowledge base e note di rilascio, e propone all'operatore una **bozza di risposta con le fonti**, direttamente dentro
[Zammad](https://zammad.org) (helpdesk open source) come bozza condivisa. L'operatore la modifica e la invia con i controlli di
Zammad: nulla parte in automatico. Tutto gira in locale: modelli su Ollama, dati su SQL Server LocalDB, Zammad in Docker dentro
WSL.

> **Stato: Fase 7 — feedback e console.** A un nuovo messaggio del cliente in Zammad (webhook, polling o avvio manuale)
> seguono, in ~10–20 s, una bozza condivisa nell'editor di risposta con la dicitura AI, una nota interna con fonti e confidenza
> e il tag `ai-bozza`: risposta, richiesta di chiarimenti o escalation, decise dal codice. Quando l'operatore invia, l'Agent
> confronta il testo con la bozza, registra l'esito (inviata com'era, modificata, scartata) e marca il ticket `ai-generata` o
> `ai-generata-in-parte`; segnala se la dicitura AI è stata tolta. La console mostra elaborazioni, esiti e dati demo. La
> valutazione arriva nella prossima fase. Tutti i dati sono sintetici e fittizi.

Che cosa aggiunge agli altri POC della serie (Order-to-Cash, Assistente sinistri, Trasporto digitale):

1. integrazione con un prodotto reale di terze parti (Zammad) invece di un sistema simulato;
2. l'operatore resta nel suo strumento: nessuna interfaccia nuova da imparare;
3. ricerca ibrida (BM25 lessicale + vettoriale, Reciprocal Rank Fusion) con filtri di applicabilità per versione;
4. utilità misurata dal comportamento reale dell'operatore (distanza fra bozza e testo inviato).

## Architettura

.NET 10 + Aspire. Il servizio `Agent` è proprietario del database, ospita i client dei modelli e parla con Zammad; la console
`Web` mostra stato dei servizi, elaborazioni, esiti delle bozze e dati demo. Zammad gira dallo stack ufficiale `zammad-docker-compose` dentro WSL, avviato e fermato
dall'AppHost. Dettagli in [docs/architettura.md](docs/architettura.md).

| Componente | Indirizzo |
|---|---|
| Dashboard di Aspire | `https://localhost:17579` |
| Agent | `http://localhost:5401` (tutte le interfacce: il webhook di Zammad arriva da WSL) |
| Console Web | `http://localhost:5402` |
| Zammad | `http://127.0.0.1:8080` |

## Come si avvia

Prerequisiti: .NET SDK 10.0.4xx, istanza LocalDB `localdev` di SQL Server 2025, [Ollama](https://ollama.com) con
`qwen3.5:9b` e `embeddinggemma`, distro WSL `Ubuntu-26.04` con Docker Engine e Compose, `vm.max_map_count` ≥ 262144
(Elasticsearch).

```powershell
# 1. Connection string negli user-secrets dell'AppHost
dotnet user-secrets set "ConnectionStrings:sql" "Server=(localdb)\localdev;Database=Ticket;Trusted_Connection=True;TrustServerCertificate=True" --project src/Dusiburg.AI.Ticket.AppHost

# 2. Token dell'amministratore di Zammad (profilo di Zammad > Accesso token, permessi admin + ticket.agent)
dotnet user-secrets set "Parameters:zammad-admin-token" "<token>" --project src/Dusiburg.AI.Ticket.AppHost

# 3. Database Ticket ricreato dal modello, con il catalogo demo e i dati simulati di data/ (cancella quello esistente)
dotnet run --project tools/Dusiburg.AI.Ticket.DbInit

# 4. Avvio: Agent, Web, Zammad in WSL e dashboard di Aspire
dotnet run --project src/Dusiburg.AI.Ticket.AppHost

# 5. Con l'AppHost acceso: struttura dell'helpdesk, poi clienti e 80 ticket storici (entrambi idempotenti)
dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- zammad bootstrap
dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- zammad seed

# 6. Indice: chunk anonimizzati di ticket risolti, articoli KB e note di rilascio con i loro embedding (~40 s su CPU)
dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- embed
dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- search --demo 1          # fonti trovate per uno scenario demo
dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- eval retrieval           # hit@k e MRR sul test set, report in eval/

# 7. Riavvio dell'AppHost (l'Agent usa ora il token di ai-assistant), trigger del webhook acceso, poi il ticket in arrivo di
#    uno scenario demo: la bozza compare in Zammad in ~20 s
dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- zammad trigger on
dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- demo 1

# Facoltativo: la pipeline a secco su un ticket (niente scritture in Zammad), o un'elaborazione manuale pubblicata dall'Agent
dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- draft 71164
Invoke-RestMethod -Method Post http://localhost:5401/api/runs -ContentType application/json -Body '{"ticketId":170}'

# 8. L'operatore invia la bozza dall'editor di Zammad (o la Cli fa la sua parte): l'esito e i tag ai-generata /
#    ai-generata-in-parte arrivano in pochi secondi; la console http://localhost:5402 li mostra in Elaborazioni ed Esiti
dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- reply 71171 [--modifica] [--riscrivi] [--senza-dicitura]
dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- close 71171
```

Al primo avvio l'AppHost clona lo stack di Zammad dentro WSL e Docker scarica ~5 GB di immagini; l'amministratore di Zammad si
crea con la procedura guidata su `http://127.0.0.1:8080`, poi si crea il token. `zammad bootstrap` crea l'utente tecnico
`ai-assistant` e scrive il suo token e il segreto del webhook negli user-secrets dell'AppHost. Chiudendo l'AppHost i container
di Zammad si fermano (`Zammad:StopOnExit=false` li lascia accesi).

Le manopole dei modelli, del retrieval e di Zammad (`OLLAMA_*`, `MODEL_*_PROVIDER`, `EMBEDDING_*`, `RETRIEVAL_*`, `RRF_K`, `CONFIDENCE_MIN_FOR_DRAFT`, `TICKET_EVENT_SOURCE`, `ZAMMAD_URL`, `Zammad:*`) sono in
[docs/architettura.md](docs/architettura.md#manopole).

### Build e test

```powershell
dotnet build Dusiburg.AI.Ticket.slnx
dotnet test --solution Dusiburg.AI.Ticket.slnx
```

I test non chiamano mai Ollama né Zammad (handler HTTP finti, probe stub); i test di SQL usano LocalDB `localdev`, con un
database dedicato per classe creato da `DbInit` e poi cancellato. La CI (GitHub Actions, Windows) esegue gli stessi; se il suo
LocalDB non ha il tipo `VECTOR` salta quelli della categoria `RequiresSql2025`.

## Struttura

```text
src/       AppHost, ServiceDefaults, Shared, Data, Ai, Zammad, Agent, Web
tools/     DbInit: ricrea il database · DataGen: genera data/tickets.json · Cli: bootstrap e seed di Zammad, ticket demo
tests/     un progetto NUnit per area
data/      dati simulati: casi, KB, note di rilascio, clienti scritti a mano; ticket generati
infra/     zammad/: configurazione dello stack (zammad.env) e script di avvio in WSL
docs/      architettura
```

## Licenza

MIT, vedi [LICENSE](LICENSE). Lo stack Docker di Zammad (AGPL-3.0) non fa parte del repository: viene clonato all'avvio.
