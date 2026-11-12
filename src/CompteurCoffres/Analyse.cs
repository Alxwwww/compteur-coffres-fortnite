using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using FortniteReplayReader;
using FortniteReplayReader.Models.NetFieldExports;
using Microsoft.Extensions.Logging.Abstractions;
using Unreal.Core.Attributes;
using Unreal.Core.Contracts;
using Unreal.Core.Models;
using Unreal.Core.Models.Enums;

namespace CompteurCoffres;

// Methode reprise de ChestCounter (LED-N/Fortnite-ChestCounter, licence MIT), reecrite ici :
// le coffre indique lui-meme qui l'a fouille (SearchingPawn), un coffre recharge peut etre recompte,
// et deux defauts de la bibliotheque de lecture sont repares de l'exterieur.

// Champs lus en "nullable" : un false explicite signale un coffre recharge, une absence ne dit rien.
public abstract class Conteneur : INetFieldExportGroup
{
    [NetFieldExport("bAlreadySearched", RepLayoutCmdType.PropertyBool)]
    public bool? bAlreadySearched { get; set; }

    [NetFieldExport("bDestroyed", RepLayoutCmdType.PropertyBool)]
    public bool? bDestroyed { get; set; }

    // Le joueur qui fouille le coffre, et aussi celui qui le detruit.
    [NetFieldExport("SearchingPawn", RepLayoutCmdType.PropertyObject)]
    public uint? SearchingPawn { get; set; }
}

[NetFieldExportGroup("/Game/Building/ActorBlueprints/Containers/Tiered_Chest_Athena.Tiered_Chest_Athena_C", ParseMode.Minimal)]
public class CoffreStandard : Conteneur { }
[NetFieldExportGroup("/Game/Building/ActorBlueprints/Containers/Tiered_Chest_5_Child.Tiered_Chest_5_Child_C", ParseMode.Minimal)]
public class CoffreStandard5 : Conteneur { }
[NetFieldExportGroup("/Game/Building/ActorBlueprints/Containers/AlwaysSpawn_NormalChest.AlwaysSpawn_NormalChest_C", ParseMode.Minimal)]
public class CoffreNormal : Conteneur { }
[NetFieldExportGroup("/Game/Building/ActorBlueprints/Containers/AlwaysSpawn_RareChest.AlwaysSpawn_RareChest_C", ParseMode.Minimal)]
public class CoffreRare : Conteneur { }
[NetFieldExportGroup("/RelicChest/Gameplay/B_TieredChest_Relics.B_TieredChest_Relics_C", ParseMode.Minimal)]
public class CoffreRelique : Conteneur { }
[NetFieldExportGroup("/SpritePosters/Gameplay/RelicChests/B_Delilah_TieredChest_Relics_Variant1.B_Delilah_TieredChest_Relics_Variant1_C", ParseMode.Minimal)]
public class CoffreReliqueVariante1 : Conteneur { }
[NetFieldExportGroup("/SpritePosters/Gameplay/RelicChests/B_Delilah_TieredChest_Relics_Variant2.B_Delilah_TieredChest_Relics_Variant2_C", ParseMode.Minimal)]
public class CoffreReliqueVariante2 : Conteneur { }
[NetFieldExportGroup("/Game/Athena/Items/ForagedItems/BuriedChest/B_Athena_BuriedChest_Chest.B_Athena_BuriedChest_Chest_C", ParseMode.Minimal)]
public class CoffreEnterre : Conteneur { }
[NetFieldExportGroup("/ExtractionRelicFinder/Gameplay/Chests/B_Athena_BuriedChest_RelicFinder.B_Athena_BuriedChest_RelicFinder_C", ParseMode.Minimal)]
public class CoffreEnterreRelique : Conteneur { }
[NetFieldExportGroup("/SpritePosters/Blueprints/Items/Booty/B_SpritePosters_Booty_Chest.B_SpritePosters_Booty_Chest_C", ParseMode.Minimal)]
public class CoffreAvis : Conteneur { }
[NetFieldExportGroup("/SpritePosters/Blueprints/Items/Booty/B_SpritePosters_Booty_Chest_Variant1.B_SpritePosters_Booty_Chest_Variant1_C", ParseMode.Minimal)]
public class CoffreAvisVariante1 : Conteneur { }
[NetFieldExportGroup("/Game/Building/ActorBlueprints/Containers/Tiered_Chest_Sunflower.Tiered_Chest_Sunflower_C", ParseMode.Minimal)]
public class CoffreSunflower : Conteneur { }

