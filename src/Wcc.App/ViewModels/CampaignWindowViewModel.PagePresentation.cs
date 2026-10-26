using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Media;
using Wcc.App.ViewModels.Wcc;
using Wcc.Core.Campaigns;
using Wcc.Core.Library;
using Wcc.Core.Projects;
using Wcc.Core.Scenarios;
using Wcc.Core.Wml;

namespace Wcc.App.ViewModels;

public sealed class CampaignChapterCard : ViewModelBase
{
    private readonly CampaignWindowViewModel _owner;
    internal CampaignChapterCard(CampaignWindowViewModel owner, WccCampaignEntry entry) { _owner = owner; Entry = entry; }
    internal WccCampaignEntry Entry { get; }
    public string Name => Entry.Name;
    public string Id => Entry.CampaignId ?? "Unresolved campaign ID";
    public WccFieldImagePreviewVm Icon => _owner.CampaignChapterIcon(Entry);
    internal void RefreshIcon() => OnPropertyChanged(nameof(Icon));
}

public sealed class CampaignDependencyCard : ViewModelBase
{
    private readonly CampaignWindowViewModel _owner;
    internal CampaignDependencyCard(CampaignWindowViewModel owner, string id, string name, string information,
        bool resolved, CampaignManagedDependencyRow? managed, CampaignAdditionalDependencySourceRow? manual,
        string? icon)
    { _owner = owner; Id = id; Name = name; Information = information; IsUnresolved = !resolved; Managed = managed; Manual = manual; IconExpression = icon; }
    public string Id { get; }
    public string Name { get; }
    public string Information { get; }
    public bool IsUnresolved { get; }
    internal CampaignManagedDependencyRow? Managed { get; }
    internal CampaignAdditionalDependencySourceRow? Manual { get; private set; }
    internal string? IconExpression { get; }
    internal WccPackageRevisionId? IconProviderRevision { get; init; }
    internal bool UsesCoreIcon { get; init; }
    internal string? IconProviderAddonId { get; init; }
    public WccFieldImagePreviewVm Icon => _owner.CampaignDependencyIcon(this);
    public bool HasManagedRecipe => Managed is not null;
    public bool HasManualSource => Manual is not null;
    public bool CanEdit => Managed?.CanEdit ?? _owner.CanEditCampaignMainRaw;
    public bool CanRemove => Managed?.CanRemove == true;
    public bool CanForget => Manual?.CanForget == true;
    public string EditTooltip => Managed?.EditTooltip ?? "Edit this dependency loading in the campaign's RAW WML.";
    public string RemoveTooltip => Managed?.RemoveTooltip ?? "Unmarked dependency loading remains available in RAW WML.";

    internal bool HasSamePresentation(CampaignDependencyCard other) => Id == other.Id && Name == other.Name &&
        Information == other.Information && IsUnresolved == other.IsUnresolved && ReferenceEquals(Managed, other.Managed) &&
        Manual?.SourcePath == other.Manual?.SourcePath && IconExpression == other.IconExpression &&
        IconProviderRevision == other.IconProviderRevision && IconProviderAddonId == other.IconProviderAddonId && UsesCoreIcon == other.UsesCoreIcon;

    internal void UpdateManualSource(CampaignDependencyCard other) => Manual = other.Manual;
    internal void RefreshAvailability()
    {
        foreach (var property in new[] { nameof(CanEdit), nameof(CanRemove), nameof(CanForget), nameof(EditTooltip), nameof(RemoveTooltip) })
            OnPropertyChanged(property);
    }
}

public partial class CampaignWindowViewModel
{
    private WccCampaignFieldEditorVm[] _pageFieldRows = [];
    private IReadOnlyList<WccCampaignFieldEditorVm> _generalPageFields = [];
    private IReadOnlyList<WccCampaignFieldEditorVm> _menuImageFields = [];
    private IReadOnlyList<CampaignFieldGroupVm> _otherLeftPageGroups = [], _otherRightPageGroups = [];
    private WccCampaignFieldEditorVm? _menuDescriptionField, _menuAlignmentField, _menuIconField, _menuImageField, _menuBackgroundField;
    private IReadOnlyList<CampaignChapterCard> _chapterCards = [];
    private WccCampaignEntry[] _chapterCardEntries = [];
    private string _pathAddonFilter = "All add-ons", _pathPurposeFilter = "All purposes";
    private IReadOnlyList<CampaignPathDisplayRow> _filteredPathRows = [];
    private IReadOnlyList<string> _pathAddonFilters = [], _pathPurposeFilters = [];
    private IReadOnlyList<CampaignDependencyCard> _dependencyCards = [];
    private object? _dependencyCardCatalog;
    private long _dependencyCardGeneration = -1;

