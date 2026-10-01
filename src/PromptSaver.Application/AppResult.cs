namespace PromptSaver.Application;

public enum AppErrorCode
{
    Validation,
    NotFound,
    Conflict,
    PersistenceUnavailable,
    DraftCorrupt,
    ClipboardUnavailable,
    ProviderUnavailable,
    ProviderRejected,
    CredentialUnavailable,
    SearchInvalid,
    Unauthorized,
    Cancelled,
    Unexpected,
}

public sealed record AppError(
    AppErrorCode Code,
    string Key,
    string Message,
    bool IsRetryable = false,
    IReadOnlyDictionary<string, string>? Details = null);

public sealed class AppContractException : ArgumentException
{
    public AppContractException(string code, string message, string? paramName = null)
        : base(message, paramName)
    {
        Code = code;
    }

    public string Code { get; }
}

public readonly struct AppResult
{
    private AppResult(bool isSuccess, AppError? error)
    {
        IsSuccess = isSuccess;
        Error = error;
    }

    public bool IsSuccess { get; }

    public AppError? Error { get; }

    public static AppResult Success() => new(true, null);

    public static AppResult Failure(AppError error) =>
        new(false, error ?? throw new ArgumentNullException(nameof(error)));

    public static AppResult<T> Success<T>(T value) => AppResult<T>.CreateSuccess(value);

    public static AppResult<T> Failure<T>(AppError error) => AppResult<T>.CreateFailure(error);
}

public sealed class AppResult<T>
{
    private readonly T? _value;

    private AppResult(bool isSuccess, T? value, AppError? error)
    {
        IsSuccess = isSuccess;
        _value = value;
        Error = error;
    }

    public bool IsSuccess { get; }

    public T Value =>
        IsSuccess
            ? _value!
            : throw new InvalidOperationException("A failed result has no value.");

    public AppError? Error { get; }

    internal static AppResult<T> CreateSuccess(T value) =>
        new(true, value, null);

    internal static AppResult<T> CreateFailure(AppError error) =>
        new(false, default, error ?? throw new ArgumentNullException(nameof(error)));
}