// Le coffre dore anime de la chasse au tresor : il porte l'identifiant du lot gagne et le joueur qui l'a ouvert.
[NetFieldExportGroup("/DelilahTreasureHuntGameplay/Gameplay/Actors/BGA_DelilahTreasureHuntAnimatedChest.BGA_DelilahTreasureHuntAnimatedChest_C", ParseMode.Minimal)]
public class CoffreTresor : INetFieldExportGroup
{
    [NetFieldExport("Instigator", RepLayoutCmdType.PropertyObject)]
    public uint? Instigator { get; set; }

    [NetFieldExport("OpeningPawn", RepLayoutCmdType.PropertyObject)]
    public uint? OpeningPawn { get; set; }

    [NetFieldExport("bSuccess", RepLayoutCmdType.PropertyBool)]
    public bool? bSuccess { get; set; }

    [NetFieldExport("PrizeTemplateId", RepLayoutCmdType.Property)]
    public DebuggingObject? PrizeTemplateId { get; set; }
}

/// <summary>Un coffre ouvert ou casse, tel que le replay le signe.</summary>
internal sealed record EvenementCoffre(double Temps, string Cle, string Type, uint? Par, bool Perime, bool Detruit)
{
    /// <summary>Coffre tombe : son auteur est deduit du butin, le serveur ne l'a pas dit.</summary>
    public bool Tombe { get; init; }
}

/// <summary>Une position d'un joueur a un instant (centimetres du jeu).</summary>
internal readonly record struct PointTrace(double T, double X, double Y, double Z);

/// <summary>Butin ejecte d'un conteneur : qui l'a ejecte, quand et ou.</summary>
internal sealed record Ejection(double Temps, uint Conteneur, PointTrace Lieu);

/// <summary>Coffre ferme detruit sans que le serveur dise par qui (son support a ete casse...).</summary>
internal sealed record CoffreOrphelin(double Temps, string Cle, string Type, uint Acteur);

/// <summary>Un lot de la chasse au tresor sorti d'un coffre dore.</summary>
internal sealed record Lot(double Temps, string Identifiant, uint? Pion);

internal sealed class Fouille
{
    public uint Pion;
    public double Debut;
    public double? Fin;
}

// Moment ou ton personnage est dans un vehicule.
internal sealed class Trajet
{
    public double Debut;
    public double? Fin;
}

// Lit un replay et note, au fil de la partie, chaque ouverture ou destruction de coffre et qui l'a faite.
internal sealed class LecteurCoffres : ReplayReader
{
    private static readonly Dictionary<Type, string> Types = new()
    {
        [typeof(CoffreStandard)] = "Classique",
        [typeof(CoffreStandard5)] = "Classique",
        [typeof(CoffreNormal)] = "Classique",
        [typeof(CoffreRare)] = "Rare",
        [typeof(CoffreRelique)] = "Relique",
        [typeof(CoffreReliqueVariante1)] = "Relique",
        [typeof(CoffreReliqueVariante2)] = "Relique",
        [typeof(CoffreEnterre)] = "Enterré",
        [typeof(CoffreEnterreRelique)] = "Enterré",
        [typeof(CoffreAvis)] = "Avis de recherche",
        [typeof(CoffreAvisVariante1)] = "Avis de recherche",
        [typeof(CoffreSunflower)] = "Sunflower",
    };

