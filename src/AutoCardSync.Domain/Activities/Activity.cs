namespace AutoCardSync.Domain.Activities;

public record Activity
{
    public Guid Id { get; init; }
    public string ActivityNumber { get; init; }
    public string Name { get; init; }
    public DateOnly Date { get; init; }
    public string Operator { get; init; }
    public Guid PrimaryTargetId { get; init; }
    public Guid BackupTargetId { get; init; }
    public string DirectoryTemplate { get; init; }
    public string? FilterPolicy { get; init; }
    public Guid ConfigVersionId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? FrozenAt { get; private set; }

    public Activity(
        Guid id,
        string activityNumber,
        string name,
        DateOnly date,
        string @operator,
        Guid primaryTargetId,
        Guid backupTargetId,
        string directoryTemplate,
        string? filterPolicy,
        Guid configVersionId,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty)
            throw new ArgumentException("Id must not be empty.", nameof(id));
        if (string.IsNullOrWhiteSpace(activityNumber))
            throw new ArgumentException("ActivityNumber must not be empty.", nameof(activityNumber));
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name must not be empty.", nameof(name));
        if (string.IsNullOrWhiteSpace(@operator))
            throw new ArgumentException("Operator must not be empty.", nameof(@operator));
        if (primaryTargetId == Guid.Empty)
            throw new ArgumentException("PrimaryTargetId must not be empty.", nameof(primaryTargetId));
        if (backupTargetId == Guid.Empty)
            throw new ArgumentException("BackupTargetId must not be empty.", nameof(backupTargetId));
        if (string.IsNullOrWhiteSpace(directoryTemplate))
            throw new ArgumentException("DirectoryTemplate must not be empty.", nameof(directoryTemplate));
        if (configVersionId == Guid.Empty)
            throw new ArgumentException("ConfigVersionId must not be empty.", nameof(configVersionId));

        Id = id;
        ActivityNumber = activityNumber;
        Name = name;
        Date = date;
        Operator = @operator;
        PrimaryTargetId = primaryTargetId;
        BackupTargetId = backupTargetId;
        DirectoryTemplate = directoryTemplate;
        FilterPolicy = filterPolicy;
        ConfigVersionId = configVersionId;
        CreatedAt = createdAt;
    }

    public bool Freeze()
    {
        if (FrozenAt.HasValue)
            return false;

        FrozenAt = DateTimeOffset.UtcNow;
        return true;
    }

    public Activity WithConfig(Guid newVersionId, string newTemplate, string? newFilter)
    {
        if (FrozenAt.HasValue)
            throw new InvalidOperationException("Cannot change configuration of a frozen activity.");

        if (newVersionId == Guid.Empty)
            throw new ArgumentException("NewVersionId must not be empty.", nameof(newVersionId));
        if (string.IsNullOrWhiteSpace(newTemplate))
            throw new ArgumentException("NewTemplate must not be empty.", nameof(newTemplate));

        return this with
        {
            ConfigVersionId = newVersionId,
            DirectoryTemplate = newTemplate,
            FilterPolicy = newFilter
        };
    }
}
