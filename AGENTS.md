# AGENTS.md
1. 不要新增只被调用一次的小 helper；只有当它能命名清楚概念、隔离复杂逻辑、复用已有边界或改善测试时才抽函数。
2. 作为对1的补充，如果同样的逻辑出现超过3次，就应考虑作为共享函数，并写入项目记忆以供继续复用（如果有项目记忆的话）。
3. 新模块应围绕职责和变化原因命名，避免按“工具集合”“杂项”“common”堆放无关能力。
4. 不要假设自己处在沙箱(sandbox)网络中，这会让你认为自己无法访问网络，干扰测试。
5. 代码分析、修改和重构应使用Rider自带的mcp工具代替read/find/grep等默认工具。
6. 建立sub agent进行任务时，**务必**撰写提示词，让sub agent也使用Rider mcp工具代替read/find/grep等默认工具。
7. 除非用户明确要求，否则不得以**任何理由**逃避、跳过、结束调试，不得私自移除断点，一定要分析到断点的所有信息才能resume。
8. 本仓库代码频繁改动，**注释可能与实际代码逻辑不一致**。判断函数行为时**一律以代码实现为准**，不得仅凭注释下结论；改动前必须读代码核实当前真实行为。
9. **测试代码不得复制/重写业务逻辑**。测试只负责组装输入（数据、文件路径、配置）和断言输出，**完全复用**业务逻辑，不重新实现业务逻辑，也不可自己绕过业务逻辑。
10. 只为可独立重用或复杂到需要隔离的逻辑编写单元测试。对于其余逻辑，对整个功能进行集成测试或组件测试可以提供更好的覆盖率，且维护成本更低。
11. 当需要验证某个函数的行为、排查bug、或确认代码路径时，优先用Rider自带的xdebug_*工具，不要使用 `python` 或 `bash` 命令行启动。只有当MCP通讯受阻时，才应使用后台进程运行函数，以避免打断工作流。
12. 创建 run configuration 的方法见 `jetbrains-run-config` skill。
13. 如果用户的话里存在多个问题，**一定要写 TODO 清单或并行开启sub agent**，以防上下文固定于一个特定的问题而丢弃其他问题。
14. 优化prompts应注意简短和通用性，不可将当前文档或问题限定的内容完全输入提示词。
15. 编写测试代码时，禁止将文件输出到项目文件夹以外的路径。
16. 编写总结、计划、方案、报告、备忘时，**务必**使用中文文件名并输出到项目路径下/plans文件夹，禁止输出到/tmp路径。
17. 调试和分析问题时，应一次分析、修改、验证完毕，不要只分析然后说"仍需实施"，或者只改代码就报称修复。每次修复后必须用单元测试/集成测试/组件测试/断点调试验证实际效果，确认问题真正解决才能报完成。
18. 集成测试/组件测试必须全部通过才能运行主流程。
19. 禁止使用环境变量作为配置项。
20. 禁止修改代码格式（除非影响代码逻辑，例如不当的缩进导致编译错误或者if分支执行不完整），这会造成无谓的时间和token消耗；格式规约由dotnet format/csharpier钩子自动进行。
21. officecli是python-docx的超集，无论是检查还是处理docx文档信息，都应该使用officecli，禁止使用python-docx，也不许转成pdf或者用libreoffice解析，只是增加不必要实体。
22. 除非用户要求，否则禁止使用任何打分机制和置信度机制，因为没有理论指导如何消费这些分数。
23. 写提示词和注释的时候，一句话一行，例如：
    ```text
    你需要做如下工作，做完报告给我。
    ```
    禁止出现明明是一句话却要强行换行的情况：
    ```text
    你需要做如下工作，
    做完报告给我。
    ```
24. **确认废弃的代码一律删除**，不要想着"保留工作"或者"兜底"。死代码不只是垃圾，还会吸附后续修复接错线。重构时先查调用方，确认零调用即删；测试、参数、兼容层随机制一并删除。
25. 使用getter/setter操作属性时，应使用C# 14引入的`field`语法，示例：
    ```csharp
    // With C# 14: use the field keyword
    public int MaxLength {
    get;
    set {
    ArgumentOutOfRangeException.ThrowIfLessThan(value, 0);
    field = value;
    }
    }
    ```
26. 一个方法最好不要超过20条语句。
27. 用插值字符串代替字符串拼接（因为C#编译器对此有优化）。示例：
    ```csharp
    // GOOD
    string result = $"Welcome, {firstName} {lastName}!";

    // BAD，是字符串拼接
    string result = string.Format("Welcome, {0} {1}!", firstName, lastName);

    // BAD，是字符串拼接
    string result = string.Concat("Welcome, ", firstName, " ", lastName, "!");    
    ```
