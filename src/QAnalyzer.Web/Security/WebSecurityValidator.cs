using Microsoft.AspNetCore.Http;
using QAnalyzer.Web.Services;

namespace QAnalyzer.Web.Security;

public sealed record ValidationFailure(int StatusCode, string Error);

/// <summary>
/// Validasi input endpoint analisis &amp; tuning agar payload kosong atau terlalu besar ditolak lebih awal.
/// </summary>
public static class WebSecurityValidator
{
    public static ValidationFailure? ValidateAnalyzeRequest(
        AnalyzeRequest? request,
        int maxSqlLength,
        int? maxPlanLength = null)
    {
        var effectiveMaxPlanLength = maxPlanLength ?? maxSqlLength;
        ValidateLimitArguments(maxSqlLength, effectiveMaxPlanLength);

        var hasSql = request is not null && !string.IsNullOrWhiteSpace(request.Sql);
        var hasPlan = request is not null && !string.IsNullOrWhiteSpace(request.SqlPlanXml);
        if (!hasSql && !hasPlan)
        {
            return new ValidationFailure(StatusCodes.Status400BadRequest, "Sql atau XML execution plan wajib diisi.");
        }

        return ValidatePayloadLengths(request!.Sql, request.SqlPlanXml, maxSqlLength, effectiveMaxPlanLength);
    }

    public static ValidationFailure? ValidateTuneRequest(TuneRequest? request, int maxSqlLength, int maxPlanLength)
    {
        ValidateLimitArguments(maxSqlLength, maxPlanLength);

        if (request is null || string.IsNullOrWhiteSpace(request.Sql))
        {
            return new ValidationFailure(StatusCodes.Status400BadRequest, "Sql wajib diisi.");
        }

        return ValidatePayloadLengths(request.Sql, request.SqlPlanXml, maxSqlLength, maxPlanLength);
    }

    private static void ValidateLimitArguments(int maxSqlLength, int maxPlanLength)
    {
        if (maxSqlLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSqlLength), "Batas panjang SQL harus lebih besar dari 0.");
        }

        if (maxPlanLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPlanLength), "Batas panjang XML execution plan harus lebih besar dari 0.");
        }
    }

    private static ValidationFailure? ValidatePayloadLengths(
        string? sql,
        string? sqlPlanXml,
        int maxSqlLength,
        int maxPlanLength)
    {
        if (!string.IsNullOrEmpty(sql) && sql.Length > maxSqlLength)
        {
            return new ValidationFailure(
                StatusCodes.Status413PayloadTooLarge,
                $"Script SQL melebihi batas maksimal {maxSqlLength} karakter.");
        }

        if (!string.IsNullOrEmpty(sqlPlanXml) && sqlPlanXml.Length > maxPlanLength)
        {
            return new ValidationFailure(
                StatusCodes.Status413PayloadTooLarge,
                $"XML execution plan melebihi batas maksimal {maxPlanLength} karakter.");
        }

        return null;
    }
}
