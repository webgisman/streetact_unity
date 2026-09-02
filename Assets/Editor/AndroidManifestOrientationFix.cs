using System.IO;
using System.Xml;
using UnityEditor.Android;
using UnityEngine;

/// <summary>
/// Force android:screenOrientation="fullSensor" sur l'activité principale du manifeste Android
/// généré, quel que soit ce que UnityEditor calcule depuis les réglages PlayerSettings (2026-09-02) :
/// malgré defaultScreenOrientation=AutoRotation et les 4 allowedAutorotateToX=true dans
/// ProjectSettings.asset, cette version d'Unity (6000.5.8f1, entrée Android "GameActivity")
/// générait quand même android:screenOrientation="userPortrait" (0xd) — bloquant tout passage en
/// paysage, y compris en forçant la rotation au niveau OS (testé via `adb shell settings put system
/// user_rotation`, sans effet). Aucun champ PlayerSettings identifié ne corrige ce calcul (essayé
/// androidResizeableActivity=0 et androidAutoRotationBehavior=0/1, sans effet sur la valeur finale)
/// — plutôt que de continuer à deviner un champ cité nulle part dans ProjectSettings.asset, on
/// patche directement le manifeste généré, qui est le point où le comportement RÉEL est décidé.
/// "fullSensor" = les 4 orientations, jamais restreint par le verrou de rotation système (contexte
/// jeu tactique plein écran, pas un souci pour un joueur qui verrouille son écran en portrait).
/// </summary>
public class AndroidManifestOrientationFix : IPostGenerateGradleAndroidProject
{
    public int callbackOrder => 10; // après la génération du manifeste, avant l'assemblage Gradle final

    public void OnPostGenerateGradleAndroidProject(string path)
    {
        string manifestPath = Path.Combine(path, "src/main/AndroidManifest.xml");
        if (!File.Exists(manifestPath))
        {
            Debug.LogWarning($"[AndroidManifestOrientationFix] Manifeste introuvable à {manifestPath} — correctif d'orientation non appliqué.");
            return;
        }

        var doc = new XmlDocument();
        doc.Load(manifestPath);

        XmlNamespaceManager nsManager = new XmlNamespaceManager(doc.NameTable);
        const string androidNs = "http://schemas.android.com/apk/res/android";
        nsManager.AddNamespace("android", androidNs);

        XmlNodeList activities = doc.SelectNodes("//activity", nsManager);
        int patched = 0;
        foreach (XmlNode activity in activities)
        {
            XmlAttribute orientationAttr = activity.Attributes["android:screenOrientation", androidNs];
            if (orientationAttr == null)
            {
                orientationAttr = doc.CreateAttribute("android", "screenOrientation", androidNs);
                activity.Attributes.Append(orientationAttr);
            }
            orientationAttr.Value = "fullSensor";
            patched++;
        }

        doc.Save(manifestPath);
        Debug.Log($"[AndroidManifestOrientationFix] android:screenOrientation forcé à \"fullSensor\" sur {patched} activité(s) dans {manifestPath}.");
    }
}
