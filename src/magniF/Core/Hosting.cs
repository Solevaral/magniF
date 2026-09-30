namespace magniF.Core;

/// <summary>
/// Режим модуля All-in-one (--hosted): без своей иконки в трее и без своего автозапуска —
/// этим занимается каркас, а управляет он программой через канал <see cref="HostLink"/>.
/// </summary>
internal static class Hosting
{
    public static bool IsHosted { get; private set; }

    public static string PipeName { get; private set; } = "AllInOne.magnif";

    public static void Init(string[] args) => (IsHosted, PipeName) = HostLink.ParseArgs(args, "magnif");
}