28. 对于小型、频繁传递的不可变数据，例如坐标、颜色或日期范围，使用struct/record struct代替class。
29. 若初始化逻辑平凡（或者说仅起到字段赋值作用而无其他逻辑），优先使用主构造函数（primary constructor）代替一般构造函数：
    ```csharp
    // Before primary constructors
    public class OrderService {
    private readonly IOrderRepository repository;

    public OrderService(IOrderRepository repository)
    {
        this.repository = repository;
    }
    }

    // With primary constructors
    public class OrderService(IOrderRepository repository){
    public async Task<Order> GetOrderAsync(Guid id) => await repository.GetByIdAsync(id);
    }
    ```
30. 对于无法直接编辑的第三方库，可以考虑使用扩展块（C# 14引入）和更早之前的扩展方法：
    ```csharp
    // C# 14 extension block
    extension(Order order) {
    public bool IsOverdue => order.DueDate < DateTimeOffset.UtcNow && !order.IsCompleted; 
    public void MarkAsShipped(DateTimeOffset shippedAt) {
            order.Status = OrderStatus.Shipped;
            order.ShippedAt = shippedAt;
        }
    }
    ```
    但不要给你能直接修改的类添加扩展，这属于逻辑浪费。
31. 在泛型类或方法中，不要与 object 类型进行来回转换，而应使用 where 约束或 as 运算符来指定泛型参数的确切特性。例如：
    ```csharp
    class SomeClass {}

    // Don't
    class MyClass {
    void SomeMethod(T t){
    object temp = t;
    SomeClass obj = (SomeClass) temp;
    }
    }
    
    // Do
    class MyClass where T : SomeClass {
    void SomeMethod(T t) {
    SomeClass obj = t;
    }
    }
    ```
32. LINQ表达式的返回结果应进行物化，例如：
    ```csharp
    var query =
        from customer in db.Customers
        where customer.Balance > GoldMemberThresholdInEuro
        select new GoldMember(customer.Name, customer.Balance);

    return query; // LINQ是延迟执行的，所以query实质上是表达式树，而非最终结果
    return query.ToList();  //所以需要ToList()、ToArray()方法进行物化
    ```
33. 何时使用IEnumerable<T>/Span<T>（栈上）/Memory<T>（堆上）？
    ```
    需要处理数据吗？
    ├── 是连续内存块（数组、字符串、栈内存、非托管内存）？
    │   ├── 方法完全是同步的？
    │   │   ├── 需要写入？→ Span<T>
    │   │   └── 只读？→ ReadOnlySpan<T>
    │   └── 需要跨 await、存为字段、或传入异步流？
    │       ├── 需要写入？→ Memory<T>
    │       └── 只读？→ ReadOnlyMemory<T>
    └── 是离散/惰性序列（数据库行、文件行、无限流）？
        └── 用 IEnumerable<T> / IAsyncEnumerable<T>
    ```

    使用Span<T>/Memory<T>提升性能的典例：

    | 对比维度    | 传统方式                            | Span / Memory 方式                | 典型提升              |
    | ------- | ------------------------------- | ------------------------------- | ----------------- |
    | 字符串切片   | `Substring`                     | `ReadOnlySpan<char>.Slice`      | **4~7.5x 速度，零分配** |
    | 字符串分割   | `string.Split`                  | `ReadOnlySpan<char>.Split`      | **~38% 速度，零分配**   |
    | 数组子集    | `Array.Copy` + `new[]`          | `Span<T>.Slice`                 | **O(1) 切片，零分配**   |
    | 数字解析    | `int.Parse(str.Substring(...))` | `int.Parse(str.AsSpan(...))`    | **零子串分配**         |
    | 小缓冲区    | `new byte[256]`                 | `stackalloc byte[256]` + `Span` | **13x 速度，零堆分配**   |
    | GUID 转换 | `Guid.Parse` / `ToString()`     | `Span` 解析/格式化                   | **40~50% 速度，减分配** |
    
    一些普通操作的速查表：

    | 场景               | 推荐类型                                     | 理由                         |
    | ---------------- | ---------------------------------------- | -------------------------- |
    | 同步字符串/数组处理       | `ReadOnlySpan<T>` / `Span<T>`            | 零分配切片，无 GC 压力              |
    | `stackalloc` 缓冲区 | `Span<T>`                                | 唯一支持栈内存的类型                 |
    | P/Invoke 同步调用    | `Span<T>`                                | 可直接映射到指针                   |
    | 异步 I/O 缓冲区       | `Memory<T>`                              | 可跨 `await`，再转 `Span` 处理    |
    | 类字段保存缓冲区         | `Memory<T>`                              | `Span` 不能作为字段              |
    | 数据库/文件行遍历        | `IEnumerable<T>` / `IAsyncEnumerable<T>` | 非连续，惰性求值                   |
    | 需要 LINQ 操作       | `IEnumerable<T>`                         | `Span`/`Memory` 不支持原生 LINQ |
    | 不确定数据是否连续        | `IEnumerable<T>`                         | 最通用的抽象                     |

