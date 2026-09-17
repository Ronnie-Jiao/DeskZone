namespace DeskZone.Core.Models;

public sealed record AddReferenceFailure(string Path, string Reason);

public sealed record AddReferencesResult(
    int Added,
    int MovedFromOtherCategories,
    int AlreadyInCategory,
    IReadOnlyList<AddReferenceFailure> Failures)
{
    public int Accepted => Added + MovedFromOtherCategories + AlreadyInCategory;
}

public sealed record MoveItemsResult(
    int Moved,
    IReadOnlyList<AddReferenceFailure> Failures);

public enum CategoryDeleteMode
{
    RejectIfNotEmpty,
    MoveItemsToCategory,
    MoveItemsToUnclassified
}
