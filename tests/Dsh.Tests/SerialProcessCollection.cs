namespace Dsh.Tests;

/** Tests that mutate process-global state or require stable real-process timing run exclusively. */
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialProcessCollection
{
    public const string Name = "serial-process";
}