    private static readonly PropertyInfo? ProprieteTemps =
        typeof(GameState).GetProperty("ReplicatedWorldTimeSecondsDouble") ?? typeof(GameState).GetProperty("ReplicatedWorldTimeSeconds");
    private static readonly PropertyInfo? ProprieteIdJoueur = typeof(FortPlayerState).GetProperty("PlayerID");
    private static readonly FieldInfo? ChampCheminsEchoues = typeof(NetGuidCache).GetField("_failedPaths", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly Regex SuffixeInstance = new(@"(_UAID_.*|_[0-9a-f]{16}_\d+)$", RegexOptions.Compiled);

    private readonly Dictionary<uint, Fouille> _fouillesOuvertes = new();
    private readonly Dictionary<uint, Trajet> _trajetsEnCours = new();
    private double _temps = -1;

    // Etat de chaque canal de coffre ; il repart de zero quand le coffre redevient visible.
    private readonly Dictionary<uint, string> _cleCanal = new();
    private readonly Dictionary<uint, double> _ouvertureCanal = new();
    private readonly Dictionary<uint, bool> _canalFouille = new();
    private readonly Dictionary<uint, uint> _dernierFouilleur = new();
    // Dernier ouvreur connu de chaque coffre, pour reconnaitre une ancienne ouverture revue de loin.
    private readonly Dictionary<string, uint?> _dernierOuvreur = new();
    private readonly HashSet<string> _vus = new();

    // Coffres poses sur la carte dont la bibliotheque ne retrouve pas la classe, en attente de reparation.
    private readonly Dictionary<uint, string> _classesEnAttente = new();
    private int _nombreGroupes = -1;

    // Pour les coffres tombes : ou etait chaque joueur, et ou est retombe le butin.
    private readonly HashSet<uint> _canauxOrphelins = new();
    private readonly HashSet<uint> _canauxLots = new();
    private static readonly Regex IdentifiantLot = new(@"[A-Za-z][A-Za-z0-9_]*:[A-Za-z0-9_.\-]+|[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}", RegexOptions.Compiled);
    public Dictionary<uint, List<PointTrace>> Positions { get; } = new();
    public List<Ejection> Ejections { get; } = new();
    public List<CoffreOrphelin> Orphelins { get; } = new();
    public List<Lot> Lots { get; } = new();
    // Une remise de tresor (GA_TreasureHandler_*) a eu lieu pour toi ou ton escouade : le type exact n'est pas lisible ici.
    public bool TresorRecu { get; private set; }

    protected override void Cleanup()
    {
        TresorRecu = _netGuidCache.NetGuidToPathName.Values.Any(c => c != null && c.Contains("GA_TreasureHandler", StringComparison.Ordinal));
        base.Cleanup();
    }

    public LecteurCoffres() : base(NullLogger<ReplayReader>.Instance, ParseMode.Full) { }

    public List<EvenementCoffre> Evenements { get; } = new();
    public List<Fouille> Fouilles { get; } = new();
    public List<Trajet> Trajets { get; } = new();
    public HashSet<uint> PionsLocaux { get; } = new();
    public Dictionary<uint, uint> EtatParPion { get; } = new();
    public Dictionary<int, uint> EtatParIdJoueur { get; } = new();
    public int CoffresVus => _vus.Count;
    /// <summary>Le replay en cours de lecture (son en-tete est connu des le debut).</summary>
    public FortniteReplayReader.Models.FortniteReplay? EnCours => Replay;

    protected override void OnChannelOpened(uint channelIndex, NetworkGUID? actor)
    {
        base.OnChannelOpened(channelIndex, actor);
        _cleCanal.Remove(channelIndex);
        _canalFouille.Remove(channelIndex);
        _dernierFouilleur.Remove(channelIndex);
        _ouvertureCanal[channelIndex] = _temps;
        PreparerClasse(channelIndex, actor);
    }

    // Un coffre pose sur la carte s'appelle parfois "Tiered_Chest_Athena47_UAID_..." au lieu de sa classe
    // "Tiered_Chest_Athena_C" : la bibliotheque ne le decode alors jamais. On lui donne la bonne classe.
    private void PreparerClasse(uint canal, NetworkGUID? acteur)
    {
        if (acteur == null || Channels[canal]?.Actor?.Archetype?.Value is > 0) return;
        if (!_netGuidCache.TryGetPathName(acteur.Value, out var chemin) || string.IsNullOrEmpty(chemin)) return;
        var nom = chemin[(chemin.LastIndexOfAny(new[] { '.', '/' }) + 1)..];
        var baseNom = Regex.Replace(SuffixeInstance.Replace(nom, ""), @"\d+$", "");
        if (baseNom.Length == 0 || baseNom + "_C" == nom || !baseNom.Contains("Chest", StringComparison.OrdinalIgnoreCase)) return;
        _classesEnAttente[acteur.Value] = baseNom + "_C";
        _nombreGroupes = -1;
        Reparer();
    }

    // Quand une nouvelle classe arrive, les coffres vus juste avant elle sont reessayes au lieu d'etre ignores
    // pour toute la partie, et les coffres en attente recoivent leur classe si elle est maintenant connue.
    private void Reparer()
    {
        var groupes = _netGuidCache.NetFieldExportGroupMap;
        if (groupes.Count == _nombreGroupes) return;
        _nombreGroupes = groupes.Count;
        if (ChampCheminsEchoues?.GetValue(_netGuidCache) is HashSet<string> echoues && echoues.Count > 0)
            echoues.RemoveWhere(c => c.Contains("Chest", StringComparison.OrdinalIgnoreCase));
        foreach (var (guid, classe) in _classesEnAttente.ToList())
        {
            var groupe = groupes.FirstOrDefault(kv => kv.Key.EndsWith("." + classe, StringComparison.Ordinal)).Value;
            if (groupe == null) continue;
            _netGuidCache.NetFieldExportGroupMapPathFixed[guid] = groupe;
            _classesEnAttente.Remove(guid);
        }
    }

    protected override void OnExportRead(uint canal, INetFieldExportGroup? groupe)
    {
        Reparer();
        var guid = Channels[canal]?.Actor?.ActorNetGUID?.Value ?? 0u;
        switch (groupe)
        {
            case GameState etatJeu:
                var temps = ProprieteTemps?.GetValue(etatJeu);
                if (temps != null) _temps = Convert.ToDouble(temps);
                break;

            case FortPlayerState etatJoueur:
                var id = ProprieteIdJoueur?.GetValue(etatJoueur);
                if (id != null) EtatParIdJoueur[Convert.ToInt32(id)] = guid;
                break;

            case PlayerPawn pion:
                if (pion.PlayerState is > 0) EtatParPion[guid] = pion.PlayerState.Value;
                // Seul le personnage du joueur qui enregistre le replay a un Controller.
                if (pion.Controller is > 0) PionsLocaux.Add(guid);
                if (PionsLocaux.Contains(guid) && pion.AttachComponent is { } attache) SuivreTrajet(guid, Convert.ToUInt32(attache));
                if (pion.ReplicatedMovement?.Location is { } lieu)
                {
                    if (!Positions.TryGetValue(guid, out var trace)) Positions[guid] = trace = new();
                    if (trace.Count == 0 || _temps - trace[^1].T >= 0.25) trace.Add(new PointTrace(_temps, lieu.X, lieu.Y, lieu.Z));
                }
                if (pion.bStartedInteractSearch == true && !_fouillesOuvertes.ContainsKey(guid))
                {
                    var fouille = new Fouille { Pion = guid, Debut = _temps };
                    Fouilles.Add(fouille);
                    _fouillesOuvertes[guid] = fouille;
                }
                else if (pion.bStartedInteractSearch == false && _fouillesOuvertes.Remove(guid, out var terminee))
                {
                    terminee.Fin = _temps;
                }
                break;

            case Conteneur coffre:
                SuivreCoffre(canal, coffre);
                break;

            // Le serveur fait du conteneur le proprietaire du butin qu'il ejecte : ca dit d'ou il sort et ou il etait.
            case FortPickup butin when butin.bTossedFromContainer == true && butin.Owner is { } proprietaire && proprietaire > 0:
                if (Channels[canal]?.Actor?.Location is { } depart)
                    Ejections.Add(new Ejection(_temps, Convert.ToUInt32(proprietaire), new PointTrace(_temps, depart.X, depart.Y, depart.Z)));
                break;

            case CoffreTresor tresor when tresor.bSuccess != false && LireLot(tresor.PrizeTemplateId) is { } identifiant && _canauxLots.Add(canal):
                Lots.Add(new Lot(_temps, identifiant, tresor.OpeningPawn ?? tresor.Instigator));
                break;
        }
        base.OnExportRead(canal, groupe);
    }

    // "DelilahPrize:dpid_item4", ou un identifiant nu (celui de l'acces a la boutique pirate), dans les octets bruts.
    private static string? LireLot(DebuggingObject? brut)
    {
        if (brut == null || brut.TotalBits == 0 || brut.Bytes == null) return null;
        var texte = new string(brut.Bytes.Select(o => o is >= 32 and < 127 ? (char)o : ' ').ToArray());
        var trouve = IdentifiantLot.Match(texte);
        return trouve.Success ? trouve.Value : null;
    }

    /// <summary>Ou etait un joueur a un instant : sa derniere position connue, si elle date de moins de 10 s.</summary>
    public PointTrace? PositionDe(uint pion, double temps)
    {
        if (!Positions.TryGetValue(pion, out var trace) || trace.Count == 0) return null;
        PointTrace? derniere = null;
        foreach (var p in trace)
        {
            if (p.T > temps + 0.5) break;
            derniere = p;
        }
        return derniere is { } d && temps - d.T <= 10 ? d : null;
    }

    private void SuivreTrajet(uint guid, uint composant)
    {
        if (composant > 0 && !_trajetsEnCours.ContainsKey(guid))
        {
            var trajet = new Trajet { Debut = _temps };
            _trajetsEnCours[guid] = trajet;
            Trajets.Add(trajet);
        }
        else if (composant == 0 && _trajetsEnCours.Remove(guid, out var fini))
        {
            fini.Fin = _temps;
        }
    }

    private void SuivreCoffre(uint canal, Conteneur coffre)
    {
        if (!_ouvertureCanal.ContainsKey(canal)) _ouvertureCanal[canal] = _temps;
        var cle = Cle(canal);
        var type = Types.GetValueOrDefault(coffre.GetType(), "Autre");
        // Etat recu avec le canal lui-meme : le coffre a ete ouvert avant de redevenir visible.
        var perime = _temps - _ouvertureCanal[canal] < 0.5;

        if (coffre.SearchingPawn is > 0) _dernierFouilleur[canal] = coffre.SearchingPawn.Value;

        if (coffre.bAlreadySearched == false)
        {
            // Recharge pendant la partie : il peut etre ouvert, et compte, a nouveau.
            _canalFouille[canal] = false;
        }
        else if (coffre.bAlreadySearched == true && !_canalFouille.GetValueOrDefault(canal))
        {
            _canalFouille[canal] = true;
            uint? par = _dernierFouilleur.TryGetValue(canal, out var p) ? p : null;
            // Etat perime + meme ouvreur que la derniere fois : c'est l'ancienne ouverture revue, pas une nouvelle.
            var dejaVu = perime && (Stable(cle) ? _dernierOuvreur.TryGetValue(cle, out var precedent) && precedent == par : _vus.Contains(cle));
            if (!dejaVu)
            {
                _vus.Add(cle);
                _dernierOuvreur[cle] = par;
                Evenements.Add(new EvenementCoffre(_temps, cle, type, par, perime, false));
            }
        }
        else if (coffre.bDestroyed == true && coffre.SearchingPawn is > 0
                 && (_vus.Add(cle) || (_canalFouille.TryGetValue(canal, out var dejaFouille) && !dejaFouille)))
        {
            // Casse au lieu d'etre ouvert : le serveur indique qui l'a detruit.
            _canalFouille[canal] = true;
            _dernierOuvreur[cle] = coffre.SearchingPawn;
            Evenements.Add(new EvenementCoffre(_temps, cle, type, coffre.SearchingPawn, perime, true));
        }
        else if (coffre.bDestroyed == true && coffre.SearchingPawn is null or 0 && !perime && Stable(cle)
                 && (!_vus.Contains(cle) || (_canalFouille.TryGetValue(canal, out var ouvert) && !ouvert))
                 && Channels[canal]?.Actor?.ActorNetGUID?.Value is { } acteur && _canauxOrphelins.Add(canal))
        {
            // Coffre ferme detruit sans auteur : son support a ete casse. On retrouve qui l'a fait a la fin,
            // grace au butin qu'il a ejecte et au joueur qui se tenait la.
            Orphelins.Add(new CoffreOrphelin(_temps, cle, type, acteur));
        }
    }

    /// <summary>
    /// Attribue chaque coffre tombe au joueur le plus proche de son butin (a moins de 4,5 m, personne de plus pres).
    /// Idee reprise de ChestCounter 1.5 : le butin d'un coffre retombe a 1-4 m du joueur qui vient de le faire tomber.
    /// </summary>
    public void AttribuerOrphelins()
    {
        foreach (var orphelin in Orphelins)
        {
            var butin = Ejections.Where(e => e.Conteneur == orphelin.Acteur && Math.Abs(e.Temps - orphelin.Temps) <= 2).Select(e => e.Lieu).ToList();
            if (butin.Count == 0) continue;
            uint plusProche = 0;
            var distanceMin = 450.0;
            foreach (var pion in Positions.Keys)
            {
                if (PositionDe(pion, orphelin.Temps) is not { } la) continue;
                var d = butin.Min(b => Math.Sqrt(Math.Pow(b.X - la.X, 2) + Math.Pow(b.Y - la.Y, 2) + Math.Pow(b.Z - la.Z, 2)));
                if (d < distanceMin) { distanceMin = d; plusProche = pion; }
            }
            if (plusProche == 0 || !_vus.Add(orphelin.Cle + "#tombe" + orphelin.Temps.ToString("F0"))) continue;
            Evenements.Add(new EvenementCoffre(orphelin.Temps, orphelin.Cle, orphelin.Type, plusProche, false, true) { Tombe = true });
        }
    }

    // Le meme coffre garde la meme cle toute la partie : son chemin sur la carte, ou sa position s'il apparait en cours de partie.
    private string Cle(uint canal)
    {
        if (_cleCanal.TryGetValue(canal, out var cle)) return cle;
        var acteur = Channels[canal]?.Actor;
        var guid = acteur?.ActorNetGUID?.Value ?? 0u;
        if (_netGuidCache.TryGetPathName(guid, out var chemin) && !string.IsNullOrEmpty(chemin)) cle = chemin;
        else if (acteur?.Location is { } lieu) cle = $"lieu@{lieu.X:F0},{lieu.Y:F0}";
        else cle = $"guid:{guid}";
        _cleCanal[canal] = cle;
        return cle;
    }

    public static bool Stable(string cle) => !cle.StartsWith("guid:", StringComparison.Ordinal);
}

public sealed class Partie
{
    public string Fichier { get; set; } = "";
    public string Cle { get; set; } = "";
    public DateTimeOffset Fin { get; set; }
    public string Mode { get; set; } = "";
    public double Minutes { get; set; }
    public int? Classement { get; set; }
    public int? Eliminations { get; set; }
    public int Ouverts { get; set; }
    public int Rouverts { get; set; }
    public int CassesAPied { get; set; }
    public int CassesVoiture { get; set; }
    public Dictionary<string, int> ParType { get; set; } = new();
    public int CoffresVus { get; set; }
    public int Fouilles { get; set; }
    public bool PersonnageTrouve { get; set; }
    // La lecture s'est bloquee en cours de route : seuls les coffres d'avant le blocage sont comptes.
    public bool Partielle { get; set; }
    // Carte de la partie (en-tete du replay) : seul le Battle Royale de la saison compte.
    public string? Carte { get; set; }
    // Dont coffres tombes (support casse), retrouves grace a leur butin.
    public int CassesTombes { get; set; }
    // Coffres de tes coequipiers (ouverts et casses), memes regles que les tiens.
    public int CoffresEquipe { get; set; }
    public int Equipiers { get; set; }
    // Lots de la chasse au tresor : les tiens, et ceux des autres joueurs de la partie.
    public List<string> Tresors { get; set; } = new();
    public List<string> TresorsAutres { get; set; } = new();
    // Un tresor a ete remis a toi ou a ton escouade pendant la partie (sans savoir lequel).
    public bool TresorRecu { get; set; }

