"""Test de bout en bout de la bataille de siège au tour par tour contre le serveur de production.

Joueur 1 (testlille1) assiège le quartier de Joueur 2 (testlille2, tuile 66648/44110), les deux
rejoignent la salle d'attente du siège sur l'instance choisie comme le fait le client
(instances vivantes triées par id, indice = siege_id % nombre), et on vérifie que le serveur
envoie "match_found" (mode "siege") aux DEUX, puis qu'il répond à "city_verify". Les deux se
déconnectent ensuite : le serveur doit rouvrir le siège ("pending").

Avec --tours N : les deux camps confirment un placement VIDE (le serveur place alors lui-même
l'escouade standard, voir UnitSpawnerUI.AutoDeployTeamFallback), puis jouent N tours en envoyant
toutes leurs unités vers la base adverse — le vrai moteur du serveur simule et renvoie chaque tour.
Les deux partent ensuite : personne ne gagne, le siège est rouvert, aucun quartier ne change de mains.

ATTENTION : agit sur le serveur de PRODUCTION avec les deux comptes de test (crée un siège s'il n'y
en a pas déjà un ouvert, qui reste ouvert ensuite). Usage, depuis la racine du projet :
    python Tools/siege_battle_test.py [--tours N]"""
import json, re, socket, struct, sys, time, urllib.request, urllib.error
from datetime import datetime, timezone

BASE = "https://novgov.com"
args = sys.argv[1:]
TOURS = 0
if "--tours" in args:
    i = args.index("--tours")
    TOURS = int(args[i + 1])
    del args[i:i + 2]
# Clé publique "anon" : lue dans le client Unity (même valeur que SupabaseAuthClient.AnonKey).
ANON = args[0] if args else re.search(r'AnonKey = "([^"]+)"', open("Assets/Scripts/Auth/SupabaseAuthClient.cs", encoding="utf-8").read()).group(1)
HOST = "novgov.com"
TILE = (66648, 44110)


def http(method, url, body=None, token=None):
    req = urllib.request.Request(url, method=method, data=json.dumps(body).encode() if body is not None else None)
    req.add_header("apikey", ANON)
    req.add_header("Content-Type", "application/json")
    if token:
        req.add_header("Authorization", "Bearer " + token)
    try:
        with urllib.request.urlopen(req, timeout=20) as r:
            return r.status, json.loads(r.read() or b"null")
    except urllib.error.HTTPError as e:
        return e.code, e.read().decode()


def login(n):
    st, d = http("POST", f"{BASE}/auth/v1/token?grant_type=password", {"email": f"testlille{n}@novgov.test", "password": f"TestLille{n}!"})
    assert st == 200, d
    return d["access_token"], d["user"]["id"]


def send(sock, msg):
    payload = json.dumps(msg).encode()
    sock.sendall(struct.pack("<i", len(payload)) + payload)


def recv_all(sock, seconds, until=None, keepalive=()):
    """Lit les messages de `sock` pendant `seconds` (ou jusqu'à ce que `until(msg)` soit vrai).
    Envoie un "heartbeat" toutes les 5 s sur `sock` et sur `keepalive`, comme le vrai client : le
    serveur ferme toute connexion muette depuis 20 s (ReceiveTimeout, GameServerBootstrap)."""
    sock.settimeout(0.5)
    out, buf, end, last_hb = [], b"", time.time() + seconds, 0.0
    while time.time() < end:
        if time.time() - last_hb >= 5:
            for k in (sock, *keepalive):
                send(k, {"type": "heartbeat"})
            last_hb = time.time()
        if until and any(until(m) for m in out):
            break
        try:
            chunk = sock.recv(65536)
            if not chunk:
                break
            buf += chunk
        except socket.timeout:
            pass
        while len(buf) >= 4:
            n = struct.unpack("<i", buf[:4])[0]
            if len(buf) < 4 + n:
                break
            out.append(json.loads(buf[4:4 + n].decode()))
            buf = buf[4 + n:]
    return out


t1, uid1 = login(1)
t2, uid2 = login(2)
print("connectés :", uid1[:8], uid2[:8])

