using System.Text;
using System.Text.Json;
using Dsh.Ma2a.Services;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Dsh.Gui.ViewModels;
using Dsh.Gui.Views;

namespace Dsh.Tests;

[Collection(GuiSerialCollection.CollectionName)]
public sealed class Ma2aGuiTests
{
    [Fact]
    public async Task NativeManagementForms_RenderNestedAgentInput_AndArrayRemovalWorks()
    {
        await HeadlessGui.RunAsync(() =>
        {
            using var model = new Ma2aViewModel();
            model.SelectedGroup = "Agents / 任务";
            model.SelectedCommand = model.Commands.Single(command => command.Operation == "agent_define");
            var arguments = model.Fields.Single(field => field.Name == "args");
            arguments.AddItemCommand.Execute(null);
            arguments.Items[0].Text = "--stdio";
            var view = new Ma2aView { DataContext = model };
            var window = new Window { Content = view, Width = 1200, Height = 800 };
            try
            {
                window.Show();
                view.GetVisualDescendants().OfType<TabControl>().Single().SelectedIndex = 1;
                window.Measure(new Size(1200, 800));
                window.Arrange(new Rect(0, 0, 1200, 800));
                Dispatcher.UIThread.RunJobs();
                var remove = view.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "移除"));
                Assert.NotNull(remove.Command);
                remove.Command.Execute(remove.CommandParameter);
                Assert.Empty(arguments.Items);
                Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == "max tasks");
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("public_relay_configure", "url")]
    [InlineData("private_relay_configure", "public_url")]
    public void RelayForms_EnforceRuntimeUrlByteLimit(string operation, string fieldName)
    {
        const int runtimeUrlLimit = 512;
        const string prefix = "https://relay.example/";
        var field = Ma2aCommandCatalog.Commands.Single(command => command.Operation == operation)
            .CreateFields().Single(field => field.Name == fieldName);
        field.Text = $"{prefix}{new string('a', runtimeUrlLimit - prefix.Length)}";
        Assert.Equal(field.Text, field.BuildValue()!.GetValue<string>());

        field.Text += "a";
        Assert.Throws<InvalidOperationException>(() => field.BuildValue());
    }

    [Fact]
    public void NativeGrantForm_ProducesAllEightIndependentPermissions()
    {
        using var model = new Ma2aViewModel();
        model.SelectedGroup = "Spaces / 授权";
        model.SelectedCommand = model.Commands.Single(command => command.Operation == "space_grant");
        model.Fields.Single(field => field.Name == "space_id").Text = new string('a', 64);
        model.Fields.Single(field => field.Name == "peer_endpoint_id").Text = new string('b', 64);
        model.Fields.Single(field => field.Name == "files_read").Checked = true;
        model.Fields.Single(field => field.Name == "process").Checked = true;

        var values = model.Fields.ToDictionary(field => field.Name, field => field.BuildValue());

        Assert.Equal(10, values.Count);
        Assert.True(values["files_read"]!.GetValue<bool>());
        Assert.True(values["process"]!.GetValue<bool>());
        Assert.False(values["files_write"]!.GetValue<bool>());
        Assert.False(values["agent"]!.GetValue<bool>());
        Assert.DoesNotContain("request_id", values.Keys);
    }

    [Fact]
    public void NativeForms_KeepUnusedExposureFieldsNull_AndRejectNoncanonicalIdentity()
    {
        using var model = new Ma2aViewModel();
        model.SelectedGroup = "资源暴露 / 托管服务";
        model.SelectedCommand = model.Commands.Single(command => command.Operation == "service_expose");
        var space = model.Fields.Single(field => field.Name == "space_id");
        space.Text = new string('A', 64);
        Assert.Throws<InvalidOperationException>(() => space.BuildValue());
        space.Text = new string('a', 64);
        model.Fields.Single(field => field.Name == "kind").Text = "exec";

        Assert.NotNull(space.BuildValue());
        Assert.All(model.Fields.Where(field => field.IsNullable), field => Assert.Null(field.BuildValue()));
    }

