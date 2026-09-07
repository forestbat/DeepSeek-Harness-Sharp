# DeepSeek-Harness-Sharp

一个用C#重写的DeepSeek Harness实现，初衷是为了解决DeepSeek Harness在高并发下性能减退和崩溃的问题（目前本人测试，TUI单进程内存占用约为20MB）。

运行方式：
1. 配置文件默认位于`%USERPROFILE%\.dsh\settings.yaml`(Windows) 或 `~/.dsh/settings.yaml`(Linux)，若无可新建。
2. 配置文件写法（目前是最小冒烟测试，只支持一个模型配置）：
    ```yaml
    provider: openai-compatiable
    model: deepseek-v4-flash
    baseUrl: https://api.deepseek.com
    apiKey: DEEPSEEK_API_KEY
    reasoningEffort: max
    ```
3. 构建程序和运行：
    ```
    dotnet build
    dotnet run --project "DeepSeek-Harness-Sharp/DeepSeek-Harness-Sharp.csproj" -- --profile tui
    // 也可以使用无头模式测试
    dotnet run --project "DeepSeek-Harness-Sharp/DeepSeek-Harness-Sharp.csproj" -- --profile headless 
    ```
4. 现在可以发送测试信息。