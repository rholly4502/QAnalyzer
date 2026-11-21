using Microsoft.AspNetCore.Http;
using QAnalyzer.Web.Services;

namespace QAnalyzer.Web.Security;

public sealed record ValidationFailure(int StatusCode, string Error);

/// <summary>
/// Validasi input endpoint analisis agar payload kosong atau terlalu besar ditolak lebih awal.
/// </summary>
public static class WebSecurityValidator
{
    public static ValidationFailure? ValidateAnalyzeRequest(AnalyzeRequest? request, int maxSqlLength)
    {
        if (maxSqlLength <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxSqlLength), "Batas panjang SQL harus lebih besar dari 0.");
        }

        if (request is null || string.IsNullOrWhiteSpace(request.Sql))
        {
            return new ValidationFailure(StatusCodes.Status400BadRequest, "Sql wajib diisi.");
        }

        if (request.Sql.Length > maxSqlLength)
        {
            return new ValidationFailure(
                StatusCodes.Status413PayloadTooLarge,
                $"Script SQL melebihi batas maksimal {maxSqlLength} karakter.");
        }

        return null;
    }
}
