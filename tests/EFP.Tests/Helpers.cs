using EFP.Core.Windows;

namespace EFP.Tests;

public static class Assert
{
    public static void Equal<T>(T expected, T actual, string what)
    {
        if (expected is Array ea && actual is Array aa)
        {
            if (!ea.Cast<object>().SequenceEqual(aa.Cast<object>()))
                throw new Exception($"{what} : attendu [{string.Join(", ", ea.Cast<object>())}], obtenu [{string.Join(", ", aa.Cast<object>())}]");
            return;
        }
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what} : attendu «{expected}», obtenu «{actual}»");
    }

    public static void NotNull(object? value, string what)
    {
        if (value is null) throw new Exception($"{what} : valeur nulle");
    }

    public static void Contains(string haystack, string needle, string what)
    {
        if (!haystack.Contains(needle)) throw new Exception($"{what} : «{needle}» absent de :\n{haystack}");
    }

    public static void Throws<TEx>(Action a, string what) where TEx : Exception
    {
        try { a(); }
        catch (TEx) { return; }
        throw new Exception($"{what} : {typeof(TEx).Name} attendue");
    }
}

/// <summary>IProgress synchrone (Progress&lt;T&gt; poste sur le pool, ce qui rend les tests non déterministes).</summary>
public sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
{
    public void Report(T value) { lock (this) action(value); }
}

/// <summary>Exécute les scripts générés dans pwsh, précédés de cmdlets simulées.</summary>
public sealed class MockedRunner(string pwsh, string mocks) : IScriptRunner
{
    public string LastOutput { get; private set; } = "";

    public async Task<ScriptOutput> RunAsync(string script, TimeSpan timeout, CancellationToken ct = default, Action<string>? onLine = null)
    {
        var res = await new PowerShellRunner { Executable = pwsh }.RunAsync(mocks + script, timeout, ct, onLine);
        LastOutput = res.StdOut + res.StdErr;
        return res;
    }
}
