namespace StickerSense.Core;

/// <summary>An error whose message is written to be spoken to the user as is.</summary>
public abstract class UserFacingException : Exception
{
    protected UserFacingException(string message, Exception? inner = null) : base(message, inner) { }
}

public enum DescribeErrorKind
{
    EngineUnavailable,
    Busy,
    Failed,
}

public sealed class DescribeException : UserFacingException
{
    private DescribeException(DescribeErrorKind kind, string message, EngineStatus? status, Exception? inner)
        : base(message, inner)
    {
        Kind = kind;
        Status = status;
    }

    public DescribeErrorKind Kind { get; }

    /// <summary>Set when <see cref="Kind"/> is <see cref="DescribeErrorKind.EngineUnavailable"/>.</summary>
    public EngineStatus? Status { get; }

    public static DescribeException EngineUnavailable(EngineStatus status) =>
        new(DescribeErrorKind.EngineUnavailable, status.Explanation, status, null);

    public static DescribeException Busy() =>
        new(DescribeErrorKind.Busy, "Still working on the last one.", null, null);

    public static DescribeException Failed(string message, Exception? inner = null) =>
        new(DescribeErrorKind.Failed, $"Couldn't describe it: {message}", null, inner);
}

public sealed class CaptureException : UserFacingException
{
    public CaptureException(string message, Exception? inner = null) : base(message, inner) { }

    public static CaptureException OffScreen() => new("The focused item isn't on screen.");

    public static CaptureException Failed(string message, Exception? inner = null) =>
        new($"Couldn't capture the screen: {message}", inner);
}
