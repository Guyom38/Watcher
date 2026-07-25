using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Watcher.Core;

/// <summary>
/// Noeud de l'arborescence de selection. Les enfants ne sont enumeres qu'au premier
/// deploiement : parcourir un disque entier a l'ouverture serait inutilisable.
/// L'etat de la case est tri-etat : coche, decoche, ou indetermine si la selection
/// des descendants est partielle.
/// </summary>
public sealed class PathNode : INotifyPropertyChanged
{
    /// <summary>Marqueur d'enfants non encore charges : donne la fleche de deploiement au noeud.</summary>
    private static readonly PathNode Placeholder = new("", "", false);

    private bool _childrenLoaded;

    public PathNode(string fullPath, string display, bool isDirectory, PathNode? parent = null)
    {
        FullPath = fullPath;
        Display = display;
        IsDirectory = isDirectory;
        Parent = parent;
        Children = new ObservableCollection<PathNode>();

        if (isDirectory) Children.Add(Placeholder);
    }

    public string FullPath { get; }
    public string Display { get; }
    public bool IsDirectory { get; }
    public PathNode? Parent { get; }
    public ObservableCollection<PathNode> Children { get; }

    public bool IsPlaceholder => FullPath.Length == 0;

    private string? _detail;
    public string? Detail { get => _detail; set => Set(ref _detail, value); }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!Set(ref _isExpanded, value)) return;
            if (value) LoadChildren();
        }
    }

    private bool? _isChecked = false;
    public bool? IsChecked
    {
        get => _isChecked;
        set => SetChecked(value, fromUser: true);
    }

    /// <summary>
    /// Propage la coche vers le bas (tous les descendants deja materialises) et
    /// recalcule l'etat des ancetres vers le haut.
    /// </summary>
    private void SetChecked(bool? value, bool fromUser, bool propagateDown = true)
    {
        if (_isChecked == value && !fromUser) return;

        _isChecked = value;
        OnPropertyChanged(nameof(IsChecked));

        if (propagateDown && value.HasValue)
            foreach (var c in Children.Where(c => !c.IsPlaceholder))
                c.SetChecked(value, fromUser: false);

        Parent?.RefreshFromChildren();
    }

    private void RefreshFromChildren()
    {
        var real = Children.Where(c => !c.IsPlaceholder).ToList();
        if (real.Count == 0) return;

        bool? state = real.All(c => c.IsChecked == true) ? true
            : real.All(c => c.IsChecked == false) ? false
            : null;

        if (_isChecked == state) return;

        _isChecked = state;
        OnPropertyChanged(nameof(IsChecked));
        Parent?.RefreshFromChildren();
    }

    public void LoadChildren()
    {
        if (_childrenLoaded || !IsDirectory) return;
        _childrenLoaded = true;
        Children.Clear();

        try
        {
            var dirs = Directory.EnumerateDirectories(FullPath)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

            foreach (var dir in dirs)
            {
                try
                {
                    var info = new DirectoryInfo(dir);
                    // Les points de reparse (jonctions, liens) creeraient des cycles.
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;

                    var node = new PathNode(dir, info.Name, true, this);
                    // Un nouvel enfant herite de l'etat du parent quand celui-ci est tranche.
                    if (_isChecked == true) node.SetChecked(true, false, propagateDown: false);
                    Children.Add(node);
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
        }
        catch (UnauthorizedAccessException)
        {
            Detail = "acces refuse";
        }
        catch (Exception ex)
        {
            Detail = "illisible";
            AppLogger.Debug($"Enumeration de « {FullPath} » impossible : {ex.Message}");
        }
    }

    /// <summary>
    /// Racines cochees, reduites au minimum : un dossier entierement coche represente
    /// tous ses descendants, inutile de les lister un par un.
    /// </summary>
    public void CollectSelectedRoots(List<string> into)
    {
        if (IsPlaceholder) return;

        if (_isChecked == true)
        {
            into.Add(FullPath);
            return;
        }

        if (_isChecked is null)
            foreach (var c in Children)
                c.CollectSelectedRoots(into);
    }

    /// <summary>Recoche l'arborescence a partir d'une liste de chemins enregistree.</summary>
    public void ApplySelection(IReadOnlyCollection<string> selected)
    {
        if (IsPlaceholder) return;

        if (selected.Any(s => Equals(s, FullPath)))
        {
            SetChecked(true, fromUser: false);
            return;
        }

        // Un descendant est-il selectionne ? Si oui, il faut materialiser ce niveau.
        var prefix = FullPath.EndsWith('\\') ? FullPath : FullPath + "\\";
        var hasDescendant = selected.Any(s => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        if (!hasDescendant) return;

        LoadChildren();
        IsExpanded = true;
        foreach (var c in Children) c.ApplySelection(selected);
    }

    private static bool Equals(string a, string b)
        => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