# Réutilise un siège déjà ouvert entre les deux comptes de test (sinon start_siege refuserait :
# "Un siège est déjà en cours sur cette zone"), sinon en déclare un nouveau.
st, open_sieges = http("GET", f"{BASE}/rest/v1/zone_sieges?status=eq.pending&attacker_user_id=eq.{uid1}&tile_x=eq.{TILE[0]}&tile_y=eq.{TILE[1]}&select=id", token=t1)
if st == 200 and open_sieges:
    siege_id = open_sieges[0]["id"]
    print("siège déjà ouvert réutilisé :", siege_id)
else:
    st, res = http("POST", f"{BASE}/rest/v1/rpc/start_siege", {"p_tile_x": TILE[0], "p_tile_y": TILE[1], "p_zoom": 17}, t1)
    print("start_siege ->", st, res)
    assert st == 200, "le siège n'a pas pu être déclaré"
    siege_id = res[0]["new_siege_id"]

st, inst = http("GET", f"{BASE}/rest/v1/server_instances?order=id.asc&select=id,public_port,updated_at", token=t1)
now = datetime.now(timezone.utc)
alive = [i for i in inst if i["public_port"] > 0 and (now - datetime.fromisoformat(i["updated_at"])).total_seconds() < 180]
port = alive[siege_id % len(alive)]["public_port"]
print(f"siège #{siege_id} -> instances vivantes {[i['id'] for i in alive]} -> port {port}")


def connect(token):
    s = socket.create_connection((HOST, port), timeout=10)
    send(s, {"type": "auth", "access_token": token})
    send(s, {"type": "join_matchmaking", "mode": "siege_battle", "siege_id": siege_id, "zone_tile_x": TILE[0], "zone_tile_y": TILE[1]})
    return s


attacker = connect(t1)
early = recv_all(attacker, 4)
print("attaquant seul en salle d'attente, messages reçus :", sorted({m.get("type") for m in early}))
assert not any(m.get("type") == "match_found" for m in early), "la bataille ne doit pas démarrer sans le défenseur"

defender = connect(t2)
is_match_found = lambda m: m.get("type") == "match_found"
msgs_a = recv_all(attacker, 25, until=is_match_found, keepalive=[defender])
msgs_d = recv_all(defender, 5, until=is_match_found, keepalive=[attacker])
mf_a = [m for m in msgs_a if m.get("type") == "match_found"]
mf_d = [m for m in msgs_d if m.get("type") == "match_found"]
for name, mf in (("attaquant", mf_a), ("défenseur", mf_d)):
    if mf:
        m = mf[0]
        print(f"{name} : match_found mode={m.get('mode')} équipe={m.get('team_id')} adversaire={m.get('opponent_username')} quartier=({m.get('zone_tile_x')},{m.get('zone_tile_y')}) carte_json={'oui' if m.get('city_data_json') else 'non'}")
    else:
        print(f"{name} : PAS de match_found — messages : {sorted({x.get('type') for x in (msgs_a if name == 'attaquant' else msgs_d)})}")

# Vérification de géométrie : un hash volontairement faux doit recevoir, tout de suite, un refus
# accompagné de la structure de bâtiments de référence (resynchronisation).
verify_ok = False
if mf_a:
    send(attacker, {"type": "city_verify", "city_building_hash": 1, "city_building_count": 0})
    is_verify = lambda m: m.get("type") == "city_verify_result"
    replies = [m for m in recv_all(attacker, 10, until=is_verify, keepalive=[defender]) if is_verify(m)]
    if replies:
        r = replies[0]
        nb = len(r.get("city_buildings") or [])
        print(f"city_verify_result : success={r.get('success')} bâtiments de référence={nb}")
        verify_ok = (not r.get("success")) and nb > 0
    else:
        print("city_verify_result : AUCUNE réponse en 10 s")

