using ActionsRing.Core.Configuration;

namespace ActionsRing.App.Services;

/// <summary>
/// Captures an editable configuration graph and restores it in place when the
/// corresponding durable save cannot be completed.
/// </summary>
public sealed class ConfigurationMutationTransaction
{
    private readonly ActionsRingConfiguration _target;
    private ActionsRingConfiguration? _snapshot;

    private ConfigurationMutationTransaction(ActionsRingConfiguration target)
    {
        _target = target;
        _snapshot = ConfigurationJson.Clone(target);
    }

    public bool IsActive => _snapshot is not null;

    public static ConfigurationMutationTransaction Capture(ActionsRingConfiguration target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return new ConfigurationMutationTransaction(target);
    }

    public async Task<bool> TryCommitAsync(Func<Task<bool>> saveAsync)
    {
        ArgumentNullException.ThrowIfNull(saveAsync);
        EnsureActive();
        try
        {
            if (await saveAsync())
            {
                Commit();
                return true;
            }
        }
        catch
        {
            Rollback();
            throw;
        }

        Rollback();
        return false;
    }

    public void Commit()
    {
        EnsureActive();
        _snapshot = null;
    }

    public void Rollback()
    {
        var snapshot = EnsureActive();
        Copy(snapshot, _target);
        _snapshot = null;
    }

    private ActionsRingConfiguration EnsureActive() =>
        _snapshot ?? throw new InvalidOperationException("The configuration transaction is already complete.");

    private static void Copy(ActionsRingConfiguration source, ActionsRingConfiguration target)
    {
        target.SchemaVersion = source.SchemaVersion;
        target.Trigger = source.Trigger;
        target.UserProfiles = source.UserProfiles;
        target.ActiveUserProfileId = source.ActiveUserProfileId;
        target.Preferences = source.Preferences;
        target.Onboarding = source.Onboarding;
    }
}
