using System.Drawing.Drawing2D;
using System.Globalization;
using System.Runtime.InteropServices;

namespace CompteurCoffres;

internal enum Voyant { Gris, Vert, Orange, Rouge }

/// <summary>Ce que la petite fenetre montre a un instant donne.</summary>
internal sealed record EtatAffiche(Resume Resume, string Statut, Voyant Voyant);

// Petite fenetre toujours visible : le total, la derniere partie, la session, et un voyant qui dit si le compteur marche.
internal sealed class FenetreCompteur : Form
{
    private const int Largeur = 300;
    private static readonly CultureInfo Fr = CultureInfo.GetCultureInfo("fr-FR");
    private static readonly Color Fond = Color.FromArgb(22, 24, 29);
    private static readonly Color Bord = Color.FromArgb(54, 58, 66);
    private static readonly Color Etiquette = Color.FromArgb(150, 156, 166);
    private static readonly Color Texte = Color.FromArgb(236, 238, 242);
    private static readonly Color Or = Color.FromArgb(245, 183, 49);
    private static readonly Color Trait = Color.FromArgb(46, 50, 57);
    private static readonly Dictionary<Voyant, Color> Couleurs = new()
    {
        [Voyant.Gris] = Color.FromArgb(120, 126, 136),
        [Voyant.Vert] = Color.FromArgb(63, 185, 80),
        [Voyant.Orange] = Color.FromArgb(226, 162, 42),
        [Voyant.Rouge] = Color.FromArgb(240, 82, 72),
    };

    private EtatAffiche? _etat;
    private Rectangle _zoneVerrou;
    private bool _verrouillee;
    private Point? _prise;
    private readonly ToolStripMenuItem _menuVerrou;

    public event Action? PositionChangee;
    public event Action? VerrouChange;
    public event Action? DemandeTableau;
    public event Action? DemandeMasquer;

    public FenetreCompteur(bool verrouillee)
    {
        _verrouillee = verrouillee;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        DoubleBuffered = true;
        BackColor = Fond;
        Opacity = 0.96;
        Text = "Compteur de coffres";

        var menu = new ContextMenuStrip();
        menu.Items.Add("Ouvrir le tableau", null, (_, _) => DemandeTableau?.Invoke());
        _menuVerrou = new ToolStripMenuItem("", null, (_, _) => Basculer());
        menu.Items.Add(_menuVerrou);
        menu.Items.Add("Masquer la fenêtre", null, (_, _) => DemandeMasquer?.Invoke());
        menu.Opening += (_, _) => _menuVerrou.Text = _verrouillee ? "Déverrouiller la position" : "Verrouiller la position";
        ContextMenuStrip = menu;
    }

    public bool Verrouillee => _verrouillee;

