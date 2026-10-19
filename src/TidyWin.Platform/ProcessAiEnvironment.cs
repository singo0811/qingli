using TidyWin.Core.AiJunk;

namespace TidyWin.Platform;

public sealed class ProcessAiEnvironment : IAiEnvironment
{
    public string? Variable(string name) => Environment.GetEnvironmentVariable(name);

    public string Folder(KnownFolder folder) => folder switch
    {
        KnownFolder.UserProfile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        KnownFolder.LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        KnownFolder.AppData => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        _ => Path.GetTempPath().TrimEnd('\\'),
    };
}
