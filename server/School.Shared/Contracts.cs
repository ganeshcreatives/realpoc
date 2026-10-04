using System.Text.Json;
using System.Text.Json.Serialization;

namespace School.Shared;

public record RegisterInput(string Email);
public record LoginInput(string Email, string Password);
public record UserDto(string Id, string Name, string Email, string Role);
public record AuthDto(string Token, long ExpiresAt, UserDto User);
public record ContextDto(string SigningKey, long ExpiresAt, long ServerTime, UserDto User);
public record StudentInput(string Id, string Name, int Grade);
public record StudentDto(string Id, string Name, int Grade);
public record BalanceDto(string StudentId, long BalanceMinor, string Currency);
public record SchoolDto(string Id, string Name, string City);
public record ApplicationInput(string SubmissionId, string StudentId, string SchoolId, string AcademicYear);
public record ApplicationDto(string Id, string StudentId, string SchoolId, string AcademicYear, string Status, long SubmittedAt);
public record EmailInput(string Email);
public record VerifyInput(string Token, string Name, string Password);
public record ResetInput(string Token, string Password);
public record DecisionInput(string Status, int Version);
public record StaffApplicationDto(string Id, string UserId, string StudentName, string SchoolId, string AcademicYear, string Status, long SubmittedAt, int Version);
public record ProblemDto(string Type, string Title, int Status, string Code, string TraceId);

public static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true };
}

public sealed class Rejection(int status, string code) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
}
