using Xunit;

// У тестируемых классов есть статическое состояние (Calculator.Degrees, IslandCommands.*,
// MediaControl.*), поэтому гонять тесты параллельно нельзя.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