    // Ne prend jamais le focus au jeu, et n'apparait pas dans la barre des taches ni dans Alt+Tab.
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parametres = base.CreateParams;
            parametres.ExStyle |= 0x00000080 /* WS_EX_TOOLWINDOW */ | 0x08000000 /* WS_EX_NOACTIVATE */;
            return parametres;
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            var coinsArrondis = 2;
            DwmSetWindowAttribute(Handle, 33, ref coinsArrondis, sizeof(int)); // Windows 11 seulement
        }
        catch (Exception) { /* Windows 10 : coins carres */ }
        Ajuster();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        Ajuster();
    }

    public void Afficher(EtatAffiche etat)
    {
        var lignesAvant = _etat == null ? -1 : Lignes(_etat.Resume);
        _etat = etat;
        if (Lignes(etat.Resume) != lignesAvant) Ajuster();
        Invalidate();
    }

    private void Ajuster()
    {
        var echelle = DeviceDpi / 96f;
        Size = new Size(S(Largeur, echelle), Hauteur(_etat, echelle));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        _zoneVerrou = Dessiner(e.Graphics, ClientSize, DeviceDpi / 96f, _etat, _verrouillee);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (_zoneVerrou.Contains(e.Location))
        {
            Basculer();
            return;
        }
        if (!_verrouillee) _prise = e.Location;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = _zoneVerrou.Contains(e.Location) ? Cursors.Hand : _verrouillee ? Cursors.Default : Cursors.SizeAll;
        if (_prise is { } prise && e.Button == MouseButtons.Left)
            Location = new Point(Location.X + e.X - prise.X, Location.Y + e.Y - prise.Y);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_prise == null) return;
        _prise = null;
        PositionChangee?.Invoke();
    }

    private void Basculer()
    {
        _verrouillee = !_verrouillee;
        Invalidate();
        VerrouChange?.Invoke();
    }

    /// <summary>Place la fenetre a l'endroit enregistre, ou a gauche de l'ecran si cet endroit n'est plus visible.</summary>
    public void Placer(int? x, int? y)
    {
        Ajuster();
        if (x is { } px && y is { } py && Screen.AllScreens.Any(ecran => ecran.WorkingArea.Contains(new Rectangle(px, py, Width / 2, Height / 2))))
        {
            Location = new Point(px, py);
            return;
        }
        var zone = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        Location = new Point(zone.Left + S(16, DeviceDpi / 96f), zone.Top + (zone.Height - Height) / 2);
    }

    // ---- Dessin (aussi utilise pour l'apercu en image) ----

    private static int S(float valeur, float echelle) => (int)Math.Round(valeur * echelle);

    private static int Lignes(Resume? resume) => 3 + (resume?.Quete != null ? 1 : 0) + (resume?.TotalEquipe != null ? 1 : 0);

    /// <summary>Un pourcentage lisible meme quand il est minuscule : 0,018 %, 1,3 %, 42 %.</summary>
    public static string Pourcentage(double chance)
    {
        var pc = chance * 100;
        var format = pc >= 10 ? "0" : pc >= 1 ? "0.0" : pc >= 0.1 ? "0.00" : "0.000";
        return pc.ToString(format, Fr) + " %";
    }

    private static int Hauteur(EtatAffiche? etat, float echelle) => S(14 + 22 + 60 + 10 + Lignes(etat?.Resume) * 21 + 4 + 20 + 12, echelle);

    public static string Nombre(int valeur) => valeur.ToString("N0", Fr).Replace(' ', ' ');

    public static Bitmap Rendre(EtatAffiche etat, float echelle, bool verrouillee)
    {
        var image = new Bitmap(S(Largeur, echelle), Hauteur(etat, echelle));
        using var g = Graphics.FromImage(image);
        Dessiner(g, image.Size, echelle, etat, verrouillee);
        return image;
    }

    /// <summary>Dessine la fenetre et renvoie la zone cliquable du cadenas.</summary>
    private static Rectangle Dessiner(Graphics g, Size taille, float s, EtatAffiche? etat, bool verrouillee)
    {
        g.Clear(Fond);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using var bord = new Pen(Bord, Math.Max(1, s));
        g.DrawRectangle(bord, 0, 0, taille.Width - 1, taille.Height - 1);

        using var titre = new Font("Segoe UI Semibold", 11 * s, GraphicsUnit.Pixel);
        using var gros = new Font("Segoe UI Semibold", 34 * s, GraphicsUnit.Pixel);
        using var petit = new Font("Segoe UI", 12 * s, GraphicsUnit.Pixel);
        using var ligne = new Font("Segoe UI", 13 * s, GraphicsUnit.Pixel);
        using var ligneForte = new Font("Segoe UI Semibold", 13 * s, GraphicsUnit.Pixel);
        using var icones = new Font("Segoe MDL2 Assets", 12 * s, GraphicsUnit.Pixel);
        using var trait = new Pen(Trait, Math.Max(1, s));
        const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

        var marge = S(14, s);
        var y = marge;
        TextRenderer.DrawText(g, "C O F F R E S", titre, new Point(marge, y), Etiquette, Flags);
        var cadenas = verrouillee ? "" : "";
        var tailleCadenas = TextRenderer.MeasureText(g, cadenas, icones, Size.Empty, Flags);
        var zoneVerrou = new Rectangle(taille.Width - marge - tailleCadenas.Width - S(4, s), y - S(4, s), tailleCadenas.Width + S(8, s), tailleCadenas.Height + S(8, s));
        TextRenderer.DrawText(g, cadenas, icones, new Point(zoneVerrou.X + S(4, s), y), verrouillee ? Texte : Etiquette, Flags);
        y += S(22, s);

        var resume = etat?.Resume;
        var total = resume == null ? "–" : Nombre(resume.Total);
        var derniere = resume?.DernierePartie is { } d ? Nombre(d) : "–";
        var largeurTotal = Math.Max(TextRenderer.MeasureText(g, total, gros, Size.Empty, Flags).Width, TextRenderer.MeasureText(g, "total", petit, Size.Empty, Flags).Width);
        TextRenderer.DrawText(g, total, gros, new Point(marge - S(2, s), y - S(4, s)), Or, Flags);
        TextRenderer.DrawText(g, "total", petit, new Point(marge, y + S(40, s)), Etiquette, Flags);
        var separation = marge + largeurTotal + S(18, s);
        g.DrawLine(trait, separation, y + S(4, s), separation, y + S(54, s));
        var x2 = separation + S(18, s);
        TextRenderer.DrawText(g, derniere, gros, new Point(x2 - S(2, s), y - S(4, s)), Texte, Flags);
        TextRenderer.DrawText(g, "dernière partie", petit, new Point(x2, y + S(40, s)), Etiquette, Flags);
        y += S(60, s);

        g.DrawLine(trait, marge, y, taille.Width - marge, y);
        y += S(10, s);

        var colonne = marge + S(70, s);
        void Ligne(string libelle, string valeur, string suite)
        {
            TextRenderer.DrawText(g, libelle, ligne, new Point(marge, y), Etiquette, Flags);
            TextRenderer.DrawText(g, valeur, ligneForte, new Point(colonne, y), Texte, Flags);
            var decalage = TextRenderer.MeasureText(g, valeur, ligneForte, Size.Empty, Flags).Width;
            var reste = new Rectangle(colonne + decalage, y, taille.Width - marge - colonne - decalage, S(18, s));
            TextRenderer.DrawText(g, suite, ligne, reste, Etiquette, Flags | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
            y += S(21, s);
        }

        if (resume == null || resume.SessionParties == 0)
            Ligne("Session", "–", "  pas de partie récente");
        else
            Ligne("Session", $"{Nombre(resume.SessionCoffres)} coffres",
                $"  {resume.SessionParties} partie{(resume.SessionParties > 1 ? "s" : "")} · moy. {Nombre((int)Math.Round(resume.SessionCoffres / (double)resume.SessionParties))}");
        Ligne("Cassés", resume == null ? "–" : Nombre(resume.Casses),
            resume == null ? "" : $"  {Nombre(resume.CassesAPied)} à pied · {Nombre(resume.CassesVoiture)} en voiture");
        if (resume?.Quete is { } quete)
        {
            if (quete >= Stockage.ObjectifQuete) Ligne("Quête", Nombre(quete), "  terminée ✓");
            else Ligne("Quête", $"{Nombre(quete)} / {Nombre(Stockage.ObjectifQuete)}", $"  encore {Nombre(Stockage.ObjectifQuete - quete)}");
        }
        if (resume?.TotalEquipe is { } equipe)
            Ligne("Escouade", Nombre(equipe), resume.DerniereEquipe is { } de ? $"  dernière partie {Nombre(de)}" : "");
        if (resume?.BoutiqueObtenue == true) Ligne("Boutique", "obtenue ✓", "  accès à la boutique pirate");
        else Ligne("Boutique", resume == null ? "–" : Pourcentage(resume.ChanceBoutique), "  chance cumulée");
        y += S(4, s);

        var couleur = Couleurs[etat?.Voyant ?? Voyant.Gris];
        using (var pinceau = new SolidBrush(couleur))
            g.FillEllipse(pinceau, marge, y + S(5, s), S(8, s), S(8, s));
        var zoneStatut = new Rectangle(marge + S(14, s), y, taille.Width - 2 * marge - S(14, s), S(18, s));
        TextRenderer.DrawText(g, etat?.Statut ?? "Démarrage…", petit, zoneStatut, etat?.Voyant == Voyant.Rouge ? couleur : Etiquette,
            Flags | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
        return zoneVerrou;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr fenetre, int attribut, ref int valeur, int taille);
}

// Fenetre ou le joueur indique ce que sa quete affiche, pour que le compteur s'aligne dessus.
internal static class DialogueQuete
{
    public static int? Demander(int? valeurActuelle)
    {
        using var fenetre = new Form
        {
            Text = "Ma quête de coffres",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = true,
            TopMost = true,
            StartPosition = FormStartPosition.CenterScreen,
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96, 96),
            ClientSize = new Size(360, 158),
            Font = new Font("Segoe UI", 9f),
        };
        var texte = new Label
        {
            Text = "Combien de coffres ta quête « Ouvrir des coffres » affiche-t-elle en ce moment ? Le compteur s'aligne sur ce chiffre (seuls les coffres ouverts font avancer la quête).",
            Location = new Point(14, 12),
            Size = new Size(332, 64),
        };
        var valeur = new NumericUpDown { Minimum = 0, Maximum = 100000, Value = Math.Clamp(valeurActuelle ?? 0, 0, 100000), Location = new Point(14, 84), Width = 120, ThousandsSeparator = true };
        var ok = new Button { Text = "Valider", DialogResult = DialogResult.OK, Location = new Point(170, 118), Size = new Size(84, 28) };
        var annuler = new Button { Text = "Annuler", DialogResult = DialogResult.Cancel, Location = new Point(262, 118), Size = new Size(84, 28) };
        fenetre.Controls.AddRange(new Control[] { texte, valeur, ok, annuler });
        fenetre.AcceptButton = ok;
        fenetre.CancelButton = annuler;
        fenetre.Shown += (_, _) => { valeur.Select(); valeur.Select(0, valeur.Text.Length); };
        return fenetre.ShowDialog() == DialogResult.OK ? (int)valeur.Value : null;
    }
}

