namespace Xrmd.Abstractions;

/// <summary>
/// Invalid caller input (logical names, metadata definitions, ...): the message is meant for the caller and is
/// returned as a 400 InvalidArgument. Other <see cref="ArgumentException"/>s are programming errors (500).
/// </summary>
public sealed class XrmdValidationException : ArgumentException
{
    public XrmdValidationException(string message) : base(message) { }

    public XrmdValidationException(string message, Exception innerException) : base(message, innerException) { }
}