    public IReadOnlyList<WccCampaignFieldEditorVm> CampaignGeneralPageFields { get { EnsureCampaignPageFields(); return _generalPageFields; } }
    public IReadOnlyList<WccCampaignFieldEditorVm> CampaignMenuImageFields { get { EnsureCampaignPageFields(); return _menuImageFields; } }
    public WccCampaignFieldEditorVm? CampaignMenuDescriptionField { get { EnsureCampaignPageFields(); return _menuDescriptionField; } }
    public WccCampaignFieldEditorVm? CampaignMenuAlignmentField { get { EnsureCampaignPageFields(); return _menuAlignmentField; } }
    public IReadOnlyList<CampaignFieldGroupVm> CampaignOtherLeftPageGroups { get { EnsureCampaignPageFields(); return _otherLeftPageGroups; } }
    public IReadOnlyList<CampaignFieldGroupVm> CampaignOtherRightPageGroups { get { EnsureCampaignPageFields(); return _otherRightPageGroups; } }
    public string CampaignMenuPreviewDescription => CampaignMenuPreviewText(_menuDescriptionField) ?? "";
    public TextAlignment CampaignMenuPreviewAlignment => (CampaignMenuPreviewText(_menuAlignmentField) ?? "left") switch
    { "center" => TextAlignment.Center, "right" => TextAlignment.Right, _ => TextAlignment.Left };
    public WccFieldImagePreviewVm CampaignMenuIconPreview => _menuIconField?.ImagePreview ?? _unavailableCampaignImagePreview;
    public WccFieldImagePreviewVm CampaignMenuImagePreview => _menuImageField?.ImagePreview ?? _unavailableCampaignImagePreview;
    // Wesnoth's campaign_menu_bg widget uses this Core art for an absent or
    // blank background. It remains a display default, never a scalar assignment.
    private const string CampaignMenuDefaultBackground = "data/core/images/story/landscape-battlefield_nohumans.webp";
    public WccFieldImagePreviewVm CampaignMenuBackgroundPreview
    {
        get
        {
            EnsureCampaignPageFields();
            return _menuBackgroundField?.UsesDefaultMenuBackground == true && _campaignMenuOwner is { } owner
                ? CurrentCampaignImagePreviews()?.Get(owner, "menu_default_background", key => key == "menu_default_background"
                    ? CampaignMenuDefaultBackground : null) ?? _unavailableCampaignImagePreview
                : _menuBackgroundField?.ImagePreview ?? _unavailableCampaignImagePreview;
        }
    }
    public string CampaignMenuBackgroundPreviewHint => _menuBackgroundField?.UsesDefaultMenuBackground == true
        ? "Default Core background: story/landscape-battlefield_nohumans.webp. Preview only; no value is written."
        : "Campaign background across both menu columns. This is an approximate preview.";