battle_ok = True
if TOURS > 0 and mf_a and mf_d:
    clients = {1: attacker, 2: defender}
    others = lambda team: [c for t, c in clients.items() if t != team]
    for s in clients.values():
        send(s, {"type": "deployment_ready"})
        send(s, {"type": "submit_deployment", "placements": []})

    is_dep = lambda m: m.get("type") == "deployment_result"
    units = {}
    for team, s in clients.items():
        dep = [m for m in recv_all(s, 90, until=is_dep, keepalive=others(team)) if is_dep(m)]
        if not dep:
            print(f"équipe {team} : PAS de deployment_result")
            battle_ok = False
            continue
        for u in dep[0].get("deployed_units") or []:
            units[u["unit_id"]] = u
    combat = {uid: u for uid, u in units.items() if u.get("unit_type") != 4}  # 4 = barricade
    for team in (1, 2):
        mine = [u for u in combat.values() if u["team_id"] == team]
        gaps = [((a["x"] - b["x"]) ** 2 + (a["z"] - b["z"]) ** 2) ** 0.5 for i, a in enumerate(mine) for b in mine[i + 1:]]
        print(f"déploiement automatique équipe {team} : {len(mine)} unités, écart minimal {min(gaps):.1f} m" if gaps else f"équipe {team} : {len(mine)} unités")

    enemy_base = {1: (25.0, 25.0), 2: (-25.0, -25.0)}
    is_tr = lambda m: m.get("type") in ("turn_result", "match_over")
    for turn in range(1, TOURS + 1):
        if not battle_ok:
            break
        for team, s in clients.items():
            bx, bz = enemy_base[team]
            orders = [{"unit_id": uid, "path": [{"x": bx, "y": 0.0, "z": bz, "action": 0}]}
                      for uid, u in combat.items() if u["team_id"] == team and not u.get("dead")]
            send(s, {"type": "submit_turn", "turn_number": turn, "orders": orders})
        over = False
        for team, s in clients.items():
            got = [m for m in recv_all(s, 150, until=is_tr, keepalive=others(team)) if is_tr(m)]
            if not got:
                print(f"tour {turn} : équipe {team} n'a reçu AUCUN résultat")
                battle_ok = False
                continue
            if got[0]["type"] == "match_over":
                print(f"tour {turn} : fin de partie, vainqueur équipe {got[0].get('winner_team')}")
                over = True
                continue
            send(s, {"type": "turn_result_ack", "turn_number": turn})
            snaps = got[0].get("snapshots") or []
            for st in (snaps[-1].get("units") or []) if snaps else []:
                if st["unit_id"] in combat and combat[st["unit_id"]]["team_id"] == team:
                    combat[st["unit_id"]].update(x=st["x"], z=st["z"], dead=st.get("dead", False), health=st.get("health"))
            if team == 1:
                n_snaps = len(snaps)
        if over:
            break
        # Le serveur ne rouvre la planification qu'après "turn_playback_start" : un ordre envoyé
        # avant serait écarté (numéro de tour différent).
        is_pb = lambda m: m.get("type") in ("turn_playback_start", "match_over")
        for team, s in clients.items():
            got = [m for m in recv_all(s, 10, until=is_pb, keepalive=others(team)) if is_pb(m)]
            if got and got[0]["type"] == "match_over":
                print(f"tour {turn} : fin de partie, vainqueur équipe {got[0].get('winner_team')}")
                over = True
        alive = {t: sum(1 for u in combat.values() if u["team_id"] == t and not u.get("dead")) for t in (1, 2)}
        print(f"tour {turn} : {n_snaps} instantanés simulés — unités vivantes : équipe 1 = {alive[1]}, équipe 2 = {alive[2]}")
        if over:
            break
        time.sleep(1)

attacker.close()
defender.close()
ok = bool(mf_a and mf_d and mf_a[0].get("mode") == "siege" and mf_a[0].get("team_id") == 1 and mf_d[0].get("team_id") == 2 and verify_ok and battle_ok)

time.sleep(8)
st, rows = http("GET", f"{BASE}/rest/v1/zone_sieges?id=eq.{siege_id}&select=id,status,deadline", token=t1)
print("statut du siège après départ des deux joueurs :", rows)
print("RÉSULTAT :", "OK" if ok else "ÉCHEC")
sys.exit(0 if ok else 1)
