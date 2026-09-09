using Xunit;

// 端到端测试(真实 Erbai.Connector.exe 子进程 + 管道)在程序集内并行时
// 存在子进程启动竞争,偶发激活/执行超时失败(单跑与串行必绿)。
// 禁并行:本程序集测试均为子进程端到端,串行执行开销可接受。
[assembly: CollectionBehavior(DisableTestParallelization = true)]