    private string? CampaignMenuPreviewText(WccCampaignFieldEditorVm? field)
    {
        if (field is null) return null;
        // Unchanged macro-backed fields use their captured scalar result. Only
        // local literal typing is previewed before the ordinary draft commit.
        if (TryGetCampaignTextInput(field, false, out var text))
        {
            if (WccScenarioFieldValueCodec.InferInputKind(text) != WccScenarioFieldValueInputKind.Literal) return null;
            return field.PreviewTextInput(text);
        }
        return field.PreviewExpression;
    }
    private void EnsureCampaignPageFields()
    {
        lock (_campaignFieldGate)
        {
            if (_pageFieldRows.SequenceEqual(_campaignFields)) return;
            _pageFieldRows = _campaignFields.ToArray();
            _generalPageFields = Array.AsReadOnly(_pageFieldRows.Where(field => field.Definition.Group == "Identity").ToArray());
            _menuImageFields = Array.AsReadOnly(new[] { "image", "background", "icon" }.Select(key => _pageFieldRows.FirstOrDefault(field => field.Key == key)).OfType<WccCampaignFieldEditorVm>().ToArray());
            _menuDescriptionField = _pageFieldRows.FirstOrDefault(field => field.Key == "description");
            _menuAlignmentField = _pageFieldRows.FirstOrDefault(field => field.Key == "description_alignment");
            _menuIconField = _pageFieldRows.FirstOrDefault(field => field.Key == "icon");
            _menuImageField = _pageFieldRows.FirstOrDefault(field => field.Key == "image");
            _menuBackgroundField = _pageFieldRows.FirstOrDefault(field => field.Key == "background");
            _otherLeftPageGroups = Array.AsReadOnly(CampaignDisplayFieldGroups.Where(group => group.Label is "Rules" or "Dates")
                .OrderBy(group => group.Label == "Rules" ? 0 : 1).ToArray());
            _otherRightPageGroups = Array.AsReadOnly(CampaignDisplayFieldGroups.Where(group => group.Label is not ("Identity" or "Presentation" or "Rules" or "Dates")).ToArray());
            }
    }
    // Called by the text buffer as well as ordinary draft publication. It reads no
    // routes or source projections and only invalidates the affected preview binding.
    internal void RefreshCampaignMenuPreview(WccCampaignFieldEditorVm field)
    {
        switch (field.Key)
        {
            case "description": OnPropertyChanged(nameof(CampaignMenuPreviewDescription)); break;
            case "description_alignment": OnPropertyChanged(nameof(CampaignMenuPreviewAlignment)); break;
            case "icon": OnPropertyChanged(nameof(CampaignMenuIconPreview)); SelectedCampaignChapterCard?.RefreshIcon(); break;
            case "image": OnPropertyChanged(nameof(CampaignMenuImagePreview)); break;
            case "background": OnPropertyChanged(nameof(CampaignMenuBackgroundPreview)); OnPropertyChanged(nameof(CampaignMenuBackgroundPreviewHint)); break;
        }
    }
    private void RaiseCampaignPagePresentationChanged()
    {
        EnsureCampaignPageFields();
        foreach (var card in CampaignChapterCards) card.RefreshIcon();
        foreach (var card in CampaignDependencyCards) card.RefreshAvailability();
        if (!CampaignPathAddonFilters.Contains(_pathAddonFilter)) CampaignPathAddonFilter = "All add-ons";
        if (!CampaignPathPurposeFilters.Contains(_pathPurposeFilter)) CampaignPathPurposeFilter = "All purposes";
        foreach (var property in new[] { nameof(CampaignGeneralPageFields), nameof(CampaignMenuImageFields), nameof(CampaignMenuDescriptionField),
            nameof(CampaignMenuAlignmentField), nameof(CampaignOtherLeftPageGroups), nameof(CampaignOtherRightPageGroups),
            nameof(CampaignMenuPreviewDescription), nameof(CampaignMenuPreviewAlignment), nameof(CampaignMenuIconPreview),
            nameof(CampaignMenuImagePreview), nameof(CampaignMenuBackgroundPreview), nameof(CampaignMenuBackgroundPreviewHint), nameof(CampaignChapterCards),
            nameof(SelectedCampaignChapterCard), nameof(CampaignPathAddonFilters), nameof(CampaignPathPurposeFilters), nameof(CampaignFilteredPathRows), nameof(CampaignDependencyCards) }) OnPropertyChanged(property);
    }
    public IReadOnlyList<CampaignChapterCard> CampaignChapterCards
    {
        get
        {
            var entries = CampaignEntries.ToArray();
            if (!_chapterCardEntries.SequenceEqual(entries))
            { _chapterCardEntries = entries; _chapterCards = Array.AsReadOnly(entries.Select(entry => new CampaignChapterCard(this, entry)).ToArray()); }
            return _chapterCards;
        }
    }
    public CampaignChapterCard? SelectedCampaignChapterCard
    {
        get => CampaignChapterCards.FirstOrDefault(card => ReferenceEquals(card.Entry, SelectedCampaignEntry));
        set { if (value is not null && CampaignChapterCards.Contains(value)) SelectedCampaignEntry = value.Entry; }
    }
    internal WccFieldImagePreviewVm CampaignChapterIcon(WccCampaignEntry entry)
    {
        if (!CampaignEntries.Any(current => ReferenceEquals(current, entry))) return _unavailableCampaignImagePreview;
        if (ReferenceEquals(entry, SelectedCampaignEntry) && _menuIconField is { } field) return field.ImagePreview;
        return CurrentCampaignImagePreviews()?.Get(entry.Owner, "chapter_icon", key => key == "chapter_icon" ? Literal(entry.Owner, "icon") : null) ?? _unavailableCampaignImagePreview;
    }
    private static string? Literal(WmlStaticScalarOwner owner, string key)
    {
        var field = owner.ResolveField(key);
        return field.State == WmlStaticScalarResolutionState.Known && CampaignInitialFieldCatalog.TryGet(key, out var definition) &&
            WccScenarioFieldValueCodec.IsSupportedResolvedValue(definition, field.ValueText)
            ? WccScenarioFieldValueCodec.DecodeLiteral(field.ValueText) : null;
    }
    internal string CampaignPathAddon(CampaignPathDisplayRow row)
    {
        var path = row.Path.Replace('\\', '/');
        var marker = path.IndexOf("add-ons/", StringComparison.Ordinal);
        if (marker >= 0) return path[(marker + 8)..].Split('/')[0];
        if (path.StartsWith("data/core", StringComparison.Ordinal) || path.StartsWith("core/", StringComparison.Ordinal)) return "Core";
        if (path.StartsWith("data/campaigns/", StringComparison.Ordinal)) return path[15..].Split('/')[0];
        if (row.SourceLocation?.SourcePath is { } source && _campaignFieldConfiguration?.Snapshot is { } snapshot)
        {
            var root = snapshot.PackagesById.Values.Where(package => package.SourceLocation is { } location &&
                source.StartsWith(Path.TrimEndingDirectorySeparator(location) + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                .OrderByDescending(package => package.SourceLocation!.Length).FirstOrDefault();
            if (root is not null && !IsPathInsideCampaignRoot(source)) return root.SourceIdentifier ?? root.Name;
        }
        return CampaignPackageAuthoringMetadata?.AddonId ?? Path.GetFileName(Path.GetDirectoryName(LoadedPath ?? "")) ?? "Project";
    }
    public IReadOnlyList<string> CampaignPathAddonFilters => StablePathFilters(ref _pathAddonFilters, "All add-ons", CampaignPathPresentationRows.Select(row => row.Addon));
    public IReadOnlyList<string> CampaignPathPurposeFilters => StablePathFilters(ref _pathPurposeFilters, "All purposes", CampaignPathPresentationRows.Select(row => row.Purpose));
    private static IReadOnlyList<string> StablePathFilters(ref IReadOnlyList<string> cached, string all, IEnumerable<string> values)
    {
        var options = new[] { all }.Concat(values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)).ToArray();
        if (!cached.SequenceEqual(options)) cached = Array.AsReadOnly(options);
        return cached;
    }
    public string CampaignPathAddonFilter { get => _pathAddonFilter; set { if (SetField(ref _pathAddonFilter, value ?? "All add-ons")) OnPropertyChanged(nameof(CampaignFilteredPathRows)); } }
    public string CampaignPathPurposeFilter { get => _pathPurposeFilter; set { if (SetField(ref _pathPurposeFilter, value ?? "All purposes")) OnPropertyChanged(nameof(CampaignFilteredPathRows)); } }
    public IReadOnlyList<CampaignPathDisplayRow> CampaignFilteredPathRows
    {
        get
        {
            var rows = CampaignPathPresentationRows.Where(row =>
                (_pathAddonFilter == "All add-ons" || row.Addon == _pathAddonFilter) &&
                (_pathPurposeFilter == "All purposes" || row.Purpose == _pathPurposeFilter)).ToArray();
            // Focus exit can stage a draft during a pointer press. Preserve the
            // bound collection when membership is unchanged so the pressed button
            // is still present when the matching pointer release arrives.
            if (!_filteredPathRows.SequenceEqual(rows)) _filteredPathRows = Array.AsReadOnly(rows);
            return _filteredPathRows;
        }
    }

    public string CampaignDependencyOverviewInformation
    {
        get
        {
            var sections = new List<string>
            {
                CampaignDependencyMetadataSummary, CampaignDependenciesGuidance,
                CampaignEditorSourcesGuidance, CampaignDependencyResolutionSummary
            };
            var provided = CampaignProjectProvidedDependencyRows;
            if (provided.Count != 0)
                sections.Add("Requirements provided by this project" + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine,
                    provided.Select(row => string.Join(Environment.NewLine,
                        new[] { row.Id, row.Status, row.RequiredBy, row.LogicalMount, row.SourcePath, row.Detail }
                            .Where(value => !string.IsNullOrWhiteSpace(value))))));
            var contained = CampaignContainedSourceRows;
            if (contained.Count != 0)
                sections.Add("Project-contained sources" + Environment.NewLine + string.Join(Environment.NewLine + Environment.NewLine,
                    contained.GroupBy(row => (row.OwningAddonId, row.RootPath, row.ExternalAddonId, row.ExternalPath))
                        .Select(group => string.Join(Environment.NewLine, new[]
                        {
                            group.Key.OwningAddonId + " / " + group.First().Name,
                            group.Key.RootPath, group.First().LogicalMount,
                            "Captured scope: " + string.Join("; ", group.Select(row => row.Scope).Distinct(StringComparer.Ordinal)),
                            "External alternative retained in RAW: " + group.Key.ExternalAddonId + " / " + group.Key.ExternalPath,
                            "This source was reached inside the project in the captured scope. Other branches, references and runtime loading keep their own diagnostics."
                        }))));
            return string.Join(Environment.NewLine + Environment.NewLine, sections.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal));
        }
    }
    public IReadOnlyList<CampaignDependencyCard> CampaignDependencyCards
    {
        get
        {
            var cards = new List<CampaignDependencyCard>();
            var managed = ManagedCampaignDependencies;
            var declarations = CampaignDeclaredDependencyRows;
            var sources = CampaignEditorDependencyRows;
            var manual = CampaignAdditionalDependencySourceRows;
            var snapshot = _campaignFieldConfiguration?.Snapshot;
            CampaignEditorDependencyRow? FindSource(string id, string? path)
            {
                var matches = sources.Where(row => path is not null && SameDependencyMetadataRoot(row.SourcePath, path) ||
                    snapshot is not null && Guid.TryParse(row.Revision, out var key) && snapshot.RevisionsById.TryGetValue(new(key), out var revision) &&
                    snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) && (package.SourceIdentifier ?? revision.AddonMetadata?.AddonId) == id).Take(2).ToArray();
                return matches.Length == 1 ? matches[0] : null;
            }
            var usedSources = new HashSet<string>(StringComparer.Ordinal);
            var usedManual = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            void Add(string id, CampaignManagedDependencyRow? recipe, CampaignDeclaredDependencyRow? declaration, CampaignEditorDependencyRow? source, CampaignAdditionalDependencySourceRow? explicitFolder)
            {
                var path = declaration?.SourcePath ?? source?.SourcePath ?? explicitFolder?.SourcePath;
                if (source is not null) usedSources.Add(source.Revision);
                if (explicitFolder is not null) usedManual.Add(explicitFolder.SourcePath);
                var manualAvailable = explicitFolder is not null && snapshot?.PackagesById.Values.Any(package =>
                    package.IsAvailable && SameDependencyMetadataRoot(package.SourceLocation, explicitFolder.SourcePath)) == true;
                var available = source?.Status is "Indexed source available" or "Dependency source available" || manualAvailable ||
                    declaration?.Status is "Dependency source available" or "Editor source available" ||
                    declaration?.HasCapturedContainedSource == true;
                var information = string.Join(Environment.NewLine, new[] { id, recipe?.Summary, declaration?.RequiredBy, declaration?.Status,
                    declaration?.LogicalMount, declaration?.Detail, source?.Kind, source?.Status, source?.Detail, source?.Revision, path,
                    explicitFolder is null ? null : "Additional source for this campaign session. Forgetting it does not change campaign WML or delete files.",
                    recipe is null ? "Existing loading remains in RAW WML." : "WCC managed loading recipe. Runtime activation follows its ordered steps." }.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal));
                WccPackageRecord[] providers = path is null ? [] : _campaignFieldConfiguration?.Snapshot?.PackagesById.Values.Where(package => package.IsAvailable &&
                    package.SourceKind == WccPackageSourceKinds.WesnothAddon && SameDependencyMetadataRoot(package.SourceLocation, path)).Take(2).ToArray() ?? [];
                var provider = providers.Length == 1 ? providers[0] : null;
                // The row's pinned revision is the provider, even when a newer
                // head or another add-on with the same public ID is indexed.
                WccPackageRevisionRecord? iconRevision = null;
                WccPackageRecord? iconPackage = null;
                if (source is not null && snapshot is not null && Guid.TryParse(source.Revision, out var exact) &&
                    snapshot.RevisionsById.TryGetValue(new(exact), out var pinned) &&
                    snapshot.PackagesById.TryGetValue(pinned.PackageId, out var pinnedPackage) && pinnedPackage.IsAvailable &&
                    path is not null && SameDependencyMetadataRoot(pinnedPackage.SourceLocation, path))
                { iconRevision = pinned; iconPackage = pinnedPackage; }
                else if (provider?.CurrentRevisionId is { } revision && snapshot!.RevisionsById.TryGetValue(revision, out var record))
                { iconRevision = record; iconPackage = provider; }
                var metadata = iconRevision?.AddonMetadata;
                string? icon = null;
                if (metadata is not null && metadata.RawFields.TryGetValue("icon", out var rawIcon) &&
                    CampaignInitialFieldCatalog.TryGet("icon", out var iconDefinition) &&
                    WccScenarioFieldValueCodec.IsSupportedResolvedValue(iconDefinition, rawIcon))
                icon = WccScenarioFieldValueCodec.DecodeLiteral(rawIcon);
                cards.Add(new(this, id, metadata?.Title ?? source?.Label ?? id, information, available, recipe, explicitFolder, icon)
                {
                    IconProviderRevision = iconRevision?.Id,
                    IconProviderAddonId = iconPackage?.SourceIdentifier ?? metadata?.AddonId,
                    UsesCoreIcon = UsesWesnothDistributionIcon(iconPackage, snapshot)
                });
            }
            foreach (var recipe in managed)
            {
                var declared = declarations.FirstOrDefault(row => row.Id == recipe.AddonId);
                var source = FindSource(recipe.AddonId, declared?.SourcePath);
                var folder = manual.FirstOrDefault(row => (declared?.SourcePath ?? source?.SourcePath) is { } path && SameDependencyMetadataRoot(row.SourcePath, path));
                if (source is null && FindDependencyProvider(recipe.AddonId, null) is { } provider &&
                    (_campaignDependencyBatch?.Pins.Any(pin => pin.AddonId == recipe.AddonId && pin.RevisionId == provider.Revision.Id) == true ||
                     sources.Any(row => row.Revision == provider.Revision.Id.ToString())))
                {
                    source = sources.FirstOrDefault(row => row.Revision == provider.Revision.Id.ToString()) ?? new(provider.Package.Name,
                        "Pending dependency provider", provider.Package.SourceLocation, "Indexed source available", provider.Revision.Id.ToString(), true);
                    folder = manual.FirstOrDefault(row => SameDependencyMetadataRoot(row.SourcePath, provider.Package.SourceLocation!));
                }
                Add(recipe.AddonId, recipe, declared, source, folder);
            }
            foreach (var declaration in declarations.Where(row => managed.All(recipe => recipe.AddonId != row.Id)))
            {
                var source = FindSource(declaration.Id, declaration.SourcePath);
                var folder = manual.FirstOrDefault(row => (declaration.SourcePath ?? source?.SourcePath) is { } path && SameDependencyMetadataRoot(row.SourcePath, path));
                Add(declaration.Id, null, declaration, source, folder);
            }
            foreach (var source in sources.Where(row => !usedSources.Contains(row.Revision)))
                Add(source.Label, null, null, source, manual.FirstOrDefault(row => source.SourcePath is { } path && SameDependencyMetadataRoot(row.SourcePath, path)));
            foreach (var folder in manual.Where(row => !usedManual.Contains(row.SourcePath))) Add(Path.GetFileName(folder.SourcePath), null, null, null, folder);
            var sameContext = ReferenceEquals(_dependencyCardCatalog, _campaignEntryCatalog) && _dependencyCardGeneration == _campaignFieldGeneration;
            if (sameContext && _dependencyCards.Count == cards.Count &&
                _dependencyCards.Zip(cards).All(pair => pair.First.HasSamePresentation(pair.Second)))
            {
                for (var index = 0; index < cards.Count; index++) _dependencyCards[index].UpdateManualSource(cards[index]);
            }
            else
            {
                _dependencyCards = cards.AsReadOnly();
                _dependencyCardCatalog = _campaignEntryCatalog;
                _dependencyCardGeneration = _campaignFieldGeneration;
            }
            return _dependencyCards;
        }
    }
    private static bool UsesWesnothDistributionIcon(WccPackageRecord? package, WccLibrarySnapshot? snapshot)
    {
        if (package is null || package.SourceKind != WccPackageSourceKinds.WesnothCore &&
            package.SourceKind != WccPackageSourceKinds.WesnothMainline ||
            package.ContentKind != WccPackageContentKinds.Core && package.ContentKind != WccPackageContentKinds.ResourcePack) return false;
        if (package.ParentPackageId is null) return true;
        // These child identities belong to the installed distribution. Mainline
        // campaigns and similarly named add-ons keep their own image provenance.
        return package.SourceKind == WccPackageSourceKinds.WesnothMainline &&
            package.ContentKind == WccPackageContentKinds.ResourcePack && (package.SourceIdentifier is "themes" or "internal" or "ai") &&
            snapshot?.PackagesById.GetValueOrDefault(package.ParentPackageId.Value) is { ParentPackageId: null } parent &&
            UsesWesnothDistributionIcon(parent, snapshot);
    }
    internal WccFieldImagePreviewVm CampaignDependencyIcon(CampaignDependencyCard card) => _campaignMenuOwner is { } owner
        ? CurrentCampaignImagePreviews()?.GetDependencyIcon(owner,
            "dependency_icon:" + card.IconProviderRevision + ":" + card.Id,
            _campaignEntryCatalog?.Capture, card.IconProviderRevision, card.IconProviderAddonId ?? card.Id, card.IconExpression, card.UsesCoreIcon)
            ?? _unavailableCampaignImagePreview
        : _unavailableCampaignImagePreview;

    internal string? CampaignPickedImageReference(string path)
    {
        var full = Path.GetFullPath(path);
        if (_project?.SourcePath is not { } main || _campaignFieldConfiguration?.Snapshot is not { } snapshot || _campaignFieldConfiguration.RootRevision is not { } root) return null;
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        string? Relative(string directory) => full.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)) + Path.DirectorySeparatorChar, comparison)
            ? Path.GetRelativePath(directory, full).Replace('\\', '/') : null;
        if (Relative(Path.Combine(Path.GetDirectoryName(main)!, "images")) is { } projectImage) return projectImage;
        if (_campaignPresetInstallPath is { } install)
        {
            var coreRoot = WccCoreInstallPaths.GetCoreDataRoot(install);
            var dataRoot = Path.GetDirectoryName(coreRoot)!;
            if (Relative(Path.Combine(coreRoot, "images")) is { } core) return core;
            if (Relative(Path.Combine(Path.GetDirectoryName(dataRoot)!, "images")) is { } ui) return ui;
            if (Relative(dataRoot) is { } data) return "data/" + data;
        }
        var visible = WccLibraryRevisionVisibility.GetVisibleRevisionIds(snapshot, root);
        foreach (var revision in visible.Select(id => snapshot.RevisionsById.GetValueOrDefault(id)).OfType<WccPackageRevisionRecord>())
            if (snapshot.PackagesById.TryGetValue(revision.PackageId, out var package) && package.SourceKind == WccPackageSourceKinds.WesnothAddon &&
                package.IsAvailable && package.SourceLocation is { } location && Relative(Path.Combine(location, "images")) is { } image)
                return "data/add-ons/" + (package.SourceIdentifier ?? revision.AddonMetadata?.AddonId ?? Path.GetFileName(location)) + "/images/" + image;
        return null;
    }
}
