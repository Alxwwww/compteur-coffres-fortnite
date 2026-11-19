using System.Diagnostics;
using System.Text;

namespace CompteurCoffres;

internal enum EtatMoteur { Demarrage, Attente, Lecture, PasDeReplays, Erreurs }

internal sealed record StatutMoteur(EtatMoteur Etat, string Texte);

// Lit les replays termines qui n'ont pas encore ete comptes.
internal sealed class Moteur
{
    public const int EssaisMax = 3;
    private static readonly TimeSpan DelaiStabilite = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan DelaiEcriture = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan DelaiNouvelEssai = TimeSpan.FromMinutes(10);

    private readonly DirectoryInfo _replays;
    private readonly SemaphoreSlim _reveil = new(0);
    private volatile StatutMoteur _statut = new(EtatMoteur.Demarrage, "Démarrage…");

    public Moteur(string dossierReplays, Stockage stockage)
    {
        _replays = new DirectoryInfo(dossierReplays);
        Stockage = stockage;
        DossierAjoutes = Path.Combine(stockage.Dossier, "Replays ajoutés");
    }

    public Stockage Stockage { get; }
    public StatutMoteur Statut => _statut;

    /// <summary>Une partie vient d'etre comptee (appele depuis le fil du lecteur).</summary>
    public event Action<Partie>? PartieAjoutee;

    /// <summary>Replays deposes dans le tableau (venus d'un autre PC ou d'une sauvegarde) : lus comme ceux de Fortnite.</summary>
    public string DossierAjoutes { get; }

    /// <summary>Lance le prochain scan tout de suite, sans attendre l'intervalle habituel.</summary>
    public void Reveiller()
    {
        lock (_reveil) if (_reveil.CurrentCount == 0) _reveil.Release();
    }

    /// <summary>Attend l'intervalle, ou moins si quelqu'un appelle <see cref="Reveiller"/>.</summary>
    public Task AttendreAsync(TimeSpan intervalle, CancellationToken annulation) => _reveil.WaitAsync(intervalle, annulation);

    /// <summary>Renvoie le nombre de nouvelles parties comptees.</summary>
    public int Scanner(CancellationToken annulation = default)
    {
        _replays.Refresh();
        var demos = _replays.Exists ? _replays.GetFiles("*.replay") : Array.Empty<FileInfo>();
        var ajoutes = Directory.Exists(DossierAjoutes) ? new DirectoryInfo(DossierAjoutes).GetFiles("*.replay") : Array.Empty<FileInfo>();
        if (demos.Length == 0 && ajoutes.Length == 0)
        {
            _statut = new(EtatMoteur.PasDeReplays, "Aucun replay : active-les dans les réglages de Fortnite");
            return 0;
        }
        var maintenant = DateTime.Now;
        var nomsDemos = demos.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var tous = demos.Concat(ajoutes.Where(f => !nomsDemos.Contains(f.Name))).ToArray();
        List<FileInfo> fichiers;
        lock (Stockage.Verrou)
        {
            fichiers = tous
                .Where(f => f.Length >= 1024 && maintenant - f.LastWriteTime >= DelaiStabilite && !Stockage.Donnees.FichiersVus.Contains(f.Name))
                .Where(f =>
                {
                    var echec = Stockage.Donnees.Echecs.FirstOrDefault(e => e.Fichier == f.Name);
                    return echec == null || (echec.Essais < EssaisMax && DateTimeOffset.Now - echec.DernierEssai >= DelaiNouvelEssai);
                })
                .OrderBy(f => f.LastWriteTime)
                .ToList();
        }

        int nouvelles = 0, rang = 0;
        var aSauver = false;
        foreach (var fichier in fichiers)
        {
            if (annulation.IsCancellationRequested) break;
            rang++;
            _statut = new(EtatMoteur.Lecture, fichiers.Count > 1 ? $"Lecture des parties ({rang}/{fichiers.Count})…" : "Lecture de ta dernière partie…");
            var (issue, partie, message) = LecteurExterne.Lire(fichier, annulation);
            switch (issue)
            {
                case IssueLecture.Lue when partie != null:
                    lock (Stockage.Verrou) Stockage.Ajouter(partie);
                    nouvelles++;
                    aSauver = true;
                    if (nouvelles % 10 == 0) Enregistrer();
                    try { PartieAjoutee?.Invoke(partie); } catch (Exception) { /* une notification ne doit pas bloquer le comptage */ }
                    break;
                case IssueLecture.Bloquee:
                    // La bibliotheque tourne en boucle sur ce replay : on garde ce qui a ete lu avant, et on passe a la suite.
                    if (partie is { Total: > 0 })
                    {
                        lock (Stockage.Verrou) Stockage.Ajouter(partie);
                        nouvelles++;
                        Journal.Ecrire(Stockage, $"Lecture bloquee de {fichier.Name} : {partie.Total} coffres gardes sur la partie lue");
                    }
                    else
                    {
                        lock (Stockage.Verrou) Stockage.NoterEchec(fichier.Name, "Lecture bloquée", definitif: true);
                        Journal.Ecrire(Stockage, $"Lecture bloquee de {fichier.Name} : rien de lisible");
                    }
                    aSauver = true;
                    break;
                case IssueLecture.Erreur:
                    // Un fichier tout juste ecrit peut etre incomplet : on reessaie sans le compter comme un echec.
                    if (DateTime.Now - fichier.LastWriteTime < DelaiEcriture) break;
                    lock (Stockage.Verrou) Stockage.NoterEchec(fichier.Name, message ?? "Erreur de lecture");
                    Journal.Ecrire(Stockage, $"Lecture impossible de {fichier.Name} : {message}");
                    aSauver = true;
                    break;
                // EnCours : la partie n'est pas finie, elle sera lue plus tard. Annulee : le programme se ferme.
            }
        }
        if (aSauver) Enregistrer();

        // Rouge seulement si la derniere partie jouee est illisible : un seul replay rate ne veut pas dire que Fortnite a change.
        bool derniereIllisible;
        var plusRecent = demos.OrderByDescending(f => f.LastWriteTime).FirstOrDefault();
        lock (Stockage.Verrou) derniereIllisible = plusRecent != null && Stockage.Donnees.Echecs.Any(e => e.Fichier == plusRecent.Name && e.Essais >= EssaisMax);
        _statut = derniereIllisible
            ? new(EtatMoteur.Erreurs, "Ta dernière partie est illisible : Fortnite a peut-être changé")
            : new(EtatMoteur.Attente, "Actif · en attente de ta prochaine partie");
        return nouvelles;
    }

