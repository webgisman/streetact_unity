using System;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Novgov.Network;
using Novgov.Server;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

/// <summary>
/// Test Éditeur AUTOMATISÉ du "handshake" de déploiement multijoueur (2026-09-19) — répond au rapport
/// "après déploiement, le jeu reste bloqué sur 'en attente de l'adversaire'" lors d'une vraie partie
/// à 2 joueurs. Isole EXACTEMENT `MatchSessionManager.RunDeploymentPhaseLive` (le coroutine qui attend
/// deployment_ready puis submit_deployment des DEUX joueurs et diffuse deployment_result) — pas
/// RunMatchLive dans son ensemble, qui a besoin de Supabase (FetchUsername/CreateMatchRecord) pour
/// des étapes qui n'ont RIEN à voir avec le bug rapporté. Deux vraies connexions TCP en boucle locale
/// (127.0.0.1, aucun serveur/handshake JWT nécessaire — PlayerConnection est construite directement
/// autour du socket accepté, exactement comme le ferait GameServerBootstrap après un auth réussi)
/// jouent le rôle des DEUX clients réels, dans l'ORDRE et le PROTOCOLE exact du jeu (deployment_ready
/// puis submit_deployment, voir MultiplayerMatchController.OpenDeploymentDock/SubmitLocalDeployment).
///
/// Verdict de la première exécution complète de ce test (2026-09-19) : AUCUN blocage — les deux
/// PlayerConnection reçoivent bien "deployment_result" dès que les deux soumissions sont drainées, sans
/// avoir besoin du moindre délai (Time.deltaTime vaut 0 hors Play Mode, voir plus bas — le chemin
/// "les deux joueurs répondent" ne dépend jamais de lui). Le blocage rapporté par l'utilisateur n'est
/// donc PAS dans ce coroutine — voir MultiplayerMatchController.RenderWaitingScreenText (2026-09-19)
/// pour la piste retenue à la place : cette attente peut légitimement durer plusieurs minutes
/// (MapReadyMaxWaitSeconds + DeploymentSeconds = 300s+300s dans le pire cas, volontairement généreux)
/// et n'affichait jusqu'ici RIEN qui bouge — indiscernable d'un gel pour le joueur. Conservé comme
/// garde-fou de non-régression : si une future modification du protocole/coroutine réintroduit un
/// vrai blocage serveur, ce test le détectera (timeout à 30s réelles, voir PumpCoroutineToCompletion).
///
/// DEUX PIÈGES RÉELS TROUVÉS EN ÉCRIVANT CE TEST, tous deux spécifiques à l'exécution hors Play Mode
/// (donc sans le moindre effet sur le jeu réel, mais à éviter de re-découvrir à la dure) :
/// 1. Contrairement à OnEnable() (voir TacticalSelectionAutoTest), Awake() n'est PAS exécuté par
///    Unity immédiatement après AddComponent dans ce contexte batch — UnitSpawnerUI.Instance restait
///    NULL tant qu'on ne l'invoque pas nous-mêmes par réflexion, exactement comme Start() est déféré
///    à la première frame (qui n'arrive jamais hors Play Mode).
/// 2. `-standaloneBuildSubtarget Player` compile IN le code client-only de SpawnUnitAt (ex: `Camera.
///    main.transform.position` pour le son de confirmation) — absent de tout vrai build serveur
///    (`#if !UNITY_SERVER`) mais qui lève une NullReferenceException dans CE test (aucune Camera dans
///    la scène minimale) si on ne teste pas sous le VRAI define. Ce test doit donc tourner avec
///    `-buildTarget Linux64 -standaloneBuildSubtarget Server` (comme ServerBuildScript.cs) pour
///    exercer exactement le code qui tourne réellement sur novgov.com — PAS `Player`.
///
/// Usage : "Unity.exe -batchmode -nographics -quit -buildTarget Linux64
/// -standaloneBuildSubtarget Server -projectPath ... -executeMethod DeploymentHandshakeAutoTest.RunAll
/// -logFile chemin.log" — cherche "[DeploymentHandshakeAutoTest]" dans le log. Remettre ensuite
/// standaloneBuildSubtarget à Player avant tout autre test batch (voir la mise en garde de
/// TacticalSelectionAutoTest — ce réglage est un état PERSISTANT du projet, pas remis à zéro entre
/// deux invocations -executeMethod séparées).
/// </summary>
public static class DeploymentHandshakeAutoTest
{
    public static void RunAll()
    {
        int passed = 0, failed = 0;
        Debug.Log("=== DeploymentHandshakeAutoTest : handshake de déploiement PvP en conditions réelles (vrais sockets TCP) ===");

        EditorAutoTestHarness.RunIsolated("DeploymentHandshakeAutoTest", "Les deux joueurs soumettent leur déploiement -> deployment_result reçu par les DEUX (pas de blocage)",
            TestBothPlayersDeployResolvesForBoth, ref passed, ref failed);

        Debug.Log($"[DeploymentHandshakeAutoTest] {passed} réussi(s), {failed} échoué(s).");
        if (failed > 0)
        {
            throw new Exception($"[DeploymentHandshakeAutoTest] {failed} test(s) échoué(s) — voir le log ci-dessus pour le détail.");
        }
    }