34. 代码中的字面量（魔数）应以常量声明封装（若用于日志记录和追踪除外），例如：
    ```csharp
    public class Whatever {
        public static readonly Color PapayaWhip = new Color(0xFFEFD5); //字面量0xFFEFD5用PapayaWhip声明
        public const int MaxNumberOfWheels = 18;  //字面量18用MaxNumberOfWheels声明
        public const byte ReadCreateOverwriteMask = 0b0010_1100;  //字面量0b0010_1100用ReadCreateOverwriteMask声明
    }
    ```
    语义非常明确且不会发生变化的情况也不必封装，例如：
    ```
    mean = (a + b) / 2; // 平均数自然是/2，声明成常量反倒是画蛇添足
    WaitMilliseconds(waitTimeInSeconds * 1000); 秒换算成毫秒必须是*1000，声明成常量也是画蛇添足
    ```
35. 避免使用嵌套的try catch块，这会减弱可读性。
36. 不要使用ref/out参数，这会降低可读性，返回复合对象、结构体或元组。
    例外情况：
    `bool success = int.TryParse(text, out int number); //使用了TryParse或者依赖库里本身就有ref/out参数，需要保证逻辑正确`
37. 若字符串含有大量转义字符，需要用原始字符串字面量代替：
    ```csharp
    string pattern = "^(https?:\\/\\/)(www\\.)?[a-zA-Z0-9]+\\.[a-z]+$"; //存在大量转义字符！需要放弃
    string pattern = """^(https?:\/\/)(www\.)?[a-zA-Z0-9]+\.[a-z]+$"""; //原始字符串字面量用"""开始和结束，转义字符减少很多
    ```
38. async/await用于I/O密集型任务，Task.Run()/Task.StartNew()则用于CPU（计算）密集型任务。
39. 建议直接await ValueTask或者ValueTask<T>，且只等待一次：
    ```csharp
    // OK / GOOD
    int bytesRead = await stream.ReadAsync(buffer, cancellationToken);

    // OK / GOOD
    int bytesRead = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

    // OK / GOOD - Get task if you want to overcome the limitations exposed by ValueTask / ValueTask<T>
    Task<int> task = stream.ReadAsync(buffer, cancellationToken).AsTask();
    ```
40. 应尽量使用C#新语法或者更规范的语法，包括且不限以下的内容：
    ```csharp
    ValueTuple<string, int> tuple = new ValueTuple<string, int>("", 1); //旧语法，不再使用
    (string, int) tuple = ("", 1); //推荐的新语法
    ```
    ```csharp
    Nullable<DateTime> startDate; //旧语法，不再使用
    DateTime? startDate; //推荐的新语法
    ```
    ```csharp
    if (startDate == null) ... //不规范，不推荐使用
    if (startDate is null) ... //规范用法
    ```
    ```csharp
    if (startDate.HasValue) ... //不规范，不推荐使用
    if (startDate is not null) ... //规范用法
    ```
    ```csharp
    if (startDate.HasValue && startDate.Value > DateTime.Now) ... // startDate.Value > DateTime.Now说明startData有值，没必要再判断一遍
    if (startDate > DateTime.Now) ...  // 正确的做法：只做必要的判断
    ```
    ```csharp
    List<string> items = new List<string>();
    List<string> items = [];  // []比new List……更加简单直观
    ```
    ```csharp
    if (list == null) list = []; // 旧语法，且易读性低，不再使用
    list ??= []; //新语法，推荐使用
    ```
41. C#支持解构元组，错误和正确示例如下：
    ```csharp
    // 错误示例，写法繁冗
    public record Point(int X, int Y);

    Point point = GetOrigin();
    int x = point.X;
    int y = point.Y;
    ```
    ```csharp
    // 正确示例，写法简单
    (int x, int y) = GetOrigin();
    
    // 也可以使用模式匹配解构数组
    if (items is [int first, int second, ..])  {
    // use first and second directly
    }
    
    //对foreach也适用
    foreach ((int key, int value) in dictionary){
    Console.WriteLine($"{key}: {value}");
    }
    ```
42. 不要使用#region标记。
43. 写注释时禁止写入被否定的决策，除非它对应的代码曾在真实的运行中出现过bug。例如：
    ```
    // 注释：白米粥里需要加入大便……
    用户驳回：白米粥里为什么要加入大便？
    // 注释：白米粥里不要加入大便，理由是大便污染了米粥……   ------> 错！白米粥一开始就不应该有大便！
    // 注释：白米粥的米和水体积比约为1：1.2……            --------> 对！说白米粥怎么做就可以了！
    // 注释：白米粥里之所以出现大便，是因为用户的狗在碗里拉屎，解决方法：把狗杀了。 ----------> 对！出现过真实的问题（狗在碗里拉屎），并给出解决方式（杀狗）！
    ```
44. 在写C#时，除非确实需要控制反转，否则不要总是试图用依赖注入去写逻辑。
45. 禁止编写无行为的try catch块：
    ```csharp
    try{
        doSomething();
    }catch {
        //do Nothing! 不应该！
    }
    ```