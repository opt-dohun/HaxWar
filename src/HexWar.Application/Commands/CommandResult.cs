namespace HexWar.Application.Commands;

// 명령 결과 반환 기본 클래스 
public class CommandResult
{
    public bool IsSuccess { get; set; }
    public string? ErrorMessage { get; set; }
    public string? ErrorCode { get; set; }

    public static CommandResult Success() => new() { IsSuccess = true };

    public static CommandResult Fail(string message, string? code = null) => new()
    {
        IsSuccess = false,
        ErrorMessage = message,
        ErrorCode = code,
    };

}

public class MoveUnitsCommandResult : CommandResult
{
    public int ActualMoved { get; init; }
    public string FromNode { get; init; } = string.Empty;
    public string ToNode { get; init; } = string.Empty;

    public static MoveUnitsCommandResult Success(int actualMoved, string from, string to) => new()
    {
        IsSuccess = true,
        ActualMoved = actualMoved,
        FromNode = from,
        ToNode = to
    };

    public static new MoveUnitsCommandResult Fail(string message, string? code = null) => new()
    {
        IsSuccess = false,
        ErrorMessage = message,
        ErrorCode = code
    };
}

public class EncounterDecisionCommandResult : CommandResult
{
    public string EdgeId { get; init; } = string.Empty;
    public string Decision { get; init; } = string.Empty;
    public bool EncounterResolved { get; init; }

    public static EncounterDecisionCommandResult Success(string edgeId, string decision, bool resolved) => new()
    {
        IsSuccess = true,
        EdgeId = edgeId,
        Decision = decision,
        EncounterResolved = resolved
    };

    public static new EncounterDecisionCommandResult Fail(string message, string? code = null) => new()
    {
        IsSuccess = false,
        ErrorMessage = message,
        ErrorCode = code
    };
}