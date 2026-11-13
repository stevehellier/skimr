using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Skimr.Infrastructure.Persistence;

namespace Skimr.Infrastructure.Identity;

/// <summary>
/// First-run setup. While no account exists, a one-time code is printed to the log at startup,
/// and the setup wizard needs it to create the first admin (PLAN D32). Whoever reaches an
/// exposed server first therefore can't claim it.
/// </summary>
internal sealed partial class SetupService(IServiceScopeFactory scopes, ILogger<SetupService> logger)
{
    // No 0/O, 1/I/L: the code is read off a log and typed by hand.
    private const string Alphabet = "ABCDEFGHJKMNPQRSTUVWXYZ23456789";
    private const int CodeLength = 12;

    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile bool _complete;
    private string? _code;

    /// <summary>Cached once true, so the setup redirect costs nothing after the first account exists.</summary>
    public bool IsComplete => _complete;

    /// <summary>For tests, which can't read the log.</summary>
    internal string? Code => _code;

    public async Task InitialiseAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SkimrDbContext>();
        _complete = await db.Users.AnyAsync(cancellationToken);
        if (!_complete)
        {
            _code = string.Create(CodeLength, 0, (span, _) =>
            {
                for (var i = 0; i < span.Length; i++)
                {
                    span[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
                }
            });
            LogSetupCode(Format(_code));
        }
    }

    public async Task<AccountResult> CompleteAsync(string code, string userName, string password, RegistrationMode mode, CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            if (_complete)
            {
                return AccountResult.Fail("Setup has already been completed.");
            }

            var entered = new string(code.Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
            if (_code is null || !Secrets.FixedTimeEquals(entered, _code))
            {
                return AccountResult.Fail("That setup code is wrong. It's printed in the server log (for Docker: docker compose logs skimr).");
            }

            await using var scope = scopes.CreateAsyncScope();
            var accounts = scope.ServiceProvider.GetRequiredService<AccountService>();
            var result = await accounts.CreateUserAsync(userName, password, isAdmin: true, cancellationToken);
            if (!result.Succeeded)
            {
                return result;
            }

            await accounts.SetRegistrationModeAsync(mode, cancellationToken);
            _complete = true;
            _code = null;
            return result;
        }
        finally
        {
            _lock.Release();
        }
    }

    private static string Format(string code) => $"{code[..4]}-{code[4..8]}-{code[8..]}";

    [LoggerMessage(Level = LogLevel.Warning, Message = "No accounts exist yet. Open /setup in your browser and enter this setup code: {Code}")]
    private partial void LogSetupCode(string code);
}
