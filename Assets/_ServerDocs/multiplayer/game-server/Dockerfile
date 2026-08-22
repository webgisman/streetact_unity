# Image minimale pour héberger le build headless Linux du serveur de jeu Unity.
# Le binaire lui-même (StreetActServer.x86_64 + dossier _Data) est copié dans ce contexte
# de build juste avant "docker compose build game-server" — voir 01-deployment-vps.md.
FROM debian:bookworm-slim

RUN apt-get update && apt-get install -y --no-install-recommends \
    libc6 libstdc++6 libgcc-s1 ca-certificates \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY StreetActServer_Data ./StreetActServer_Data
COPY StreetActServer.x86_64 ./StreetActServer.x86_64
COPY UnityPlayer.so ./UnityPlayer.so
RUN chmod +x ./StreetActServer.x86_64

EXPOSE 7777

ENTRYPOINT ["./StreetActServer.x86_64", "-batchmode", "-nographics", "-logFile", "/dev/stdout"]
