namespace magniF.Core;

/// <summary>
/// Режим модуля All in One (--hosted): без своего автозапуска — этим занимается All in One,
/// а управляет он программой через канал <see cref="HostLink"/>. Иконка в трее остаётся.
/// </summary>
internal static class Hosting
{
    public static bool IsHosted { get; private set; }

    public static string PipeName { get; private set; } = "AllInOne.magnif";

    public static void Init(string[] args) => (IsHosted, PipeName) = HostLink.ParseArgs(args, "magnif");
}
