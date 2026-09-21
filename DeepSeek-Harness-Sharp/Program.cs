namespace DeepSeek_Harness_Sharp;

public static class Program
{
    public static Task<int> Main(string[] args) => Dsh.Host.HarnessEntrypoint.RunAsync(args);
}
