namespace DeskZone.Core.Models;

public enum OperationType
{
    AddReference,
    MoveBetweenCategories,
    RemoveReference,
    ManagedMove,
    RenameFile,
    DeleteCategory
}

public enum OperationStatus
{
    Prepared,
    Succeeded,
    Failed,
    RolledBack,
    NeedsRecovery
}

public sealed record OperationLog(
    Guid Id,
    OperationType OperationType,
    string? SourcePath,
    string? DestinationPath,
    string? MetadataJson,
    OperationStatus Status,
    bool CanUndo,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
