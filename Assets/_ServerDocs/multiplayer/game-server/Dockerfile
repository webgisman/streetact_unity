# Image minimale pour héberger le build headless Linux du serveur de jeu Unity.
# Le binaire lui-même (NovgovServer.x86_64 + dossier _Data) est copié dans ce contexte
# de build juste avant "docker compose build game-server-1" — voir 01-deployment-vps.md.
FROM debian:bookworm-slim

RUN apt-get update && apt-get install -y --no-install-recommends \
    libc6 libstdc++6 libgcc-s1 ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY NovgovServer_Data ./NovgovServer_Data
COPY NovgovServer.x86_64 ./NovgovServer.x86_64
COPY UnityPlayer.so ./UnityPlayer.so
RUN chmod +x ./NovgovServer.x86_64

EXPOSE 7777

ENTRYPOINT ["./NovgovServer.x86_64", "-batchmode", "-nographics", "-logFile", "/dev/stdout"]