// Coffres ouverts avant d'utiliser le compteur : saisis a la main, ou estimes depuis l'onglet Carriere de Fortnite.
internal static class DialogueAvant
{
    public const int MinParties = 10;

    public static int? Demander(int actuel, int partiesComptees, double moyenne)
    {
        using var fenetre = new Form
        {
            Text = "Mes coffres d'avant le compteur",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            ShowInTaskbar = true,
            TopMost = true,
            StartPosition = FormStartPosition.CenterScreen,
            AutoScaleMode = AutoScaleMode.Dpi,
            AutoScaleDimensions = new SizeF(96, 96),
            ClientSize = new Size(400, 262),
            Font = new Font("Segoe UI", 9f),
        };
        var texte = new Label
        {
            Text = "Tu as ouvert des coffres avant d'utiliser le compteur ? Indique combien : ils s'ajoutent à ton total et à tes chances.",
            Location = new Point(14, 12),
            Size = new Size(372, 36),
        };
        var coffres = new NumericUpDown { Minimum = 0, Maximum = 1_000_000, Value = Math.Clamp(actuel, 0, 1_000_000), Location = new Point(14, 54), Width = 130, ThousandsSeparator = true };
        var aide = new Label
        {
            Text = $"Pour l'estimer : dans Fortnite, onglet Carrière, regarde ton nombre de parties de cette saison. Le compteur fait (parties − {partiesComptees} déjà comptées) × ta moyenne ({moyenne:0.0} coffres par partie).",
            Location = new Point(14, 92),
            Size = new Size(372, 50),
        };
        var parties = new NumericUpDown { Minimum = 0, Maximum = 100_000, Value = 0, Location = new Point(14, 148), Width = 130, ThousandsSeparator = true };
        var estimer = new Button { Text = "Estimer", Location = new Point(154, 146), Size = new Size(90, 28), Enabled = partiesComptees >= MinParties };
        var resultat = new Label
        {
            Text = partiesComptees >= MinParties ? "" : $"Il faut au moins {MinParties} parties comptées pour estimer.",
            Location = new Point(14, 182),
            Size = new Size(372, 34),
            ForeColor = SystemColors.GrayText,
        };
        estimer.Click += (_, _) =>
        {
            var manquantes = (int)parties.Value - partiesComptees;
            if (manquantes <= 0)
            {
                resultat.Text = "Toutes tes parties sont déjà comptées : rien à ajouter.";
                coffres.Value = 0;
                return;
            }
            var estimation = (int)Math.Round(manquantes * moyenne);
            coffres.Value = Math.Clamp(estimation, 0, 1_000_000);
            resultat.Text = $"{manquantes} parties d'avant × {moyenne:0.0} = environ {FenetreCompteur.Nombre(estimation)} coffres.";
        };
        var ok = new Button { Text = "Valider", DialogResult = DialogResult.OK, Location = new Point(210, 224), Size = new Size(84, 28) };
        var annuler = new Button { Text = "Annuler", DialogResult = DialogResult.Cancel, Location = new Point(302, 224), Size = new Size(84, 28) };
        fenetre.Controls.AddRange(new Control[] { texte, coffres, aide, parties, estimer, resultat, ok, annuler });
        fenetre.AcceptButton = ok;
        fenetre.CancelButton = annuler;
        return fenetre.ShowDialog() == DialogResult.OK ? (int)coffres.Value : null;
    }
}