    // ---- Fabrique de connexion réelle en boucle locale ------------------------------------------

    /// <summary>Un côté "joueur" complet : le PlayerConnection SERVEUR (branché sur le socket
    /// accepté) + le TcpClient/NetworkStream côté FAUX CLIENT que ce test pilote lui-même pour
    /// envoyer exactement les messages qu'un vrai client enverrait.</summary>
    private class FakePlayer : IDisposable
    {
        public PlayerConnection Server;
        public TcpClient ClientSocket;
        public NetworkStream ClientStream;

        public void Send(NetMessage msg) => NetFraming.WriteMessage(ClientStream, msg);

        public NetMessage TryReceive(int timeoutMs)
        {
            ClientSocket.ReceiveTimeout = timeoutMs;
            return NetFraming.ReadMessage(ClientStream);
        }

        public void Dispose()
        {
            try { Server?.Close(); } catch { }
            try { ClientStream?.Close(); } catch { }
            try { ClientSocket?.Close(); } catch { }
        }
    }

    private static FakePlayer MakeLoopbackPlayer(string userId, int teamId)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var clientSocket = new TcpClient();
        clientSocket.Connect(IPAddress.Loopback, port);
        TcpClient serverSideSocket = listener.AcceptTcpClient();
        listener.Stop();

        var serverConn = new PlayerConnection(serverSideSocket, serverSideSocket.GetStream(), userId) { TeamId = teamId };
        serverConn.StartReceiving();

