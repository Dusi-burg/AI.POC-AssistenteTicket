# AI.POC-AssistenteTicket

**Bozze di risposta dentro Zammad, basate sullo storico dei ticket e sulla knowledge base, con un modello locale.**

*[Read in English](README.md)*

Un proof of concept: quando un cliente scrive all'helpdesk, un assistente recupera ticket simili già risolti, articoli della
knowledge base e note di rilascio, e propone all'operatore una **bozza di risposta con le fonti**, direttamente dentro
[Zammad](https://zammad.org) (helpdesk open source) come bozza condivisa. L'operatore la modifica e la invia con i controlli di
Zammad: nulla parte in automatico. Tutto gira in locale: modelli su Ollama, dati su SQL Server LocalDB, Zammad in Docker dentro
WSL.

> **Stato: Fase 8 — valutazione, documentazione e demo.** A un nuovo messaggio del cliente in Zammad (webhook, polling o avvio
> manuale) seguono, in ~10–20 s, una bozza condivisa nell'editor di risposta con la dicitura AI, una nota interna con fonti e
> confidenza e il tag `ai-bozza`: risposta, richiesta di chiarimenti o escalation, decise dal codice. Quando l'operatore invia,
> l'Agent confronta il testo con la bozza, registra l'esito e marca il ticket `ai-generata` o `ai-generata-in-parte`; segnala se
> la dicitura AI è stata tolta. La console mostra elaborazioni, esiti e dati demo. Tutti i dati sono sintetici e fittizi.
>
> Per chi non sviluppa: [il progetto in breve](docs/il-progetto-in-breve.md). Per presentarlo: [la demo](docs/demo.md).

Che cosa aggiunge agli altri POC della serie (Order-to-Cash, Assistente sinistri, Trasporto digitale):

1. integrazione con un prodotto reale di terze parti (Zammad) invece di un sistema simulato;
2. l'operatore resta nel suo strumento: nessuna interfaccia nuova da imparare;
3. ricerca ibrida (BM25 lessicale + vettoriale, Reciprocal Rank Fusion) con filtri di applicabilità per versione;
4. utilità misurata dal comportamento reale dell'operatore (distanza fra bozza e testo inviato).

| | |
|---|---|
| ![La bozza condivisa in Zammad, con la dicitura AI in coda](docs/images/01-bozza-editor.png) | ![Nota interna con confidenza e fonti](docs/images/02-nota-interna.png) |
| *Scenario 1: la bozza nell'editor di Zammad, con la dicitura AI aggiunta dal codice* | *La nota interna per l'operatore: confidenza, decisione, fonti citate e motivo* |
| ![Console: elaborazioni dei sei scenari](docs/images/04-console-elaborazioni.png) | ![Console: esiti con l'avviso di una dicitura tolta](docs/images/06-console-esiti.png) |
| *Console: i sei scenari della demo, risposta, chiarimenti o escalation decisi dal codice* | *Console: che cosa ne hanno fatto gli operatori, con l'avviso di una dicitura AI tolta* |

Altre schermate nella [demo](docs/demo.md#schermate).

## Risultati

Misure del 2026-10-19 sui ticket del test set, esclusi dall'indice, con `qwen3.5:9b` in locale (report in [eval/](eval)):

| Criterio (PLAN §1) | Risultato | Soglia |
|---|---|---|
| Retrieval: fonte del caso fra le prime 5 (hit@5) | **100%** su 135 ticket (97% sulla sola parafrasi del sintomo), MRR@10 1,000 | ≥ 80% |
| Bozze utili (invio diretto o piccole modifiche), griglia manuale su 30 casi | **26 su 30 (86,7%)** — giudizi proposti da Claude, da rivedere | ≥ 60% |
| Termini tecnici non supportati dalle fonti nelle bozze proposte | **0 su 35** | < 5% |
| Latenza dal messaggio alla bozza in Zammad | **9–20 s** dal vivo (6 scenari); pipeline a secco media 9,9 s, p95 13,0 s | < 45 s |
| Demo ripetibile con gli scenari | 6 su 6 con l'esito atteso, da `DbInit` in poi | sì |

I 30 casi della griglia sono i 15 trabocchetti del test set (vaghi, senza storico, istruzioni malevole, modalità o versione
sbagliate, prodotto ambiguo) e 15 casi normali stratificati per prodotto e modalità; le 4 bozze da scartare sono tutte
trabocchetti.

**Confronto con il cloud** (stessi 30 casi, stessa pipeline, stessa griglia proposta da Claude; dati sintetici):

| | `qwen3.5:9b` in locale | `claude-haiku-5-5` |
|---|---|---|
| Bozze utili | 26 su 30 (86,7%): 18 corrette, 8 con modifiche, 4 da scartare | **28 su 30 (93,3%)**: 19 corrette, 9 con modifiche, 2 da scartare |
| Trabocchetti con l'atteso automatico | 6 su 7 (un ticket vago risposto) | **7 su 7** |
| Decisioni: risposta / chiarimenti / escalation | 26 / 2 / 2 | 21 / 8 / 1 |
| Termini tecnici non supportati | 0 su 35 | 0 su 35 |
| Tempo medio della pipeline (p95) | **9,9 s** (13,0 s) | 12,7 s (16,7 s) |
| Costo per 30 casi | 0 (in locale) | 0,046 $ (155K token in ingresso, 62K in uscita) |

Il modello cloud chiede più spesso chiarimenti prima di proporre una soluzione: evita le risposte sbagliate sui trabocchetti, ma
in due casi normali chiede quando la risposta era già nelle fonti. Il locale risponde di più e ricade a volte nel "voi".

```powershell
dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- eval retrieval                       # hit@k e MRR, varianti del retrieval
dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- eval drafts                          # bozze sui 30 casi (~5 min), report, letture e griglia vuota
dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- eval drafts --replay eval/drafts-<data>-qwen3.5-9b.json --griglia eval/griglia-<data>-qwen3.5-9b.json
dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- eval drafts --provider anthropic     # confronto con il cloud (~6 min, ~0,05 $): serve Parameters:anthropic-api-key
```

### Limiti

- **Dati simulati**: più ordinati di quelli veri, e i ticket dello stesso caso condividono oggetti e dettagli (la query completa
  satura il retrieval); nel test set il nome del referente non sempre coincide con la firma nel testo.
- **Modello da 9B su 8 GB di VRAM**: è il limite inferiore della qualità. Casi deboli: un ticket vago a cui risponde invece di
  chiedere, versioni citate dal cliente diverse da quella del problema, qualche ricaduta nel "voi" invece del Lei.
- **Griglia manuale** proposta dall'assistente di sviluppo, da rivedere da chi conosce l'assistenza.
- **Sorgente polling** provata con i test, non dal vivo; l'invio dall'editor di Zammad provato a mano in Fase 0, poi simulato
  con la Cli.
- **Dicitura AI**: testo e perimetro normativo da validare con chi segue la compliance.
- Nessuna autenticazione reale né alta disponibilità; Zammad in configurazione di prova.

### AI di Zammad a fianco (facoltativa)

`zammad ai on` accende l'AI nativa di Zammad 7.2 sullo stesso `qwen3.5:9b`: riassunto del ticket, strumenti di testo
dell'editor e un agente di triage che scrive categoria, priorità e il tag `zammad-ai-triage`. Zammad non scrive risposte con
storico, knowledge base e note di rilascio: è ciò che aggiunge il POC. Sui 6 scenari demo (2026-10-21) il triage concorda con la
nostra classificazione su 5 categorie e 5 priorità su 6, in ~5 s; la bozza costa +1–2 s di coda sulla GPU, dopo aver dato a
Ollama lo stesso contesto della pipeline (con contesti diversi il modello si ricaricava a ogni ticket: +8 s). Prerequisiti e
comandi in [docs/demo.md](docs/demo.md#ai-di-zammad-facoltativa-fase-9a).

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

Dopo un `DbInit` va rieseguito `zammad seed` (ricollega organizzazioni e ticket storici al nuovo database, senza creare nulla).
Le manopole dei modelli, del retrieval e di Zammad (`OLLAMA_*`, `MODEL_*_PROVIDER`, `EMBEDDING_*`, `RETRIEVAL_*`, `RRF_K`, `CONFIDENCE_MIN_FOR_DRAFT`, `TICKET_EVENT_SOURCE`, `FEEDBACK_MAX_EDIT_DISTANCE`, `ZAMMAD_URL`, `Zammad:*`) sono in
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
src/       AppHost, ServiceDefaults, Shared, Data, Ai, Rag, Zammad, Core, Agent, Web
tools/     DbInit: ricrea il database · DataGen: genera data/tickets.json · Cli: Zammad, ticket demo, indice, valutazione, operatore simulato
tests/     un progetto NUnit per area
data/      dati simulati: casi, KB, note di rilascio, clienti scritti a mano; ticket generati
eval/      report di valutazione (retrieval, bozze) con letture e griglia
infra/     zammad/: configurazione dello stack (zammad.env) e script di avvio in WSL
docs/      architettura, demo, il progetto in breve
```

## Licenza

MIT, vedi [LICENSE](LICENSE). Lo stack Docker di Zammad (AGPL-3.0) non fa parte del repository: viene clonato all'avvio.