    [Fact]
    public void NativeForms_ValidateNestedCollections_WithoutRawJsonInput()
    {
        var command = Ma2aCommandCatalog.Commands.Single(command => command.Operation == "agent_task_submit");
        var message = command.CreateFields().Single(field => field.Name == "message");
        var part = Assert.Single(message.Children.Single(field => field.Name == "parts").Items);
        part.Children.Single(field => field.Name == "content").Text = "Inspect the authorized root.";

        var value = message.BuildValue()!;

        Assert.Equal("user", value["role"]!.GetValue<string>());
        Assert.Equal("text", value["parts"]![0]!["kind"]!.GetValue<string>());
        Assert.Equal("Inspect the authorized root.", value["parts"]![0]!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task RemoteServicePage_RendersEveryNativeFamily_AndUsesTypedOperationFields()
    {
        await HeadlessGui.RunAsync(() =>
        {
            using var model = new Ma2aViewModel();
            var expected = new[] { "agent", "exec", "files", "process", "proxy", "service", "shell", "transfer" };
            Assert.Equal(expected, model.Remote.Operations.Select(operation => operation.Family).Distinct().Order().ToArray());
            var view = new Ma2aRemoteView { DataContext = model.Remote };
            var window = new Window { Content = view, Width = 1200, Height = 800 };
            try
            {
                window.Show();
                model.Remote.SelectedOperation = model.Remote.Operations.Single(operation => operation.Name == "call");
                Dispatcher.UIThread.RunJobs();
                view.GetVisualDescendants().OfType<Expander>().Single(expander => Equals(expander.Header, "托管服务流交互")).IsExpanded = true;
                Dispatcher.UIThread.RunJobs();
                Assert.Contains(model.Remote.Fields, field => field.Name == "body_hex" && field.IsString);
                Assert.Contains(model.Remote.Fields, field => field.Name == "version" && field.IsNumber);
                Assert.Contains(model.Remote.Fields, field => field.Name == "keep_input_open" && field.IsBoolean);
                Assert.Contains(view.GetVisualDescendants().OfType<Button>(), button => Equals(button.Content, "发送控制"));
            }
            finally { window.Close(); }
            return Task.CompletedTask;
        });
    }

    [Theory]
    [InlineData("submit")]
    [InlineData("send")]
    public void RemoteAgentMessage_UsesTheNativeUtf8ByteBoundary(string name)
    {
        var operation = Ma2aRemoteOperation.Create().Single(item => item.Family == "agent" && item.Name == name);
        var message = operation.Fields.Single(field => field.Name == "message");
        message.Text = new string('界', Ma2aServiceRequests.MaximumAgentMessageBytes / Encoding.UTF8.GetByteCount("界"));

        Assert.Equal(Ma2aServiceRequests.MaximumAgentMessageBytes, Encoding.UTF8.GetByteCount(message.BuildValue()!.GetValue<string>()));
        message.Text += "a";
        Assert.Throws<InvalidOperationException>(() => message.BuildValue());
    }

    [Fact]
    public void HostedInlineBody_UsesTheNativeBinaryBoundary()
    {
        var operation = Ma2aRemoteOperation.Create().Single(item => item.Family == "service" && item.Name == "call");
        var body = operation.Fields.Single(field => field.Name == "body_hex");
        body.Text = Convert.ToHexStringLower(new byte[Ma2aServiceRequests.MaximumHostedCallBodyBytes]);
        operation.Fields.Single(field => field.Name == "version").Number = 1;
        operation.Fields.Single(field => field.Name == "service").Text = "example";
        operation.Fields.Single(field => field.Name == "operation").Text = "echo";

        var values = operation.Fields.ToDictionary(field => field.Name, field => field.BuildValue());
        Assert.NotNull(operation.Build!(values));
        body.Text += "00";
        Assert.Throws<InvalidOperationException>(() => body.BuildValue());
    }

    [Theory]
    [InlineData(null, "00", true)]
    [InlineData("payload.bin", "", true)]
    [InlineData("payload.bin", "00", false)]
    [InlineData("", "", false)]
    [InlineData(" ", "", false)]
    public void HostedInputFile_IsOptionalAndExclusiveWithInlineInput(string? path, string hex, bool valid)
    {
        var operation = Ma2aRemoteOperation.Create().Single(item => item.Family == "service" && item.Name == "call");
        var file = operation.Fields.Single(field => field.Name == "input_file");
        Assert.True(file.IsNullable && file.IsNull);
        file.Text = path ?? "";
        file.IsNull = path is null;
        operation.Fields.Single(field => field.Name == "service").Text = "example";
        operation.Fields.Single(field => field.Name == "operation").Text = "pipe";
        operation.Fields.Single(field => field.Name == "version").Number = 2;
        operation.Fields.Single(field => field.Name == "input_hex").Text = hex;
        var values = operation.Fields.ToDictionary(field => field.Name, field => field.BuildValue());

        if (valid) Assert.NotNull(operation.Build!(values));
        else Assert.Throws<ArgumentException>(() => operation.Build!(values));
    }

    [Theory]
    [InlineData("service", "call", "input_hex", true)]
    [InlineData("process", "process stdin", "input", false)]
    public void StreamInput_PreservesPayloadsLargerThanOneNativeFrame(string family, string name, string fieldName, bool hex)
    {
        var operation = Ma2aRemoteOperation.Create().Single(item => item.Family == family && item.Name == name);
        var field = operation.Fields.Single(item => item.Name == fieldName);
        var payload = new string('界', Ma2aServiceSession.MaximumDataLength);
        field.Text = hex ? Convert.ToHexStringLower(Encoding.UTF8.GetBytes(payload)) : payload;

        Assert.Equal(field.Text, field.BuildValue()!.GetValue<string>());
        Assert.True(Encoding.UTF8.GetByteCount(payload) > Ma2aServiceSession.MaximumDataLength);
    }

    [Theory]
    [InlineData("files", "list", "path")]
    [InlineData("files", "stat", "path")]
    [InlineData("files", "mkdir", "path")]
    [InlineData("files", "read", "path")]
    [InlineData("files", "write", "path")]
    [InlineData("files", "rename", "from")]
    [InlineData("files", "rename", "to")]
    [InlineData("files", "remove", "path")]
    [InlineData("transfer", "download", "path")]
    [InlineData("transfer", "upload", "path")]
    public void RemotePaths_KeepTheRootAndSeparatorsOutsideTheComponentByteBudget(string family, string name, string fieldName)
    {
        var operation = Ma2aRemoteOperation.Create().Single(item => item.Family == family && item.Name == name);
        var field = operation.Fields.Single(item => item.Name == fieldName);
        field.Text = $"{new string('a', 64)}:/{string.Join('/', Enumerable.Repeat(new string('b', 64), 64))}";

        Assert.Equal(Ma2aServiceRequests.MaximumRemotePathTextBytes, Encoding.UTF8.GetByteCount(field.BuildValue()!.GetValue<string>()));
        Assert.NotNull(Ma2aServiceRequests.Path(field.Text));
        field.Text += "b";
        Assert.Throws<InvalidOperationException>(() => field.BuildValue());
    }

    [Fact]
    public void FilesRead_PreservesTheNativeZeroLengthRequest()
    {
        var operation = Ma2aRemoteOperation.Create().Single(item => item.Family == "files" && item.Name == "read");
        var length = operation.Fields.Single(field => field.Name == "length");
        operation.Fields.Single(field => field.Name == "path").Text = "data:/file";
        Assert.Equal((ulong)Ma2aServiceRequests.MaximumInlineFileBytes, length.BuildValue()!.GetValue<ulong>());
        length.Number = Ma2aServiceRequests.MaximumInlineFileBytes + 1;
        Assert.Throws<InvalidOperationException>(() => length.BuildValue());
        length.Number = 0;
        var values = operation.Fields.ToDictionary(field => field.Name, field => field.BuildValue());

        Assert.Equal(0U, operation.Build!(values)[3]!.GetValue<uint>());
    }

    [Theory]
    [InlineData("transfer", "upload")]
    [InlineData("transfer", "download")]
    [InlineData("agent", "artifact")]
    public void LocalTransferPaths_AreValidatedByTheLocalFilesystem(string family, string name)
    {
        var operation = Ma2aRemoteOperation.Create().Single(item => item.Family == family && item.Name == name);
        var field = operation.Fields.Single(item => item.Name == "local_path");
        field.Text = new string('p', 4097);

        Assert.Equal(field.Text, field.BuildValue()!.GetValue<string>());
    }

    [Fact]
    public void ResultTree_RetainsDistinctConnectivityAndAuthorizationFields()
    {
        using var value = JsonDocument.Parse("""{"connections":[{"state":"connected"}],"spaces":[{"files_read":false}],"reachability":{"direct":true}}""");
        var nodes = Ma2aResultNode.ChildrenOf(value.RootElement);

        Assert.Equal(["connections", "spaces", "reachability"], nodes.Select(node => node.Label));
        Assert.Equal("state: connected", Assert.Single(Assert.Single(nodes[0].Children).Children).Label);
        Assert.Equal("files read: False", Assert.Single(Assert.Single(nodes[1].Children).Children).Label);
    }

    [Fact]
    public async Task FailedMutation_RetainsRequestIdentity_AndNeedsExplicitReconciliation()
    {
        var state = TempTree.CreateSocketDirectory("ma2ag");
        try
        {
            using var model = new Ma2aViewModel { StateDirectory = state };
            model.SelectedGroup = "Spaces / 授权";
            model.SelectedCommand = model.Commands.Single(command => command.Operation == "space_create");
            model.Fields.Single(field => field.Name == "name").Text = "gui-request-identity";
            model.Confirmed = true;

            await model.ExecuteCommand.ExecuteAsync(null);
            var request = model.RequestId;
            Assert.True(model.HasPending);
            Assert.Equal(32, request.Length);

            await model.RetryCommand.ExecuteAsync(null);
            Assert.True(model.HasPending);
            Assert.Equal(request, model.RequestId);
            model.SelectedCommand = model.Commands.Single(command => command.Operation == "space_list");
            Assert.True(model.CanExecuteSelected);
            await model.ExecuteCommand.ExecuteAsync(null);
            Assert.True(model.HasPending);
            Assert.Equal(request, model.RequestId);
            model.Confirmed = false;
            model.AcknowledgePendingCommand.Execute(null);
            Assert.True(model.HasPending);
            model.Confirmed = true;
            model.AcknowledgePendingCommand.Execute(null);
            Assert.False(model.HasPending);
        }
        finally { TempTree.Delete(state); }
    }
}
