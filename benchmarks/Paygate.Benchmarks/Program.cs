using BenchmarkDotNet.Running;

// dotnet run -c Release --project benchmarks/Paygate.Benchmarks -- --filter "*"
BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