    [JsonIgnore] public int Casses => CassesAPied + CassesVoiture;
    [JsonIgnore] public int Total => Ouverts + Casses;
    [JsonIgnore] public bool Comptee => ModesJeu.Compte(this);
}

internal static class Analyse
{
    private static readonly System.Text.Json.JsonSerializerOptions Json = new();

    /// <summary>
    /// Lit un replay dans ce processus (lance a part par le compteur) et ecrit le resultat dans <paramref name="sortie"/>.
    /// Toutes les 3 secondes, le resultat partiel est ecrit aussi : si la bibliotheque boucle sur un replay,
    /// le compteur arrete ce processus et garde les coffres deja vus. Code 0 : lu ; 2 : partie pas finie ; 3 : erreur.
    /// </summary>
    public static int LireVers(string chemin, string sortie)
    {
        var fichier = new FileInfo(chemin);
        var lecteur = new LecteurCoffres();
        FortniteReplayReader.Models.FortniteReplay? replay = null;
        Exception? erreur = null;
        var fil = new Thread(() =>
        {
            try
            {
                using var flux = new FileStream(fichier.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                replay = lecteur.ReadReplay(flux);
            }
            catch (Exception e) { erreur = e; }
        }) { IsBackground = true, Name = "Lecture du replay" };
        fil.Start();
        while (!fil.Join(TimeSpan.FromSeconds(3)))
        {
            try
            {
                if (lecteur.EnCours is { Info: { IsLive: false, LengthInMs: > 0 } } enCours)
                {
                    var partielle = Construire(lecteur, enCours, fichier, final: false);
                    partielle.Partielle = true;
                    Ecrire(sortie, partielle);
                }
            }
            catch (Exception) { /* lecture en train de modifier les donnees : au prochain tour */ }
        }
        if (erreur != null)
        {
            File.WriteAllText(sortie + ".erreur", erreur.GetType().Name + ": " + erreur.Message);
            return 3;
        }
        if (replay?.Info is not { IsLive: false, LengthInMs: > 0 }) return 2;
        Ecrire(sortie, Construire(lecteur, replay, fichier, final: true));
        return 0;
    }

    private static void Ecrire(string sortie, Partie partie)
    {
        File.WriteAllText(sortie + ".tmp", System.Text.Json.JsonSerializer.Serialize(partie, Json));
        File.Move(sortie + ".tmp", sortie, overwrite: true);
    }

    public static Partie? LireResultat(string sortie)
    {
        try { return File.Exists(sortie) ? System.Text.Json.JsonSerializer.Deserialize<Partie>(File.ReadAllText(sortie), Json) : null; }
        catch (Exception) { return null; }
    }

    private static Partie Construire(LecteurCoffres lecteur, FortniteReplayReader.Models.FortniteReplay replay, FileInfo fichier, bool final)
    {
        var info = replay.Info!;
        if (final) lecteur.AttribuerOrphelins();
        var mesPions = lecteur.PionsLocaux.ToHashSet();
        var evenements = lecteur.Evenements.ToList();
        // Tout ce que le replay t'attribue. Un etat perime n'est garde que pour un coffre reconnaissable.
        bool Retenu(EvenementCoffre e) => !e.Perime || LecteurCoffres.Stable(e.Cle);
        var miens = evenements.Where(e => e.Par is { } p && mesPions.Contains(p) && Retenu(e)).ToList();
        var ouverts = miens.Where(e => !e.Detruit).ToList();
        var casses = miens.Where(e => e.Detruit).ToList();
        var trajets = lecteur.Trajets.ToList();
        var enVoiture = casses.Count(c => trajets.Any(r => c.Temps >= r.Debut && c.Temps <= (r.Fin ?? double.MaxValue)));
        var rouverts = ouverts.Count - ouverts.Select(e => e.Cle).Distinct().Count();

        int? classement = null, eliminations = null;
        var joueurs = replay.PlayerData?.ToList() ?? new();
        var monEtat = mesPions.Select(p => lecteur.EtatParPion.TryGetValue(p, out var e) ? e : 0u).FirstOrDefault(e => e != 0);
        FortniteReplayReader.Models.PlayerData? moi = null;
        if (monEtat != 0)
        {
            var monId = lecteur.EtatParIdJoueur.Where(kv => kv.Value == monEtat).Select(kv => (int?)kv.Key).FirstOrDefault();
            moi = monId == null ? null : joueurs.FirstOrDefault(j => j.Id == monId);
            classement = moi?.Placement;
            eliminations = moi?.Kills == null ? null : (int)moi.Kills;
        }

        // Coequipiers : meme equipe que toi. Leurs coffres sont comptes avec les memes regles que les tiens, tant que
        // le replay les montre (un coffre ouvert a plus de 150 m environ n'y figure pas).
        var pionsEquipe = new HashSet<uint>();
        var equipiers = 0;
        if (moi?.TeamIndex is { } equipe)
        {
            var mates = joueurs.Where(j => j.TeamIndex == equipe && j.Id != moi.Id && j.Id != null).ToList();
            equipiers = mates.Count;
            var idsMates = mates.Select(j => j.Id!.Value).ToHashSet();
            var etatsMates = lecteur.EtatParIdJoueur.Where(kv => idsMates.Contains(kv.Key)).Select(kv => kv.Value).ToHashSet();
            foreach (var (pion, etat) in lecteur.EtatParPion) if (etatsMates.Contains(etat)) pionsEquipe.Add(pion);
        }
        var coffresEquipe = evenements.Count(e => e.Par is { } p && pionsEquipe.Contains(p) && Retenu(e));

        // Lots des coffres dores : les tiens, et ceux des autres (hors coequipiers).
        var lots = lecteur.Lots.ToList();
        var tresors = lots.Where(l => l.Pion is { } p && mesPions.Contains(p)).Select(l => l.Identifiant).ToList();
        var tresorsAutres = lots.Where(l => l.Pion is not { } p || (!mesPions.Contains(p) && !pionsEquipe.Contains(p))).Select(l => l.Identifiant).ToList();
        var carte = replay.Header?.LevelNamesAndTimes?.Select(l => l.Item1).FirstOrDefault(l => !string.IsNullOrEmpty(l));

        return new Partie
        {
            Fichier = fichier.Name,
            Cle = $"{info.Timestamp:O}|{info.LengthInMs}",
            Fin = new DateTimeOffset(fichier.LastWriteTime),
            Mode = (replay.GameData?.CurrentPlaylist ?? "Inconnu").Replace("Playlist_", "", StringComparison.OrdinalIgnoreCase),
            Minutes = Math.Round(info.LengthInMs / 60000.0, 1),
            Classement = classement,
            Eliminations = eliminations,
            Ouverts = ouverts.Count,
            Rouverts = rouverts,
            CassesAPied = casses.Count - enVoiture,
            CassesVoiture = enVoiture,
            ParType = ouverts.GroupBy(e => e.Type).ToDictionary(g => g.Key, g => g.Count()),
            CoffresVus = lecteur.CoffresVus,
            Fouilles = lecteur.Fouilles.ToList().Count(f => mesPions.Contains(f.Pion) && f.Fin != null),
            PersonnageTrouve = mesPions.Count > 0,
            Carte = carte,
            CassesTombes = casses.Count(c => c.Tombe),
            CoffresEquipe = coffresEquipe,
            Equipiers = equipiers,
            Tresors = tresors,
            TresorsAutres = tresorsAutres,
            TresorRecu = lecteur.TresorRecu || tresors.Count > 0,
        };
    }
}
