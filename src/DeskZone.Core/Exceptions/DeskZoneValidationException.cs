namespace DeskZone.Core.Exceptions;

public sealed class DeskZoneValidationException : Exception
{
    public DeskZoneValidationException(string message) : base(message)
    {
    }
}