    public void Enregistrer()
    {
        lock (Stockage.Verrou)
        {
            Stockage.Sauvegarder();
            Tableau.Ecrire(Stockage);
        }
    }
}

internal enum IssueLecture { Lue, EnCours, Bloquee, Erreur, Annulee }

// Lit chaque replay dans un processus a part, avec une limite de temps : un replay qui fait boucler la
// bibliotheque ne bloque plus le compteur, et la memoire de la lecture est rendue des la fin.
internal static class LecteurExterne
{
    private static readonly TimeSpan Limite = TimeSpan.FromMinutes(2);

    public static (IssueLecture Issue, Partie? Partie, string? Message) Lire(FileInfo fichier, CancellationToken annulation)
    {
        var dossier = Path.Combine(Path.GetTempPath(), "CompteurCoffres");
        Directory.CreateDirectory(dossier);
        var sortie = Path.Combine(dossier, Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var demarrage = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
            demarrage.ArgumentList.Add("--lire");
            demarrage.ArgumentList.Add(fichier.FullName);
            demarrage.ArgumentList.Add(sortie);
            using var processus = Process.Start(demarrage) ?? throw new InvalidOperationException("lecteur non demarre");
            try { processus.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { /* deja fini */ }
            var fin = DateTime.Now + Limite;
            while (!processus.WaitForExit(500))
            {
                if (!annulation.IsCancellationRequested && DateTime.Now < fin) continue;
                try { processus.Kill(entireProcessTree: true); } catch (Exception) { /* deja fini */ }
                processus.WaitForExit(5000);
                return annulation.IsCancellationRequested
                    ? (IssueLecture.Annulee, null, null)
                    : (IssueLecture.Bloquee, Analyse.LireResultat(sortie), "lecture trop longue");
            }
            return processus.ExitCode switch
            {
                0 when Analyse.LireResultat(sortie) is { } partie => (IssueLecture.Lue, partie, null),
                2 => (IssueLecture.EnCours, null, null),
                _ => (IssueLecture.Erreur, null, File.Exists(sortie + ".erreur") ? File.ReadAllText(sortie + ".erreur") : $"code {processus.ExitCode}"),
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return (IssueLecture.Erreur, null, e.GetType().Name + ": " + e.Message);
        }
        finally
        {
            foreach (var f in new[] { sortie, sortie + ".tmp", sortie + ".erreur" })
                try { File.Delete(f); } catch (Exception) { /* fichier temporaire */ }
        }
    }
}

// Petit serveur local : le tableau s'ouvre aussi a l'adresse http://localhost:8766, visible seulement sur ce PC.
internal sealed class ServeurLocal : IDisposable
{
    private static readonly int[] Ports = { 8766, 8767, 8768, 8769 };
    private readonly string _page;
    private readonly Func<string>? _etat;
    private readonly ActionsTableau? _actions;
    private System.Net.HttpListener? _ecoute;

    /// <param name="etat">Ce que montre la petite fenetre, en JSON : sert la version OBS (/overlay) et le statut du tableau.</param>
    /// <param name="actions">Ce que les boutons du tableau peuvent faire : ajouter des replays, importer, exporter, regler.</param>
    public ServeurLocal(string page, Func<string>? etat = null, ActionsTableau? actions = null)
    {
        _page = page;
        _etat = etat;
        _actions = actions;
    }

    // Seule la page du tableau peut agir : l'en-tete X-Compteur oblige un autre site a demander la permission
    // au navigateur (CORS), que ce serveur ne donne jamais ; l'origine et l'hote doivent etre ce PC.
    private bool Autorisee(System.Net.HttpListenerRequest requete)
    {
        if (requete.Headers["X-Compteur"] != "1" || Adresse == null) return false;
        var origine = requete.Headers["Origin"];
        return origine == null || origine.TrimEnd('/') == Adresse.TrimEnd('/');
    }

    private static void EcrireJson(System.Net.HttpListenerResponse reponse, int code, object contenu)
    {
        reponse.StatusCode = code;
        reponse.ContentType = "application/json; charset=utf-8";
        reponse.OutputStream.Write(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(contenu));
    }

    private static string? PageObs;

    private static string LireRessource(string nom)
    {
        using var flux = System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream(nom) ?? throw new InvalidOperationException(nom);
        using var lecteur = new StreamReader(flux);
        return lecteur.ReadToEnd();
    }

    public string? Adresse { get; private set; }

    public void Demarrer()
    {
        foreach (var port in Ports)
        {
            try
            {
                var ecoute = new System.Net.HttpListener();
                ecoute.Prefixes.Add($"http://localhost:{port}/");
                ecoute.Start();
                _ecoute = ecoute;
                Adresse = $"http://localhost:{port}/";
                new Thread(Boucle) { IsBackground = true, Name = "Serveur du tableau" }.Start();
                return;
            }
            catch (System.Net.HttpListenerException) { /* port occupe : le suivant */ }
        }
    }

    private void Boucle()
    {
        while (_ecoute is { IsListening: true } ecoute)
        {
            System.Net.HttpListenerContext contexte;
            try { contexte = ecoute.GetContext(); }
            catch (Exception) { return; }
            try
            {
                var requete = contexte.Request;
                var chemin = requete.Url?.AbsolutePath ?? "/";
                contexte.Response.Headers["Cache-Control"] = "no-store";
                if (requete.Url?.Host is not ("localhost" or "127.0.0.1"))
                {
                    contexte.Response.StatusCode = 403;
                    continue;
                }
                if (requete.HttpMethod == "POST")
                {
                    if (_actions == null || !Autorisee(requete))
                    {
                        contexte.Response.StatusCode = 403;
                        continue;
                    }
                    var (code, reponse) = chemin switch
                    {
                        "/ajouter" => _actions.AjouterReplay(requete),
                        "/importer" => _actions.Importer(requete),
                        "/reglages" => _actions.Reglages(requete),
                        _ => (404, (object)new { erreur = "Adresse inconnue" }),
                    };
                    EcrireJson(contexte.Response, code, reponse);
                    continue;
                }
                if (requete.HttpMethod != "GET")
                {
                    contexte.Response.StatusCode = 405;
                    continue;
                }
                if (chemin == "/exporter" && _actions != null)
                {
                    contexte.Response.ContentType = "application/json; charset=utf-8";
                    contexte.Response.Headers["Content-Disposition"] = $"attachment; filename=\"coffres-fortnite-{DateTime.Now:yyyy-MM-dd}.json\"";
                    contexte.Response.OutputStream.Write(_actions.Exporter());
                    continue;
                }
                if (chemin == "/etat.json" && _etat != null)
                {
                    contexte.Response.ContentType = "application/json; charset=utf-8";
                    contexte.Response.OutputStream.Write(Encoding.UTF8.GetBytes(_etat()));
                    continue;
                }
                if (chemin == "/overlay")
                {
                    // Source navigateur pour OBS : fond transparent, la meme carte que la petite fenetre.
                    contexte.Response.ContentType = "text/html; charset=utf-8";
                    contexte.Response.OutputStream.Write(Encoding.UTF8.GetBytes(PageObs ??= LireRessource("overlay.html")));
                    continue;
                }
                if (chemin is not ("/" or "/tableau.html"))
                {
                    contexte.Response.StatusCode = 404;
                    continue;
                }
                byte[] octets;
                if (File.Exists(_page))
                {
                    using var fichier = new FileStream(_page, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var memoire = new MemoryStream();
                    fichier.CopyTo(memoire);
                    octets = memoire.ToArray();
                }
                else
                {
                    octets = Encoding.UTF8.GetBytes("<!doctype html><meta charset=utf-8><meta http-equiv=refresh content=5><p>Tableau en préparation…</p>");
                }
                contexte.Response.ContentType = "text/html; charset=utf-8";
                contexte.Response.Headers["Cache-Control"] = "no-store";
                contexte.Response.OutputStream.Write(octets);
            }
            catch (Exception) { /* fichier en cours de remplacement : la page se recharge seule */ }
            finally
            {
                try { contexte.Response.Close(); } catch (Exception) { }
            }
        }
    }

    public void Dispose()
    {
        try { _ecoute?.Stop(); _ecoute?.Close(); } catch (Exception) { }
    }
}

internal static class Journal
{
    private const long TailleMax = 512 * 1024;

    public static void Ecrire(Stockage stockage, string message)
    {
        try
        {
            var chemin = Path.Combine(stockage.Dossier, "journal.txt");
            if (File.Exists(chemin) && new FileInfo(chemin).Length > TailleMax) File.Delete(chemin);
            File.AppendAllText(chemin, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
        }
        catch (IOException) { /* le journal ne doit jamais bloquer le comptage */ }
    }
}

internal static class Demarrage
{
    private static string Raccourci => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Compteur de coffres.lnk");

    public static bool Actif => File.Exists(Raccourci);

    public static void Activer()
    {
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell indisponible");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic lien = shell.CreateShortcut(Raccourci);
        lien.TargetPath = Environment.ProcessPath!;
        lien.WorkingDirectory = Path.GetDirectoryName(Environment.ProcessPath!)!;
        lien.Description = "Compte automatiquement les coffres Fortnite";
        lien.Save();
    }

    public static void Desactiver()
    {
        if (File.Exists(Raccourci)) File.Delete(Raccourci);
    }
}

// Icone pres de l'horloge et petite fenetre : montrent que le programme tourne et ce qu'il a compte.
internal sealed class ApplicationIcone : ApplicationContext
{
    private static readonly TimeSpan Intervalle = TimeSpan.FromSeconds(30);

    private readonly Moteur _moteur;
    private readonly NotifyIcon _icone;
    private readonly Icon _image;
    private readonly FenetreCompteur _fenetre;
    private readonly ServeurLocal _serveur;
    private readonly ToolStripMenuItem _menuFenetre;
    private readonly CancellationTokenSource _annulation = new();
    private readonly System.Windows.Forms.Timer _rafraichissement;
    private readonly Task _boucle;
    private bool _fortniteOuvert;
    private int _tours;
    private volatile string _etatJson = "{}";

    public ApplicationIcone(Moteur moteur)
    {
        _moteur = moteur;
        _image = Icones.Application();
        _serveur = new ServeurLocal(Tableau.Chemin(moteur.Stockage), () => _etatJson, new ActionsTableau(moteur));
        _serveur.Demarrer();
        var reglages = moteur.Stockage.Donnees.Fenetre;

        _fenetre = new FenetreCompteur(reglages.Verrouillee);
        _fenetre.PositionChangee += () => Regler(r => { r.X = _fenetre.Left; r.Y = _fenetre.Top; });
        _fenetre.VerrouChange += () => Regler(r => r.Verrouillee = _fenetre.Verrouillee);
        _fenetre.DemandeTableau += OuvrirTableau;
        _fenetre.DemandeMasquer += () => AfficherFenetre(false);

        var menu = new ContextMenuStrip();
        menu.Items.Add(_serveur.Adresse is { } adresse ? $"Ouvrir le tableau ({adresse.Replace("http://", "").TrimEnd('/')})" : "Ouvrir le tableau", null, (_, _) => OuvrirTableau());
        _menuFenetre =new ToolStripMenuItem("Afficher la petite fenêtre") { Checked = reglages.Visible, CheckOnClick = true };
        _menuFenetre.CheckedChanged += (_, _) => AfficherFenetre(_menuFenetre.Checked);
        menu.Items.Add(_menuFenetre);
        var escouade = new ToolStripMenuItem("Afficher le total d'escouade") { Checked = reglages.Escouade, CheckOnClick = true };
        escouade.CheckedChanged += (_, _) => { Regler(r => r.Escouade = escouade.Checked); Rafraichir(); };
        menu.Items.Add(escouade);
        var boutique = new ToolStripMenuItem("J'ai eu l'accès à la boutique") { Checked = moteur.Stockage.Donnees.BoutiqueObtenue != null, CheckOnClick = true };
        boutique.CheckedChanged += (_, _) =>
        {
            lock (_moteur.Stockage.Verrou)
            {
                _moteur.Stockage.Donnees.BoutiqueObtenue = boutique.Checked ? _moteur.Stockage.Donnees.BoutiqueObtenue ?? DateTimeOffset.Now : null;
                _moteur.Stockage.Sauvegarder();
                Tableau.Ecrire(_moteur.Stockage);
            }
            Rafraichir();
        };
        // Coche aussi depuis les reglages du tableau : le menu se remet a jour a chaque ouverture.
        menu.Opening += (_, _) => { var obtenue = _moteur.Stockage.Donnees.BoutiqueObtenue != null; if (boutique.Checked != obtenue) boutique.Checked = obtenue; };
        menu.Items.Add(boutique);
        menu.Items.Add("Ajouter mes coffres d'avant le compteur…", null, (_, _) => DemanderAvant());
        menu.Items.Add("Indiquer où en est ma quête…", null, (_, _) => DemanderQuete());
        if (_serveur.Adresse != null) menu.Items.Add("Copier l'adresse pour OBS", null, (_, _) => CopierObs());
        menu.Items.Add(new ToolStripSeparator());
        var demarrage = new ToolStripMenuItem("Lancer au démarrage de Windows") { Checked = Demarrage.Actif, CheckOnClick = true };
        demarrage.CheckedChanged += (_, _) =>
        {
            if (demarrage.Checked) Demarrage.Activer(); else Demarrage.Desactiver();
        };
        menu.Items.Add(demarrage);
        menu.Items.Add("Ouvrir le dossier des données", null, (_, _) => Ouvrir(moteur.Stockage.Dossier));
        menu.Items.Add("Désinstaller…", null, (_, _) => Desinstaller());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quitter", null, (_, _) => Quitter());

        _icone = new NotifyIcon { Icon = _image, Text = "Compteur de coffres", ContextMenuStrip = menu, Visible = true };
        _icone.MouseDoubleClick += (_, e) => { if (e.Button == MouseButtons.Left) OuvrirTableau(); };

        _fenetre.Placer(reglages.X, reglages.Y);
        Rafraichir();
        if (reglages.Visible) _fenetre.Show();
        if (moteur.Stockage.PremierLancement)
            _icone.ShowBalloonTip(8000, "Compteur de coffres lancé",
                "Il lit tes replays Fortnite et compte tes coffres après chaque partie. Clic droit sur le coffre près de l'horloge pour les options.",
                ToolTipIcon.Info);

        // Un tresor dans une partie qui vient de finir : on le dit tout de suite (pas pendant la lecture des anciennes).
        var ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
        moteur.PartieAjoutee += partie =>
        {
            if (DateTimeOffset.Now - partie.Fin > TimeSpan.FromHours(3) || (partie.Tresors.Count == 0 && !partie.TresorRecu)) return;
            var noms = partie.Tresors.Select(i => Tresors.Trouver(i)?.Nom ?? "un trésor de la chasse").ToList();
            var (titre, texte) = noms.Count > 0
                ? ("Trésor obtenu !", $"{string.Join(", ", noms)}, trouvé dans un coffre doré pendant ta dernière partie.")
                : ("Trésor reçu !", "Un trésor a été remis à ton escouade pendant ta dernière partie. Le replay ne dit pas lequel : si c'est l'accès à la boutique, coche « J'ai eu l'accès à la boutique » dans ce menu.");
            ui.Post(_ => _icone.ShowBalloonTip(15000, titre, texte, ToolTipIcon.Info), null);
        };

        _rafraichissement = new System.Windows.Forms.Timer { Interval = 1000 };
        _rafraichissement.Tick += (_, _) => Rafraichir();
        _rafraichissement.Start();

        _boucle = Task.Run(BoucleAsync);
    }

    private void Rafraichir()
    {
        if (_tours++ % 5 == 0)
        {
            var processus = Process.GetProcessesByName("FortniteClient-Win64-Shipping");
            _fortniteOuvert = processus.Length > 0;
            foreach (var p in processus) p.Dispose();
        }
        var statut = _moteur.Statut;
        var (texte, voyant) = statut.Etat switch
        {
            EtatMoteur.Attente when _fortniteOuvert => ("Actif · ta partie sera comptée à la fin", Voyant.Vert),
            EtatMoteur.Attente => (statut.Texte, Voyant.Vert),
            EtatMoteur.Lecture => (statut.Texte, Voyant.Orange),
            EtatMoteur.PasDeReplays or EtatMoteur.Erreurs => (statut.Texte, Voyant.Rouge),
            _ => (statut.Texte, Voyant.Gris),
        };
        var resume = _moteur.Stockage.Resumer();
        if (_fenetre.Visible) _fenetre.Afficher(new EtatAffiche(resume, texte, voyant));
        var info = $"Coffres : {FenetreCompteur.Nombre(resume.Total)} · {texte}";
        _icone.Text = info.Length > 127 ? info[..127] : info;
        _etatJson = System.Text.Json.JsonSerializer.Serialize(new
        {
            total = resume.Total,
            derniere = resume.DernierePartie,
            session = new { coffres = resume.SessionCoffres, parties = resume.SessionParties },
            casses = new { total = resume.Casses, aPied = resume.CassesAPied, voiture = resume.CassesVoiture },
            quete = resume.Quete,
            objectif = Stockage.ObjectifQuete,
            equipe = resume.TotalEquipe,
            derniereEquipe = resume.DerniereEquipe,
            boutique = FenetreCompteur.Pourcentage(resume.ChanceBoutique),
            boutiqueObtenue = resume.BoutiqueObtenue,
            statut = texte,
            voyant = voyant.ToString().ToLowerInvariant(),
            // Change a chaque nouvelle version du tableau : la page ouverte se recharge alors seule.
            version = Tableau.Version,
        });
    }

    private void DemanderAvant()
    {
        int actuel, parties;
        double moyenne;
        lock (_moteur.Stockage.Verrou)
        {
            var comptees = _moteur.Stockage.Comptees();
            actuel = _moteur.Stockage.Donnees.CoffresAvant;
            parties = comptees.Count;
            moyenne = parties > 0 ? comptees.Average(p => p.Total) : 0;
        }
        if (DialogueAvant.Demander(actuel, parties, moyenne) is not { } valeur) return;
        lock (_moteur.Stockage.Verrou)
        {
            _moteur.Stockage.Donnees.CoffresAvant = valeur;
            _moteur.Stockage.Sauvegarder();
            Tableau.Ecrire(_moteur.Stockage);
        }
        Rafraichir();
    }

    private void CopierObs()
    {
        var adresse = _serveur.Adresse + "overlay";
        try { Clipboard.SetText(adresse); } catch (Exception) { /* presse-papiers occupe */ }
        _icone.ShowBalloonTip(8000, "Adresse pour OBS copiée",
            $"{adresse} : dans OBS, ajoute une source « Navigateur » avec cette adresse (300 × 260). Le fond est transparent.", ToolTipIcon.Info);
    }

    // Desinstallation : plus rien au demarrage, le programme supprime (et l'historique si demande).
    // Un programme ne peut pas se supprimer lui-meme : un petit script attend qu'il soit ferme, puis efface les fichiers.
    private void Desinstaller()
    {
        var choix = MessageBox.Show(
            "Désinstaller le compteur de coffres ?\n\nOui : supprimer aussi ton historique de coffres.\nNon : garder ton historique (dossier des données).\nAnnuler : ne rien faire.",
            "Désinstaller le compteur", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question, MessageBoxDefaultButton.Button3);
        if (choix == DialogResult.Cancel || Environment.ProcessPath is not { } exe) return;
        try { Demarrage.Desactiver(); } catch (Exception) { /* deja absent */ }
        var lignes = new List<string>
        {
            "@echo off",
            "chcp 65001 >nul",
            "for /l %%i in (1,1,60) do (",
            $"  del /f /q \"{exe}\" >nul 2>&1",
            $"  if not exist \"{exe}\" goto fini",
            "  ping -n 2 127.0.0.1 >nul",
            ")",
            ":fini",
            $"del /f /q \"{exe}.ancien\" >nul 2>&1",
        };
        if (choix == DialogResult.Yes)
        {
            lignes.Add($"rmdir /s /q \"{Path.GetFullPath(_moteur.Stockage.Dossier)}\" >nul 2>&1");
            lignes.Add("rmdir /s /q \"%TEMP%\\.net\\CompteurCoffres.ReplayReader\" >nul 2>&1");
            lignes.Add("rmdir /s /q \"%TEMP%\\CompteurCoffres\" >nul 2>&1");
        }
        lignes.Add("(goto) 2>nul & del \"%~f0\"");
        try
        {
            var script = Path.Combine(Path.GetTempPath(), $"CompteurCoffres-desinstallation-{Environment.ProcessId}.cmd");
            File.WriteAllText(script, string.Join("\r\n", lignes) + "\r\n", new UTF8Encoding(false));
            Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() })?.Dispose();
        }
        catch (Exception e)
        {
            MessageBox.Show("La désinstallation n'a pas pu démarrer : " + e.Message, "Désinstaller le compteur", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        Quitter();
    }

    private void Regler(Action<ReglagesFenetre> changement)
    {
        lock (_moteur.Stockage.Verrou)
        {
            changement(_moteur.Stockage.Donnees.Fenetre);
            _moteur.Stockage.Sauvegarder();
        }
    }

    private void AfficherFenetre(bool visible)
    {
        if (_menuFenetre.Checked != visible) _menuFenetre.Checked = visible; // rappelle cette methode une fois
        if (visible == _fenetre.Visible) return;
        if (visible)
        {
            _fenetre.Placer(_moteur.Stockage.Donnees.Fenetre.X, _moteur.Stockage.Donnees.Fenetre.Y);
            Rafraichir();
            _fenetre.Show();
        }
        else
        {
            _fenetre.Hide();
        }
        Regler(r => r.Visible = visible);
    }

    private void DemanderQuete()
    {
        if (DialogueQuete.Demander(_moteur.Stockage.Quete()) is not { } valeur) return;
        lock (_moteur.Stockage.Verrou)
        {
            _moteur.Stockage.Caler(valeur);
            _moteur.Stockage.Sauvegarder();
            Tableau.Ecrire(_moteur.Stockage);
        }
        Rafraichir();
    }

    private async Task BoucleAsync()
    {
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
        while (!_annulation.IsCancellationRequested)
        {
            try
            {
                if (_moteur.Scanner(_annulation.Token) > 0) LibererMemoire();
            }
            catch (Exception e)
            {
                Journal.Ecrire(_moteur.Stockage, "Erreur du scan : " + e);
            }
            try { await _moteur.AttendreAsync(Intervalle, _annulation.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    // Entre deux parties le programme ne fait qu'attendre : il rend au systeme la memoire de la lecture.
    private static void LibererMemoire()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        using var processus = Process.GetCurrentProcess();
        SetProcessWorkingSetSize(processus.Handle, -1, -1);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr processus, nint minimum, nint maximum);

    private void OuvrirTableau()
    {
        var chemin = Tableau.Chemin(_moteur.Stockage);
        if (!File.Exists(chemin)) lock (_moteur.Stockage.Verrou) Tableau.Ecrire(_moteur.Stockage);
        Ouvrir(_serveur.Adresse ?? chemin);
    }

    private static void Ouvrir(string chemin) => Process.Start(new ProcessStartInfo(chemin) { UseShellExecute = true });

    private void Quitter()
    {
        _annulation.Cancel();
        _rafraichissement.Stop();
        _fenetre.Hide();
        _icone.Visible = false;
        try { _boucle.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { /* on quitte de toute facon */ }
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _icone.Dispose();
            _image.Dispose();
            _fenetre.Dispose();
            _serveur.Dispose();
            _rafraichissement.Dispose();
            _annulation.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal static class Icones
{
    /// <summary>L'icone du programme (celle du fichier .exe), ou un coffre dessine si elle est introuvable.</summary>
    public static Icon Application()
    {
        try
        {
            if (Environment.ProcessPath is { } exe && Icon.ExtractAssociatedIcon(exe) is { } icone) return icone;
        }
        catch (Exception) { /* on dessine un coffre */ }
        using var image = new Bitmap(32, 32);
        using var g = Graphics.FromImage(image);
        g.FillRectangle(new SolidBrush(Color.FromArgb(150, 92, 40)), 3, 14, 26, 15);
        g.FillRectangle(new SolidBrush(Color.FromArgb(182, 116, 52)), 3, 6, 26, 8);
        g.FillRectangle(new SolidBrush(Color.FromArgb(250, 190, 40)), 3, 13, 26, 3);
        return Icon.FromHandle(image.GetHicon());
    }
}

internal static class Program
{
    private static string DossierDonnees => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CompteurCoffres");
    private static string DossierReplays => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FortniteGame", "Saved", "Demos");

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length >= 2 && args[0] == "--audit")
            return Audit(args[1], Option(args, "--donnees") ?? DossierDonnees, Option(args, "--replays") ?? DossierReplays);
        if (args.Length >= 3 && args[0] == "--lire")
        {
            try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { }
            return Analyse.LireVers(args[1], args[2]);
        }
        if (args.Length >= 4 && args[0] == "--chemins")
            return Analyse.Chemins(args[1], args[2], args[3]);
        if (args.Length >= 2 && args[0] == "--apercu")
            return Apercu(args[1], args.Contains("--vide"));
        if (args.Length >= 2 && args[0] == "--test-serveur")
        {
            // Verification : sert une page quelques secondes et ecrit l'adresse obtenue.
            using var serveur = new ServeurLocal(args[1]);
            serveur.Demarrer();
            File.WriteAllText(args[1] + ".adresse", serveur.Adresse ?? "aucune");
            Thread.Sleep(TimeSpan.FromSeconds(8));
            return serveur.Adresse == null ? 1 : 0;
        }
        if (args.Length >= 2 && args[0] == "--tableau")
            return TableauSeul(args[1], Option(args, "--replays") ?? DossierReplays, int.TryParse(Option(args, "--minutes"), out var m) ? m : 10);
        if (args.Length >= 2 && args[0] == "--demarrage")
        {
            if (args[1] == "on") Demarrage.Activer(); else Demarrage.Desactiver();
            return 0;
        }

        // Une mise a jour faite pendant que le programme tournait laisse l'ancien fichier a cote : on le supprime.
        try { if (Environment.ProcessPath is { } exe && File.Exists(exe + ".ancien")) File.Delete(exe + ".ancien"); } catch (Exception) { }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        var stockage = new Stockage(Option(args, "--donnees") ?? DossierDonnees);
        using var unique = new Mutex(true, @"Local\CompteurCoffresFortnite", out var premier);
        if (!premier)
        {
            // Deja lance : un second clic ouvre simplement le tableau.
            var chemin = Tableau.Chemin(stockage);
            if (File.Exists(chemin)) Process.Start(new ProcessStartInfo(chemin) { UseShellExecute = true });
            return 0;
        }

        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
        var moteur = new Moteur(DossierReplays, stockage);
        lock (stockage.Verrou)
        {
            stockage.Sauvegarder();
            Tableau.Ecrire(stockage);
        }
        Application.Run(new ApplicationIcone(moteur));
        return 0;
    }

    private static string? Option(string[] args, string nom)
    {
        var i = Array.IndexOf(args, nom);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    // Verification : le tableau et ses boutons, sans icone ni petite fenetre, sur un dossier de donnees a part.
    private static int TableauSeul(string donnees, string replays, int minutes)
    {
        var stockage = new Stockage(donnees);
        var moteur = new Moteur(replays, stockage);
        moteur.Enregistrer();
        string Etat()
        {
            var resume = stockage.Resumer();
            var statut = moteur.Statut;
            return System.Text.Json.JsonSerializer.Serialize(new
            {
                total = resume.Total,
                statut = statut.Texte,
                voyant = statut.Etat switch { EtatMoteur.Attente => "vert", EtatMoteur.Lecture => "orange", EtatMoteur.Erreurs or EtatMoteur.PasDeReplays => "rouge", _ => "gris" },
                version = Tableau.Version,
            });
        }
        using var serveur = new ServeurLocal(Tableau.Chemin(stockage), Etat, new ActionsTableau(moteur));
        serveur.Demarrer();
        File.WriteAllText(Path.Combine(donnees, "adresse.txt"), serveur.Adresse ?? "aucune");
        var fin = DateTime.Now.AddMinutes(minutes);
        while (DateTime.Now < fin)
        {
            moteur.Scanner();
            moteur.Enregistrer();
            try { moteur.AttendreAsync(TimeSpan.FromSeconds(10), CancellationToken.None).Wait(); } catch (AggregateException) { }
        }
        return 0;
    }

    // Verification visuelle : dessine la petite fenetre dans des images, sans rien afficher.
    private static int Apercu(string chemin, bool vide)
    {
        var resume = vide ? new Resume(0, null, null, 0, 0, 0, 0, 0, 0, 0, null, null) : new Stockage(DossierDonnees).Resumer();
        var etat = vide
            ? new EtatAffiche(resume, "Lecture des parties (3/45)…", Voyant.Orange)
            : new EtatAffiche(resume, "Actif · en attente de ta prochaine partie", Voyant.Vert);
        foreach (var echelle in new[] { 1f, 1.5f })
        {
            using var image = FenetreCompteur.Rendre(etat, echelle, verrouillee: false);
            image.Save(Path.ChangeExtension(chemin, null) + $"-{(int)(echelle * 100)}.png", System.Drawing.Imaging.ImageFormat.Png);
        }
        return 0;
    }

    // Verification : recompte tout et ecrit un rapport, sans icone.
    private static int Audit(string rapport, string donnees, string replays)
    {
        var stockage = new Stockage(donnees);
        var moteur = new Moteur(replays, stockage);
        var chrono = Stopwatch.StartNew();
        var nouvelles = moteur.Scanner();
        chrono.Stop();
        moteur.Enregistrer();

        var parties = stockage.Donnees.Parties;
        var calage = stockage.Donnees.Calage;
        string Bilan(IEnumerable<Partie> l) => $"ouverts {l.Sum(p => p.Ouverts)}, a pied {l.Sum(p => p.CassesAPied)}, voiture {l.Sum(p => p.CassesVoiture)}";
        var texte = new StringBuilder();
        texte.AppendLine($"Nouvelles parties : {nouvelles} en {chrono.Elapsed.TotalSeconds:F0} s, parties {parties.Count}, echecs {stockage.Donnees.Echecs.Count}");
        texte.AppendLine($"Statut : {moteur.Statut.Etat} - {moteur.Statut.Texte}");
        texte.AppendLine($"Toutes les parties : {Bilan(parties)}");
        var comptees = parties.Where(p => p.Comptee).ToList();
        texte.AppendLine($"Parties comptees : {comptees.Count} ({Bilan(comptees)}), non comptees : {string.Join(", ", parties.Where(p => !p.Comptee).GroupBy(p => p.Mode + " / " + ModesJeu.Raison(p)).Select(g => $"{g.Key} x{g.Count()}"))}");
        texte.AppendLine($"Coffres tombes retrouves : {comptees.Sum(p => p.CassesTombes)}, coffres des coequipiers : {comptees.Sum(p => p.CoffresEquipe)} ({comptees.Count(p => p.Equipiers > 0)} parties en equipe)");
        texte.AppendLine($"Cartes : {string.Join(", ", parties.GroupBy(p => p.Carte ?? "?").Select(g => $"{g.Key} x{g.Count()}"))}");
        texte.AppendLine($"Lots a moi : {string.Join(", ", parties.SelectMany(p => p.Tresors).Select(i => Tresors.Trouver(i)?.Nom ?? i))} | lots des autres : {parties.Sum(p => p.TresorsAutres.Count)} ({string.Join(", ", parties.SelectMany(p => p.TresorsAutres).Select(i => Tresors.Trouver(i)?.Nom ?? i))}) | parties avec un tresor recu : {parties.Count(p => p.TresorRecu)}");
        foreach (var cote in Tresors.Cotes)
            texte.AppendLine($"Chance {Tresors.NomType(cote.Type)} (1/{cote.UnSur}) : {FenetreCompteur.Pourcentage(Tresors.Chance(cote, stockage.CoffresDates(), stockage.Donnees.CoffresAvant))}");
        if (calage != null) texte.AppendLine($"Apres le calage ({calage.Valeur} le {calage.Date:dd/MM HH:mm}) : {Bilan(parties.Where(p => p.Fin > calage.Date))}");
        var resume = stockage.Resumer();
        texte.AppendLine($"Quete estimee : {resume.Quete?.ToString() ?? "non indiquee"}, total : {resume.Total}, session : {resume.SessionCoffres} coffres en {resume.SessionParties} parties");
        foreach (var e in stockage.Donnees.Echecs) texte.AppendLine($"ECHEC {e.Fichier} x{e.Essais} : {e.Message}");
        foreach (var p in parties.TakeLast(4))
            texte.AppendLine($"{p.Fin:dd/MM HH:mm} {p.Mode} {p.Minutes} min top {p.Classement} ouverts {p.Ouverts} a pied {p.CassesAPied} voiture {p.CassesVoiture}");
        File.WriteAllText(rapport, texte.ToString());
        return 0;
    }
}
