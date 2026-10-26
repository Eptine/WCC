using System;
using System.IO;
using System.Threading.Tasks;

namespace Wcc.App.ViewModels;

public enum WmlEditorClosePolicy { ApplyOnClose, ExplicitApply }

/// <summary>
/// One independent WML buffer. Its host owns source identity, validation and
/// staging or persistence; this model never reads or writes a file itself.
/// </summary>
public sealed class WmlEditorDocumentViewModel : ViewModelBase
{
    private readonly Action<string>? _onApply;
    private readonly Func<string, string?>? _validate;
    private string _text;
    private string _acceptedText;
    private string? _validationMessage;
    private long _version;
    private long _lastAppliedVersion = -1;
    private bool _isApplying;
    private bool _isCancelled;

    public WmlEditorDocumentViewModel(string title, string initialText,
        Action<string>? onApply = null, bool isReadOnly = false,
        string? descriptionText = null, Func<string, string?>? validate = null,
        WmlEditorClosePolicy closePolicy = WmlEditorClosePolicy.ExplicitApply)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(initialText);
        if (!Enum.IsDefined(closePolicy)) throw new ArgumentOutOfRangeException(nameof(closePolicy));
        Title = title;
        _text = _acceptedText = initialText;
        _onApply = onApply;
        _validate = validate;
        IsReadOnly = isReadOnly;
        DescriptionText = descriptionText;
        ClosePolicy = closePolicy;
    }

    public string Title { get; }
    public bool IsReadOnly { get; }
    public string? DescriptionText { get; }
    public WmlEditorClosePolicy ClosePolicy { get; }
    public bool IsApplying => _isApplying;
    public bool IsCancelled => _isCancelled;
    public bool IsModified => !string.Equals(_text, _acceptedText, StringComparison.Ordinal);
    public string? ValidationMessage => _validationMessage;
    // Legacy close callbacks also apply an unchanged initial buffer. A prior
    // validation error must permit an explicit retry after host state changes.
    public bool CanApply => !IsReadOnly && !_isCancelled && !_isApplying && _onApply is not null;

    public string Text
    {
        get => _text;
        set
        {
            if (IsReadOnly || _isCancelled) return;
            value ??= string.Empty;
            if (string.Equals(_text, value, StringComparison.Ordinal)) return;
            _text = value;
            _version++;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsModified));
        }
    }

    public bool TryApply()
    {
        if (!CanApply) return false;
        var version = _version;
        var text = _text;
        var succeeded = false;
        _lastAppliedVersion = -1;
        try
        {
            _isApplying = true;
            OnPropertyChanged(nameof(IsApplying));
            OnPropertyChanged(nameof(CanApply));
            if (!IsAttemptCurrent(version)) return false;
            var problem = _validate?.Invoke(text);
            if (!IsAttemptCurrent(version)) return false;
            if (!string.IsNullOrWhiteSpace(problem))
            {
                SetValidationMessage(problem);
                return false;
            }
            _onApply!(text);
            // The host accepted this exact buffer, even if it changed during
            // that callback. Newer text remains dirty relative to this result.
            _acceptedText = text;
            _lastAppliedVersion = version;
            OnPropertyChanged(nameof(IsModified));
            if (!IsAttemptCurrent(version)) return false;
            SetValidationMessage(null);
            if (!IsAttemptCurrent(version)) return false;
            succeeded = true;
        }
        catch (Exception failure) when (failure is InvalidOperationException or ArgumentException or
                                       IOException or UnauthorizedAccessException)
        {
            SetValidationMessage(string.IsNullOrWhiteSpace(failure.Message)
                ? "Apply failed. Review the current buffer and try again." : failure.Message);
        }
        finally
        {
            _isApplying = false;
            OnPropertyChanged(nameof(IsApplying));
            OnPropertyChanged(nameof(CanApply));
        }
        // Property callbacks after leaving the busy state may create newer
        // intent. A window must not close on the preceding successful attempt.
        return succeeded && IsAttemptCurrent(version);
    }

    public bool TryClose()
    {
        if (_isApplying) return false;
        if (_isCancelled || IsReadOnly || _onApply is null) return true;
        if (ClosePolicy == WmlEditorClosePolicy.ExplicitApply)
        {
            Cancel();
            return true;
        }
        // Closing after an explicit successful Apply must not call its host a
        // second time. The first legacy close still applies unchanged content.
        return _lastAppliedVersion == _version || TryApply();
    }

    /// <summary>
    /// Applies a writable buffer through an asynchronous host. Standalone file
    /// hosts supply persistence here; validation remains the document's policy.
    /// </summary>
    public async Task<bool> TryApplyAsync(Func<string, Task> onApply)
    {
        ArgumentNullException.ThrowIfNull(onApply);
        if (IsReadOnly || _isCancelled || _isApplying) return false;
        var version = _version;
        var text = _text;
        var succeeded = false;
        _lastAppliedVersion = -1;
        try
        {
            _isApplying = true;
            OnPropertyChanged(nameof(IsApplying));
            OnPropertyChanged(nameof(CanApply));
            if (!IsAttemptCurrent(version)) return false;
            var problem = _validate?.Invoke(text);
            if (!IsAttemptCurrent(version)) return false;
            if (!string.IsNullOrWhiteSpace(problem))
            {
                SetValidationMessage(problem);
                return false;
            }
            await onApply(text);
            _acceptedText = text;
            _lastAppliedVersion = version;
            OnPropertyChanged(nameof(IsModified));
            if (!IsAttemptCurrent(version)) return false;
            SetValidationMessage(null);
            if (!IsAttemptCurrent(version)) return false;
            succeeded = true;
        }
        catch (Exception failure) when (failure is InvalidOperationException or ArgumentException or
                                       IOException or UnauthorizedAccessException)
        {
            SetValidationMessage(string.IsNullOrWhiteSpace(failure.Message)
                ? "Save failed. Review the current buffer and try again." : failure.Message);
        }
        finally
        {
            _isApplying = false;
            OnPropertyChanged(nameof(IsApplying));
            OnPropertyChanged(nameof(CanApply));
        }
        return succeeded && IsAttemptCurrent(version);
    }

    public void Cancel()
    {
        if (_isCancelled) return;
        _isCancelled = true;
        _version++;
        OnPropertyChanged(nameof(IsCancelled));
        OnPropertyChanged(nameof(CanApply));
    }

    private bool IsAttemptCurrent(long version)
    {
        if (_isCancelled) return false;
        if (_version == version) return true;
        SetValidationMessage("The text changed during Apply. Review the current buffer and apply again.");
        return false;
    }

    private void SetValidationMessage(string? message)
    {
        if (string.Equals(_validationMessage, message, StringComparison.Ordinal)) return;
        _validationMessage = message;
        OnPropertyChanged(nameof(ValidationMessage));
    }
}