        return new FakePlayer { Server = serverConn, ClientSocket = clientSocket, ClientStream = clientSocket.GetStream() };
    }


    /// <summary>Pompe manuellement un coroutine (même principe que MatchSessionManager.
    /// RunMatchLiveGuarded, qui fait exactement ça en jeu réel) jusqu'à ce qu'il se termine ou que
    /// <paramref name="realWorldTimeoutSeconds"/> secondes RÉELLES (Stopwatch, pas Time.deltaTime —
    /// hors Play Mode ce dernier ne vaut jamais rien) se soient écoulées. Retourne false = timeout
    /// (le coroutine ne s'est jamais terminé : c'est la signature exacte d'un blocage serveur réel).</summary>
    private static bool PumpCoroutineToCompletion(System.Collections.IEnumerator routine, float realWorldTimeoutSeconds)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.Elapsed.TotalSeconds < realWorldTimeoutSeconds)
        {
            bool moved;
            try { moved = routine.MoveNext(); }
            catch (Exception e)
            {
                Debug.LogError($"[DeploymentHandshakeAutoTest] Exception pendant le pompage du coroutine serveur : {e}");
                return false;
            }
            if (!moved) return true; // coroutine terminé normalement
            // routine.Current peut être un WaitForSeconds/null — sans vraie boucle de jeu pour les
            // honorer, on les traite comme un simple "encore une frame" et on repompe aussitôt.
        }
        return false; // timeout réel écoulé sans jamais atteindre la fin du coroutine — BLOCAGE.
    }

    // ---- Test -----------------------------------------------------------------------------------

    private static bool TestBothPlayersDeployResolvesForBoth()
    {
        GameObject spawnerGo = new GameObject("TestUnitSpawnerUI");
        UnitSpawnerUI spawnerUI = spawnerGo.AddComponent<UnitSpawnerUI>();
        // Awake() N'EST PAS exécuté immédiatement par AddComponent dans ce contexte batch hors Play
        // Mode (voir le 1er piège documenté en tête de fichier) — invoqué nous-mêmes par réflexion,
        // même principe que MakeRealUnit pour UnitAI.Start() dans TacticalSelectionAutoTest.
        MethodInfo awake = typeof(UnitSpawnerUI).GetMethod("Awake", BindingFlags.NonPublic | BindingFlags.Instance);
        if (awake == null) throw new Exception("UnitSpawnerUI.Awake() introuvable par réflexion — a-t-elle été renommée ?");
        awake.Invoke(spawnerUI, null);
        if (UnitSpawnerUI.Instance == null)
            throw new Exception("UnitSpawnerUI.Instance toujours NULL après invocation manuelle de Awake() — le test ne peut pas continuer.");

        MatchSessionManager mgr = EditorAutoTestHarness.MakeRealManagerWithoutStart();

        using FakePlayer p1 = MakeLoopbackPlayer("test-user-1", 1);
        using FakePlayer p2 = MakeLoopbackPlayer("test-user-2", 2);

        MethodInfo runDeploymentPhaseLive = typeof(MatchSessionManager).GetMethod(
            "RunDeploymentPhaseLive", BindingFlags.NonPublic | BindingFlags.Instance);
        if (runDeploymentPhaseLive == null)
            throw new Exception("MatchSessionManager.RunDeploymentPhaseLive introuvable par réflexion — a-t-elle été renommée ?");

        var routine = (System.Collections.IEnumerator)runDeploymentPhaseLive.Invoke(
            mgr, new object[] { "test-match-1", p1.Server, p2.Server });

        // Exactement la séquence réelle d'un client (voir MultiplayerMatchController.
        // OpenDeploymentDock puis SubmitLocalDeployment) : d'abord "carte chargée, dock ouvert",
        // ENSUITE le placement soumis. Un seul Fantassin par camp — le strict minimum pour que
        // ResolveDeployment prenne la branche "placement du joueur conservé", pas le repli fixe.
        p1.Send(new NetMessage { type = "deployment_ready" });
        p2.Send(new NetMessage { type = "deployment_ready" });
        p1.Send(new NetMessage { type = "submit_deployment", placements = new[] { new UnitPlacement { unit_type = 0, x = -20f, y = 0f, z = -20f } } });
        p2.Send(new NetMessage { type = "submit_deployment", placements = new[] { new UnitPlacement { unit_type = 0, x = 20f, y = 0f, z = 20f } } });

        bool completed = PumpCoroutineToCompletion(routine, realWorldTimeoutSeconds: 30f);

        if (!completed)
        {
            Debug.LogError("[DeploymentHandshakeAutoTest] RunDeploymentPhaseLive ne s'est JAMAIS terminé en 30s réelles malgré deployment_ready+submit_deployment envoyés par les DEUX joueurs — reproduit le blocage rapporté ('en attente de l'adversaire' indéfiniment).");
            return false;
        }

        NetMessage result1, result2;
        try
        {
            result1 = p1.TryReceive(5000);
            result2 = p2.TryReceive(5000);
        }
        catch (Exception e)
        {
            Debug.LogError($"[DeploymentHandshakeAutoTest] Le coroutine s'est terminé mais deployment_result n'a pas pu être lu sur un des deux sockets : {e}");
            return false;
        }

        bool ok = true;
        if (result1 == null || result1.type != "deployment_result")
        {
            Debug.LogError($"[DeploymentHandshakeAutoTest] Joueur 1 : attendu 'deployment_result', obtenu '{result1?.type}'.");
            ok = false;
        }
        if (result2 == null || result2.type != "deployment_result")
        {
            Debug.LogError($"[DeploymentHandshakeAutoTest] Joueur 2 : attendu 'deployment_result', obtenu '{result2?.type}'.");
            ok = false;
        }
        // PAS d'assertion sur le CONTENU de deployed_units ici (délibéré) : dans cette scène minimale
        // hors Play Mode, UnitAI.AllLivingUnits reste vide même quand SpawnUnitAt réussit et renvoie
        // une instance non nulle (constaté empiriquement — OnEnable() ne semble pas s'y accrocher de
        // la même façon que pour un UnitAI ajouté directement par TacticalSelectionAutoTest, cause
        // exacte non identifiée, possiblement liée à Instantiate()/Destroy() différé hors Play Mode).
        // Ce test se limite donc, en toute honnêteté, à ce qu'il a RÉELLEMENT vérifié : le handshake
        // deployment_ready/submit_deployment/deployment_result lui-même ne bloque jamais — pas le
        // contenu exact du roster déployé, qui demanderait un vrai Play Mode pour être fiable ici.
        return ok;
    }
}
