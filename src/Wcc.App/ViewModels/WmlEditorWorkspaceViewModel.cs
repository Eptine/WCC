using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;

namespace Wcc.App.ViewModels;

/// <summary>
/// A document tab is an inspection or buffer identity, not file-write authority.
/// The root host continues to validate and apply only RootDocument.
/// </summary>
public sealed class WmlEditorTabViewModel
{
    internal WmlEditorTabViewModel(string identity, WmlEditorDocumentViewModel document, bool isRoot)
    {
        Identity = identity;
        Document = document;
        IsRoot = isRoot;
    }

    public string Identity { get; }
    public WmlEditorDocumentViewModel Document { get; }
    public string Title => Document.Title;
    public string? SourceDescription => Document.DescriptionText;
    public bool IsRoot { get; }
    public bool IsReadOnly => Document.IsReadOnly;
    public bool CanClose => !IsRoot;
    public string DisplayTitle => IsReadOnly ? Title + " (Read)" : Title;
}

/// <summary>
/// Independent editor tabs. Source resolution, versioned source identities and
/// host callbacks belong to the caller; navigation never applies any document.
/// </summary>
public sealed class WmlEditorWorkspaceViewModel : ViewModelBase
{
    private readonly ObservableCollection<WmlEditorTabViewModel> _tabs = [];
    private WmlEditorTabViewModel _selectedTab;
    private bool _hasModifiedSourceTabs;

    public WmlEditorWorkspaceViewModel(WmlEditorDocumentViewModel rootDocument, string rootIdentity = "root")
    {
        ArgumentNullException.ThrowIfNull(rootDocument);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootIdentity);
        RootDocument = rootDocument;
        RootTab = new(rootIdentity, rootDocument, true);
        _tabs.Add(RootTab);
        Tabs = new(_tabs);
        _selectedTab = RootTab;
    }

    public WmlEditorDocumentViewModel RootDocument { get; }
    public WmlEditorTabViewModel RootTab { get; }
    public ReadOnlyObservableCollection<WmlEditorTabViewModel> Tabs { get; }
    public bool HasModifiedSourceTabs => _hasModifiedSourceTabs;

    public WmlEditorTabViewModel SelectedTab
    {
        get => _selectedTab;
        set
        {
            // A tab control may transiently clear selection while recycling its
            // items. Never select a removed or another workspace's document.
            if (value is null || !_tabs.Contains(value)) return;
            SetField(ref _selectedTab, value);
        }
    }

    /// <summary>
    /// Reuses the first frozen buffer for this exact identity. The caller must
    /// include a content hash or version when it wants a newer source tab.
    /// These inspection tabs do not prohibit a later host-owned editing route.
    /// </summary>
    public WmlEditorTabViewModel OpenReadOnlySource(string identity, string title, string text,
        string? description = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(text);
        return OpenSourceDocument(identity, new(title, text, isReadOnly: true, descriptionText: description));
    }

    /// <summary>
    /// Opens a host-owned independent source document. Its callback and policy stay on that
    /// document; this workspace never applies or cancels it. The first exact identity wins.
    /// A read-mode workspace cannot acquire a writable source tab through this route.
    /// </summary>
    public WmlEditorTabViewModel OpenSourceDocument(string identity, WmlEditorDocumentViewModel document)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        ArgumentNullException.ThrowIfNull(document);
        if (string.Equals(identity, RootTab.Identity, StringComparison.Ordinal))
            throw new ArgumentException("A source tab cannot reuse the root document identity.", nameof(identity));
        if (ReferenceEquals(document, RootDocument))
            throw new ArgumentException("A source tab must have its own document, separate from the root.", nameof(document));
        if (RootDocument.IsReadOnly && !document.IsReadOnly)
            throw new InvalidOperationException("A read-mode workspace cannot open a writable source document.");
        var tab = _tabs.FirstOrDefault(existing => string.Equals(existing.Identity, identity, StringComparison.Ordinal));
        if (tab is null)
        {
            tab = new(identity, document, false);
            document.PropertyChanged += SourceDocumentChanged;
            _tabs.Add(tab);
            UpdateModifiedSourceTabs();
        }
        // Collection observers can close an inspection tab during publication.
        // Preserve their selection rather than returning to a removed buffer.
        if (_tabs.Contains(tab)) SelectedTab = tab;
        return tab;
    }

    public bool CloseTab(WmlEditorTabViewModel tab)
    {
        ArgumentNullException.ThrowIfNull(tab);
        var index = _tabs.IndexOf(tab);
        if (index <= 0) return false;
        _tabs.RemoveAt(index);
        tab.Document.PropertyChanged -= SourceDocumentChanged;
        UpdateModifiedSourceTabs();
        // A collection callback may have selected another current tab. Keep that
        // choice; otherwise select the nearest remaining predecessor or root.
        if (!_tabs.Contains(_selectedTab))
            SelectedTab = _tabs[Math.Min(index - 1, _tabs.Count - 1)];
        return true;
    }

    private void SourceDocumentChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (string.IsNullOrEmpty(args.PropertyName) || args.PropertyName == nameof(WmlEditorDocumentViewModel.IsModified))
            UpdateModifiedSourceTabs();
    }

    private void UpdateModifiedSourceTabs()
        => SetField(ref _hasModifiedSourceTabs, _tabs.Any(tab => !tab.IsRoot && tab.Document.IsModified),
            nameof(HasModifiedSourceTabs));
}
