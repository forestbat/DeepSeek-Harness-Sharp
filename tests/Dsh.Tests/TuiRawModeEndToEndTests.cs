using System.Diagnostics;
using System.Runtime.Versioning;
using Dsh.Boot;
using Dsh.Core;
using Dsh.Llm;
using Dsh.Persistence;
using Dsh.Ptc;
using Dsh.Tui;

namespace Dsh.Tests;

/** Real CLI consumers of the kernel terminal, using the same production PTY fixture as other TUI E2E. */
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class TuiRawModeEndToEndTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public async Task RealCli_RawInputSurvivesBackgroundRefreshAndKernelResize_ThenRestoresTerminal(int run)
    {
        RequireUnix();
        using var terminal = await PtyTuiHarness.StartOwnedTerminalAsync(copyRealConfiguration: false);
        var scenario = new Scenario(terminal);
        scenario.WaitForRawInput();
        foreach (var probe in new[] { "zqx", "vkj", "ypw" })
            scenario.ExerciseInputAcrossRefreshAndResize(probe);
        scenario.ExitAndAssertRestored(output, $"run {run}");
    }

    [Fact]
    [Trait("Category", "LlmSmoke")]
    public async Task RealCli_RealAssistantTurnRenders_ThenRestoresTerminal()
    {
        RequireUnix();
        using var terminal = await PtyTuiHarness.StartOwnedTerminalAsync(copyRealConfiguration: true);
        var scenario = new Scenario(terminal);
        scenario.WaitForRawInput();
        // The answer is deliberately absent from the prompt: input echo cannot satisfy the assertion.
        const string prompt = "Spell out in English the number equal to six times seven. Reply only with the answer, without digits, formatting or tools.";
        scenario.Write($"{prompt}\r");
        scenario.WaitFor(() => ReadCompletedAssistantReply(terminal.Home) is { } reply
                && !prompt.Contains(reply, StringComparison.OrdinalIgnoreCase)
                && scenario.ContainsVisible(reply, ignoreCase: true),
            TimeSpan.FromSeconds(5), "The real assistant answer did not complete in the visible TUI.");
        scenario.ExitAndAssertRestored(output, "real model turn");
    }

    [Fact(Explicit = true)]
    [Trait("Category", "LlmSmoke")]
    public async Task RealCli_CodeAndNativeToolsComposeWithoutSelectingAMode()
    {
        RequireUnix();
        using var terminal = await PtyTuiHarness.StartOwnedTerminalAsync(copyRealConfiguration: true);
        var scenario = new Scenario(terminal);
        scenario.WaitForRawInput();
        var resultPath = Path.Combine(terminal.Home, "capability-result.txt");
        var prompt = $"Use run_code to compute 17 * 19 in C#, then use write directly to save only the computed integer to {resultPath}; do not write files inside run_code and report the result after both calls.";
        scenario.Write($"{prompt}\r");
        scenario.WaitFor(() => File.Exists(resultPath)
                && ReadCompletedAssistantReply(terminal.Home) is not null
                && scenario.ContainsVisible("323"),
            TimeSpan.FromSeconds(90), "The real TUI did not complete the tool turn and display the computed result.");
        Assert.Equal("323", (await File.ReadAllTextAsync(resultPath, TestContext.Current.CancellationToken)).Trim());
        using var persistence = new JsonlSessionPersistence(new HarnessHome(terminal.Home).SessionsPath);
        var history = new List<SessionEventPayload>();
        foreach (var stored in persistence.List())
        {
            using var handle = persistence.Open(stored.Header.Id, SessionAccess.Read);
            history.AddRange(handle.Read().Select(item => item.Data));
        }
        var calls = history.OfType<ToolCallPayload>().ToArray();
        var results = history.OfType<ToolResultPayload>().ToArray();
        Assert.Contains(calls, call => call.Name == ToolRunCode.ToolName
            && results.Any(result => result.Message.Block.ToolCallId == call.CallId
                && result.Error is null && result.Message.Block.IsError is not true
                && MessageText.Flatten(result.Message.Content).Contains("323", StringComparison.Ordinal)));
        Assert.Contains(calls, call => call.Name == "write"
            && results.Any(result => result.Message.Block.ToolCallId == call.CallId
                && result.Error is null && result.Message.Block.IsError is not true));
        scenario.ExitAndAssertRestored(output, "real code/native capability turn");
    }

    private static string? ReadCompletedAssistantReply(string home)
    {
        using var persistence = new JsonlSessionPersistence(new HarnessHome(home).SessionsPath);
        foreach (var session in persistence.List())
        {
            using var handle = persistence.Open(session.Header.Id, SessionAccess.Read);
            var events = handle.Read();
            var end = events.Select(entry => entry.Data).OfType<TurnEndPayload>().LastOrDefault();
            if (end is null)
                continue;
            Assert.IsType<TurnEndReason.Completed>(end.Reason);
            return events.Select(entry => entry.Data).OfType<AssistantMessagePayload>()
                .SelectMany(assistant => assistant.Message.Content.OfType<TextBlock>())
                .Select(block => block.Text.Trim()).LastOrDefault(text => text.Length > 0);
        }
        return null;
    }

    private static void RequireUnix()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Assert.Skip("Kernel termios and Unix PTY session ownership require Linux/macOS.");
    }

    private sealed class Scenario(PtyTuiHarness terminal)
    {
        private bool _rawSeen;
        private bool _exitSent;
        private VtScreen _screen = new(140, 40);
        private int _mode;
        private bool _hasRenderedFrame;

        public void WaitForRawInput()
        {
            WaitFor(() => _screen.IsAlternateScreen && _mode == 0 && _hasRenderedFrame,
                TimeSpan.FromSeconds(5), "The built real CLI did not render its input after taking ownership of the terminal.");
            Assert.Equal(7, ModeBits(File.ReadAllText(Path.Combine(terminal.Home, "termios-before"))));
            Assert.True(_rawSeen, "The real CLI never entered raw mode.");
            Assert.Equal(0, _mode);
        }

        public void ExerciseInputAcrossRefreshAndResize(string probe)
        {
            var emptyInput = SnapshotInputRow();
            var deadline = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
            for (var length = 1; length <= probe.Length; length++)
            {
                Write(probe.Substring(length - 1, 1));
                var prefix = probe[..length];
                WaitUntil(() => InputContains(prefix), deadline,
                    "A character sent without Enter did not render in the real input row.");
            }

            // TIOCSWINSZ generates the real foreground-group SIGWINCH, not a synthetic managed resize event.
            terminal.Session.Resize(41, 141);
            WaitUntil(() => InputContains(probe), deadline,
                "Typed input did not survive the larger kernel viewport.");
            terminal.Session.Resize(40, 140);
            WaitUntil(() => InputContains(probe), deadline,
                "Typed input did not survive restoring the kernel viewport.");

            // Cross several background Git refreshes while continuously checking the live kernel input mode.
            var refreshEnd = Stopwatch.GetTimestamp() + 3 * Stopwatch.Frequency;
            do { Observe(); } while (Stopwatch.GetTimestamp() < refreshEnd);
            Assert.True(InputContains(probe),
                "A background refresh removed unsent input from the real TUI.");

            for (var length = probe.Length; length > 0; length--)
            {
                Write("\u007f");
                var oldPrefix = probe[..length];
                var remaining = probe[..(length - 1)];
                WaitUntil(() => length == 1
                    ? InputEquals(emptyInput)
                    : InputContains(remaining) && !InputContains(oldPrefix),
                    deadline, $"Backspace did not remove exactly one unsent input character (previous length {length}).");
            }
        }

        public void ExitAndAssertRestored(ITestOutputHelper output, string description)
        {
            _exitSent = true;
            Write("/exit\r");
            var deadline = Stopwatch.GetTimestamp() + 30 * Stopwatch.Frequency;
            var exitPath = Path.Combine(terminal.Home, "exit-code");
            WaitUntil(() => File.Exists(exitPath), deadline, "Normal /exit did not terminate the real CLI.");
            Assert.Equal("0", File.ReadAllText(exitPath).Trim());
            // The existing shell owner keeps Darwin's tty alive; its next canonical read processes PENDIN.
            Write("\n");
            var restoredPath = Path.Combine(terminal.Home, "termios-after");
            WaitUntil(() => File.Exists(restoredPath), deadline, "The caller could not inspect its restored terminal.");
            // -g captures all flags and control characters; -a additionally reports speeds and Linux's line discipline.
            Assert.Equal(File.ReadAllText(Path.Combine(terminal.Home, "termios-before")), File.ReadAllText(restoredPath));
            WaitUntil(() => !_screen.IsAlternateScreen && ContainsVisible("DSH-RAW-MAIN-SCREEN"),
                deadline, "Normal exit did not restore the caller's original main screen.");
            output.WriteLine($"{description}: raw entered; normal exit=0; complete stty state restored; original main screen restored");
        }

        public void WaitFor(Func<bool> predicate, TimeSpan budget, string failure)
            => WaitUntil(predicate, Stopwatch.GetTimestamp() + (long)(budget.TotalSeconds * Stopwatch.Frequency), failure);

        private void WaitUntil(Func<bool> predicate, long deadline, string failure)
        {
            do
            {
                Observe();
                if (predicate()) return;
            } while (Stopwatch.GetTimestamp() < deadline);
            Assert.Fail(failure); // Never publish the terminal stream or private configuration on failure.
        }

        private void Observe()
        {
            TestContext.Current.CancellationToken.ThrowIfCancellationRequested();
            // Certify completion before copying the production VT state. Checking the live transcript
            // afterwards could validate an older, half-painted first frame as the empty-input baseline.
            _hasRenderedFrame = terminal.Snapshot().Contains("\u001b[?2026l", StringComparison.Ordinal);
            _screen = terminal.ReadTerminalScreen();
            var ttyPath = Path.Combine(terminal.Home, "terminal");
            if (!File.Exists(ttyPath))
                return;
            var info = new ProcessStartInfo("stty",
                [OperatingSystem.IsMacOS() ? "-f" : "-F", File.ReadAllText(ttyPath).Trim(), "-a"])
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            info.Environment["LC_ALL"] = "C";
            using var process = TestProcessScope.Start(info) ?? throw new InvalidOperationException("stty did not start.");
            var state = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            Assert.Equal(0, process.ExitCode);
            _mode = ModeBits(state);
            if (_mode == 0) _rawSeen = true;
            if (_rawSeen && !_exitSent)
                Assert.True(_mode == 0, $"The TUI lost raw input before normal exit (ICANON/ECHO/ISIG bits={_mode}).");
        }

        public void Write(string text) => terminal.WriteTextAsync(text).GetAwaiter().GetResult();

        private static int ModeBits(string state)
        {
            var flags = state.Split([' ', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries);
            Assert.True(flags.Contains("icanon") || flags.Contains("-icanon"), "stty did not report canonical mode.");
            Assert.True(flags.Contains("echo") || flags.Contains("-echo"), "stty did not report echo mode.");
            Assert.True(flags.Contains("isig") || flags.Contains("-isig"), "stty did not report signal mode.");
            return (flags.Contains("icanon") ? 1 : 0) | (flags.Contains("echo") ? 2 : 0) | (flags.Contains("isig") ? 4 : 0);
        }

        public bool ContainsVisible(string marker, bool ignoreCase = false)
            => ContainsVisible(marker, 0, _screen.Height, ignoreCase);

        private bool ContainsVisible(string marker, int firstRow, int endRow, bool ignoreCase = false)
        {
            for (var y = firstRow; y < endRow; y++)
            {
                var row = _screen.Row(y);
                for (var start = 0; start < row.Length; start++)
                {
                    var index = 0;
                    for (var x = start; x < row.Length && index < marker.Length; x++)
                    {
                        var character = row[x].Character;
                        if (character == '\0') continue;
                        var expected = marker[index];
                        if (ignoreCase)
                        {
                            character = char.ToLowerInvariant(character);
                            expected = char.ToLowerInvariant(expected);
                        }
                        if (character != expected) break;
                        index++;
                    }
                    if (index == marker.Length) return true;
                }
            }
            return false;
        }

        private bool InputContains(string marker)
        {
            var input = LayoutEngine.Calculate(_screen.Width, _screen.Height).Input;
            return ContainsVisible(marker, input.Y, input.Y + 1);
        }

        private char[] SnapshotInputRow()
        {
            var input = LayoutEngine.Calculate(_screen.Width, _screen.Height).Input;
            var row = _screen.Row(input.Y);
            var characters = new char[row.Length];
            for (var x = 0; x < row.Length; x++)
                characters[x] = row[x].Character;
            return characters;
        }

        private bool InputEquals(ReadOnlySpan<char> expected)
        {
            var input = LayoutEngine.Calculate(_screen.Width, _screen.Height).Input;
            var row = _screen.Row(input.Y);
            if (row.Length != expected.Length) return false;
            for (var x = 0; x < row.Length; x++)
                if (row[x].Character != expected[x]) return false;
            return true;
        }
    }
}
