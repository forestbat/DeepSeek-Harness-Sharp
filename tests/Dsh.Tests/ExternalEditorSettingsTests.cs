using Dsh.Boot;

namespace Dsh.Tests;

public sealed class ExternalEditorSettingsTests
{
    [Fact]
    public void Settings_ParsesFavoriteAndArguments()
    {
        var root = Path.Combine(Path.GetTempPath(), $"dsh-editor-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "settings.yaml"), """
                external_editor:
                  - name: VS Code
                    command: code
                    args: ["--goto", "{path}:{line}:{column}"]
                  - name: Notepad
                    command: notepad.exe
                    args: ["{path}"]
                """);
            var settings = HarnessSettings.Load(new HarnessHome(root));
            Assert.Equal(2, settings.ExternalEditor.Count);
            Assert.Equal("VS Code", settings.ExternalEditor[0].Name);
            Assert.Equal("code", settings.ExternalEditor[0].Command);
            Assert.Contains("{line}", settings.ExternalEditor[0].Args[1]);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
