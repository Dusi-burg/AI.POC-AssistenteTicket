# Valutazione del retrieval — 2026-10-16 17:10

> Generato da `dotnet run --project tools/Dusiburg.AI.Ticket.Cli -- eval retrieval` (fase 5). Query **completa**: oggetto e
> primo messaggio dei ticket del test set, puliti e anonimizzati come i documenti. Query **solo sintomo**: la variante di sintomo
> riservata al test set, mai indicizzata (senza oggetto e dettagli, che i ticket simulati dello stesso caso condividono).
> Contesto (prodotto, modalità, versione) dal ticket. Rilevanti: i ticket indicizzati dello stesso caso (`CaseCode`) e gli
> articoli e le note citati dal caso. Metriche per fonte; "prompt" = le prime 4 fonti (`PROMPT_SOURCES_MAX`).

- Embedding: `embeddinggemma`; `RETRIEVAL_TOP_K` 20, `RRF_K` 60, `RETRIEVAL_VERSION_PENALTY` 0,500
- Indice: 664 chunk ticket, 121 chunk KB, 60 chunk note di rilascio
- Casi misurati: 135 (esclusi 0 senza fonti rilevanti nell'indice e 15 trabocchetti, a parte)
- Configurazione di default, query completa: **hit@5 100,0%** (soglia 80,0%: raggiunta), **MRR@10 1,000**, precisione del prompt 96,9%
- Solo sintomo: hit@5 97,0%, MRR@10 0,965, precisione del prompt 82,8%

## Varianti — query completa

| Variante | hit@1 | hit@3 | hit@5 | MRR@10 | Precisione prompt | KB/nota nel prompt | Latenza media | p95 |
|---|---|---|---|---|---|---|---|---|
| vettoriale | 100,0% | 100,0% | 100,0% | 1,000 | 96,1% | 95,5% | 5 ms | 4 ms |
| BM25 | 98,5% | 99,3% | 100,0% | 0,991 | 93,7% | 96,2% | 1 ms | 3 ms |
| ibrida (RRF) | 100,0% | 100,0% | 100,0% | 1,000 | 96,5% | 97,0% | 2 ms | 3 ms |
| vettoriale + penalità versione | 100,0% | 100,0% | 100,0% | 1,000 | 96,3% | 96,2% | 2 ms | 2 ms |
| ibrida + penalità versione (default) | 100,0% | 100,0% | 100,0% | 1,000 | 96,9% | 97,0% | 2 ms | 3 ms |

Latenza per query senza l'embedding (calcolato una volta e condiviso fra le varianti): ricerca SQL, BM25 e fusione. "KB/nota nel prompt": casi che citano un articolo o una nota, con almeno uno di essi fra le fonti del prompt.

## Varianti — solo sintomo

| Variante | hit@1 | hit@3 | hit@5 | MRR@10 | Precisione prompt | KB/nota nel prompt | Latenza media | p95 |
|---|---|---|---|---|---|---|---|---|
| vettoriale | 94,8% | 97,0% | 100,0% | 0,965 | 88,5% | 90,2% | 3 ms | 3 ms |
| BM25 | 81,5% | 91,1% | 95,6% | 0,871 | 71,5% | 83,5% | 1 ms | 1 ms |
| ibrida (RRF) | 95,6% | 97,0% | 97,0% | 0,965 | 82,2% | 88,7% | 2 ms | 3 ms |
| vettoriale + penalità versione | 94,8% | 97,0% | 100,0% | 0,965 | 88,5% | 90,2% | 2 ms | 3 ms |
| ibrida + penalità versione (default) | 95,6% | 97,0% | 97,0% | 0,965 | 82,8% | 88,7% | 2 ms | 3 ms |

## Precisione del prompt per prodotto e modalità

| Variante | DA (64) | FF (71) | OnPrem (76) | Saas (59) |
|---|---|---|---|---|
| vettoriale | 94,9% | 97,2% | 97,4% | 94,5% |
| BM25 | 95,3% | 92,3% | 94,4% | 92,8% |
| ibrida (RRF) | 96,5% | 96,5% | 96,7% | 96,2% |
| vettoriale + penalità versione | 94,9% | 97,5% | 97,7% | 94,5% |
| ibrida + penalità versione (default) | 96,5% | 97,2% | 97,4% | 96,2% |
| vettoriale · solo sintomo | 85,2% | 91,5% | 88,8% | 88,1% |
| BM25 · solo sintomo | 72,3% | 70,8% | 71,7% | 71,2% |
| ibrida (RRF) · solo sintomo | 80,5% | 83,8% | 82,6% | 81,8% |
| vettoriale + penalità versione · solo sintomo | 85,2% | 91,5% | 88,8% | 88,1% |
| ibrida + penalità versione (default) · solo sintomo | 81,6% | 83,8% | 83,2% | 82,2% |

## Trabocchetti (configurazione di default, query completa)

Con un caso atteso conta la posizione della prima fonte rilevante; senza (`NoHistory`, `Vague`) conta che le fonti trovate
siano deboli: il punteggio RRF massimo, con entrambe le classifiche al primo posto, è 2/(k+1). La decisione su chiarimenti ed
escalation è della Fase 6.

| Ticket | Tipo | Caso atteso | Prima rilevante | Prima fonte | Punteggio | Distanza coseno |
|---|---|---|---|---|---|---|
| HT-2026-0427 | VersionOutOfRange | FF-TLS-485 | 1 | HT-2025-0379 | 0,0323 | 0,248 |
| HT-2026-0438 | ModeMismatch | FF-TLS-SAAS | 1 | HT-2025-0271 | 0,0323 | 0,256 |
| HT-2026-0444 | AmbiguousProduct | DA-OCR-NONATTIVO | 1 | HT-2026-0290 | 0,0323 | 0,335 |
| HT-2026-0450 | Vague | — | oltre 10 | HT-2025-0322 | 0,0318 | 0,329 |
| HT-2026-0457 | ModeMismatch | DA-DISCO-ONPREM | 1 | HT-2025-0377 | 0,0318 | 0,240 |
| HT-2026-0464 | NoHistory | — | oltre 10 | RN-DA-3.0.4-06 | 0,0315 | 0,436 |
| HT-2026-0468 | PromptInjection | FF-SCARTO-DUPLICATO | 1 | HT-2024-0013 | 0,0323 | 0,197 |
| HT-2026-0475 | VersionOutOfRange | FF-RICEZIONE-CANALE | 1 | HT-2026-0137 | 0,0318 | 0,188 |
| HT-2026-0482 | NoHistory | — | oltre 10 | HT-2026-0283 | 0,0302 | 0,517 |
| HT-2026-0488 | PromptInjection | DA-PERMESSI-CACHE | 1 | HT-2026-0217 | 0,0323 | 0,227 |
| HT-2026-0491 | VersionOutOfRange | FF-FIRMA-CERT-ONPREM | 8 | HT-2026-0072 | 0,0325 | 0,206 |
| HT-2026-0493 | VersionOutOfRange | — | oltre 10 | HT-2026-0316 | 0,0325 | 0,304 |
| HT-2026-0495 | Vague | — | oltre 10 | HT-2025-0389 | 0,0325 | 0,342 |
| HT-2026-0497 | ModeMismatch | FF-RICEZIONE-CANALE | 1 | HT-2026-0137 | 0,0323 | 0,298 |
| HT-2026-0511 | PromptInjection | TR-SSO | 1 | HT-2026-0023 | 0,0328 | 0,134 |

## Casi deboli — query completa: 1

Configurazione di default: nessuna fonte rilevante fra le prime 5, oppure meno di metà delle 4 fonti del prompt rilevanti.

| Ticket | Caso | Modalità | Versione | Prima rilevante | Precisione prompt | Fonti del prompt (caso) |
|---|---|---|---|---|---|---|
| HT-2026-0346 | FF-SEZIONALI-CONFIG | Saas | 5.2.1 | 1 | 25,0% | KB-FF-0013, HT-2026-0422 (FF-NUMERAZIONE-ANNO), HT-2024-0004 (FF-NUMERAZIONE-ANNO), HT-2026-0074 (FF-NUMERAZIONE-ANNO) |

## Casi deboli — solo sintomo: 12

Configurazione di default: nessuna fonte rilevante fra le prime 5, oppure meno di metà delle 4 fonti del prompt rilevanti.

| Ticket | Caso | Modalità | Versione | Prima rilevante | Precisione prompt | Fonti del prompt (caso) |
|---|---|---|---|---|---|---|
| HT-2025-0004 | TR-LICENZA-SAAS | Saas | 4.8.5 | 1 | 25,0% | KB-FF-0102, RN-FF-5.2.1-03, KB-FF-0013, HT-2026-0244 (FF-SEZIONALI-CONFIG) |
| HT-2025-0047 | TR-PRESTAZIONI-ONPREM | OnPrem | 2.4.7 | 6 | 0,0% | KB-DA-0004, HT-2025-0258 (DA-INDEX-LENTA-ANTIVIRUS), HT-2025-0184 (DA-INDEX-LENTA-ANTIVIRUS), HT-2026-0402 (DA-INDEX-LENTA-ANTIVIRUS) |
| HT-2025-0075 | TR-LICENZA-SAAS | Saas | 2.4.7 | 1 | 25,0% | KB-DA-0102, HT-2026-0421 (DA-QUOTA-SAAS), HT-2026-0336 (DA-QUOTA-SAAS), HT-2026-0454 (DA-QUOTA-SAAS) |
| HT-2025-0104 | TR-LICENZA-SAAS | Saas | 5.0.3 | 1 | 25,0% | KB-FF-0102, RN-FF-5.2.1-03, KB-FF-0013, HT-2026-0244 (FF-SEZIONALI-CONFIG) |
| HT-2026-0055 | TR-LICENZA-SAAS | Saas | 3.1.1 | 1 | 25,0% | KB-DA-0102, HT-2026-0421 (DA-QUOTA-SAAS), HT-2026-0336 (DA-QUOTA-SAAS), HT-2026-0454 (DA-QUOTA-SAAS) |
| HT-2026-0159 | FF-TLS-485 | OnPrem | 4.8.5 | 6 | 0,0% | HT-2026-0396 (FF-TLS-ONPREM), HT-2025-0263 (FF-TLS-ONPREM), HT-2026-0280 (FF-TLS-ONPREM), KB-FF-0004 |
| HT-2026-0164 | FF-TLS-485 | OnPrem | 4.8.5 | 6 | 0,0% | HT-2026-0396 (FF-TLS-ONPREM), HT-2025-0263 (FF-TLS-ONPREM), HT-2026-0280 (FF-TLS-ONPREM), KB-FF-0004 |
| HT-2026-0223 | DA-WF-TIMER-311 | Saas | 3.1.1 | 1 | 25,0% | KB-DA-0006, HT-2025-0210 (DA-WF-APPROVATORE), KB-DA-0005, HT-2026-0201 (DA-WF-CONFIG) |
| HT-2026-0254 | TR-PRESTAZIONI-ONPREM | OnPrem | 3.1.1 | 6 | 0,0% | KB-DA-0004, HT-2025-0258 (DA-INDEX-LENTA-ANTIVIRUS), HT-2025-0184 (DA-INDEX-LENTA-ANTIVIRUS), HT-2026-0402 (DA-INDEX-LENTA-ANTIVIRUS) |
| HT-2026-0299 | FF-SEZIONALI-CONFIG | Saas | 5.2.0 | 1 | 25,0% | KB-FF-0013, HT-2026-0422 (FF-NUMERAZIONE-ANNO), HT-2024-0004 (FF-NUMERAZIONE-ANNO), HT-2026-0074 (FF-NUMERAZIONE-ANNO) |
| HT-2026-0308 | DA-WF-TIMER-311 | Saas | 3.1.1 | 1 | 25,0% | KB-DA-0006, HT-2025-0210 (DA-WF-APPROVATORE), KB-DA-0005, HT-2026-0201 (DA-WF-CONFIG) |
| HT-2026-0346 | FF-SEZIONALI-CONFIG | Saas | 5.2.1 | 1 | 25,0% | KB-FF-0013, HT-2026-0422 (FF-NUMERAZIONE-ANNO), HT-2024-0004 (FF-NUMERAZIONE-ANNO), HT-2026-0074 (FF-NUMERAZIONE-ANNO) |

