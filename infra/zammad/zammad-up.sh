#!/usr/bin/env bash
# Avvia Zammad in primo piano dentro WSL (G7). Lo lancia l'AppHost come risorsa eseguibile "zammad-wsl": finché questo processo
# vive, la VM di WSL resta accesa, e i log dello stack finiscono nel dashboard di Aspire.
#
# Lo stack ufficiale zammad-docker-compose è sotto AGPL-3.0 e non è copiato nel repository: se manca, si clona al tag fissato
# in una cartella di WSL e si usa così com'è. Dal repository arriva solo la configurazione (zammad.env).
#
# Uso: zammad-up.sh <file .env in percorso WSL> <tag dello stack> [stop|keep]
#   stop (default): quando l'AppHost si chiude, i container si fermano (docker compose stop).
#   keep: i container restano accesi.

set -euo pipefail

env_file="$1"
stack_tag="$2"
on_exit="${3:-stop}"
project="zammad"
stack_dir="${ZAMMAD_STACK_DIR:-$HOME/zammad/zammad-docker-compose}"
stack_repository="https://github.com/zammad/zammad-docker-compose.git"

if [ ! -d "$stack_dir/.git" ]; then
    echo "Stack Zammad assente: clono $stack_repository al tag $stack_tag in $stack_dir"
    mkdir -p "$(dirname "$stack_dir")"
    git clone --quiet --depth 1 --branch "$stack_tag" "$stack_repository" "$stack_dir"
fi

current_tag="$(git -C "$stack_dir" describe --tags --exact-match 2>/dev/null || echo "nessuno")"

if [ "$current_tag" != "$stack_tag" ]; then
    echo "Stack Zammad al tag $current_tag: passo a $stack_tag"
    git -C "$stack_dir" fetch --quiet --depth 1 origin tag "$stack_tag"
    git -C "$stack_dir" checkout --quiet "$stack_tag"
fi

cd "$stack_dir"

# Quando l'AppHost si chiude, Aspire uccide wsl.exe e questo script riceve SIGHUP (verificato in Fase 1): i container invece
# resterebbero accesi e, con restart unless-stopped, ripartirebbero al prossimo avvio di Docker in WSL. La trap li ferma.
# A quel punto stdout e stderr sono già chiusi (non c'è più chi legge): niente set -e, SIGPIPE ignorato e output su un log in WSL.
stop_log="$(dirname "$stack_dir")/zammad-stop.log"

stop_stack() {
    trap - HUP TERM INT
    trap '' PIPE
    set +e

    {
        echo "$(date -Is) segnale ricevuto, on_exit=$on_exit"

        if [ "$on_exit" = "stop" ]; then
            docker compose --project-name "$project" stop
            echo "$(date -Is) docker compose stop: codice $?"
        fi
    } >> "$stop_log" 2>&1 < /dev/null

    kill "$compose_pid" 2> /dev/null
    exit 0
}

# Progetto "zammad": stessi volumi dell'installazione della Fase 0. Solo docker-compose.yml, senza override locali.
# compose up gira come figlio (non exec), così la trap di questo script resta attiva.
docker compose --project-name "$project" --env-file "$env_file" --file docker-compose.yml up &
compose_pid=$!

trap stop_stack HUP TERM INT
wait "$compose_pid"
